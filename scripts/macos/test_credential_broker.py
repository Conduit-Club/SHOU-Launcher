#!/usr/bin/env python3
"""Verify the production frozen broker with changed, unrelated and tampered callers.

Only a fresh isolated service and a fixed synthetic value are used. On GitHub's
ephemeral runner, use a temporary default keychain and restore it on every exit.
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
import uuid
from manage_signing import initialize, run


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    output = parser.parse_args().directory.resolve()
    output.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[2]
    native = repo / 'src/SHOU.Launcher.Desktop/Native/MacOS'
    manifest = json.loads((native / 'frozen-arm64-v1/manifest.json').read_text())
    payload = native / 'frozen-arm64-v1/SHOU.CredentialBroker'
    assert hashlib.sha256(payload.read_bytes()).hexdigest() == manifest['payload_sha256']
    assert hashlib.sha256((native / 'credential_broker.c').read_bytes()).hexdigest() == manifest['source_sha256']
    identity = os.environ.get('SHOU_MACOS_SIGNING_IDENTITY')
    keychain = os.environ.get('SHOU_MACOS_SIGNING_KEYCHAIN')
    if not identity or not keychain:
        config = initialize(Path.home() / 'Library/Application Support/SHOU-Launcher-Signing')
        identity, keychain = config['identity'], config['keychain']
    assert identity == manifest['signing_identity']
    previous = [line.strip().strip('"') for line in run(['/usr/bin/security','list-keychains','-d','user']).splitlines()]
    default = run(['/usr/bin/security','default-keychain','-d','user']).strip('"')
    temporary = output / 'synthetic.keychain-db'
    service = 'com.conduitclub.shou-launcher.isolated.' + hashlib.sha256(uuid.uuid4().bytes).hexdigest()
    cases = []; created = False; clients = []
    broker = output / 'SHOU.CredentialBroker'
    shutil.copy2(payload, broker)
    run(['/usr/bin/codesign','--verify','--strict',broker])
    try:
        run(['/usr/bin/security','list-keychains','-d','user','-s',keychain,*previous])
        for version, name, identifier, adhoc in [(1,'previous','club.conduit.shou.launcher',False),
                (2,'updated','club.conduit.shou.launcher',False),
                (3,'unrelated','club.conduit.shou.launcher',True),
                (4,'other-app','club.conduit.shou.other',False)]:
            client = output / name; clients.append(client)
            run(['/usr/bin/xcrun','clang','-DVERSION='+str(version),'-DTEST_SERVICE="'+service+'"',
                 repo / 'scripts/macos/fixtures/credential_broker_client.c','-o',client])
            options = ['--sign','-'] if adhoc else ['--sign',identity,'--keychain',keychain]
            run(['/usr/bin/codesign','--force','--timestamp=none','--identifier',identifier,*options,client])
        bundle = output / 'tampered.app'; mac = bundle / 'Contents/MacOS'; resources=bundle/'Contents/Resources'
        mac.mkdir(parents=True); resources.mkdir(); shutil.copy2(clients[1],mac/'client')
        (bundle/'Contents/Info.plist').write_bytes(plistlib.dumps(dict(CFBundleExecutable='client',CFBundleIdentifier='club.conduit.shou.launcher',CFBundlePackageType='APPL')))
        (resources/'sealed.txt').write_text('original')
        run(['/usr/bin/codesign','--force','--sign',identity,'--keychain',keychain,'--timestamp=none',bundle])
        (resources/'sealed.txt').write_text('modified after signing')
        if os.environ.get('GITHUB_ACTIONS') == 'true':
            password=secrets.token_urlsafe(48)
            run(['/usr/bin/security','create-keychain','-p',password,temporary])
            run(['/usr/bin/security','unlock-keychain','-p',password,temporary])
            run(['/usr/bin/security','default-keychain','-d','user','-s',temporary])
            run(['/usr/bin/security','list-keychains','-d','user','-s',temporary,*previous])
        else:
            run(['/usr/bin/security','list-keychains','-d','user','-s',*previous])
        caller_hashes=[]
        for client in clients[:2]:
            signed=subprocess.run(['/usr/bin/codesign','-d','--verbose=4',str(client)],capture_output=True,text=True,check=True)
            caller_hashes.append(re.search(r'CDHash=(\w+)',signed.stderr)[1])
        assert caller_hashes[0] != caller_hashes[1]
        def request(client, operation, expected, label):
            result=run([client,broker,str(operation)])
            assert re.search(r'status=(-?\d+)',result)[1] == str(expected), (label,result)
            assert 'matched=1' in result
            cases.append(dict(case=label,status='PASS',native_status=expected))
        request(clients[0],2,0,'previous-writes'); created=True
        request(clients[0],1,0,'previous-reads')
        request(clients[1],1,0,'updated-caller-reads-without-ui')
        request(clients[1],2,0,'updated-caller-writes-without-ui')
        request(clients[2],1,-25293,'ad-hoc-caller-denied')
        request(clients[3],1,-25293,'same-certificate-other-app-denied')
        request(mac/'client',1,-25293,'tampered-parent-resource-denied')
        request(clients[0],5,-50,'unrelated-service-denied')
        request(clients[1],3,0,'updated-caller-deletes'); created=False
        request(clients[0],1,-25300,'deleted-fixture-absent')
        inspection=subprocess.run(['/usr/bin/codesign','-d','--verbose=4',str(broker)],capture_output=True,text=True,check=True)
        result=dict(status='PASS',identity=identity,broker_sha256=manifest['payload_sha256'],
                    broker_cdhash=re.search(r'CDHash=(\w+)',inspection.stderr)[1],
                    caller_cdhashes=caller_hashes,
                    keychain='temporary CI keychain' if temporary.exists() else 'current login; unique synthetic namespace',
                    cases=cases,synthetic_only=True,authentication_ui=False,minecraft_started=False)
        (output/'result.json').write_text(json.dumps(result,indent=2)+'\n')
        print(json.dumps(result,indent=2))
    finally:
        try:
            if created: run([clients[0],broker,'3'])
        finally:
            try:
                if temporary.exists():
                    run(['/usr/bin/security','default-keychain','-d','user','-s',default])
                    run(['/usr/bin/security','delete-keychain',temporary])
            finally:
                run(['/usr/bin/security','list-keychains','-d','user','-s',*previous])
                # Do not publish production-identifier test executables as artifacts.
                for client in clients:
                    client.unlink(missing_ok=True)
                if (output/'tampered.app').exists(): shutil.rmtree(output/'tampered.app')
                broker.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
