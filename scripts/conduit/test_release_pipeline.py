#!/usr/bin/env python3
"""Exercise real release scripts against disposable Git/ZIP fixtures, never user tags."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--git', default=shutil.which('git'))
    args = parser.parse_args()
    out = args.directory.resolve(); out.mkdir(parents=True, exist_ok=False)
    repo = Path(__file__).resolve().parents[2]
    pwsh = shutil.which('pwsh'); assert pwsh and args.git
    cases = []

    def check(command, label, expected_error=None, cwd=None):
        result = subprocess.run([str(x) for x in command], cwd=cwd, capture_output=True, text=True, timeout=90)
        if expected_error:
            assert result.returncode != 0 and expected_error.lower() in (result.stdout + result.stderr).lower(), (label, result.stdout, result.stderr)
        else:
            assert result.returncode == 0, (label, result.stdout, result.stderr)
        cases.append(dict(case=label, status='PASS'))
        return result

    wrapper = out / 'identity.ps1'
    wrapper.write_text("param([string]$Script,[string]$Tag,[string]$Config='Release',[string]$Suffix='',[switch]$Pre)\n"
                       "$ErrorActionPreference='Stop'\n& $Script -Configuration $Config -ReleaseTag $Tag -BuildSuffix $Suffix -Prerelease:$Pre | ConvertTo-Json\n")
    identity_cases = [
        ('short-prerelease', 'v1.6', True, True, None, 'Release', ''),
        ('short-tag-needs-prerelease', 'v1.6', False, True, 'matching version tag', 'Release', ''),
        ('stable-outside-main-denied', 'v1.6.0', False, True, 'based on origin/main', 'Release', ''),
        ('full-tag-explicit-prerelease', 'v1.6.0', True, True, None, 'Release', ''),
        ('suffixed-prerelease', 'v1.6.0-rc.1', True, True, None, 'Release', ''),
        ('wrong-version-denied', 'v9.9', True, True, 'matching version tag', 'Release', ''),
        ('stable-on-main', 'v1.6.0', False, False, None, 'Release', ''),
        ('beta-config-denied', 'v1.6', True, True, 'Release configuration', 'Beta', ''),
        ('tag-and-suffix-denied', 'v1.6', True, True, 'no build suffix', 'Release', 'dev.fixture'),
    ]
    for label, tag, pre, feature, error, configuration, suffix in identity_cases:
        fixture = out / label; (fixture / 'scripts/conduit').mkdir(parents=True)
        (fixture / 'Plain Craft Launcher 2').mkdir(); (fixture / 'PCL.Core/Conduit').mkdir(parents=True)
        shutil.copy2(repo / 'scripts/conduit/Get-BuildIdentity.ps1', fixture / 'scripts/conduit/Get-BuildIdentity.ps1')
        (fixture / 'Plain Craft Launcher 2/metadata.json').write_text(json.dumps(dict(version=dict(base='1.6.0'), codename='xb')))
        (fixture / 'PCL.Core/Conduit/ClubRelease.cs').write_text('CurrentVersion = "1.6.0"; CurrentCodename = "xb";')
        def git(*parts):
            result = subprocess.run([args.git, *parts], cwd=fixture, capture_output=True, text=True)
            assert result.returncode == 0, result.stderr
        git('init', '-q', '-b', 'main'); git('add', '.')
        git('-c', 'user.name=Release Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-qm', 'seed')
        git('update-ref', 'refs/remotes/origin/main', 'HEAD')
        if feature:
            (fixture / 'feature.txt').write_text('unmerged fixture')
            git('add', '.'); git('-c', 'user.name=Release Fixture', '-c', 'user.email=fixture@example.invalid', '-c', 'commit.gpgsign=false', 'commit', '-qm', 'feature')
        git('tag', tag)
        command = [pwsh, '-NoProfile', '-File', wrapper, '-Script', fixture / 'scripts/conduit/Get-BuildIdentity.ps1', '-Tag', tag, '-Config', configuration]
        if pre: command += ['-Pre']
        if suffix: command += ['-Suffix', suffix]
        result = check(command, label, error)
        if not error:
            identity = json.loads(result.stdout.lstrip('\ufeff'))
            assert identity['Version'] == '1.6.0' and identity['Codename'] == 'xb' and identity['Label'] == tag

    source = '1' * 40; native = repo / 'src/SHOU.Launcher.Desktop/Native/MacOS'
    manifest = json.loads((native / 'frozen-arm64-v1/manifest.json').read_text())
    baseline = out / 'package-baseline'; baseline.mkdir(); (baseline / 'dist').mkdir()
    def info(rid):
        return dict(entry='Avalonia Desktop', runtime=rid, version='1.6.0', codename='xb', label='v1.6', release_tag='v1.6',
                    source_sha=source, source_dirty=False, prerelease=True, self_contained=True, configuration='Release',
                    credential_verification=dict(MS_CLIENT_IDConfigured=True, CURSEFORGE_API_KEYEmbedded=True))
    def save_metadata(folder, metadata):
        (folder / 'BUILD-INFO.json').write_text(json.dumps(metadata, indent=2) + '\n')
    def archive(folder, rid, entries):
        file = folder / f'SHOU-Launcher-{rid}-v1.6.zip'
        with zipfile.ZipFile(file, 'w', compression=zipfile.ZIP_DEFLATED) as z:
            for name, value in entries.items(): z.writestr(name, value)
        (folder / (file.name + '.sha256')).write_text(hashlib.sha256(file.read_bytes()).hexdigest() + '  ' + file.name + '\n')
    for rid, machine in [('win-x64', 0x8664), ('win-arm64', 0xaa64)]:
        folder = baseline / 'dist' / rid; folder.mkdir()
        exe = bytearray(512); exe[:2] = b'MZ'; struct.pack_into('<I', exe, 60, 128); exe[128:132] = b'PE\0\0'; struct.pack_into('<H', exe, 132, machine)
        metadata = info(rid); metadata['payload'] = dict(name='SHOU.Launcher.Desktop.exe', bytes=len(exe), sha256=hashlib.sha256(exe).hexdigest())
        save_metadata(folder, metadata); archive(folder, rid, {'SHOU.Launcher.Desktop.exe': exe})
    folder = baseline / 'dist/osx-arm64'; folder.mkdir(); metadata = info('osx-arm64')
    metadata.update(signing_identity=manifest['signing_identity'], credential_broker=dict(frozen_payload_sha256=manifest['payload_sha256']))
    save_metadata(folder, metadata)
    archive(folder, 'osx-arm64', {'SHOU Launcher.app/Contents/Info.plist': b'fixture plist',
        'SHOU Launcher.app/Contents/MacOS/SHOU.Launcher.Core.dll': b'fixture managed payload',
        'SHOU Launcher.app/Contents/MacOS/BUILD-INFO.json': (folder / 'BUILD-INFO.json').read_bytes(),
        'SHOU Launcher.app/Contents/Helpers/SHOU.CredentialBroker': (native / 'frozen-arm64-v1/SHOU.CredentialBroker').read_bytes()})
    for name in ['SIGNING-INFO.json', 'CREDENTIAL-BROKER-INFO.json']:
        (folder / name).write_text(json.dumps(dict(identity=manifest['signing_identity'])))
    # Report fixtures must never become release assets.
    (baseline / 'reports').mkdir(); (baseline / 'reports/unrelated.zip').write_bytes(b'not a release package')
    shutil.copytree(baseline / 'dist/win-x64', baseline / 'reports/decoy')

    def metadata_change(base, key, value, rid='win-x64'):
        file = base / 'dist' / rid / 'BUILD-INFO.json'; j = json.loads(file.read_text()); j[key] = value; save_metadata(file.parent, j)
    def rewrite_zip(base, rid, change):
        folder = base / 'dist' / rid; file = next(folder.glob('*.zip'))
        with zipfile.ZipFile(file) as z: entries = {n: z.read(n) for n in z.namelist()}
        change(entries); archive(folder, rid, entries)
    def wrong_machine(base):
        def change(entries):
            exe = bytearray(entries['SHOU.Launcher.Desktop.exe']); struct.pack_into('<H', exe, 132, 0xaa64); entries['SHOU.Launcher.Desktop.exe'] = exe
        rewrite_zip(base, 'win-x64', change)
    def missing_config(base):
        metadata_change(base, 'credential_verification', dict(MS_CLIENT_IDConfigured=True, CURSEFORGE_API_KEYEmbedded=False))
    def nested_artifacts(base):
        artifact = base / 'Desktop-Release-fixture'; artifact.mkdir(); shutil.move(base / 'dist', artifact / 'dist')
    package_cases = [
        ('complete-packages', lambda b: None, None),
        ('nested-action-artifacts', nested_artifacts, None),
        ('missing-arm64', lambda b: shutil.rmtree(b / 'dist/win-arm64'), 'All three Desktop packages'),
        ('mixed-source', lambda b: metadata_change(b, 'source_sha', '2' * 40), 'identity/source/release mode mismatch'),
        ('wrong-codename', lambda b: metadata_change(b, 'codename', 'old'), 'identity/source/release mode mismatch'),
        ('missing-client-config', missing_config, 'client configuration'),
        ('wrong-checksum', lambda b: next((b / 'dist/win-x64').glob('*.sha256')).write_text('bad'), 'checksum sidecar'),
        ('loose-dll-denied', lambda b: rewrite_zip(b, 'win-x64', lambda e: e.update({'loose.dll': b'extra'})), 'one EXE'),
        ('wrong-pe-machine', wrong_machine, 'architecture'),
        ('changed-broker', lambda b: rewrite_zip(b, 'osx-arm64', lambda e: e.update({'SHOU Launcher.app/Contents/Helpers/SHOU.CredentialBroker': b'changed'})), 'frozen credential'),
        ('mixed-mac-metadata', lambda b: rewrite_zip(b, 'osx-arm64', lambda e: e.update({'SHOU Launcher.app/Contents/MacOS/BUILD-INFO.json': b'{}'})), 'embedded and external'),
        ('duplicate-runtime', lambda b: shutil.copytree(b / 'dist/win-x64', b / 'dist/duplicate-win'), 'Duplicate package'),
    ]
    for label, mutate, error in package_cases:
        fixture = out / label; shutil.copytree(baseline, fixture); mutate(fixture)
        destination = out / (label + '-assets')
        check([pwsh, '-NoProfile', '-File', repo / 'scripts/conduit/Collect-ReleaseAssets.ps1', '-InputRoot', fixture,
               '-OutputRoot', destination, '-Tag', 'v1.6', '-SourceSha', source, '-Version', '1.6.0', '-Codename', 'xb', '-Prerelease'], label, error)
        if error:
            assert not destination.exists(), 'Rejected packages left a partial release directory.'
        else:
            evidence = destination / 'evidence'
            result = json.loads((evidence / 'RELEASE-MANIFEST.json').read_text(encoding='utf-8-sig'))
            assert len(result['packages']) == 3 and result['source_sha'] == source and result['prerelease'] is True
            public = {p.name for p in destination.iterdir() if p.is_file()}
            assert public == {f'SHOU-Launcher-{rid}-v1.6.zip' for rid in ['osx-arm64', 'win-x64', 'win-arm64']}, public
            assert len(list(evidence.iterdir())) == 10, 'Internal verification records must remain available.'
            for package in result['packages']:
                name = package['file']
                digest = hashlib.sha256((destination / name).read_bytes()).hexdigest()
                assert digest == package['sha256']
                assert (evidence / (name + '.sha256')).read_text().strip() == digest + '  ' + name
    # Exercise the actual YAML publisher script with a local gh stub. Even extra
    # files in the upload root must never become public release attachments.
    publisher = out / 'publisher'; publisher.mkdir()
    shutil.copytree(out / 'complete-packages-assets', publisher / 'release-assets')
    (publisher / 'release-assets/debug.zip').write_bytes(b'not a compiled package')
    (publisher / 'release-assets/internal.json').write_text('{}')
    notes = publisher / 'docs/conduit/RELEASE-v1.6.0.md'; notes.parent.mkdir(parents=True)
    shutil.copy2(repo / 'docs/conduit/RELEASE-v1.6.0.md', notes)
    workflow = (repo / '.github/workflows/conduit-release.yml').read_text()
    step = workflow.split('      - name: Create reviewable Desktop release draft\n', 1)[1]
    body = step.split('        run: |\n', 1)[1]
    script = '\n'.join(line[10:] for line in body.splitlines() if line.startswith('          '))
    assert script and 'gh release upload' in script
    harness = publisher / 'publish.ps1'
    harness.write_text("$ErrorActionPreference='Stop'\n$global:calls=[Collections.Generic.List[object]]::new()\n"
                       "function gh { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) "
                       "$global:calls.Add(@{arguments=@($Arguments)});$global:LASTEXITCODE=0 }\n"
                       "$env:RELEASE_TAG='v1.6';$env:RELEASE_VERSION='1.6.0';$env:RELEASE_CODENAME='xb';$env:RELEASE_PRERELEASE='true'\n"
                       + script + "\n$global:calls | ConvertTo-Json -Depth 5\n")
    published = json.loads(check([pwsh, '-NoProfile', '-File', harness], 'publisher-compiled-packages-only', cwd=publisher).stdout.lstrip('\ufeff'))
    assert len(published) == 2
    create, upload = (call['arguments'] for call in published)
    assert create[:3] == ['release', 'create', 'v1.6'] and '--prerelease' in create and '--draft' in create and '--latest=false' in create
    assert upload[:3] == ['release', 'upload', 'v1.6'] and len(upload) == 6
    assert {Path(name).name for name in upload[3:]} == {f'SHOU-Launcher-{rid}-v1.6.zip' for rid in ['osx-arm64', 'win-x64', 'win-arm64']}

    result = dict(status='PASS', cases=cases, fixture_only=True, user_refs_modified=False, releases_created=False)
    (out / 'result.json').write_text(json.dumps(result, indent=2) + '\n'); print(json.dumps(result, indent=2))


if __name__ == '__main__': main()
