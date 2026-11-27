// Conduit Club. Stable, single-request Keychain client; protocol version 1.
// Keep this executable unchanged across ordinary launcher updates. A changed
// broker version can require a new macOS authorization, unlike a UI update.
#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <CommonCrypto/CommonDigest.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <signal.h>

#define MAGIC 0x53484f55u
#define MAX_VALUE 1048576u
#define APP_ID "club.conduit.shou.launcher"
#define BROKER_ID APP_ID ".credentials.v1"

static int transfer(int fd, void *bytes, size_t count, int writing) {
    unsigned char *p = bytes;
    while (count) {
        ssize_t n = writing ? write(fd, p, count) : read(fd, p, count);
        if (n <= 0) return 0;
        p += n; count -= (size_t)n;
    }
    return 1;
}

static void release(CFTypeRef value) { if (value) CFRelease(value); }
static void erase_free(void *value, size_t length) {
    if (value) {
        volatile unsigned char *p = value;
        while (length--) *p++ = 0;
        free(value);
    }
}

static int service_allowed(const char *service) {
    static const char base[] = "com.conduitclub.shou-launcher";
    static const char isolated[] = "com.conduitclub.shou-launcher.isolated.";
    if (!strcmp(service, base)) return 1;
    size_t prefix = sizeof(isolated) - 1;
    if (strlen(service) != prefix + 64 || strncmp(service, isolated, prefix)) return 0;
    for (size_t i = prefix; i < prefix + 64; i++)
        if (!((service[i] >= '0' && service[i] <= '9') || (service[i] >= 'a' && service[i] <= 'f'))) return 0;
    return 1;
}

