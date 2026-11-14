#!/usr/bin/env python3
"""Explicitly rebuild/sign the frozen credential broker (not an ordinary app build).

Changing its executable can require one new Keychain authorization. Keep the
reviewable source and frozen signed asset together; never ship a private key.
"""
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import os
import re
from manage_signing import initialize, run

if platform.system() != 'Darwin':
    raise SystemExit('The native broker must be built on macOS.')
repo = Path(__file__).resolve().parents[2]
source = repo / 'src/SHOU.Launcher.Desktop/Native/MacOS/credential_broker.c'
directory = repo / 'src/SHOU.Launcher.Desktop/Native/MacOS/frozen-arm64-v1'
directory.mkdir(exist_ok=True)
payload = directory / 'SHOU.CredentialBroker'
subprocess.run(['/usr/bin/xcrun', 'clang', '-arch', 'arm64', '-mmacosx-version-min=14.0',
                '-g0', '-O2', '-Wno-deprecated-declarations', str(source),
                '-framework', 'Security', '-framework', 'CoreFoundation', '-o', str(payload)], check=True)
payload.chmod(0o755)
identity = (dict(identity=os.environ['SHOU_MACOS_SIGNING_IDENTITY'],keychain=os.environ['SHOU_MACOS_SIGNING_KEYCHAIN'])
            if os.environ.get('SHOU_MACOS_SIGNING_IDENTITY') and os.environ.get('SHOU_MACOS_SIGNING_KEYCHAIN')
            else initialize(Path.home() / 'Library/Application Support/SHOU-Launcher-Signing'))
previous = [line.strip().strip('"') for line in run(['/usr/bin/security','list-keychains','-d','user']).splitlines()]
identifier = 'club.conduit.shou.launcher.credentials.v1'
try:
    run(['/usr/bin/security','list-keychains','-d','user','-s',identity['keychain'],*previous])
    run(['/usr/bin/codesign','--force','--sign',identity['identity'],'--keychain',identity['keychain'],
         '--timestamp=none','--options','runtime','--runtime-version','14.0.0','--digest-algorithm','sha256',
         '--identifier',identifier,'--requirements','=designated => identifier "'+identifier+'" and certificate leaf = H"'+identity['identity']+'"',payload])
finally:
    run(['/usr/bin/security','list-keychains','-d','user','-s',*previous])
run(['/usr/bin/codesign','--verify','--strict',payload])
inspection=subprocess.run(['/usr/bin/codesign','-d','--verbose=4',str(payload)],capture_output=True,text=True,check=True)
manifest = dict(protocol=1, identifier='club.conduit.shou.launcher.credentials.v1',
                signing_identity=identity['identity'],
                cdhash=re.search(r'CDHash=(\w+)',inspection.stderr)[1],
                source_sha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                payload_sha256=hashlib.sha256(payload.read_bytes()).hexdigest(),
                compiler=subprocess.check_output(['/usr/bin/xcrun','clang','--version'],text=True).splitlines()[0],
                sdk=subprocess.check_output(['/usr/bin/xcrun','--show-sdk-version'],text=True).strip(),
                minimum_macos='14.0', architecture='arm64')
(directory / 'manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps(manifest,indent=2))
