#ifndef GHOSTSHELL_GHOSTTY_H
#define GHOSTSHELL_GHOSTTY_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

bool ghostshell_ghostty_initialize(void);
const char *ghostshell_ghostty_last_error(void);

enum {
    GHOSTSHELL_TERMINAL_OPTIONS_VERSION_1 = 1,
    GHOSTSHELL_TERMINAL_SCREEN_STATE_VERSION_1 = 1,
    GHOSTSHELL_TERMINAL_HOST_KEY_EVENT_VERSION_1 = 1,
    GHOSTSHELL_TERMINAL_PHYSICAL_INPUT_EVENT_VERSION_1 = 1,
};

typedef struct {
    const char *name;
    const char *value;
} ghostshell_environment_variable_v1;

typedef enum {
    GHOSTSHELL_CURSOR_STYLE_BLOCK = 0,
    GHOSTSHELL_CURSOR_STYLE_BAR = 1,
    GHOSTSHELL_CURSOR_STYLE_UNDERLINE = 2,
} ghostshell_cursor_style_v1;

typedef enum {
    GHOSTSHELL_CLIPBOARD_ASK = 0,
    GHOSTSHELL_CLIPBOARD_ALLOW = 1,
    GHOSTSHELL_CLIPBOARD_DENY = 2,
} ghostshell_clipboard_access_v1;

typedef enum {
    GHOSTSHELL_PASTE_PROTECT_UNSAFE = 0,
    GHOSTSHELL_PASTE_PROTECT_UNSAFE_INCLUDING_BRACKETED = 1,
    GHOSTSHELL_PASTE_ALLOW_UNSAFE = 2,
} ghostshell_paste_safety_v1;

typedef enum {
    GHOSTSHELL_LINK_CONFIRM = 0,
    GHOSTSHELL_LINK_OPEN = 1,
    GHOSTSHELL_LINK_DISABLED = 2,
} ghostshell_link_policy_v1;

typedef enum {
    GHOSTSHELL_SHELL_INTEGRATION_DETECT = 0,
    GHOSTSHELL_SHELL_INTEGRATION_DISABLED = 1,
    GHOSTSHELL_SHELL_INTEGRATION_BASH = 2,
    GHOSTSHELL_SHELL_INTEGRATION_ELVISH = 3,
    GHOSTSHELL_SHELL_INTEGRATION_FISH = 4,
    GHOSTSHELL_SHELL_INTEGRATION_NUSHELL = 5,
    GHOSTSHELL_SHELL_INTEGRATION_ZSH = 6,
} ghostshell_shell_integration_v1;

typedef enum {
    GHOSTSHELL_BELL_VISUAL = 0,
    GHOSTSHELL_BELL_SYSTEM = 1,
    GHOSTSHELL_BELL_SYSTEM_AND_VISUAL = 2,
    GHOSTSHELL_BELL_DISABLED = 3,
} ghostshell_bell_mode_v1;

typedef enum {
    GHOSTSHELL_COMPATIBILITY_GHOSTTY = 0,
    GHOSTSHELL_COMPATIBILITY_XTERM_256_COLOR = 1,
    GHOSTSHELL_COMPATIBILITY_LEGACY = 2,
} ghostshell_compatibility_profile_v1;

// Values intentionally mirror GhostShell.Application.TerminalKey. This is a
// GhostSHELL-owned ABI: callers never depend on libghostty's unversioned enums.
typedef enum {
    GHOSTSHELL_KEY_ENTER = 0,
    GHOSTSHELL_KEY_TAB = 1,
    GHOSTSHELL_KEY_BACKSPACE = 2,
    GHOSTSHELL_KEY_ESCAPE = 3,
    GHOSTSHELL_KEY_SPACE = 4,
    GHOSTSHELL_KEY_UP = 5,
    GHOSTSHELL_KEY_DOWN = 6,
    GHOSTSHELL_KEY_LEFT = 7,
    GHOSTSHELL_KEY_RIGHT = 8,
    GHOSTSHELL_KEY_HOME = 9,
    GHOSTSHELL_KEY_END = 10,
    GHOSTSHELL_KEY_PAGE_UP = 11,
    GHOSTSHELL_KEY_PAGE_DOWN = 12,
    GHOSTSHELL_KEY_INSERT = 13,
    GHOSTSHELL_KEY_DELETE = 14,
    GHOSTSHELL_KEY_F1 = 15,
    GHOSTSHELL_KEY_F2 = 16,
    GHOSTSHELL_KEY_F3 = 17,
    GHOSTSHELL_KEY_F4 = 18,
    GHOSTSHELL_KEY_F5 = 19,
    GHOSTSHELL_KEY_F6 = 20,
    GHOSTSHELL_KEY_F7 = 21,
    GHOSTSHELL_KEY_F8 = 22,
    GHOSTSHELL_KEY_F9 = 23,
    GHOSTSHELL_KEY_F10 = 24,
    GHOSTSHELL_KEY_F11 = 25,
    GHOSTSHELL_KEY_F12 = 26,
    GHOSTSHELL_KEY_F13 = 27,
    GHOSTSHELL_KEY_F14 = 28,
    GHOSTSHELL_KEY_F15 = 29,
    GHOSTSHELL_KEY_F16 = 30,
    GHOSTSHELL_KEY_F17 = 31,
    GHOSTSHELL_KEY_F18 = 32,
    GHOSTSHELL_KEY_F19 = 33,
    GHOSTSHELL_KEY_F20 = 34,
} ghostshell_terminal_key_v1;

