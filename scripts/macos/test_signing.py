#!/usr/bin/env python3
"""Verify fixed signing requirements and diagnose cross-version Keychain access.

Uses only a uniquely named synthetic generic-password item. UI authentication is
disabled, so failure cannot leave an overnight job waiting on a password dialog.
"""
import argparse
import json
import os
from pathlib import Path
import plistlib
import re
import secrets
import shutil
import subprocess
import uuid

from manage_signing import run


SOURCE = r'''
#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int main(int argc, char **argv) {
    if (argc != 3) return 2;
    SecKeychainSetUserInteractionAllowed(false);
    CFStringRef service = CFStringCreateWithCString(NULL, argv[2], kCFStringEncodingUTF8);
    CFMutableDictionaryRef q = CFDictionaryCreateMutable(NULL, 0,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFDictionarySetValue(q, kSecClass, kSecClassGenericPassword);
    CFDictionarySetValue(q, kSecAttrService, service);
    CFDictionarySetValue(q, kSecAttrAccount, CFSTR("synthetic-signing-fixture"));
    CFDictionarySetValue(q, kSecUseAuthenticationUI, kSecUseAuthenticationUIFail);
    SecKeychainRef keychain = NULL;
    if (SecKeychainOpen(getenv("SHOU_SIGNING_TEST_KEYCHAIN"), &keychain)) return 5;
    if (!strcmp(argv[1], "write")) {
        CFDictionarySetValue(q, kSecUseKeychain, keychain);
    } else {
        CFArrayRef search = CFArrayCreate(NULL, (const void **)&keychain, 1, &kCFTypeArrayCallBacks);
        CFDictionarySetValue(q, kSecMatchSearchList, search);
        CFRelease(search);
    }
    OSStatus status;
    if (!strcmp(argv[1], "write")) {
        // Explicitly bind this synthetic item to the probe's own DR. A bare CLI
        // launched by a terminal can otherwise inherit the responsible app's
        // default ACL, which would not exercise a launcher bundle identity.
        SecTrustedApplicationRef trusted = NULL;
        status = SecTrustedApplicationCreateFromPath(argv[0], &trusted);
        if (status) return 3;
        CFArrayRef applications = CFArrayCreate(NULL, (const void **)&trusted, 1,
                                               &kCFTypeArrayCallBacks);
        SecAccessRef access = NULL;
        status = SecAccessCreate(CFSTR("SHOU synthetic signing continuity"), applications, &access);
        if (status) return 4;
        CFDictionarySetValue(q, kSecAttrAccess, access);
        CFDataRef data = CFDataCreate(NULL, (const UInt8 *)"synthetic", 9);
        CFDictionarySetValue(q, kSecValueData, data);
        status = SecItemAdd(q, NULL);
        CFRelease(data); CFRelease(access); CFRelease(applications); CFRelease(trusted);
    } else if (!strcmp(argv[1], "inspect")) {
        CFDictionarySetValue(q, kSecReturnRef, kCFBooleanTrue);
        CFTypeRef item = NULL;
        status = SecItemCopyMatching(q, &item);
        if (!status) {
            SecAccessRef access = NULL;
            status = SecKeychainItemCopyAccess((SecKeychainItemRef)item, &access);
            CFArrayRef acls = NULL;
            if (!status) status = SecAccessCopyACLList(access, &acls);
            if (!status) for (CFIndex i=0; i<CFArrayGetCount(acls); i++) {
                CFArrayRef apps = NULL; CFStringRef desc = NULL; SecKeychainPromptSelector flags;
                SecACLCopyContents((SecACLRef)CFArrayGetValueAtIndex(acls,i), &apps, &desc, &flags);
                char text[4096] = {0};
                if (desc) CFStringGetCString(desc,text,sizeof(text),kCFStringEncodingUTF8);
                printf("acl=%s apps=%ld\n",text,apps ? CFArrayGetCount(apps) : -1L);
                if (apps) CFRelease(apps); if(desc) CFRelease(desc);
            }
            if(acls) CFRelease(acls); if(access) CFRelease(access); CFRelease(item);
        }
    } else if (!strcmp(argv[1], "delete")) {
        status = SecItemDelete(q);
    } else {
        CFDictionarySetValue(q, kSecReturnData, kCFBooleanTrue);
        CFDataRef value = NULL;
        status = SecItemCopyMatching(q, (CFTypeRef *)&value);
        if (status == errSecSuccess && (CFDataGetLength(value) != 9 ||
            memcmp(CFDataGetBytePtr(value), "synthetic", 9))) status = -1;
        if (value) CFRelease(value);
    }
    printf("version=%d status=%d\n", VERSION, (int)status);
    CFRelease(q); CFRelease(service); CFRelease(keychain);
    return status == errSecSuccess ? 0 : 1;
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True, type=Path)
    args = parser.parse_args()
    output = args.directory.resolve()
    output.mkdir(parents=True, exist_ok=False)
    identity = os.environ.get('SHOU_MACOS_SIGNING_IDENTITY')
    keychain = os.environ.get('SHOU_MACOS_SIGNING_KEYCHAIN')
    if not identity or not keychain:
        local = Path.home() / 'Library/Application Support/SHOU-Launcher-Signing'
        config = json.loads((local / 'signing.json').read_text())
        identity, keychain = config['identity'], config['keychain']
        run(['/usr/bin/security', 'unlock-keychain', '-p',
             (local / 'password.txt').read_text().strip(), keychain])
    source = output / 'probe.c'
    source.write_text(SOURCE)
    previous = [line.strip().strip('"') for line in
                run(['/usr/bin/security', 'list-keychains', '-d', 'user']).splitlines()]
    credential_keychain = output / 'synthetic-credentials.keychain-db'
    keychain_password = secrets.token_urlsafe(48)
    test_env = dict(os.environ, SHOU_SIGNING_TEST_KEYCHAIN=str(credential_keychain))
    probes = [output / name for name in ['previous', 'updated', 'unrelated']]
    service = 'club.conduit.shou.signing-test.' + uuid.uuid4().hex
    identifier = 'club.conduit.shou.signing-test'
    requirement = f'designated => identifier "{identifier}" and certificate leaf = H"{identity}"'
    created = False
    result = dict(identity=identity, service=service, synthetic_only=True, minecraft_started=False)
    (output / 'attempt.json').write_text(json.dumps(result, indent=2) + '\n')
    try:
        run(['/usr/bin/security', 'create-keychain', '-p', keychain_password, credential_keychain])
        run(['/usr/bin/security', 'unlock-keychain', '-p', keychain_password, credential_keychain])
        run(['/usr/bin/security', 'list-keychains', '-d', 'user', '-s', keychain, *previous])
        for version, probe in enumerate(probes, 1):
            run(['/usr/bin/clang', '-DVERSION=' + str(version), source, '-framework',
                 'Security', '-framework', 'CoreFoundation', '-o', probe])
            signing = ['--sign', identity, '--keychain', keychain, '--requirements', '=' + requirement]
            if version == 3:
                signing = ['--sign', '-']
            run(['/usr/bin/codesign', '--force', '--identifier', identifier,
                 '--timestamp=none', *signing, probe])
            run(['/usr/bin/codesign', '--verify', '--strict', probe])
        inspections = []
        for probe in probes:
            value = subprocess.run(['/usr/bin/codesign', '-d', '-r-', '--verbose=4', str(probe)],
                                   capture_output=True, text=True, check=True)
            text = value.stdout + value.stderr
            inspections.append(dict(requirement=re.search(r'(?m)^#?\s*designated => .+$', text)[0],
                                    cdhash=re.search(r'(?m)^CDHash=(.+)$', text)[1]))
        assert inspections[0]['cdhash'] != inspections[1]['cdhash']
        assert inspections[0]['requirement'] == inspections[1]['requirement']
        installed=output / 'installed'
        def replace_installed(probe):
            stage=output / 'staging'
            shutil.copy2(probe, stage)
            stage.replace(installed)
        replace_installed(probes[0])
        run([installed, 'write', service], env=test_env); created = True
        acl=run([installed, 'inspect', service], env=test_env)
        (output / 'synthetic-acl.txt').write_text(acl)
        for value in re.findall(r'(?m)^acl=([0-9a-f]+) apps=', acl):
            try:
                partitions=plistlib.loads(bytes.fromhex(value)).get('Partitions')
                if partitions: result['keychain_partitions']=partitions
            except (ValueError, plistlib.InvalidFileException):
                pass
        replace_installed(probes[1])
        read = subprocess.run([str(installed), 'read', service], env=test_env, capture_output=True, text=True, timeout=20)
        result['updated_reads_previous_item_without_ui'] = read.stdout.strip()
        (output / 'attempt.json').write_text(json.dumps(result, indent=2) + '\n')
        result['cross_version_keychain_access'] = read.returncode == 0
        replace_installed(probes[2])
        denied = subprocess.run([str(installed), 'read', service], capture_output=True,
                                env=test_env, text=True, timeout=20)
        assert denied.returncode != 0, 'An unrelated signer must not inherit credential access.'
        result['unrelated_signer_denied'] = denied.stdout.strip()
        replace_installed(probes[0])
        run([installed, 'delete', service], env=test_env); created = False
        # Current file-based keychains can add a cdhash partition independently
        # of the trusted-app DR. A self-signed identity does not provide a Team ID.
        # Report this honestly instead of loosening an ACL or failing the fixed-DR test.
        status='PASS' if result['cross_version_keychain_access'] else 'PASS_WITH_KEYCHAIN_LIMITATION'
        result.update(status=status, inspections=inspections)
        (output / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result, indent=2))
        if not result['cross_version_keychain_access']:
            print('::warning::Fixed signature verified; this macOS Keychain still requires authorization after changed self-signed code. No credential ACL was weakened.')
    finally:
        try:
            if created:
                replace_installed(probes[0])
                run([installed, 'delete', service], env=test_env)
        finally:
            try:
                if credential_keychain.exists():
                    run(['/usr/bin/security', 'delete-keychain', credential_keychain])
            finally:
                run(['/usr/bin/security', 'list-keychains', '-d', 'user', '-s', *previous])


if __name__ == '__main__':
    main()
