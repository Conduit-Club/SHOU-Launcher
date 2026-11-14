#!/usr/bin/env python3
"""Portable ephemeral-runner gate; local night work must keep its existing controller."""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys

GIB = 1024 ** 3

def space_decision(free, used, peak, reserve_gib=20):
    if free < 10 * GIB:
        return 'STOP_FREE_BELOW_10_GIB'
    if free < reserve_gib * GIB or free - peak < reserve_gib * GIB:
        return 'BLOCKED_SYSTEM_RESERVE_' + str(reserve_gib) + '_GIB'
    if used + peak > 35 * GIB:
        return 'BLOCKED_TASK_BUDGET_35_GIB'
    return 'PASS'

def tree_bytes(root):
    def is_link(path):
        info = path.lstat()
        return stat.S_ISLNK(info.st_mode) or bool(getattr(info, 'st_file_attributes', 0) & 0x400)
    total = 0
    for folder, directories, files in os.walk(root, followlinks=False):
        directories[:] = [d for d in directories if not is_link(Path(folder) / d)]
        for name in files:
            path = Path(folder) / name
            try:
                if not is_link(path):
                    total += path.stat().st_size
            except FileNotFoundError:
                pass
    return total

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--worker', choices=['main', 'ui', 'platform'], required=True)
    parser.add_argument('--job', required=True)
    parser.add_argument('--peak-gib', type=float, required=True)
    parser.add_argument('--cwd', type=Path, required=True)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        raise ValueError('This gate is CI-only; use the existing local control/dev.py')
    root = Path(os.environ['SHOU_DEV_ROOT']).resolve(strict=True)
    if (root / 'control/NIGHT-GUARDS.md').exists():
        raise ValueError('Cannot replace or bypass the night controller')
    policy = json.loads((root / 'control/resource-policy.json').read_text())
    if policy.get('profile') != 'ephemeral-github-standard' or policy.get('reserve_gib') != 8 or policy.get('stop_free_gib') != 10:
        raise ValueError('Unregistered ephemeral runner policy')
    registry = json.loads((root / 'control/tools.json').read_text())
    java_home = Path(registry['java']['home'])
    if not java_home.is_absolute() or not java_home.is_dir():
        raise ValueError('The selected CI Java home is unavailable')
    suffix = '.exe' if os.name == 'nt' else ''
    if any(not (java_home / 'bin' / (tool + suffix)).is_file() for tool in ['java', 'javac', 'jar', 'jcmd']):
        raise ValueError('The registered CI JDK is incomplete')
    checkout = Path(os.environ['GITHUB_WORKSPACE']).resolve(strict=True)
    cwd = args.cwd.resolve(strict=True)
    if cwd != checkout or not re.fullmatch(r'[A-Za-z0-9_.-]+', args.job) or args.job in ['.', '..'] or not 0 < args.peak_gib <= 35:
        raise ValueError('Invalid registered checkout, job identity or peak budget')
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command or not Path(command[0]).is_absolute():
        raise ValueError('An explicit absolute tool is required')
    state = root / 'state'
    state.mkdir(exist_ok=True)
    lock = state / 'heavy.lock'
    if lock.is_symlink():
        raise ValueError('Lock must not be a symbolic link')
    with lock.open('a+b') as handle:
        if os.name == 'nt':
            import msvcrt
            if handle.tell() == 0:
                handle.write(b'0'); handle.flush()
            handle.seek(0)
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        free = shutil.disk_usage(root).free
        used = tree_bytes(root)
        decision = space_decision(free, used, args.peak_gib * GIB, reserve_gib=policy['reserve_gib'])
        event = {'at': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'worker': args.worker,
            'job': args.job, 'free_bytes': free, 'task_bytes': used, 'peak_gib': args.peak_gib,
            'decision': decision, 'resource_policy': policy, 'warning_25_gib': used + args.peak_gib * GIB >= 25 * GIB}
        reports = root / 'reports'; reports.mkdir(exist_ok=True)
        with (reports / 'space-events.jsonl').open('a') as output:
            output.write(json.dumps(event) + '\n')
        if decision != 'PASS':
            print(json.dumps(event)); return 75
        environment = dict(os.environ)
        environment['SHOU_WORKER'] = args.worker
        environment['SHOU_JOB_ID'] = args.job
        # Preserve the registered alias spelling for tests and use the same JDK for bootstrap compilation.
        environment['JAVA_HOME'] = str(java_home)
        mapping = {'DOTNET_CLI_HOME': root / 'state/cli-home' / args.worker,
            'TMPDIR': root / 'tmp' / args.worker / args.job,
            'NUGET_PACKAGES': root / 'cache/nuget/packages',
            'NUGET_HTTP_CACHE_PATH': root / 'cache/nuget/http',
            'NUGET_SCRATCH': root / 'cache/nuget/scratch',
            'NUGET_PLUGINS_CACHE_PATH': root / 'cache/nuget/plugins'}
        for name, path in mapping.items():
            path.mkdir(parents=True, exist_ok=True); environment[name] = str(path)
        environment['TMP'] = environment['TEMP'] = environment['TMPDIR']
        owner = state / 'heavy-owner.json'
        owner.write_text(json.dumps({'worker': args.worker, 'job': args.job, 'pid': os.getpid(), 'cwd': str(cwd)}))
        code = 1
        try:
            code = subprocess.run(command, cwd=cwd, env=environment).returncode
            return code
        finally:
            # Remove only this controller's inactive owner metadata; never remove the lock inode.
            if owner.exists() and json.loads(owner.read_text()).get('pid') == os.getpid():
                owner.unlink()
            with (reports / 'commands.jsonl').open('a') as output:
                output.write(json.dumps({'command': command, 'cwd': str(cwd), 'exit_code': code,
                    'job': args.job, 'source_sha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=cwd, text=True).strip()}) + '\n')

if __name__ == '__main__':
    sys.exit(main())