typedef enum {
    GHOSTSHELL_KEY_MODIFIER_NONE = 0,
    GHOSTSHELL_KEY_MODIFIER_SHIFT = 1 << 0,
    GHOSTSHELL_KEY_MODIFIER_ALT = 1 << 1,
    GHOSTSHELL_KEY_MODIFIER_CONTROL = 1 << 2,
    GHOSTSHELL_KEY_MODIFIER_META = 1 << 3,
} ghostshell_terminal_key_modifiers_v1;

// Values intentionally mirror
// GhostShell.Application.TerminalCharacterChordModifier.
typedef enum {
    GHOSTSHELL_CHARACTER_CHORD_CONTROL = 0,
    GHOSTSHELL_CHARACTER_CHORD_ALT = 1,
} ghostshell_terminal_character_chord_modifier_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint32_t physical_key;
    uint32_t codepoint;
    uint32_t modifiers;
    uint32_t is_repeat;
} ghostshell_terminal_host_key_event_v1;

// Return true to consume the physical key before libghostty receives it. The
// callback runs synchronously on the native UI thread and must return promptly.
typedef bool (*ghostshell_terminal_host_key_interceptor_v1)(
    void *userdata,
    const ghostshell_terminal_host_key_event_v1 *event);

// Values intentionally mirror
// GhostShell.Application.NativeRendererPhysicalInputKind.
typedef enum {
    GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN = 0,
    GHOSTSHELL_PHYSICAL_INPUT_KEY_UP = 1,
    GHOSTSHELL_PHYSICAL_INPUT_MODIFIERS_CHANGED = 2,
    GHOSTSHELL_PHYSICAL_INPUT_IME_PREEDIT = 3,
    GHOSTSHELL_PHYSICAL_INPUT_IME_COMMIT = 4,
    GHOSTSHELL_PHYSICAL_INPUT_PASTE = 5,
    GHOSTSHELL_PHYSICAL_INPUT_MOUSE_MOVE = 6,
    GHOSTSHELL_PHYSICAL_INPUT_MOUSE_DRAG = 7,
    GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_DOWN = 8,
    GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_UP = 9,
    GHOSTSHELL_PHYSICAL_INPUT_MOUSE_SCROLL = 10,
} ghostshell_terminal_physical_input_kind_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint32_t kind;
    uint32_t reserved;
    uint64_t authority_epoch;
} ghostshell_terminal_physical_input_event_v1;

// Return true only after the exact human attachment has synchronously reclaimed
// input authority. The callback runs on the native UI thread and must not wait
// on an asynchronous client or transport. Missing, failed, or false gates consume
// the physical input before libghostty receives it.
typedef bool (*ghostshell_terminal_physical_input_gate_v1)(
    void *userdata,
    const ghostshell_terminal_physical_input_event_v1 *event);

// Values intentionally mirror GhostShell.Application.TerminalMouseButton and
// TerminalMouseEventKind.
typedef enum {
    GHOSTSHELL_MOUSE_BUTTON_NONE = 0,
    GHOSTSHELL_MOUSE_BUTTON_LEFT = 1,
    GHOSTSHELL_MOUSE_BUTTON_MIDDLE = 2,
    GHOSTSHELL_MOUSE_BUTTON_RIGHT = 3,
    GHOSTSHELL_MOUSE_BUTTON_WHEEL_UP = 4,
    GHOSTSHELL_MOUSE_BUTTON_WHEEL_DOWN = 5,
} ghostshell_terminal_mouse_button_v1;

typedef enum {
    GHOSTSHELL_MOUSE_EVENT_DOWN = 0,
    GHOSTSHELL_MOUSE_EVENT_UP = 1,
    GHOSTSHELL_MOUSE_EVENT_MOVE = 2,
    GHOSTSHELL_MOUSE_EVENT_DRAG = 3,
    GHOSTSHELL_MOUSE_EVENT_WHEEL_UP = 4,
    GHOSTSHELL_MOUSE_EVENT_WHEEL_DOWN = 5,
} ghostshell_terminal_mouse_event_v1;