// Require the same signing certificate as this broker and the exact launcher ID.
// A process simply knowing a PID, path, service name or protocol cannot gain access.
static OSStatus authorized_parent(pid_t pid, SecCodeRef *result) {
    OSStatus status = errSecAuthFailed;
    SecCodeRef self = NULL, parent = NULL;
    SecStaticCodeRef static_parent = NULL;
    CFDictionaryRef info = NULL, attributes = NULL;
    CFDataRef certificate = NULL;
    CFNumberRef number = NULL;
    CFStringRef expression = NULL;
    SecRequirementRef requirement = NULL;
    if (pid <= 1 || getuid() != geteuid() || getgid() != getegid()) goto done;
    if (SecCodeCopySelf(kSecCSDefaultFlags, &self) ||
        SecCodeCopySigningInformation(self, kSecCSSigningInformation, &info)) goto done;
    CFStringRef identifier = CFDictionaryGetValue(info, kSecCodeInfoIdentifier);
    CFArrayRef certificates = CFDictionaryGetValue(info, kSecCodeInfoCertificates);
    CFNumberRef flags = CFDictionaryGetValue(info, kSecCodeInfoFlags);
    uint32_t flag_value = 0;
    if (flags) CFNumberGetValue(flags, kCFNumberSInt32Type, &flag_value);
    if (!identifier || !CFEqual(identifier, CFSTR(BROKER_ID)) || !(flag_value & 0x10000u) ||
        !certificates || CFArrayGetCount(certificates) < 1) goto done;
    certificate = SecCertificateCopyData((SecCertificateRef)CFArrayGetValueAtIndex(certificates, 0));
    if (!certificate) goto done;
    unsigned char digest[CC_SHA1_DIGEST_LENGTH]; char hex[41];
    CC_SHA1(CFDataGetBytePtr(certificate), (CC_LONG)CFDataGetLength(certificate), digest);
    for (size_t i = 0; i < sizeof(digest); i++) snprintf(hex + 2*i, 3, "%02x", digest[i]);
    expression = CFStringCreateWithFormat(NULL, NULL,
        CFSTR("identifier \"%s\" and certificate leaf = H\"%s\""), APP_ID, hex);
    if (!expression || SecRequirementCreateWithString(expression, kSecCSDefaultFlags, &requirement)) goto done;
    number = CFNumberCreate(NULL, kCFNumberIntType, &pid);
    const void *keys[] = { kSecGuestAttributePid }, *values[] = { number };
    attributes = CFDictionaryCreate(NULL, keys, values, 1,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    if (!number || !attributes || SecCodeCopyGuestWithAttributes(NULL, attributes, kSecCSDefaultFlags, &parent) ||
        SecCodeCheckValidity(parent, kSecCSDefaultFlags, requirement) ||
        SecCodeCopyStaticCode(parent, kSecCSDefaultFlags, &static_parent)) goto done;
    // Verify the whole parent bundle, including its managed assemblies/resources.
    // Checking only its apphost would accept a modified .NET payload on disk.
    if (SecStaticCodeCheckValidity(static_parent,
            kSecCSStrictValidate | kSecCSCheckAllArchitectures | kSecCSCheckNestedCode, requirement)) goto done;
    if (getppid() != pid) goto done;
    *result = parent; parent = NULL; status = errSecSuccess;
done:
    release(requirement); release(expression); release(number); release(attributes);
    release(certificate); release(info); release(static_parent); release(parent); release(self);
    return status;
}

static OSStatus operate(uint32_t operation, const char *service, const char *key,
                        unsigned char *value, uint32_t length, CFDataRef *result) {
    OSStatus status = errSecAllocate;
    CFMutableDictionaryRef query = CFDictionaryCreateMutable(NULL, 0,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFStringRef service_string = CFStringCreateWithCString(NULL, service, kCFStringEncodingUTF8);
    CFStringRef key_string = CFStringCreateWithCString(NULL, key, kCFStringEncodingUTF8);
    CFDataRef data = NULL;
    CFMutableDictionaryRef changes = NULL;
    if (!query || !service_string || !key_string) goto done;
    CFDictionarySetValue(query, kSecClass, kSecClassGenericPassword);
    CFDictionarySetValue(query, kSecAttrService, service_string);
    CFDictionarySetValue(query, kSecAttrAccount, key_string);
    CFDictionarySetValue(query, kSecUseAuthenticationUI,
        operation == 4 ? kSecUseAuthenticationUIAllow : kSecUseAuthenticationUIFail);
    if (SecKeychainSetUserInteractionAllowed(operation == 4)) { status = errSecInteractionNotAllowed; goto done; }
    if (operation == 1 || operation == 4) {
        CFDictionarySetValue(query, kSecReturnData, kCFBooleanTrue);
        CFDictionarySetValue(query, kSecMatchLimit, kSecMatchLimitOne);
        status = SecItemCopyMatching(query, (CFTypeRef *)result);
        if (!status && (!*result || CFGetTypeID(*result) != CFDataGetTypeID() || CFDataGetLength(*result) > MAX_VALUE)) {
            release(*result); *result = NULL; status = errSecDecode;
        }
    } else if (operation == 2) {
        data = CFDataCreate(NULL, value, length);
        changes = CFDictionaryCreateMutable(NULL, 0,
            &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        if (!data || !changes) goto done;
        CFDictionarySetValue(changes, kSecValueData, data);
        status = SecItemUpdate(query, changes);
        if (status == errSecItemNotFound) {
            CFDictionarySetValue(query, kSecValueData, data);
            status = SecItemAdd(query, NULL);
            if (status == errSecDuplicateItem) {
                CFDictionaryRemoveValue(query, kSecValueData);
                status = SecItemUpdate(query, changes);
            }
        }
    } else if (operation == 3) {
        status = SecItemDelete(query);
        if (status == errSecItemNotFound) status = errSecSuccess;
    } else status = errSecParam;
done:
    release(changes); release(data); release(query); release(service_string); release(key_string);
    return status;
}

int main(int argc, char **argv) {
    (void)argv;
    if (argc != 1) return 2;
    signal(SIGPIPE, SIG_IGN);
    alarm(30); // Also bounds an abandoned pipe if the launcher crashes.
    SecKeychainSetUserInteractionAllowed(false);
    pid_t parent_pid = getppid(); SecCodeRef parent = NULL;
    int32_t status = authorized_parent(parent_pid, &parent);
    uint32_t greeting[2] = { MAGIC, (uint32_t)status };
    if (!transfer(STDOUT_FILENO, greeting, sizeof(greeting), 1) || status) { release(parent); return 1; }
    uint32_t header[4] = {0};
    if (!transfer(STDIN_FILENO, header, sizeof(header), 0) || header[0] < 1 || header[0] > 4 ||
        !header[1] || header[1] > 4096 || !header[2] || header[2] > 4096 || header[3] > MAX_VALUE ||
        (header[0] != 2 && header[3])) { release(parent); return 2; }
    alarm(header[0] == 4 ? 300 : 30);
    char *service = calloc(1, header[1]+1), *key = calloc(1, header[2]+1);
    unsigned char *value = calloc(1, header[3] ? header[3] : 1);
    CFDataRef result = NULL;
    status = errSecParam;
    if (!service || !key || !value) { status = errSecAllocate; goto done; }
    if (!transfer(STDIN_FILENO, service, header[1], 0) || !transfer(STDIN_FILENO, key, header[2], 0) ||
        !transfer(STDIN_FILENO, value, header[3], 0)) goto done;
    if (memchr(service, 0, header[1]) || memchr(key, 0, header[2]) || !service_allowed(service)) goto done;
    // Re-resolve the live caller after the request, detecting reparenting, exec
    // or a changed bundle before accessing any Keychain item.
    SecCodeRef refreshed_parent = NULL;
    status = authorized_parent(parent_pid, &refreshed_parent);
    release(refreshed_parent);
    if (status) goto done;
    status = operate(header[0], service, key, value, header[3], &result);
done:
    erase_free(value, header[3]); erase_free(key, header[2]); erase_free(service, header[1]);
    uint32_t response[2] = { (uint32_t)status, status == 0 && result ? (uint32_t)CFDataGetLength(result) : 0 };
    int sent = transfer(STDOUT_FILENO, response, sizeof(response), 1);
    if (sent && response[1]) sent = transfer(STDOUT_FILENO, (void *)CFDataGetBytePtr(result), response[1], 1);
    release(result); release(parent);
    return sent ? 0 : 3;
}
