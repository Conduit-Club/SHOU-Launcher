#!/usr/bin/env python3
"""Exercise the real signed launcher across an update and credential-component faults.

No real account, interactive authorization or game is used. The input candidate
stays unchanged; temporary copies and signed test executables are removed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import secrets
import shutil
import subprocess
from manage_signing import initialize, run


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app',type=Path,required=True)
    parser.add_argument('--directory',type=Path,required=True)
    args=parser.parse_args(); app=args.app.resolve(); output=args.directory.resolve()
    output.mkdir(parents=True,exist_ok=False)
    original={p.relative_to(app).as_posix():hashlib.sha256(p.read_bytes()).hexdigest()
              for p in app.rglob('*') if p.is_file() and not p.is_symlink()}
    identity=os.environ.get('SHOU_MACOS_SIGNING_IDENTITY'); keychain=os.environ.get('SHOU_MACOS_SIGNING_KEYCHAIN')
    if not identity or not keychain:
        config=initialize(Path.home()/'Library/Application Support/SHOU-Launcher-Signing')
        identity,keychain=config['identity'],config['keychain']
    previous=[s.strip().strip('"') for s in run(['/usr/bin/security','list-keychains','-d','user']).splitlines()]
    default=run(['/usr/bin/security','default-keychain','-d','user']).strip('"')
    temporary=output/'synthetic.keychain-db'; updated=output/'updated.app'; held=output/'held-component'
    fake=output/'fake-component'; capture=output/'sent-to-fake.txt'; created=False; cases=[]
    managed_backup=output/'sealed-core-original.dll'
    data=output/'data'; run_id='packaged-credential-update'
    repo=Path(__file__).resolve().parents[2]
    broker_relative=Path('Contents/Helpers/SHOU.CredentialBroker')
    broker_hash=hashlib.sha256((app/broker_relative).read_bytes()).hexdigest()

    def sign_bundle(bundle):
        current=[s.strip().strip('"') for s in run(['/usr/bin/security','list-keychains','-d','user']).splitlines()]
        try:
            run(['/usr/bin/security','list-keychains','-d','user','-s',keychain,*current])
            run(['/usr/bin/codesign','--force','--sign',identity,'--keychain',keychain,'--timestamp=none',
                 '--requirements','=designated => identifier "club.conduit.shou.launcher" and certificate leaf = H"'+identity+'"',
                 '--entitlements',repo/'scripts/macos/Entitlements.plist',bundle])
        finally: run(['/usr/bin/security','list-keychains','-d','user','-s',*current])

    def command(bundle, action, label, expected=True):
        result=output/(label+'.json')
        # Credential-smoke exits before creating an Avalonia window.
        subprocess.run(['/usr/bin/open','-n','-W',str(bundle),'--args','--data-dir',str(data),
                        '--credential-smoke',action,'--run-id',run_id,'--result-file',str(result),
                        '--exit-after','25'],check=True,timeout=45,capture_output=True)
        report=json.loads(result.read_text())
        assert (report['status']=='PASS') == expected, (label,report.get('errorType'))
        cases.append(dict(case=label,status='PASS',application_result=report['status']))
        return report

    try:
        subprocess.run(['/usr/bin/ditto',str(app),str(updated)],check=True)
        info=updated/'Contents/Info.plist'; metadata=plistlib.loads(info.read_bytes())
        metadata['CFBundleVersion'] += '.credential-update-test'; info.write_bytes(plistlib.dumps(metadata))
        sign_bundle(updated)
        hashes=[]
        for candidate in [app,updated]:
            run(['/usr/bin/codesign','--verify','--deep','--strict',candidate])
            inspect=subprocess.run(['/usr/bin/codesign','-d','--verbose=4',str(candidate)],capture_output=True,text=True,check=True)
            hashes.append(re.search(r'CDHash=(\w+)',inspect.stderr)[1])
        assert hashes[0] != hashes[1]
        assert hashlib.sha256((updated/broker_relative).read_bytes()).hexdigest()==broker_hash
        if os.environ.get('GITHUB_ACTIONS')=='true':
            password=secrets.token_urlsafe(48)
            run(['/usr/bin/security','create-keychain','-p',password,temporary])
            run(['/usr/bin/security','unlock-keychain','-p',password,temporary])
            run(['/usr/bin/security','default-keychain','-d','user','-s',temporary])
            run(['/usr/bin/security','list-keychains','-d','user','-s',temporary,*previous])
        command(app,'write','previous-write'); created=True
        command(updated,'read','updated-read-without-ui')
        command(updated,'write','updated-write-without-ui')
        command(app,'read','previous-read-after-update')
        command(updated,'delete','updated-delete'); created=False
        missing=command(app,'read','deleted-item-absent',False)
        assert missing.get('matched') is False
        # A modified managed/resources bundle must not be able to use the broker.
        resource=updated/'Contents/Resources/club-logo.png'; original_resource=resource.read_bytes()
        resource.write_bytes(original_resource+b'\0')
        command(updated,'write','tampered-parent-denied',False)
        resource.write_bytes(original_resource)
        command(app,'read','tampered-parent-did-not-write',False)
        managed=updated/'Contents/MacOS/SHOU.Launcher.Core.dll'
        subprocess.run(['/usr/bin/ditto',str(managed),str(managed_backup)],check=True)
        with managed.open('ab') as file: file.write(b'\0') # CLR accepts a PE overlay; its signature must still fail.
        command(updated,'write','tampered-managed-assembly-denied',False)
        subprocess.run(['/usr/bin/ditto',str(managed_backup),str(managed)],check=True)
        managed_backup.unlink()
        command(app,'read','tampered-managed-assembly-did-not-write',False)
        (updated/broker_relative).rename(held)
        command(updated,'write','missing-component-no-legacy-fallback',False)
        command(app,'read','missing-component-did-not-write',False)
        held.rename(updated/broker_relative)
        source=output/'fake.c'
        source.write_text('#include <stdint.h>\n#include <unistd.h>\n#include <stdio.h>\n'
            'int main(){const char *path='+json.dumps(str(capture))+';FILE*f=fopen(path,"w");'
            'if(!f)return 1;fputs("0",f);fclose(f);uint32_t h[2]={0x53484f55u,0};write(1,h,8);'
            'char b[128];int total=0;ssize_t n;while((n=read(0,b,sizeof b))>0){total+=n;'
            'f=fopen(path,"w");fprintf(f,"%d",total);fclose(f);}return 0;}')
        run(['/usr/bin/xcrun','clang',source,'-o',fake])
        run(['/usr/bin/codesign','--force','--sign','-','--identifier','club.conduit.shou.launcher.credentials.v1',fake])
        (updated/broker_relative).rename(held); shutil.copy2(fake,updated/broker_relative); sign_bundle(updated)
        command(updated,'write','impostor-component-rejected-before-secret',False)
        assert capture.read_text()=='0', 'The untrusted child received request/credential bytes.'
        command(app,'read','impostor-component-did-not-write',False)
        (updated/broker_relative).unlink(); held.rename(updated/broker_relative); sign_bundle(updated)
        command(updated,'write','restored-component-write'); created=True
        command(app,'read','restored-component-read')
        command(app,'delete','restored-component-delete'); created=False
        assert all(hashlib.sha256((app/name).read_bytes()).hexdigest()==value for name,value in original.items())
        result=dict(status='PASS',caller_cdhashes=hashes,broker_sha256=broker_hash,
                    candidate_unchanged=True,cases=cases,synthetic_only=True,
                    keychain='temporary CI keychain' if temporary.exists() else 'current login; unique isolated namespace',
                    authentication_ui=False,minecraft_started=False)
        (output/'result.json').write_text(json.dumps(result,indent=2)+'\n'); print(json.dumps(result,indent=2))
    finally:
        try:
            if created: command(app,'delete','failure-cleanup')
        finally:
            try:
                if temporary.exists():
                    try: run(['/usr/bin/security','default-keychain','-d','user','-s',default])
                    finally: run(['/usr/bin/security','delete-keychain',temporary])
            finally:
                run(['/usr/bin/security','list-keychains','-d','user','-s',*previous])
                if updated.exists(): shutil.rmtree(updated)
                held.unlink(missing_ok=True); fake.unlink(missing_ok=True)
                managed_backup.unlink(missing_ok=True)


if __name__=='__main__':
    main()
