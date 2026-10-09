// Para pseudo-terminala dla testów portu szeregowego (posix_openpt jest ukryte w nagłówkach glibc
// bez _XOPEN_SOURCE, więc Swift go nie widzi).
#define _XOPEN_SOURCE 600
#include <fcntl.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int wp_open_pty(char *slave_name, int len) {
    int m = posix_openpt(O_RDWR | O_NOCTTY);
    if (m < 0) return -1;
    if (grantpt(m) != 0 || unlockpt(m) != 0) { close(m); return -1; }
    const char *n = ptsname(m);
    if (!n || (int)strlen(n) >= len) { close(m); return -1; }
    strcpy(slave_name, n);
    return m;
}
