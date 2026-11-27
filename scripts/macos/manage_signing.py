#!/usr/bin/env python3
"""Create/reuse a local signing identity, or import that identity on a CI runner.

Private material stays outside the checkout. This does not change system trust,
the default keychain, or credential item access controls.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import secrets
import shutil
import subprocess
import tempfile


def run(args, env=None):
    result = subprocess.run([str(a) for a in args], capture_output=True, env=env, timeout=120)
    if result.returncode:
        # security/openssl arguments can contain passwords. Never print argv or
        # propagate CalledProcessError, even when import fails.
        raise RuntimeError(f"{Path(args[0]).name} {args[1]} failed (exit {result.returncode})")
    return result.stdout.decode().strip()


def private_write(path, value):
    with path.open('x', encoding='utf-8') as out:
        os.chmod(path, 0o600)
        out.write(value)


def openssl():
    return shutil.which('openssl') or '/usr/bin/openssl'


def certificate_identity(cert):
    der = subprocess.run([openssl(), 'x509', '-in', str(cert), '-outform', 'DER'],
                         capture_output=True, check=True).stdout
    return hashlib.sha1(der).hexdigest().upper(), hashlib.sha256(der).hexdigest()


def import_identity(directory, password):
    keychain = directory / 'signing.keychain-db'
    previous = run(['/usr/bin/security', 'list-keychains', '-d', 'user'])
    search_list = [line.strip().strip('"') for line in previous.splitlines()]
    try:
        run(['/usr/bin/security', 'create-keychain', '-p', password, keychain])
        run(['/usr/bin/security', 'set-keychain-settings', '-lut', '21600', keychain])
        run(['/usr/bin/security', 'unlock-keychain', '-p', password, keychain])
        run(['/usr/bin/security', 'import', directory / 'identity.p12', '-k', keychain,
             '-P', password, '-T', '/usr/bin/codesign'])
        run(['/usr/bin/security', 'set-key-partition-list', '-S', 'apple-tool:,apple:',
             '-s', '-k', password, keychain])
        os.chmod(keychain, 0o600)
    finally:
        run(['/usr/bin/security', 'list-keychains', '-d', 'user', '-s', *search_list])
    return keychain


def configuration(directory, keychain):
    sha1, sha256 = certificate_identity(directory / 'certificate.pem')
    config = dict(identity=sha1, certificate_sha256=sha256,
                  keychain=str(keychain), notarized=False)
    private_write(directory / 'signing.json', json.dumps(config, indent=2) + '\n')
    return config


def initialize(directory):
    config_path = directory / 'signing.json'
    if config_path.exists():
        config = json.loads(config_path.read_text())
        actual, _ = certificate_identity(directory / 'certificate.pem')
        if actual != config['identity'] or not Path(config['keychain']).is_file():
            raise RuntimeError('Existing signing identity is incomplete; restore its backup rather than generate a replacement.')
        run(['/usr/bin/security', 'unlock-keychain', '-p',
             (directory / 'password.txt').read_text().strip(), config['keychain']])
        return config
    if directory.exists() and any(directory.iterdir()):
        raise RuntimeError('Signing directory is not empty. Refusing to overwrite an existing or partial identity.')
    directory.mkdir(parents=True, mode=0o700, exist_ok=True)
    os.chmod(directory, 0o700)
    password = secrets.token_urlsafe(48)
    private_write(directory / 'password.txt', password + '\n')
    # An encrypted export is retained for backup/CI. The unencrypted intermediate
    # key exists only in this private temporary directory and is removed on exit.
    with tempfile.TemporaryDirectory(prefix='create-', dir=directory) as tmp:
        key = Path(tmp) / 'private.pem'
        cert = directory / 'certificate.pem'
        child_env = dict(os.environ, SHOU_P12_PASSWORD=password)
        run([openssl(), 'req', '-x509', '-newkey', 'rsa:3072', '-sha256', '-nodes',
             '-days', '3650', '-subj', '/CN=SHOU Launcher Local Signing/O=Conduit Club',
             '-addext', 'basicConstraints=critical,CA:FALSE',
             '-addext', 'keyUsage=critical,digitalSignature',
             '-addext', 'extendedKeyUsage=critical,codeSigning',
             '-keyout', key, '-out', cert])
        os.chmod(key, 0o600)
        run([openssl(), 'pkcs12', '-export', '-inkey', key, '-in', cert,
             '-name', 'SHOU Launcher Local Signing', '-keypbe', 'PBE-SHA1-3DES',
             '-certpbe', 'PBE-SHA1-3DES', '-macalg', 'sha1',
             '-passout', 'env:SHOU_P12_PASSWORD', '-out', directory / 'identity.p12'], env=child_env)
    os.chmod(directory / 'identity.p12', 0o600)
    return configuration(directory, import_identity(directory, password))


def install_ci(directory):
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        raise RuntimeError('CI import requires an ephemeral GitHub Actions runner.')
    if directory.exists():
        raise RuntimeError('CI signing directory must be new.')
    encoded = os.environ.get('MACOS_SIGNING_CERTIFICATE_BASE64', '')
    password = os.environ.get('MACOS_SIGNING_PASSWORD', '')
    if not encoded or not password:
        raise RuntimeError('Configure MACOS_SIGNING_CERTIFICATE_BASE64 and MACOS_SIGNING_PASSWORD before building a signed Mac candidate.')
    directory.mkdir(parents=True, mode=0o700)
    os.chmod(directory, 0o700)
    p12 = directory / 'identity.p12'
    p12.write_bytes(base64.b64decode(encoded, validate=True))
    os.chmod(p12, 0o600)
    child_env = dict(os.environ, SHOU_P12_PASSWORD=password)
    run([openssl(), 'pkcs12', '-in', p12, '-clcerts', '-nokeys',
         '-passin', 'env:SHOU_P12_PASSWORD', '-out', directory / 'certificate.pem'], env=child_env)
    config = configuration(directory, import_identity(directory, password))
    with open(os.environ['GITHUB_ENV'], 'a', encoding='utf-8') as out:
        out.write(f"SHOU_MACOS_SIGNING_IDENTITY={config['identity']}\n")
        out.write(f"SHOU_MACOS_SIGNING_KEYCHAIN={config['keychain']}\n")
    return config


def cleanup_ci(directory):
    if os.environ.get('GITHUB_ACTIONS') != 'true':
        raise RuntimeError('Cleanup is for an ephemeral GitHub Actions runner only.')
    expected = Path(os.environ['RUNNER_TEMP']).resolve() / 'shou-macos-signing'
    if directory != expected:
        raise RuntimeError('Refusing cleanup outside the registered CI signing directory.')
    keychain = directory / 'signing.keychain-db'
    if keychain.exists():
        run(['/usr/bin/security', 'delete-keychain', keychain])
    if directory.exists():
        shutil.rmtree(directory)


def main():
    if platform.system() != 'Darwin':
        raise RuntimeError('Mac signing requires macOS.')
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['init', 'install-ci', 'cleanup-ci'])
    parser.add_argument('--directory', type=Path)
    args = parser.parse_args()
    default = Path.home() / 'Library/Application Support/SHOU-Launcher-Signing'
    directory = (args.directory or default).expanduser().resolve()
    if args.action == 'cleanup-ci':
        cleanup_ci(directory)
        print('Removed ephemeral CI signing keychain and private files.')
    else:
        config = initialize(directory) if args.action == 'init' else install_ci(directory)
        print(json.dumps(dict(identity=config['identity'], certificate_sha256=config['certificate_sha256'],
                              config=str(directory / 'signing.json'), notarized=False), indent=2))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Neither traceback nor subprocess arguments may expose private material.
        raise SystemExit(f'Signing setup failed: {error if isinstance(error, RuntimeError) else type(error).__name__}')
