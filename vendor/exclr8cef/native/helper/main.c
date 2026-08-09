// Subprocess helper executable.
// CEF spawns this for renderer/GPU/utility processes. The shim's
// excef_execute_process handles the per-platform setup (CefScopedLibraryLoader
// on macOS, plain CefExecuteProcess elsewhere) and returns the exit code.

#include "exclr8cef.h"

int main(int argc, char** argv) {
    return excef_execute_process(argc, argv);
}
