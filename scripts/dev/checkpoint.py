#!/usr/bin/env python3
"""Precise uncommitted patch + SHA-256 snapshot without touching the Git index."""
import argparse, hashlib, json, os, pathlib, subprocess, datetime

def git(repo, *args, accepted=(0,)):
    result = subprocess.run(['git', '-C', str(repo), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode not in accepted:
        raise RuntimeError(result.stderr.decode(errors='replace'))
    return result.stdout

def paths(repo):
    raw = git(repo, 'ls-files', '-z') + git(repo, 'ls-files', '--others', '--exclude-standard', '-z')
    result = sorted(set(x.decode() for x in raw.split(b'\0') if x))
    if any(pathlib.PurePosixPath(x).name.startswith('.env') and pathlib.PurePosixPath(x).name != '.env.example' for x in result):
        raise RuntimeError('Credential-like file unexpectedly in snapshot set')
    return result

def capture(repo, out, baseline=None):
    repo, out = repo.resolve(), out.resolve()
    root=pathlib.Path(os.environ.get('SHOU_DEV_ROOT', '/Users/moear/Code/SHOU-Launcher/.shou-dev')).resolve()
    if not repo.is_relative_to(root / 'worktrees') or not out.is_relative_to(root / 'reports'):
        raise ValueError('Checkpoint must use a task worktree and reports destination')
    out.mkdir(parents=True,exist_ok=True)
    names=paths(repo)
    data={}
    for name in names:
        source=repo/name
        if not source.is_file(): continue
        if source.is_symlink(): raise ValueError('Snapshot does not follow source symlinks: '+name)
        target=out/'files'/name;target.parent.mkdir(parents=True,exist_ok=True)
        content=source.read_bytes();target.write_bytes(content);target.chmod(source.stat().st_mode & 0o777)
        data[name]={'sha256':hashlib.sha256(content).hexdigest(),'mode':source.stat().st_mode & 0o777}
    if baseline is None:
        patch=git(repo,'diff','--binary','HEAD','--')
        for name in git(repo,'ls-files','--others','--exclude-standard','-z').split(b'\0'):
            if not name: continue
            patch+=git(repo,'diff','--no-index','--binary','--','/dev/null',name.decode(),accepted=(0,1))
    else:
        old=json.loads((baseline/'snapshot.json').read_text())['files']
        patch=b''
        for name in sorted(set(old)|set(data)):
            if old.get(name)==data.get(name): continue
            before=(baseline/'files'/name) if name in old else pathlib.Path('/dev/null')
            after=(repo/name) if name in data else pathlib.Path('/dev/null')
            delta=git(repo,'diff','--no-index','--binary','--',str(before),str(after),accepted=(0,1))
            lines=delta.splitlines(keepends=True)
            fixed=[]
            for line in lines:
                if line.startswith(b'diff --git '): line=('diff --git a/'+name+' b/'+name+'\n').encode()
                elif line.startswith(b'--- ') and before!=pathlib.Path('/dev/null'): line=('--- a/'+name+'\n').encode()
                elif line.startswith(b'+++ ') and after!=pathlib.Path('/dev/null'): line=('+++ b/'+name+'\n').encode()
                fixed.append(line)
            patch+=b''.join(fixed)
    (out/'changes.patch').write_bytes(patch)
    manifest={'created_at':datetime.datetime.now(datetime.timezone.utc).isoformat(),'worktree':str(repo),
        'branch':git(repo,'branch','--show-current').decode().strip(),'base_sha':git(repo,'merge-base','HEAD','origin/dev').decode().strip(),'current_sha':git(repo,'rev-parse','HEAD').decode().strip(),
        'dirty':bool(git(repo,'status','--porcelain').strip()),'baseline_snapshot':str(baseline) if baseline else None,
        'patch_path':str(out/'changes.patch'),'patch_sha256':hashlib.sha256(patch).hexdigest(),'files':data}
    (out/'snapshot.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n')
    (out/'SHA256SUMS').write_text(''.join(v['sha256']+'  '+k+'\n' for k,v in data.items()))
    print(json.dumps({k:v for k,v in manifest.items() if k!='files'},ensure_ascii=False))

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--repo',type=pathlib.Path,required=True);p.add_argument('--out',type=pathlib.Path,required=True);p.add_argument('--baseline',type=pathlib.Path)
    a=p.parse_args();capture(a.repo,a.out,a.baseline)
