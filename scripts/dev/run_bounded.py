#!/usr/bin/env python3
"""Bound a single owned child process group; retain exact command/exit/timeout evidence."""
import argparse
import datetime
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--timeout', type=float, required=True)
p.add_argument('--report', type=Path, required=True)
p.add_argument('command', nargs=argparse.REMAINDER)
a = p.parse_args()
command = a.command[1:] if a.command[:1] == ['--'] else a.command
root = Path(os.environ['SHOU_DEV_ROOT']).resolve()
report = a.report.resolve()
if not report.is_relative_to(root / 'reports') or not command or a.timeout <= 0:
    raise ValueError('A registered report, positive deadline and explicit command are required')
report.parent.mkdir(parents=True, exist_ok=True)
log = report.with_suffix('.log')
started = time.time()
timed_out = False
with log.open('w') as output:
    child = subprocess.Popen(command, stdout=output, stderr=subprocess.STDOUT,
        start_new_session=os.name != 'nt',
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == 'nt' else 0)
    try:
        code = child.wait(timeout=a.timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        if os.name == 'nt':
            subprocess.run(['taskkill', '/PID', str(child.pid), '/T', '/F'], check=False, stdout=output, stderr=subprocess.STDOUT)
        else:
            os.killpg(child.pid, signal.SIGTERM)
            try: child.wait(timeout=5)
            except subprocess.TimeoutExpired: os.killpg(child.pid, signal.SIGKILL)
        child.wait()
        code = 124
record = {'command': command, 'cwd': str(Path.cwd()), 'owned_pid': child.pid,
    'exit_code': code, 'timed_out': timed_out, 'seconds': time.time() - started,
    'finished_at': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'log': str(log)}
temporary = report.with_suffix('.tmp')
temporary.write_text(json.dumps(record, indent=2) + '\n')
os.replace(temporary, report)
print(json.dumps(record), flush=True)
if code: print(log.read_text(errors='replace')[-8000:], flush=True)
sys.exit(code)
