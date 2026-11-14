// Synthetic native integration client. Never permits real account namespaces.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/wait.h>
#ifndef TEST_SERVICE
#error A unique isolated test service is required
#endif
static int io(int fd, void *bytes, size_t length, int writing) {
    while (length) {
        ssize_t n = writing ? write(fd, bytes, length) : read(fd, bytes, length);
        if (n <= 0) return 0;
        bytes = (char *)bytes + n; length -= n;
    }
    return 1;
}
int main(int argc, char **argv) {
    if (argc != 3) return 2;
    int input[2], output[2];
    if (pipe(input) || pipe(output)) return 3;
    pid_t child = fork();
    if (!child) {
        dup2(input[0], 0); dup2(output[1], 1);
        close(input[0]); close(input[1]); close(output[0]); close(output[1]);
        execl(argv[1], argv[1], NULL); _exit(4);
    }
    close(input[0]); close(output[1]);
    uint32_t greeting[2] = {0};
    if (!io(output[0], greeting, 8, 0) || greeting[0] != 0x53484f55u) return 5;
    int32_t status = (int32_t)greeting[1];
    int matched = 1;
    if (!status) {
        uint32_t operation = (uint32_t)atoi(argv[2]);
        const char *service = operation == 5 ? "invalid-shou-owned-test-scope" : TEST_SERVICE;
        const char *key = "native-smoke:frozen-broker-test";
        const char value[] = "SHOU synthetic broker fixture";
        uint32_t request[4] = {operation == 5 ? 1 : operation,
            (uint32_t)strlen(service), (uint32_t)strlen(key), operation == 2 ? sizeof(value)-1 : 0};
        if (!io(input[1], request, sizeof(request), 1) || !io(input[1], (void *)service, request[1], 1) ||
            !io(input[1], (void *)key, request[2], 1) || !io(input[1], (void *)value, request[3], 1)) return 6;
        close(input[1]);
        uint32_t response[2] = {0}; char returned[256] = {0};
        if (!io(output[0], response, 8, 0) || response[1] > sizeof(returned) ||
            !io(output[0], returned, response[1], 0)) return 7;
        status = (int32_t)response[0];
        matched = response[1] == 0 || (response[1] == sizeof(value)-1 && !memcmp(returned,value,sizeof(value)-1));
        memset(returned, 0, sizeof(returned));
    } else close(input[1]);
    close(output[0]); waitpid(child, NULL, 0);
    printf("version=%d status=%d matched=%d\n", VERSION, status, matched);
    return matched ? 0 : 8;
}
