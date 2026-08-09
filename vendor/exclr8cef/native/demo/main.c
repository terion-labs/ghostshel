// Exclr8CEF Stage 2 demo.
// Opens chrome://version in a CEF-managed top-level window so we can
// visually confirm the entire stack — download, build, link, helper
// subprocesses, message loop, browser creation — is working end-to-end.

#include <stdio.h>
#include "exclr8cef.h"

int main(int argc, char** argv) {
#if !defined(__APPLE__)
    // On Windows/Linux the same binary is reused as the subprocess; CEF
    // re-invokes it with --type=*. On macOS the subprocess is a separate
    // Helper.app binary, so the main exe never calls excef_execute_process.
    int subproc = excef_execute_process(argc, argv);
    if (subproc >= 0) return subproc;
#endif

    if (excef_initialize(argc, argv, NULL) != 0) {
        fprintf(stderr, "excef_initialize failed\n");
        return 1;
    }

    excef_versions v;
    excef_get_versions(&v);
    printf("Exclr8CEF %s — running CEF %s (Chromium %s)\n",
           v.shim_version, v.cef_version, v.chromium_version);

    if (excef_create_browser("chrome://version") != 0) {
        fprintf(stderr, "excef_create_browser failed\n");
        excef_shutdown();
        return 1;
    }

    excef_run_message_loop();
    excef_shutdown();
    return 0;
}
