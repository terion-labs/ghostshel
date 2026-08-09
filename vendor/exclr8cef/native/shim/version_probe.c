// Stage 1 smoke test. Links the shim only; calls excef_get_versions and
// prints. If this works on a host, every layer below the shim is wired up.

#include <stdio.h>
#include "exclr8cef.h"

int main(void) {
    excef_versions v;
    excef_get_versions(&v);
    printf("Exclr8CEF shim version : %s\n", v.shim_version);
    printf("CEF version            : %s\n", v.cef_version);
    printf("Chromium version       : %s\n", v.chromium_version);
    return 0;
}
