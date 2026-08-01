#include <ghostty/vt.h>

#if GHOSTTY_GHOSTSHELL_EXTENSION_ABI != 1u
#error "Unexpected GhostSHELL libghostty-vt extension ABI"
#endif

int main(void) {
  return ghostty_ghostshell_extension_abi() ==
                 GHOSTTY_GHOSTSHELL_EXTENSION_ABI
             ? 0
             : 1;
}