// Colors are encoded as 0x00RRGGBB.
typedef struct {
    uint32_t struct_size;
    float font_size;
    uint32_t cursor_style;
    uint32_t cursor_blink;
    uint64_t scrollback_limit_bytes;
    uint32_t foreground_rgb;
    uint32_t background_rgb;
    uint32_t cursor_rgb;
    uint32_t selection_background_rgb;
    const uint32_t *ansi_palette_rgb;
    size_t ansi_palette_count;
    // Fields below this point were added additively to options version 1.
    // Callers must set struct_size; the shim defaults fields that are absent.
    const char *font_family;
    double line_height;
    uint32_t clipboard_read;
    uint32_t clipboard_write;
    uint32_t paste_safety;
    uint32_t link_policy;
    uint32_t ime_enabled;
    uint32_t shell_integration;
    uint32_t bell_mode;
    uint32_t compatibility;
} ghostshell_terminal_render_profile_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    const char *working_directory;
    const char *executable;
    const char *const *arguments;
    size_t argument_count;
    const ghostshell_environment_variable_v1 *environment;
    size_t environment_count;
    const ghostshell_terminal_render_profile_v1 *render_profile;
    // Fields below this point were added additively to options version 1.
    // Callers must set struct_size; the shim defaults fields that are absent.
    const char *const *terminal_keybindings;
    size_t terminal_keybinding_count;
    uint32_t terminal_keymap_present;
} ghostshell_terminal_options_v1;

typedef struct {
    uint32_t struct_size;
    uint32_t version;
    uint32_t rows;
    uint32_t columns;
    uint32_t cursor_row;
    uint32_t cursor_column;
    uint32_t alternate_screen;
    uint32_t bracketed_paste;
    uint32_t mouse_captured;
} ghostshell_terminal_screen_state_v1;

// The original entrypoint remains source and binary compatible for embedders
// that only need Ghostty's configured shell and a working directory.
void *ghostshell_terminal_attach(void *host_nsview, const char *working_directory);
void *ghostshell_terminal_attach_v1(
    void *host_nsview,
    const ghostshell_terminal_options_v1 *options);
bool ghostshell_terminal_confirm_close(void *terminal);
bool ghostshell_terminal_needs_close_confirmation(void *terminal);
bool ghostshell_terminal_reparent(void *terminal, void *host_nsview);
void ghostshell_terminal_detach_view(void *terminal);
void ghostshell_terminal_detach(void *terminal);
bool ghostshell_terminal_set_host_key_interceptor_v1(
    void *terminal,
    ghostshell_terminal_host_key_interceptor_v1 interceptor,
    void *userdata);
bool ghostshell_terminal_set_physical_input_gate_v1(
    void *terminal,
    ghostshell_terminal_physical_input_gate_v1 gate,
    void *userdata);
uint64_t ghostshell_terminal_input_epoch_v1(void *terminal);
void ghostshell_terminal_focus(void *terminal);
void ghostshell_terminal_resize(void *terminal, double width, double height, double scale);
bool ghostshell_terminal_resize_grid_v1(
    void *terminal,
    uint32_t columns,
    uint32_t rows);
void ghostshell_terminal_send_text(void *terminal, const char *utf8, size_t length);
bool ghostshell_terminal_send_text_at_epoch_v1(
    void *terminal,
    const char *utf8,
    size_t length,
    uint64_t expected_epoch);
// Sends a confirmed paste through libghostty's paste encoder. The caller owns
// unsafe-paste confirmation; libghostty owns newline filtering and bracketed mode.
void ghostshell_terminal_paste_text(void *terminal, const char *utf8, size_t length);
bool ghostshell_terminal_paste_text_at_epoch_v1(
    void *terminal,
    const char *utf8,
    size_t length,
    uint64_t expected_epoch);
// Sends a physical key through libghostty's live key encoder, preserving
// application-cursor/keypad and modifyOtherKeys/kitty-keyboard modes.
bool ghostshell_terminal_send_key(void *terminal, uint32_t key, uint32_t modifiers);
bool ghostshell_terminal_send_key_at_epoch_v1(
    void *terminal,
    uint32_t key,
    uint32_t modifiers,
    uint64_t expected_epoch);
// Sends one lowercase ASCII character chord through libghostty's live key
// encoder. This agent-only operation is deliberately unavailable without an
// input-authority epoch.
bool ghostshell_terminal_send_chord_at_epoch_v1(
    void *terminal,
    uint32_t character,
    uint32_t modifier,
    uint64_t expected_epoch);
// Coordinates are zero-based terminal cells. Returns false when the terminal
// has not captured mouse input or the request is outside the current grid.
bool ghostshell_terminal_send_mouse(
    void *terminal,
    uint32_t button,
    uint32_t event_kind,
    uint32_t column,
    uint32_t row,
    uint32_t modifiers);
bool ghostshell_terminal_send_mouse_at_epoch_v1(
    void *terminal,
    uint32_t button,
    uint32_t event_kind,
    uint32_t column,
    uint32_t row,
    uint32_t modifiers,
    uint64_t expected_epoch);
bool ghostshell_terminal_read_screen_state_v1(
    void *terminal,
    ghostshell_terminal_screen_state_v1 *state);
// Returns the required UTF-8 byte count. The buffer is NUL-terminated when capacity is nonzero.
size_t ghostshell_terminal_read_working_directory(void *terminal, char *buffer, size_t capacity);
// Returns the required UTF-8 byte count. The buffer is NUL-terminated when capacity is nonzero.
size_t ghostshell_terminal_read_screen(void *terminal, char *buffer, size_t capacity);
bool ghostshell_terminal_process_exited(void *terminal);

#ifdef __cplusplus
}
#endif

#endif
