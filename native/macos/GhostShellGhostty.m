#import "GhostShellGhostty.h"

#import <AppKit/AppKit.h>
#import <dispatch/dispatch.h>

#include "ghostty.h"
#include <inttypes.h>
#include <limits.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

@class GhostShellTerminalView;

static bool gs_initialized;
static bool gs_initialization_attempted;
static char gs_last_error[512];

static const size_t gs_max_launch_item_count = 4096;
static const size_t gs_max_shell_command_bytes = 1024 * 1024;
static const size_t gs_max_terminal_keymap_bytes = 1024 * 1024;
static const size_t gs_max_terminal_keymap_line_bytes = 4095;
static const char *gs_hosted_terminal_keybindings[] = {
    "shift+page_up=scroll_page_up",
    "shift+page_down=scroll_page_down",
    "super+home=scroll_to_top",
    "super+end=scroll_to_bottom",
    "super+page_up=scroll_page_up",
    "super+page_down=scroll_page_down",
};

#define GS_FIELD_END(type, field) (offsetof(type, field) + sizeof(((type *)0)->field))
#define GS_PROFILE_HAS(profile, field) ((profile)->struct_size >= GS_FIELD_END(ghostshell_terminal_render_profile_v1, field))
#define GS_OPTIONS_HAS(options, field) ((options)->struct_size >= GS_FIELD_END(ghostshell_terminal_options_v1, field))

static void gs_wakeup(void *userdata);
static bool gs_action(ghostty_app_t app, ghostty_target_s target, ghostty_action_s action);
static bool gs_read_clipboard(void *userdata, ghostty_clipboard_e clipboard, void *state);
static void gs_confirm_read_clipboard(
    void *userdata,
    const char *text,
    void *state,
    ghostty_clipboard_request_e request);
static void gs_write_clipboard(
    void *userdata,
    ghostty_clipboard_e clipboard,
    const ghostty_clipboard_content_s *content,
    size_t count,
    bool confirm);
static void gs_close_surface(void *userdata, bool process_alive);
static bool gs_add_size(size_t *total, size_t amount);

static void gs_set_error(NSString *message) {
    const char *utf8 = message.UTF8String ?: "Unknown Ghostty error";
    snprintf(gs_last_error, sizeof(gs_last_error), "%s", utf8);
}

static bool gs_is_valid_rgb(uint32_t color) {
    return (color & 0xFF000000U) == 0;
}

static bool gs_string_is_bounded(const char *value, bool allowEmpty) {
    if (value == NULL) return false;
    size_t length = strnlen(value, gs_max_shell_command_bytes + 1);
    return length <= gs_max_shell_command_bytes && (allowEmpty || length > 0);
}

static bool gs_config_string_is_safe(const char *value) {
    if (!gs_string_is_bounded(value, false)) return false;
    size_t length = strlen(value);
    return memchr(value, '\r', length) == NULL && memchr(value, '\n', length) == NULL;
}

static bool gs_validate_render_profile(const ghostshell_terminal_render_profile_v1 *profile) {
    if (profile == NULL) return true;
    if (profile->struct_size < GS_FIELD_END(ghostshell_terminal_render_profile_v1, ansi_palette_count)) {
        gs_set_error(@"The terminal render-profile structure is too small");
        return false;
    }
    if (!isfinite(profile->font_size) || profile->font_size < 6 || profile->font_size > 96) {
        gs_set_error(@"The terminal render profile contains an invalid font size");
        return false;
    }
    if (profile->cursor_style > GHOSTSHELL_CURSOR_STYLE_UNDERLINE || profile->cursor_blink > 1) {
        gs_set_error(@"The terminal render profile contains an invalid cursor style");
        return false;
    }
#if SIZE_MAX < UINT64_MAX
    if (profile->scrollback_limit_bytes > SIZE_MAX) {
        gs_set_error(@"The terminal render profile contains an unsupported scrollback limit");
        return false;
    }
#endif
    if (profile->ansi_palette_rgb == NULL || profile->ansi_palette_count != 16) {
        gs_set_error(@"The terminal render profile must contain exactly 16 ANSI colors");
        return false;
    }
    if (!gs_is_valid_rgb(profile->foreground_rgb) ||
        !gs_is_valid_rgb(profile->background_rgb) ||
        !gs_is_valid_rgb(profile->cursor_rgb) ||
        !gs_is_valid_rgb(profile->selection_background_rgb)) {
        gs_set_error(@"The terminal render profile contains an invalid RGB color");
        return false;
    }
    for (size_t index = 0; index < profile->ansi_palette_count; index++) {
        if (!gs_is_valid_rgb(profile->ansi_palette_rgb[index])) {
            gs_set_error(@"The terminal render profile contains an invalid ANSI color");
            return false;
        }
    }
    if (GS_PROFILE_HAS(profile, font_family) &&
        !gs_config_string_is_safe(profile->font_family)) {
        gs_set_error(@"The terminal render profile contains an invalid font family");
        return false;
    }
    if (GS_PROFILE_HAS(profile, line_height) &&
        (!isfinite(profile->line_height) || profile->line_height < 0.8 || profile->line_height > 3)) {
        gs_set_error(@"The terminal render profile contains an invalid line height");
        return false;
    }
    if ((GS_PROFILE_HAS(profile, clipboard_read) && profile->clipboard_read > GHOSTSHELL_CLIPBOARD_DENY) ||
        (GS_PROFILE_HAS(profile, clipboard_write) && profile->clipboard_write > GHOSTSHELL_CLIPBOARD_DENY) ||
        (GS_PROFILE_HAS(profile, paste_safety) &&
            profile->paste_safety > GHOSTSHELL_PASTE_ALLOW_UNSAFE) ||
        (GS_PROFILE_HAS(profile, link_policy) && profile->link_policy > GHOSTSHELL_LINK_DISABLED) ||
        (GS_PROFILE_HAS(profile, ime_enabled) && profile->ime_enabled > 1) ||
        (GS_PROFILE_HAS(profile, shell_integration) &&
            profile->shell_integration > GHOSTSHELL_SHELL_INTEGRATION_ZSH) ||
        (GS_PROFILE_HAS(profile, bell_mode) && profile->bell_mode > GHOSTSHELL_BELL_DISABLED) ||
        (GS_PROFILE_HAS(profile, compatibility) &&
            profile->compatibility > GHOSTSHELL_COMPATIBILITY_LEGACY)) {
        gs_set_error(@"The terminal render profile contains an unsupported policy value");
        return false;
    }
    return true;
}

static bool gs_validate_options(const ghostshell_terminal_options_v1 *options) {
    if (options == NULL ||
        options->struct_size < GS_FIELD_END(ghostshell_terminal_options_v1, render_profile)) {
        gs_set_error(@"The terminal launch-options structure is missing or too small");
        return false;
    }
    if (options->version != GHOSTSHELL_TERMINAL_OPTIONS_VERSION_1) {
        gs_set_error(@"The terminal launch-options version is not supported");
        return false;
    }
    if (options->argument_count > gs_max_launch_item_count ||
        options->environment_count > gs_max_launch_item_count ||
        (GS_OPTIONS_HAS(options, terminal_keybinding_count) &&
            options->terminal_keybinding_count > gs_max_launch_item_count)) {
        gs_set_error(@"The terminal launch contains too many arguments, environment variables, or keybindings");
        return false;
    }
    if ((options->argument_count > 0 && options->arguments == NULL) ||
        (options->executable == NULL && options->argument_count > 0) ||
        (options->executable != NULL && !gs_string_is_bounded(options->executable, false)) ||
        (options->working_directory != NULL &&
            !gs_string_is_bounded(options->working_directory, true))) {
        gs_set_error(@"The terminal executable and arguments are inconsistent");
        return false;
    }
    for (size_t index = 0; index < options->argument_count; index++) {
        if (!gs_string_is_bounded(options->arguments[index], true)) {
            gs_set_error(@"The terminal argument list contains an invalid value");
            return false;
        }
    }
    if (options->environment_count > 0 && options->environment == NULL) {
        gs_set_error(@"The terminal environment is missing");
        return false;
    }
    for (size_t index = 0; index < options->environment_count; index++) {
        const ghostshell_environment_variable_v1 variable = options->environment[index];
        if (!gs_string_is_bounded(variable.name, false) ||
            memchr(variable.name, '=', strlen(variable.name)) != NULL ||
            !gs_string_is_bounded(variable.value, true)) {
            gs_set_error(@"The terminal environment contains an invalid variable");
            return false;
        }
    }
    if (GS_OPTIONS_HAS(options, terminal_keymap_present)) {
        if (options->terminal_keymap_present > 1 ||
            (options->terminal_keymap_present == 0 &&
                (options->terminal_keybinding_count != 0 || options->terminal_keybindings != NULL)) ||
            (options->terminal_keybinding_count > 0 && options->terminal_keybindings == NULL)) {
            gs_set_error(@"The terminal keymap fields are inconsistent");
            return false;
        }
        size_t keymapBytes = 0;
        if (options->terminal_keymap_present != 0) {
            keymapBytes = sizeof("keybind = clear\n") - 1;
            for (size_t index = 0;
                 index < sizeof(gs_hosted_terminal_keybindings) /
                    sizeof(gs_hosted_terminal_keybindings[0]);
                 index++) {
                keymapBytes += sizeof("keybind = \n") - 1 +
                    strlen(gs_hosted_terminal_keybindings[index]);
            }
        }
        for (size_t index = 0; index < options->terminal_keybinding_count; index++) {
            const char *binding = options->terminal_keybindings[index];
            if (!gs_config_string_is_safe(binding)) {
                gs_set_error(@"The terminal keymap contains an invalid binding");
                return false;
            }
            size_t serializedLength = strlen(binding) + sizeof("keybind = \n") - 1;
            if (serializedLength > gs_max_terminal_keymap_line_bytes) {
                gs_set_error(@"A terminal keybinding exceeds libghostty's bounded line size");
                return false;
            }
            if (serializedLength > gs_max_terminal_keymap_bytes ||
                keymapBytes > gs_max_terminal_keymap_bytes - serializedLength) {
                gs_set_error(@"The terminal keymap exceeds the bounded configuration size");
                return false;
            }
            keymapBytes += serializedLength;
        }
    }
    return gs_validate_render_profile(options->render_profile);
}

static bool gs_add_size(size_t *total, size_t amount) {
    if (amount > gs_max_shell_command_bytes || *total > gs_max_shell_command_bytes - amount) {
        return false;
    }
    *total += amount;
    return true;
}

// libghostty 1.3.1 accepts only a shell command for embedded surfaces. Quote
// every structured argv word so the compatibility shell cannot expand it.
static char *gs_build_shell_command(const ghostshell_terminal_options_v1 *options) {
    if (options->executable == NULL) return NULL;

    size_t total = 1;
    size_t wordCount = options->argument_count + 1;
    for (size_t index = 0; index < wordCount; index++) {
        const char *word = index == 0 ? options->executable : options->arguments[index - 1];
        size_t length = strnlen(word, gs_max_shell_command_bytes + 1);
        if (length > gs_max_shell_command_bytes || !gs_add_size(&total, length + 2)) {
            gs_set_error(@"The terminal command is too large");
            return NULL;
        }
        for (size_t character = 0; character < length; character++) {
            if (word[character] == '\'' && !gs_add_size(&total, 3)) {
                gs_set_error(@"The terminal command is too large");
                return NULL;
            }
        }
        if (index > 0 && !gs_add_size(&total, 1)) {
            gs_set_error(@"The terminal command is too large");
            return NULL;
        }
    }

    char *command = malloc(total);
    if (command == NULL) {
        gs_set_error(@"Unable to allocate the terminal command");
        return NULL;
    }

    char *output = command;
    for (size_t index = 0; index < wordCount; index++) {
        if (index > 0) *output++ = ' ';
        const char *word = index == 0 ? options->executable : options->arguments[index - 1];
        *output++ = '\'';
        for (const char *character = word; *character != '\0'; character++) {
            if (*character == '\'') {
                *output++ = '\'';
                *output++ = '\\';
                *output++ = '\'';
                *output++ = '\'';
            } else {
                *output++ = *character;
            }
        }
        *output++ = '\'';
    }
    *output = '\0';
    return command;
}

static bool gs_write_rgb(FILE *file, uint32_t color) {
    return fprintf(file, "#%06X", color & 0x00FFFFFFU) >= 0;
}

static bool gs_write_config_string(FILE *file, const char *value) {
    if (fputc('"', file) == EOF) return false;
    for (const char *character = value; *character != '\0'; character++) {
        if ((*character == '"' || *character == '\\') && fputc('\\', file) == EOF) return false;
        if (fputc(*character, file) == EOF) return false;
    }
    return fputc('"', file) != EOF;
}

static const char *gs_clipboard_access_name(uint32_t value) {
    switch (value) {
        case GHOSTSHELL_CLIPBOARD_ALLOW: return "allow";
        case GHOSTSHELL_CLIPBOARD_DENY: return "deny";
        default: return "ask";
    }
}

static const char *gs_shell_integration_name(uint32_t value) {
    switch (value) {
        case GHOSTSHELL_SHELL_INTEGRATION_DISABLED: return "none";
        case GHOSTSHELL_SHELL_INTEGRATION_BASH: return "bash";
        case GHOSTSHELL_SHELL_INTEGRATION_ELVISH: return "elvish";
        case GHOSTSHELL_SHELL_INTEGRATION_FISH: return "fish";
        case GHOSTSHELL_SHELL_INTEGRATION_NUSHELL: return "nushell";
        case GHOSTSHELL_SHELL_INTEGRATION_ZSH: return "zsh";
        default: return "detect";
    }
}

static const char *gs_bell_features(uint32_t value) {
    switch (value) {
        case GHOSTSHELL_BELL_SYSTEM:
            return "system,no-audio,no-attention,no-title,no-border";
        case GHOSTSHELL_BELL_SYSTEM_AND_VISUAL:
            return "system,no-audio,attention,title,border";
        case GHOSTSHELL_BELL_DISABLED:
            return "false";
        default:
            return "no-system,no-audio,attention,title,border";
    }
}

static bool gs_apply_hosted_input_policy(ghostty_config_t config) {
    char path[] = "/tmp/ghostshell-terminal-input.XXXXXX";
    int descriptor = mkstemp(path);
    if (descriptor < 0) {
        gs_set_error(@"Unable to create the hosted terminal input policy");
        return false;
    }
    (void)fchmod(descriptor, S_IRUSR | S_IWUSR);

    FILE *file = fdopen(descriptor, "w");
    if (file == NULL) {
        close(descriptor);
        unlink(path);
        gs_set_error(@"Unable to write the hosted terminal input policy");
        return false;
    }

    // TerminalCharacterChord.Alt is a semantic terminal modifier, not a
    // request to compose text through the active macOS keyboard layout.
    // libghostty exposes this choice as surface configuration rather than as
    // per-event metadata, so hosted surfaces consistently treat Option as Alt.
    bool written = fprintf(file, "macos-option-as-alt = true\n") >= 0;
    if (fclose(file) != 0) written = false;
    if (!written) {
        unlink(path);
        gs_set_error(@"Unable to write the hosted terminal input policy");
        return false;
    }

    uint32_t diagnosticsBefore = ghostty_config_diagnostics_count(config);
    ghostty_config_load_file(config, path);
    unlink(path);
    if (ghostty_config_diagnostics_count(config) > diagnosticsBefore) {
        gs_set_error(@"libghostty rejected the hosted terminal input policy");
        return false;
    }
    return true;
}

static bool gs_apply_render_profile(
    ghostty_config_t config,
    const ghostshell_terminal_render_profile_v1 *profile) {
    if (profile == NULL) return true;

    char path[] = "/tmp/ghostshell-terminal-profile.XXXXXX";
    int descriptor = mkstemp(path);
    if (descriptor < 0) {
        gs_set_error(@"Unable to create the terminal render-profile input");
        return false;
    }
    (void)fchmod(descriptor, S_IRUSR | S_IWUSR);

    FILE *file = fdopen(descriptor, "w");
    if (file == NULL) {
        close(descriptor);
        unlink(path);
        gs_set_error(@"Unable to write the terminal render-profile input");
        return false;
    }

    const char *cursorStyle = profile->cursor_style == GHOSTSHELL_CURSOR_STYLE_BLOCK
        ? "block"
        : profile->cursor_style == GHOSTSHELL_CURSOR_STYLE_BAR ? "bar" : "underline";
    bool written = fprintf(file, "foreground = ") >= 0 &&
        gs_write_rgb(file, profile->foreground_rgb) &&
        fprintf(file, "\nbackground = ") >= 0 &&
        gs_write_rgb(file, profile->background_rgb) &&
        fprintf(file, "\ncursor-color = ") >= 0 &&
        gs_write_rgb(file, profile->cursor_rgb) &&
        fprintf(file, "\nselection-background = ") >= 0 &&
        gs_write_rgb(file, profile->selection_background_rgb) &&
        fprintf(
            file,
            "\ncursor-style = %s\ncursor-style-blink = %s\nscrollback-limit = %" PRIu64 "\n",
            cursorStyle,
            profile->cursor_blink == 0 ? "false" : "true",
            profile->scrollback_limit_bytes) >= 0;
    for (size_t index = 0; written && index < profile->ansi_palette_count; index++) {
        written = fprintf(file, "palette = %zu=", index) >= 0 &&
            gs_write_rgb(file, profile->ansi_palette_rgb[index]) &&
            fprintf(file, "\n") >= 0;
    }

    if (written && GS_PROFILE_HAS(profile, font_family)) {
        written = fprintf(file, "font-family = \"\"\nfont-family = ") >= 0 &&
            gs_write_config_string(file, profile->font_family) &&
            fprintf(file, "\n") >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, line_height)) {
        int64_t hundredthsPercent = (int64_t)llround((profile->line_height - 1) * 10000);
        uint64_t magnitude = hundredthsPercent < 0
            ? (uint64_t)(-hundredthsPercent)
            : (uint64_t)hundredthsPercent;
        written = fprintf(
            file,
            "adjust-cell-height = %s%" PRIu64 ".%02" PRIu64 "%%\n",
            hundredthsPercent < 0 ? "-" : "",
            magnitude / 100,
            magnitude % 100) >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, clipboard_read)) {
        written = fprintf(
            file,
            "clipboard-read = %s\n",
            gs_clipboard_access_name(profile->clipboard_read)) >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, clipboard_write)) {
        written = fprintf(
            file,
            "clipboard-write = %s\n",
            gs_clipboard_access_name(profile->clipboard_write)) >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, paste_safety)) {
        bool protect = profile->paste_safety != GHOSTSHELL_PASTE_ALLOW_UNSAFE;
        bool bracketedSafe = profile->paste_safety == GHOSTSHELL_PASTE_PROTECT_UNSAFE;
        written = fprintf(
            file,
            "clipboard-paste-protection = %s\nclipboard-paste-bracketed-safe = %s\n",
            protect ? "true" : "false",
            bracketedSafe ? "true" : "false") >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, link_policy)) {
        bool linksEnabled = profile->link_policy != GHOSTSHELL_LINK_DISABLED;
        bool previewsEnabled = profile->link_policy == GHOSTSHELL_LINK_OPEN;
        written = fprintf(
            file,
            "link-url = %s\nlink-previews = %s\n",
            linksEnabled ? "true" : "false",
            previewsEnabled ? "true" : "false") >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, shell_integration)) {
        written = fprintf(
            file,
            "shell-integration = %s\n",
            gs_shell_integration_name(profile->shell_integration)) >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, bell_mode)) {
        written = fprintf(file, "bell-features = %s\n", gs_bell_features(profile->bell_mode)) >= 0;
    }
    if (written && GS_PROFILE_HAS(profile, compatibility)) {
        const char *term = profile->compatibility == GHOSTSHELL_COMPATIBILITY_GHOSTTY
            ? "xterm-ghostty"
            : "xterm-256color";
        const char *graphemeWidth = profile->compatibility == GHOSTSHELL_COMPATIBILITY_LEGACY
            ? "legacy"
            : "unicode";
        written = fprintf(
            file,
            "term = %s\ngrapheme-width-method = %s\n",
            term,
            graphemeWidth) >= 0;
    }

    if (fclose(file) != 0) written = false;
    if (!written) {
        unlink(path);
        gs_set_error(@"Unable to write the terminal render-profile input");
        return false;
    }

    uint32_t diagnosticsBefore = ghostty_config_diagnostics_count(config);
    ghostty_config_load_file(config, path);
    unlink(path);
    if (ghostty_config_diagnostics_count(config) > diagnosticsBefore) {
        gs_set_error(@"libghostty rejected the terminal render profile");
        return false;
    }
    return true;
}

static bool gs_apply_terminal_keymap(
    ghostty_config_t config,
    const ghostshell_terminal_options_v1 *options) {
    if (!GS_OPTIONS_HAS(options, terminal_keymap_present) ||
        options->terminal_keymap_present == 0) {
        return true;
    }

    char path[] = "/tmp/ghostshell-terminal-keymap.XXXXXX";
    int descriptor = mkstemp(path);
    if (descriptor < 0) {
        gs_set_error(@"Unable to create the terminal keymap input");
        return false;
    }
    (void)fchmod(descriptor, S_IRUSR | S_IWUSR);

    FILE *file = fdopen(descriptor, "w");
    if (file == NULL) {
        close(descriptor);
        unlink(path);
        gs_set_error(@"Unable to write the terminal keymap input");
        return false;
    }

    bool written = fprintf(file, "keybind = clear\n") >= 0;
    for (size_t index = 0;
         written && index < sizeof(gs_hosted_terminal_keybindings) /
            sizeof(gs_hosted_terminal_keybindings[0]);
         index++) {
        written = fprintf(
            file,
            "keybind = %s\n",
            gs_hosted_terminal_keybindings[index]) >= 0;
    }
    for (size_t index = 0; written && index < options->terminal_keybinding_count; index++) {
        written = fprintf(
            file,
            "keybind = %s\n",
            options->terminal_keybindings[index]) >= 0;
    }
    if (fclose(file) != 0) written = false;
    if (!written) {
        unlink(path);
        gs_set_error(@"Unable to write the terminal keymap input");
        return false;
    }

    uint32_t diagnosticsBefore = ghostty_config_diagnostics_count(config);
    ghostty_config_load_file(config, path);
    unlink(path);
    if (ghostty_config_diagnostics_count(config) > diagnosticsBefore) {
        gs_set_error(@"libghostty rejected the selected terminal keymap");
        return false;
    }
    return true;
}

static ghostty_input_mods_e gs_modifiers(NSEventModifierFlags flags) {
    uint32_t mods = GHOSTTY_MODS_NONE;
    if ((flags & NSEventModifierFlagShift) != 0) mods |= GHOSTTY_MODS_SHIFT;
    if ((flags & NSEventModifierFlagControl) != 0) mods |= GHOSTTY_MODS_CTRL;
    if ((flags & NSEventModifierFlagOption) != 0) mods |= GHOSTTY_MODS_ALT;
    if ((flags & NSEventModifierFlagCommand) != 0) mods |= GHOSTTY_MODS_SUPER;
    if ((flags & NSEventModifierFlagCapsLock) != 0) mods |= GHOSTTY_MODS_CAPS;
    return (ghostty_input_mods_e)mods;
}

static uint32_t gs_host_key_modifiers(NSEventModifierFlags flags) {
    uint32_t modifiers = GHOSTSHELL_KEY_MODIFIER_NONE;
    if ((flags & NSEventModifierFlagShift) != 0) {
        modifiers |= GHOSTSHELL_KEY_MODIFIER_SHIFT;
    }
    if ((flags & NSEventModifierFlagOption) != 0) {
        modifiers |= GHOSTSHELL_KEY_MODIFIER_ALT;
    }
    if ((flags & NSEventModifierFlagControl) != 0) {
        modifiers |= GHOSTSHELL_KEY_MODIFIER_CONTROL;
    }
    if ((flags & NSEventModifierFlagCommand) != 0) {
        modifiers |= GHOSTSHELL_KEY_MODIFIER_META;
    }
    return modifiers;
}

static bool gs_terminal_modifiers_are_valid(uint32_t modifiers) {
    const uint32_t all = GHOSTSHELL_KEY_MODIFIER_SHIFT |
        GHOSTSHELL_KEY_MODIFIER_ALT |
        GHOSTSHELL_KEY_MODIFIER_CONTROL |
        GHOSTSHELL_KEY_MODIFIER_META;
    return (modifiers & ~all) == 0;
}

static NSEventModifierFlags gs_ns_modifiers(uint32_t modifiers) {
    NSEventModifierFlags flags = 0;
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_SHIFT) != 0) {
        flags |= NSEventModifierFlagShift;
    }
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_ALT) != 0) {
        flags |= NSEventModifierFlagOption;
    }
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_CONTROL) != 0) {
        flags |= NSEventModifierFlagControl;
    }
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_META) != 0) {
        flags |= NSEventModifierFlagCommand;
    }
    return flags;
}

static ghostty_input_mods_e gs_programmatic_mouse_modifiers(uint32_t modifiers) {
    uint32_t result = GHOSTTY_MODS_NONE;
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_SHIFT) != 0) result |= GHOSTTY_MODS_SHIFT;
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_ALT) != 0) result |= GHOSTTY_MODS_ALT;
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_CONTROL) != 0) result |= GHOSTTY_MODS_CTRL;
    if ((modifiers & GHOSTSHELL_KEY_MODIFIER_META) != 0) result |= GHOSTTY_MODS_SUPER;
    return (ghostty_input_mods_e)result;
}

typedef struct {
    unsigned short key_code;
    unichar character;
} gs_programmatic_key_spec;

static bool gs_get_programmatic_key_spec(uint32_t key, gs_programmatic_key_spec *spec) {
    if (spec == NULL) return false;

    switch (key) {
        case GHOSTSHELL_KEY_ENTER: *spec = (gs_programmatic_key_spec){36, '\r'}; return true;
        case GHOSTSHELL_KEY_TAB: *spec = (gs_programmatic_key_spec){48, '\t'}; return true;
        case GHOSTSHELL_KEY_BACKSPACE:
            *spec = (gs_programmatic_key_spec){51, NSDeleteCharacter};
            return true;
        case GHOSTSHELL_KEY_ESCAPE: *spec = (gs_programmatic_key_spec){53, 0x1B}; return true;
        case GHOSTSHELL_KEY_SPACE: *spec = (gs_programmatic_key_spec){49, ' '}; return true;
        case GHOSTSHELL_KEY_UP:
            *spec = (gs_programmatic_key_spec){126, NSUpArrowFunctionKey};
            return true;
        case GHOSTSHELL_KEY_DOWN:
            *spec = (gs_programmatic_key_spec){125, NSDownArrowFunctionKey};
            return true;
        case GHOSTSHELL_KEY_LEFT:
            *spec = (gs_programmatic_key_spec){123, NSLeftArrowFunctionKey};
            return true;
        case GHOSTSHELL_KEY_RIGHT:
            *spec = (gs_programmatic_key_spec){124, NSRightArrowFunctionKey};
            return true;
        case GHOSTSHELL_KEY_HOME:
            *spec = (gs_programmatic_key_spec){115, NSHomeFunctionKey};
            return true;
        case GHOSTSHELL_KEY_END:
            *spec = (gs_programmatic_key_spec){119, NSEndFunctionKey};
            return true;
        case GHOSTSHELL_KEY_PAGE_UP:
            *spec = (gs_programmatic_key_spec){116, NSPageUpFunctionKey};
            return true;
        case GHOSTSHELL_KEY_PAGE_DOWN:
            *spec = (gs_programmatic_key_spec){121, NSPageDownFunctionKey};
            return true;
        case GHOSTSHELL_KEY_INSERT:
            *spec = (gs_programmatic_key_spec){114, NSInsertFunctionKey};
            return true;
        case GHOSTSHELL_KEY_DELETE:
            *spec = (gs_programmatic_key_spec){117, NSDeleteFunctionKey};
            return true;
        case GHOSTSHELL_KEY_F1: *spec = (gs_programmatic_key_spec){122, NSF1FunctionKey}; return true;
        case GHOSTSHELL_KEY_F2: *spec = (gs_programmatic_key_spec){120, NSF2FunctionKey}; return true;
        case GHOSTSHELL_KEY_F3: *spec = (gs_programmatic_key_spec){99, NSF3FunctionKey}; return true;
        case GHOSTSHELL_KEY_F4: *spec = (gs_programmatic_key_spec){118, NSF4FunctionKey}; return true;
        case GHOSTSHELL_KEY_F5: *spec = (gs_programmatic_key_spec){96, NSF5FunctionKey}; return true;
        case GHOSTSHELL_KEY_F6: *spec = (gs_programmatic_key_spec){97, NSF6FunctionKey}; return true;
        case GHOSTSHELL_KEY_F7: *spec = (gs_programmatic_key_spec){98, NSF7FunctionKey}; return true;
        case GHOSTSHELL_KEY_F8: *spec = (gs_programmatic_key_spec){100, NSF8FunctionKey}; return true;
        case GHOSTSHELL_KEY_F9: *spec = (gs_programmatic_key_spec){101, NSF9FunctionKey}; return true;
        case GHOSTSHELL_KEY_F10: *spec = (gs_programmatic_key_spec){109, NSF10FunctionKey}; return true;
        case GHOSTSHELL_KEY_F11: *spec = (gs_programmatic_key_spec){103, NSF11FunctionKey}; return true;
        case GHOSTSHELL_KEY_F12: *spec = (gs_programmatic_key_spec){111, NSF12FunctionKey}; return true;
        case GHOSTSHELL_KEY_F13: *spec = (gs_programmatic_key_spec){105, NSF13FunctionKey}; return true;
        case GHOSTSHELL_KEY_F14: *spec = (gs_programmatic_key_spec){107, NSF14FunctionKey}; return true;
        case GHOSTSHELL_KEY_F15: *spec = (gs_programmatic_key_spec){113, NSF15FunctionKey}; return true;
        case GHOSTSHELL_KEY_F16: *spec = (gs_programmatic_key_spec){106, NSF16FunctionKey}; return true;
        case GHOSTSHELL_KEY_F17: *spec = (gs_programmatic_key_spec){64, NSF17FunctionKey}; return true;
        case GHOSTSHELL_KEY_F18: *spec = (gs_programmatic_key_spec){79, NSF18FunctionKey}; return true;
        case GHOSTSHELL_KEY_F19: *spec = (gs_programmatic_key_spec){80, NSF19FunctionKey}; return true;
        case GHOSTSHELL_KEY_F20: *spec = (gs_programmatic_key_spec){90, NSF20FunctionKey}; return true;
        default: return false;
    }
}

static bool gs_get_programmatic_character_spec(
    uint32_t character,
    gs_programmatic_key_spec *spec) {
    static const unsigned short letterKeyCodes[] = {
        0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46,
        45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6,
    };
    if (spec == NULL || character < 'a' || character > 'z') return false;

    *spec = (gs_programmatic_key_spec){
        letterKeyCodes[character - 'a'],
        (unichar)character,
    };
    return true;
}

static bool gs_get_character_chord_modifiers(
    uint32_t modifier,
    uint32_t *keyModifiers) {
    if (keyModifiers == NULL) return false;

    switch (modifier) {
        case GHOSTSHELL_CHARACTER_CHORD_CONTROL:
            *keyModifiers = GHOSTSHELL_KEY_MODIFIER_CONTROL;
            return true;
        case GHOSTSHELL_CHARACTER_CHORD_ALT:
            *keyModifiers = GHOSTSHELL_KEY_MODIFIER_ALT;
            return true;
        default:
            return false;
    }
}

static uint32_t gs_first_codepoint(NSString *text) {
    if (text.length == 0) return 0;

    NSData *utf32 = [text dataUsingEncoding:NSUTF32LittleEndianStringEncoding];
    if (utf32.length < sizeof(uint32_t)) return 0;

    uint32_t value = 0;
    [utf32 getBytes:&value length:sizeof(value)];
    return value;
}

static void gs_send_enter(ghostty_surface_t surface) {
    ghostty_input_key_s key = {0};
    key.action = GHOSTTY_ACTION_PRESS;
    key.keycode = 36;
    key.unshifted_codepoint = '\r';
    ghostty_surface_key(surface, key);
    key.action = GHOSTTY_ACTION_RELEASE;
    ghostty_surface_key(surface, key);
}

static void gs_send_programmatic_text(ghostty_surface_t surface, const char *utf8, size_t length) {
    size_t segmentStart = 0;
    for (size_t index = 0; index < length; index++) {
        if (utf8[index] != '\n' && utf8[index] != '\r') continue;

        if (index > segmentStart) {
            ghostty_surface_text(surface, utf8 + segmentStart, index - segmentStart);
        }
        gs_send_enter(surface);

        if (utf8[index] == '\r' && index + 1 < length && utf8[index + 1] == '\n') index++;
        segmentStart = index + 1;
    }

    if (segmentStart < length) {
        ghostty_surface_text(surface, utf8 + segmentStart, length - segmentStart);
    }
}

static BOOL gs_confirm_user_action(
    NSString *message,
    NSString *informativeText,
    NSString *approveButtonTitle) {
    __block NSModalResponse response = NSModalResponseCancel;
    dispatch_block_t showAlert = ^{
        NSAlert *alert = [[NSAlert alloc] init];
        alert.alertStyle = NSAlertStyleWarning;
        alert.messageText = message;
        alert.informativeText = informativeText;
        [alert addButtonWithTitle:approveButtonTitle];
        [alert addButtonWithTitle:@"Cancel"];
        response = [alert runModal];
    };

    if (NSThread.isMainThread) {
        showAlert();
    } else {
        dispatch_sync(dispatch_get_main_queue(), showAlert);
    }
    return response == NSAlertFirstButtonReturn;
}

@interface GhostShellTerminalView : NSView <NSTextInputClient, NSSearchFieldDelegate>
@property(nonatomic, assign) ghostty_app_t app;
@property(nonatomic, assign) ghostty_surface_t surface;
@property(nonatomic, strong) NSTrackingArea *terminalTrackingArea;
@property(nonatomic, strong) NSMutableAttributedString *markedText;
@property(nonatomic, strong) NSMutableArray<NSString *> *keyTextAccumulator;
@property(nonatomic, strong) NSSearchField *searchField;
@property(nonatomic, assign) ssize_t searchTotal;
@property(nonatomic, assign) ssize_t searchSelected;
@property(nonatomic, strong) id eventMonitor;
@property(nonatomic, assign) BOOL suppressNextLeftMouseUp;
@property(nonatomic, assign) BOOL imeEnabled;
@property(nonatomic, assign) uint32_t linkPolicy;
@property(nonatomic, assign) uint32_t bellMode;
@property(nonatomic, assign) double contentScale;
@property(nonatomic, copy) NSString *workingDirectory;
@property(nonatomic, assign) ghostshell_terminal_host_key_interceptor_v1 hostKeyInterceptor;
@property(nonatomic, assign) void *hostKeyInterceptorUserdata;
@property(nonatomic, assign) ghostshell_terminal_physical_input_gate_v1 physicalInputGate;
@property(nonatomic, assign) void *physicalInputGateUserdata;
@property(nonatomic, assign) uint64_t physicalInputEpoch;
@property(nonatomic, strong) NSMutableIndexSet *interceptedPhysicalKeys;
@property(nonatomic, assign) BOOL bypassHostKeyInterceptor;
@property(nonatomic, assign) BOOL deliveringPhysicalKey;
@property(nonatomic, assign) BOOL physicalInputDeniedDuringInterpretation;
- (BOOL)startWithOptions:(const ghostshell_terminal_options_v1 *)options
          loadUserConfig:(BOOL)loadUserConfig;
- (void)shutdown;
- (void)updateSurfaceGeometryWithWidth:(double)width height:(double)height scale:(double)scale;
- (NSEvent *)handleLocalEvent:(NSEvent *)event;
- (BOOL)acceptPhysicalInput:(ghostshell_terminal_physical_input_kind_v1)kind;
- (void)clearMarkedText;
- (void)showSearchWithNeedle:(NSString *)needle;
- (void)hideSearch;
- (void)updateSearchAccessibility;
@end

@implementation GhostShellTerminalView

- (instancetype)initWithFrame:(NSRect)frameRect {
    self = [super initWithFrame:frameRect];
    if (self == nil) return nil;

    self.markedText = [[NSMutableAttributedString alloc] init];
    self.interceptedPhysicalKeys = [[NSMutableIndexSet alloc] init];
    self.contentScale = 1;
    self.searchField = [[NSSearchField alloc] initWithFrame:NSZeroRect];
    self.searchField.hidden = YES;
    self.searchField.placeholderString = @"Find in terminal";
    self.searchField.sendsSearchStringImmediately = YES;
    self.searchField.delegate = self;
    self.searchField.target = self;
    self.searchField.action = @selector(searchFieldChanged:);
    [self.searchField setAccessibilityLabel:@"Find in terminal output"];
    [self addSubview:self.searchField];
    self.searchTotal = -1;
    self.searchSelected = -1;
    __weak GhostShellTerminalView *weakSelf = self;
    self.eventMonitor = [NSEvent
        addLocalMonitorForEventsMatchingMask:NSEventMaskKeyUp | NSEventMaskLeftMouseDown
                                     handler:^NSEvent *(NSEvent *event) {
        GhostShellTerminalView *view = weakSelf;
        return view == nil ? event : [view handleLocalEvent:event];
    }];
    return self;
}

- (BOOL)acceptsFirstResponder {
    return YES;
}

- (void)layout {
    [super layout];
    CGFloat available = MAX(0, self.bounds.size.width - 24);
    CGFloat width = MIN(320, available);
    self.searchField.frame = NSMakeRect(
        MAX(12, self.bounds.size.width - width - 12),
        MAX(12, self.bounds.size.height - 40),
        width,
        28);
}

- (void)showSearchWithNeedle:(NSString *)needle {
    self.searchField.stringValue = needle ?: @"";
    self.searchField.hidden = NO;
    [self setNeedsLayout:YES];
    [self.window makeFirstResponder:self.searchField];
    [self.searchField selectText:nil];
    [self updateSearchAccessibility];
}

- (void)hideSearch {
    self.searchField.hidden = YES;
    [self.window makeFirstResponder:self];
}

- (void)updateSearchAccessibility {
    NSString *status = nil;
    if (self.searchTotal <= 0) {
        status = self.searchTotal == 0 ? @"No matches" : @"Searching terminal output";
    } else if (self.searchSelected >= 0) {
        status = [NSString stringWithFormat:
            @"Match %zd of %zd",
            self.searchSelected + 1,
            self.searchTotal];
    } else {
        status = [NSString stringWithFormat:@"%zd matches", self.searchTotal];
    }
    self.searchField.toolTip = status;
    [self.searchField setAccessibilityHelp:status];
}

- (void)searchFieldChanged:(NSSearchField *)sender {
    if (self.surface == NULL) return;
    NSString *action = [@"search:" stringByAppendingString:sender.stringValue ?: @""];
    const char *utf8 = action.UTF8String;
    if (utf8 != NULL) {
        ghostty_surface_binding_action(
            self.surface,
            utf8,
            [action lengthOfBytesUsingEncoding:NSUTF8StringEncoding]);
    }
}

- (BOOL)control:(NSControl *)control
        textView:(NSTextView *)textView
doCommandBySelector:(SEL)commandSelector {
    (void)textView;
    if (control != self.searchField) {
        return NO;
    }

    if (commandSelector == @selector(cancelOperation:)) {
        if (self.surface != NULL) {
            static const char endSearch[] = "end_search";
            ghostty_surface_binding_action(self.surface, endSearch, sizeof(endSearch) - 1);
        }
        [self hideSearch];
        return YES;
    }

    if (commandSelector == @selector(insertNewline:) && self.surface != NULL) {
        BOOL previous = (NSApplication.sharedApplication.currentEvent.modifierFlags &
            NSEventModifierFlagShift) != 0;
        static const char nextSearch[] = "navigate_search:next";
        static const char previousSearch[] = "navigate_search:previous";
        const char *action = previous ? previousSearch : nextSearch;
        size_t length = previous ? sizeof(previousSearch) - 1 : sizeof(nextSearch) - 1;
        ghostty_surface_binding_action(self.surface, action, length);
        return YES;
    }

    return NO;
}

- (BOOL)startWithOptions:(const ghostshell_terminal_options_v1 *)options
          loadUserConfig:(BOOL)loadUserConfig {
    if (!ghostshell_ghostty_initialize()) return NO;

    const ghostshell_terminal_render_profile_v1 *profile = options->render_profile;
    self.imeEnabled = profile == NULL || !GS_PROFILE_HAS(profile, ime_enabled)
        ? YES
        : profile->ime_enabled != 0;
    self.linkPolicy = profile != NULL && GS_PROFILE_HAS(profile, link_policy)
        ? profile->link_policy
        : GHOSTSHELL_LINK_CONFIRM;
    self.bellMode = profile != NULL && GS_PROFILE_HAS(profile, bell_mode)
        ? profile->bell_mode
        : GHOSTSHELL_BELL_VISUAL;
    self.workingDirectory = options->working_directory == NULL
        ? nil
        : [NSString stringWithUTF8String:options->working_directory];

    ghostty_config_t appConfig = ghostty_config_new();
    if (appConfig == NULL) {
        gs_set_error(@"ghostty_config_new failed");
        return NO;
    }
    if (loadUserConfig) {
        // Preserve historical behavior only for the legacy attach symbol.
        // Every versioned launch is isolated from unrelated user Ghostty files,
        // including a valid launch that has no selected keymap snapshot.
        ghostty_config_load_default_files(appConfig);
        ghostty_config_load_recursive_files(appConfig);
    }
    if (!gs_apply_hosted_input_policy(appConfig)) {
        ghostty_config_free(appConfig);
        return NO;
    }
    if (!gs_apply_render_profile(appConfig, options->render_profile)) {
        ghostty_config_free(appConfig);
        return NO;
    }
    if (!gs_apply_terminal_keymap(appConfig, options)) {
        ghostty_config_free(appConfig);
        return NO;
    }
    ghostty_config_finalize(appConfig);

    ghostty_runtime_config_s runtime = {
        .userdata = (__bridge void *)self,
        .supports_selection_clipboard = false,
        .wakeup_cb = gs_wakeup,
        .action_cb = gs_action,
        .read_clipboard_cb = gs_read_clipboard,
        .confirm_read_clipboard_cb = gs_confirm_read_clipboard,
        .write_clipboard_cb = gs_write_clipboard,
        .close_surface_cb = gs_close_surface,
    };
    self.app = ghostty_app_new(&runtime, appConfig);
    ghostty_config_free(appConfig);
    if (self.app == NULL) {
        gs_set_error(@"ghostty_app_new failed");
        return NO;
    }
    ghostty_app_set_focus(self.app, NSApplication.sharedApplication.isActive);

    char *command = gs_build_shell_command(options);
    if (options->executable != NULL && command == NULL) {
        [self shutdown];
        return NO;
    }

    ghostty_surface_config_s config = ghostty_surface_config_new();
    config.platform_tag = GHOSTTY_PLATFORM_MACOS;
    config.platform.macos.nsview = (__bridge void *)self;
    config.userdata = (__bridge void *)self;
    config.scale_factor = self.window.backingScaleFactor ?: NSScreen.mainScreen.backingScaleFactor;
    config.font_size = options->render_profile == NULL ? 0 : options->render_profile->font_size;
    config.working_directory = options->working_directory;
    config.command = command;
    config.env_vars = (ghostty_env_var_s *)options->environment;
    config.env_var_count = options->environment_count;
    config.context = GHOSTTY_SURFACE_CONTEXT_WINDOW;

    self.surface = ghostty_surface_new(self.app, &config);
    free(command);
    if (self.surface == NULL) {
        gs_set_error(@"ghostty_surface_new failed");
        [self shutdown];
        return NO;
    }

    [self updateSurfaceGeometry];
    ghostty_surface_set_occlusion(self.surface, true);
    return YES;
}

- (void)shutdown {
    self.hostKeyInterceptor = NULL;
    self.hostKeyInterceptorUserdata = NULL;
    self.physicalInputGate = NULL;
    self.physicalInputGateUserdata = NULL;
    [self.interceptedPhysicalKeys removeAllIndexes];

    ghostty_surface_t surface = self.surface;
    self.surface = NULL;
    if (surface != NULL) {
        ghostty_surface_set_focus(surface, false);
        ghostty_surface_set_occlusion(surface, false);
        ghostty_surface_free(surface);
    }

    ghostty_app_t app = self.app;
    self.app = NULL;
    if (app != NULL) ghostty_app_free(app);
}

- (BOOL)acceptPhysicalInput:(ghostshell_terminal_physical_input_kind_v1)kind {
    if (self.bypassHostKeyInterceptor) return YES;
    if (self.physicalInputEpoch == UINT64_MAX) return NO;

    self.physicalInputEpoch++;
    if (self.physicalInputGate == NULL) return NO;

    const ghostshell_terminal_physical_input_event_v1 event = {
        .struct_size = sizeof(event),
        .version = GHOSTSHELL_TERMINAL_PHYSICAL_INPUT_EVENT_VERSION_1,
        .kind = (uint32_t)kind,
        .reserved = 0,
        .authority_epoch = self.physicalInputEpoch,
    };
    return self.physicalInputGate(self.physicalInputGateUserdata, &event);
}

- (void)dealloc {
    if (self.eventMonitor != nil) [NSEvent removeMonitor:self.eventMonitor];
    [self shutdown];
}

- (void)viewDidMoveToWindow {
    [super viewDidMoveToWindow];
    [self updateSurfaceGeometry];
}

- (void)setFrameSize:(NSSize)newSize {
    [super setFrameSize:newSize];
    [self updateSurfaceGeometry];
}

- (void)viewDidChangeBackingProperties {
    [super viewDidChangeBackingProperties];
    [self updateSurfaceGeometry];
}

- (void)updateSurfaceGeometry {
    CGFloat scale = self.window.backingScaleFactor ?: NSScreen.mainScreen.backingScaleFactor;
    [self updateSurfaceGeometryWithWidth:self.bounds.size.width
                                  height:self.bounds.size.height
                                   scale:scale];
}

- (void)updateSurfaceGeometryWithWidth:(double)width height:(double)height scale:(double)scale {
    if (self.surface == NULL || width <= 0 || height <= 0) return;

    double effectiveScale = scale > 0 ? scale : 1;
    self.contentScale = effectiveScale;
    ghostty_surface_set_content_scale(self.surface, effectiveScale, effectiveScale);
    ghostty_surface_set_size(
        self.surface,
        (uint32_t)llround(width * effectiveScale),
        (uint32_t)llround(height * effectiveScale));
}

- (BOOL)becomeFirstResponder {
    BOOL result = [super becomeFirstResponder];
    if (result && self.surface != NULL) {
        if (!self.searchField.hidden) {
            static const char endSearch[] = "end_search";
            ghostty_surface_binding_action(self.surface, endSearch, sizeof(endSearch) - 1);
            [self hideSearch];
        }
        ghostty_surface_set_focus(self.surface, true);
    }
    return result;
}

- (BOOL)resignFirstResponder {
    self.suppressNextLeftMouseUp = NO;
    if (self.surface != NULL) ghostty_surface_set_focus(self.surface, false);
    return [super resignFirstResponder];
}

- (void)updateTrackingAreas {
    [super updateTrackingAreas];
    if (self.terminalTrackingArea != nil) [self removeTrackingArea:self.terminalTrackingArea];

    self.terminalTrackingArea = [[NSTrackingArea alloc]
        initWithRect:NSZeroRect
             options:NSTrackingMouseMoved | NSTrackingActiveAlways | NSTrackingInVisibleRect
               owner:self
            userInfo:nil];
    [self addTrackingArea:self.terminalTrackingArea];
}

- (void)sendMousePosition:(NSEvent *)event {
    if (self.surface == NULL) return;
    NSPoint point = [self convertPoint:event.locationInWindow fromView:nil];
    ghostty_surface_mouse_pos(
        self.surface,
        point.x,
        self.bounds.size.height - point.y,
        gs_modifiers(event.modifierFlags));
}

- (NSEvent *)handleLocalEvent:(NSEvent *)event {
    if (event.type == NSEventTypeKeyUp) {
        if ((event.modifierFlags & NSEventModifierFlagCommand) != 0 &&
            self.window.firstResponder == self) {
            [self keyUp:event];
            return nil;
        }
        return event;
    }

    if (event.type != NSEventTypeLeftMouseDown || event.window != self.window) return event;

    NSPoint location = [self convertPoint:event.locationInWindow fromView:nil];
    if ([self hitTest:location] != self) return event;

    self.suppressNextLeftMouseUp = NO;
    if (self.window.firstResponder == self) return event;

    if (NSApplication.sharedApplication.isActive && self.window.isKeyWindow) {
        [self.window makeFirstResponder:self];
        self.suppressNextLeftMouseUp = YES;
        return nil;
    }

    [self.window makeFirstResponder:self];
    return event;
}

- (void)mouseMoved:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_MOVE]) return;
    [self sendMousePosition:event];
}

- (void)mouseDragged:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_DRAG]) return;
    [self sendMousePosition:event];
}

- (void)rightMouseDragged:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_DRAG]) return;
    [self sendMousePosition:event];
}

- (void)otherMouseDragged:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_DRAG]) return;
    [self sendMousePosition:event];
}

- (void)sendMouseButton:(NSEvent *)event
                   state:(ghostty_input_mouse_state_e)state
                  button:(ghostty_input_mouse_button_e)button {
    if (self.surface == NULL) return;
    [self sendMousePosition:event];
    ghostty_surface_mouse_button(self.surface, state, button, gs_modifiers(event.modifierFlags));
}

- (void)mouseDown:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_DOWN]) return;
    [self.window makeFirstResponder:self];
    [self sendMouseButton:event state:GHOSTTY_MOUSE_PRESS button:GHOSTTY_MOUSE_LEFT];
}

- (void)mouseUp:(NSEvent *)event {
    if (self.suppressNextLeftMouseUp) {
        self.suppressNextLeftMouseUp = NO;
        return;
    }
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_UP]) return;
    [self sendMouseButton:event state:GHOSTTY_MOUSE_RELEASE button:GHOSTTY_MOUSE_LEFT];
}

- (void)rightMouseDown:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_DOWN]) return;
    [self.window makeFirstResponder:self];
    [self sendMouseButton:event state:GHOSTTY_MOUSE_PRESS button:GHOSTTY_MOUSE_RIGHT];
}

- (void)rightMouseUp:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_UP]) return;
    [self sendMouseButton:event state:GHOSTTY_MOUSE_RELEASE button:GHOSTTY_MOUSE_RIGHT];
}

- (void)otherMouseDown:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_DOWN]) return;
    [self.window makeFirstResponder:self];
    [self sendMouseButton:event state:GHOSTTY_MOUSE_PRESS button:GHOSTTY_MOUSE_MIDDLE];
}

- (void)otherMouseUp:(NSEvent *)event {
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_BUTTON_UP]) return;
    [self sendMouseButton:event state:GHOSTTY_MOUSE_RELEASE button:GHOSTTY_MOUSE_MIDDLE];
}

- (void)scrollWheel:(NSEvent *)event {
    if (self.surface == NULL) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MOUSE_SCROLL]) return;

    double x = event.scrollingDeltaX;
    double y = event.scrollingDeltaY;
    int scrollMods = event.hasPreciseScrollingDeltas ? 1 : 0;
    if (event.hasPreciseScrollingDeltas) {
        x *= 2;
        y *= 2;
    }
    ghostty_surface_mouse_scroll(self.surface, x, y, scrollMods);
}

- (ghostty_input_key_s)keyEvent:(NSEvent *)event
                         action:(ghostty_input_action_e)action
           translationModifiers:(NSEventModifierFlags)translationModifiers {
    ghostty_input_key_s key = {0};
    key.action = action;
    key.keycode = event.keyCode;
    key.mods = gs_modifiers(event.modifierFlags);
    key.consumed_mods = gs_modifiers(
        translationModifiers & ~(NSEventModifierFlagControl | NSEventModifierFlagCommand));
    key.unshifted_codepoint = gs_first_codepoint([event charactersByApplyingModifiers:0]);
    return key;
}

- (ghostty_input_key_s)keyEvent:(NSEvent *)event action:(ghostty_input_action_e)action {
    return [self keyEvent:event action:action translationModifiers:event.modifierFlags];
}

- (NSString *)textForKeyEvent:(NSEvent *)event {
    NSString *text = event.characters;
    if (text.length != 1) return text;

    uint32_t codepoint = gs_first_codepoint(text);
    if (codepoint < 0x20) {
        return [event charactersByApplyingModifiers:
            event.modifierFlags & ~NSEventModifierFlagControl];
    }
    if (codepoint >= 0xF700 && codepoint <= 0xF8FF) return nil;
    return text;
}

- (NSEvent *)translationEventForKeyEvent:(NSEvent *)event {
    ghostty_input_mods_e translatedGhostty =
        ghostty_surface_key_translation_mods(self.surface, gs_modifiers(event.modifierFlags));
    uint32_t translated = (uint32_t)translatedGhostty;
    NSEventModifierFlags flags = event.modifierFlags;

    struct {
        NSEventModifierFlags eventFlag;
        uint32_t ghosttyFlag;
    } mappings[] = {
        {NSEventModifierFlagShift, GHOSTTY_MODS_SHIFT},
        {NSEventModifierFlagControl, GHOSTTY_MODS_CTRL},
        {NSEventModifierFlagOption, GHOSTTY_MODS_ALT},
        {NSEventModifierFlagCommand, GHOSTTY_MODS_SUPER},
    };
    for (size_t index = 0; index < sizeof(mappings) / sizeof(mappings[0]); index++) {
        if ((translated & mappings[index].ghosttyFlag) != 0) {
            flags |= mappings[index].eventFlag;
        } else {
            flags &= ~mappings[index].eventFlag;
        }
    }

    if (flags == event.modifierFlags) return event;

    return [NSEvent
        keyEventWithType:event.type
                location:event.locationInWindow
           modifierFlags:flags
               timestamp:event.timestamp
            windowNumber:event.windowNumber
                 context:nil
              characters:[event charactersByApplyingModifiers:flags] ?: @""
     charactersIgnoringModifiers:event.charactersIgnoringModifiers ?: @""
               isARepeat:event.isARepeat
                 keyCode:event.keyCode] ?: event;
}

- (void)sendKeyEvent:(NSEvent *)event
     translationEvent:(NSEvent *)translationEvent
                action:(ghostty_input_action_e)action
                  text:(NSString *)text
             composing:(BOOL)composing {
    ghostty_input_key_s key = [self keyEvent:event
                                         action:action
                           translationModifiers:translationEvent.modifierFlags];
    key.composing = composing;
    const char *utf8 = text.UTF8String;
    key.text = utf8 != NULL && (unsigned char)utf8[0] >= 0x20 ? utf8 : NULL;
    BOOL previousPhysicalDelivery = self.deliveringPhysicalKey;
    self.deliveringPhysicalKey = !self.bypassHostKeyInterceptor;
    @try {
        ghostty_surface_key(self.surface, key);
    } @finally {
        self.deliveringPhysicalKey = previousPhysicalDelivery;
    }
}

- (BOOL)interceptHostKeyDown:(NSEvent *)event {
    if (self.bypassHostKeyInterceptor) return NO;

    // A consumed press owns its repeats and matching release. Re-entering the
    // application resolver for auto-repeat can accidentally turn a held prefix
    // into a second sequence stroke. It can also send a repeat to libghostty
    // without the press that established the key state.
    if (event.isARepeat && [self.interceptedPhysicalKeys containsIndex:event.keyCode]) {
        return YES;
    }
    if (!event.isARepeat) {
        // Recover if AppKit did not deliver a prior release after focus moved.
        [self.interceptedPhysicalKeys removeIndex:event.keyCode];
    }
    if (self.hostKeyInterceptor == NULL) return NO;

    // Keep Shift when deriving the semantic character (for %, &, and similar
    // application bindings), while Control/Option/Command remain modifiers.
    NSString *characters = event.characters;
    uint32_t codepoint = gs_first_codepoint(characters);
    if (codepoint > 0 && codepoint < 0x20 &&
        (event.modifierFlags &
            (NSEventModifierFlagControl | NSEventModifierFlagOption | NSEventModifierFlagCommand)) != 0) {
        characters = [event charactersByApplyingModifiers:
            event.modifierFlags & NSEventModifierFlagShift];
        codepoint = gs_first_codepoint(characters);
    }
    ghostshell_terminal_host_key_event_v1 keyEvent = {
        .struct_size = sizeof(keyEvent),
        .version = GHOSTSHELL_TERMINAL_HOST_KEY_EVENT_VERSION_1,
        .physical_key = event.keyCode,
        .codepoint = codepoint,
        .modifiers = gs_host_key_modifiers(event.modifierFlags),
        .is_repeat = event.isARepeat ? 1U : 0U,
    };
    if (!self.hostKeyInterceptor(self.hostKeyInterceptorUserdata, &keyEvent)) return NO;

    [self.interceptedPhysicalKeys addIndex:event.keyCode];
    return YES;
}

- (void)keyDown:(NSEvent *)event {
    if (self.surface == NULL) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN]) return;
    if ([self interceptHostKeyDown:event]) return;

    NSEvent *translationEvent = [self translationEventForKeyEvent:event];
    ghostty_input_action_e action = event.isARepeat ? GHOSTTY_ACTION_REPEAT : GHOSTTY_ACTION_PRESS;
    if (!self.imeEnabled) {
        [self sendKeyEvent:event
           translationEvent:translationEvent
                      action:action
                        text:[self textForKeyEvent:translationEvent]
                   composing:NO];
        return;
    }

    BOOL hadMarkedText = self.markedText.length > 0;

    self.physicalInputDeniedDuringInterpretation = NO;
    self.keyTextAccumulator = [[NSMutableArray alloc] init];
    [self interpretKeyEvents:@[translationEvent]];
    if (self.physicalInputDeniedDuringInterpretation) {
        self.keyTextAccumulator = nil;
        self.physicalInputDeniedDuringInterpretation = NO;
        return;
    }
    [self syncPreeditClearingIfEmpty:hadMarkedText];

    NSArray<NSString *> *committedText = [self.keyTextAccumulator copy];
    self.keyTextAccumulator = nil;
    if (committedText.count > 0) {
        for (NSString *text in committedText) {
            [self sendKeyEvent:event
               translationEvent:translationEvent
                          action:action
                            text:text
                       composing:NO];
        }
        return;
    }

    [self sendKeyEvent:event
       translationEvent:translationEvent
                  action:action
                    text:[self textForKeyEvent:translationEvent]
               composing:hadMarkedText || self.markedText.length > 0];
}

- (void)keyUp:(NSEvent *)event {
    if (self.surface == NULL) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_KEY_UP]) {
        [self.interceptedPhysicalKeys removeIndex:event.keyCode];
        return;
    }
    if (!self.bypassHostKeyInterceptor &&
        [self.interceptedPhysicalKeys containsIndex:event.keyCode]) {
        [self.interceptedPhysicalKeys removeIndex:event.keyCode];
        return;
    }
    ghostty_surface_key(self.surface, [self keyEvent:event action:GHOSTTY_ACTION_RELEASE]);
}

- (void)flagsChanged:(NSEvent *)event {
    if (self.surface == NULL || self.markedText.length > 0) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MODIFIERS_CHANGED]) return;

    NSEventModifierFlags flag = 0;
    switch (event.keyCode) {
        case 0x38:
        case 0x3C: flag = NSEventModifierFlagShift; break;
        case 0x3B:
        case 0x3E: flag = NSEventModifierFlagControl; break;
        case 0x3A:
        case 0x3D: flag = NSEventModifierFlagOption; break;
        case 0x37:
        case 0x36: flag = NSEventModifierFlagCommand; break;
        case 0x39: flag = NSEventModifierFlagCapsLock; break;
        default: return;
    }

    ghostty_input_action_e action = (event.modifierFlags & flag) != 0
        ? GHOSTTY_ACTION_PRESS
        : GHOSTTY_ACTION_RELEASE;
    ghostty_input_key_s key = {0};
    key.action = action;
    key.keycode = event.keyCode;
    key.mods = gs_modifiers(event.modifierFlags);
    ghostty_surface_key(self.surface, key);
}

- (BOOL)hasMarkedText {
    return self.imeEnabled && self.markedText.length > 0;
}

- (NSRange)markedRange {
    return !self.imeEnabled || self.markedText.length == 0
        ? NSMakeRange(NSNotFound, 0)
        : NSMakeRange(0, self.markedText.length);
}

- (NSRange)selectedRange {
    if (self.surface == NULL) return NSMakeRange(NSNotFound, 0);

    ghostty_text_s text = {0};
    if (!ghostty_surface_read_selection(self.surface, &text)) {
        return NSMakeRange(NSNotFound, 0);
    }

    NSRange range = NSMakeRange((NSUInteger)text.offset_start, (NSUInteger)text.offset_len);
    ghostty_surface_free_text(self.surface, &text);
    return range;
}

- (void)setMarkedText:(id)string
        selectedRange:(NSRange)selectedRange
      replacementRange:(NSRange)replacementRange {
    (void)selectedRange;
    (void)replacementRange;
    if (!self.imeEnabled) {
        if (self.markedText.length > 0) [self.markedText.mutableString setString:@""];
        if (self.surface != NULL) ghostty_surface_preedit(self.surface, NULL, 0);
        return;
    }

    NSAttributedString *attributedValue = nil;
    if ([string isKindOfClass:NSAttributedString.class]) {
        attributedValue = (NSAttributedString *)string;
    } else if ([string isKindOfClass:NSString.class]) {
        attributedValue = [[NSAttributedString alloc] initWithString:(NSString *)string];
    } else {
        return;
    }
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_IME_PREEDIT]) {
        self.physicalInputDeniedDuringInterpretation = YES;
        return;
    }

    self.markedText = [[NSMutableAttributedString alloc]
        initWithAttributedString:attributedValue];

    if (self.keyTextAccumulator == nil) [self syncPreeditClearingIfEmpty:YES];
}

- (void)clearMarkedText {
    if (self.markedText.length == 0) return;
    [self.markedText.mutableString setString:@""];
    [self syncPreeditClearingIfEmpty:YES];
}

- (void)unmarkText {
    if (self.markedText.length == 0) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_IME_PREEDIT]) {
        self.physicalInputDeniedDuringInterpretation = YES;
        return;
    }
    [self clearMarkedText];
}

- (NSArray<NSAttributedStringKey> *)validAttributesForMarkedText {
    return @[];
}

- (NSAttributedString *)attributedSubstringForProposedRange:(NSRange)range
                                                actualRange:(NSRangePointer)actualRange {
    (void)range;
    if (actualRange != NULL) *actualRange = NSMakeRange(NSNotFound, 0);
    return nil;
}

- (NSUInteger)characterIndexForPoint:(NSPoint)point {
    (void)point;
    return 0;
}

- (NSRect)firstRectForCharacterRange:(NSRange)range actualRange:(NSRangePointer)actualRange {
    if (actualRange != NULL) *actualRange = range;
    if (self.surface == NULL || self.window == nil) return NSZeroRect;

    double x = 0;
    double y = 0;
    double width = 1;
    double height = 1;
    ghostty_surface_ime_point(self.surface, &x, &y, &width, &height);
    NSRect viewRect = NSMakeRect(
        x,
        self.bounds.size.height - y,
        MAX(width, 1),
        MAX(height, 1));
    NSRect windowRect = [self convertRect:viewRect toView:nil];
    return [self.window convertRectToScreen:windowRect];
}

- (void)insertText:(id)string replacementRange:(NSRange)replacementRange {
    (void)replacementRange;
    if (!self.imeEnabled) return;

    NSString *value = nil;
    if ([string isKindOfClass:NSAttributedString.class]) {
        value = ((NSAttributedString *)string).string;
    } else if ([string isKindOfClass:NSString.class]) {
        value = (NSString *)string;
    }
    if (value == nil) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_IME_COMMIT]) {
        self.physicalInputDeniedDuringInterpretation = YES;
        return;
    }

    [self clearMarkedText];
    if (self.keyTextAccumulator != nil) {
        [self.keyTextAccumulator addObject:value];
        return;
    }

    const char *utf8 = value.UTF8String;
    if (self.surface != NULL && utf8 != NULL) {
        ghostty_surface_text(
            self.surface,
            utf8,
            [value lengthOfBytesUsingEncoding:NSUTF8StringEncoding]);
    }
}

- (void)doCommandBySelector:(SEL)selector {
    (void)selector;
}

- (void)syncPreeditClearingIfEmpty:(BOOL)clearIfEmpty {
    if (self.surface == NULL) return;

    if (!self.imeEnabled) {
        if (clearIfEmpty) ghostty_surface_preedit(self.surface, NULL, 0);
        return;
    }

    NSString *value = self.markedText.string;
    const char *utf8 = value.UTF8String;
    if (value.length > 0 && utf8 != NULL) {
        ghostty_surface_preedit(
            self.surface,
            utf8,
            [value lengthOfBytesUsingEncoding:NSUTF8StringEncoding]);
    } else if (clearIfEmpty) {
        ghostty_surface_preedit(self.surface, NULL, 0);
    }
}

@end

static GhostShellTerminalView *gs_view(void *userdata) {
    return userdata == NULL ? nil : (__bridge GhostShellTerminalView *)userdata;
}

static void gs_wakeup(void *userdata) {
    GhostShellTerminalView *view = gs_view(userdata);
    if (view == nil) return;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (view.app != NULL) ghostty_app_tick(view.app);
    });
}

static GhostShellTerminalView *gs_target_view(ghostty_target_s target) {
    if (target.tag != GHOSTTY_TARGET_SURFACE || target.target.surface == NULL) return nil;
    return gs_view(ghostty_surface_userdata(target.target.surface));
}

static bool gs_action(ghostty_app_t app, ghostty_target_s target, ghostty_action_s action) {
    (void)app;
    if (action.tag == GHOSTTY_ACTION_RENDER && target.tag == GHOSTTY_TARGET_SURFACE) {
        ghostty_surface_t surface = target.target.surface;
        GhostShellTerminalView *view = gs_view(ghostty_surface_userdata(surface));
        dispatch_async(dispatch_get_main_queue(), ^{
            if (view.surface == surface) ghostty_surface_draw(surface);
        });
        return true;
    }

    GhostShellTerminalView *view = gs_target_view(target);
    switch (action.tag) {
        case GHOSTTY_ACTION_OPEN_URL: {
            if (view == nil) return false;
            if (view.linkPolicy == GHOSTSHELL_LINK_DISABLED) return true;

            ghostty_action_open_url_s openUrl = action.action.open_url;
            if (openUrl.url == NULL || openUrl.len == 0 ||
                openUrl.len > gs_max_shell_command_bytes ||
                openUrl.len > (uintptr_t)NSUIntegerMax) {
                return true;
            }

            NSString *value = [[NSString alloc]
                initWithBytes:openUrl.url
                       length:(NSUInteger)openUrl.len
                     encoding:NSUTF8StringEncoding];
            NSURL *url = nil;
            if (value != nil) {
                url = openUrl.kind == GHOSTTY_ACTION_OPEN_URL_KIND_UNKNOWN && !value.isAbsolutePath
                    ? [NSURL URLWithString:value]
                    : [NSURL fileURLWithPath:value];
            }
            if (url == nil) return true;

            __block BOOL shouldOpen = view.linkPolicy == GHOSTSHELL_LINK_OPEN;
            dispatch_block_t openBlock = ^{
                if (!shouldOpen) {
                    NSString *details = [NSString stringWithFormat:
                        @"The terminal requested to open this link in your default application:\n%@",
                        value];
                    shouldOpen = gs_confirm_user_action(@"Open Terminal Link?", details, @"Open Link");
                }
                if (shouldOpen) [NSWorkspace.sharedWorkspace openURL:url];
            };
            if (NSThread.isMainThread) {
                openBlock();
            } else {
                dispatch_sync(dispatch_get_main_queue(), openBlock);
            }
            return true;
        }
        case GHOSTTY_ACTION_RING_BELL: {
            if (view == nil) return false;
            uint32_t bellMode = view.bellMode;
            if (bellMode == GHOSTSHELL_BELL_DISABLED) return true;

            dispatch_async(dispatch_get_main_queue(), ^{
                if (bellMode == GHOSTSHELL_BELL_SYSTEM ||
                    bellMode == GHOSTSHELL_BELL_SYSTEM_AND_VISUAL) {
                    NSBeep();
                }
                if (bellMode == GHOSTSHELL_BELL_VISUAL ||
                    bellMode == GHOSTSHELL_BELL_SYSTEM_AND_VISUAL) {
                    [NSApplication.sharedApplication requestUserAttention:NSInformationalRequest];
                }
            });
            return true;
        }
        case GHOSTTY_ACTION_PWD: {
            if (view == nil || action.action.pwd.pwd == NULL) return false;
            NSString *workingDirectory = [NSString stringWithUTF8String:action.action.pwd.pwd];
            if (workingDirectory == nil) return false;
            dispatch_async(dispatch_get_main_queue(), ^{
                view.workingDirectory = workingDirectory;
            });
            return true;
        }
        case GHOSTTY_ACTION_START_SEARCH: {
            if (view == nil) return false;
            const char *rawNeedle = action.action.start_search.needle;
            NSString *needle = rawNeedle == NULL
                ? @""
                : [NSString stringWithUTF8String:rawNeedle] ?: @"";
            dispatch_async(dispatch_get_main_queue(), ^{
                view.searchTotal = -1;
                view.searchSelected = -1;
                [view showSearchWithNeedle:needle];
            });
            return true;
        }
        case GHOSTTY_ACTION_END_SEARCH: {
            if (view == nil) return false;
            dispatch_async(dispatch_get_main_queue(), ^{
                [view hideSearch];
            });
            return true;
        }
        case GHOSTTY_ACTION_SEARCH_TOTAL: {
            if (view == nil) return false;
            dispatch_async(dispatch_get_main_queue(), ^{
                view.searchTotal = action.action.search_total.total;
                [view updateSearchAccessibility];
            });
            return true;
        }
        case GHOSTTY_ACTION_SEARCH_SELECTED: {
            if (view == nil) return false;
            dispatch_async(dispatch_get_main_queue(), ^{
                view.searchSelected = action.action.search_selected.selected;
                [view updateSearchAccessibility];
            });
            return true;
        }
        case GHOSTTY_ACTION_SET_TITLE:
        case GHOSTTY_ACTION_CELL_SIZE:
        case GHOSTTY_ACTION_RENDERER_HEALTH:
        case GHOSTTY_ACTION_COMMAND_FINISHED:
        case GHOSTTY_ACTION_PROGRESS_REPORT:
        case GHOSTTY_ACTION_SCROLLBAR:
            return true;
        default:
            return false;
    }
}

static bool gs_read_clipboard(void *userdata, ghostty_clipboard_e clipboard, void *state) {
    if (clipboard != GHOSTTY_CLIPBOARD_STANDARD) return false;
    GhostShellTerminalView *view = gs_view(userdata);
    if (view.surface == NULL) return false;
    if (!view.bypassHostKeyInterceptor &&
        ![view acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_PASTE]) {
        return false;
    }

    NSString *text = [NSPasteboard.generalPasteboard stringForType:NSPasteboardTypeString];
    if (text.length == 0) return false;

    ghostty_surface_complete_clipboard_request(view.surface, text.UTF8String, state, false);
    return true;
}

static void gs_confirm_read_clipboard(
    void *userdata,
    const char *text,
    void *state,
    ghostty_clipboard_request_e request) {
    GhostShellTerminalView *view = gs_view(userdata);
    if (view.surface == NULL) return;
    ghostty_surface_t surface = view.surface;

    NSString *message = @"Allow Clipboard Access?";
    NSString *details = @"A program in this terminal requested access to the system clipboard.";
    NSString *approve = @"Allow";
    switch (request) {
        case GHOSTTY_CLIPBOARD_REQUEST_PASTE:
            message = @"Paste Potentially Unsafe Text?";
            details = @"The clipboard contains text that may execute one or more terminal commands.";
            approve = @"Paste";
            break;
        case GHOSTTY_CLIPBOARD_REQUEST_OSC_52_READ:
            details = @"A program in this terminal requested permission to read the system clipboard.";
            break;
        case GHOSTTY_CLIPBOARD_REQUEST_OSC_52_WRITE:
            details = @"A program in this terminal requested permission to replace the system clipboard.";
            break;
    }

    BOOL approved = gs_confirm_user_action(message, details, approve);
    if (view.surface != surface) return;
    ghostty_surface_complete_clipboard_request(surface, approved && text != NULL ? text : "", state, true);
}

static void gs_write_clipboard(
    void *userdata,
    ghostty_clipboard_e clipboard,
    const ghostty_clipboard_content_s *content,
    size_t count,
    bool confirm) {
    GhostShellTerminalView *view = gs_view(userdata);
    if (view.surface == NULL || clipboard != GHOSTTY_CLIPBOARD_STANDARD || content == NULL) return;
    if (confirm && !gs_confirm_user_action(
            @"Allow Clipboard Write?",
            @"A program in this terminal requested permission to replace the system clipboard.",
            @"Allow")) {
        return;
    }

    for (size_t index = 0; index < count; index++) {
        if (content[index].data == NULL) continue;
        NSString *value = [NSString stringWithUTF8String:content[index].data];
        if (value == nil) continue;

        [NSPasteboard.generalPasteboard clearContents];
        [NSPasteboard.generalPasteboard setString:value forType:NSPasteboardTypeString];
        return;
    }
}

static void gs_close_surface(void *userdata, bool process_alive) {
    GhostShellTerminalView *view = gs_view(userdata);
    if (view == nil) return;

    // Ghostty can request close from inside its callback stack. Teardown must
    // happen later so we never free the surface while Ghostty is still using it.
    dispatch_async(dispatch_get_main_queue(), ^{
        if (view.surface == NULL) return;
        if (!process_alive) {
            [view shutdown];
            return;
        }

        NSWindow *window = view.window;
        if (window == nil) return;

        NSAlert *alert = [[NSAlert alloc] init];
        alert.messageText = @"Close Terminal?";
        alert.informativeText =
            @"The terminal still has a running process. Closing it will kill that process.";
        [alert addButtonWithTitle:@"Close Terminal"];
        [alert addButtonWithTitle:@"Cancel"];
        [alert beginSheetModalForWindow:window completionHandler:^(NSModalResponse response) {
            if (response == NSAlertFirstButtonReturn) [view shutdown];
        }];
    });
}

bool ghostshell_ghostty_initialize(void) {
    if (gs_initialized) return true;
    if (gs_initialization_attempted) return false;
    gs_initialization_attempted = true;

    static char executable[] = "ghostshell";
    static char *arguments[] = {executable, NULL};
    if (ghostty_init(1, arguments) != GHOSTTY_SUCCESS) {
        gs_set_error(@"ghostty_init failed");
        return false;
    }

    gs_initialized = true;
    gs_last_error[0] = '\0';
    return true;
}

const char *ghostshell_ghostty_last_error(void) {
    return gs_last_error;
}

static void *gs_terminal_attach_impl(
    void *host_nsview,
    const ghostshell_terminal_options_v1 *options,
    BOOL loadUserConfig) {
    if (host_nsview == NULL) {
        gs_set_error(@"Avalonia did not provide an NSView host");
        return NULL;
    }
    if (!gs_validate_options(options)) return NULL;

    NSView *host = (__bridge NSView *)host_nsview;
    GhostShellTerminalView *view = [[GhostShellTerminalView alloc] initWithFrame:host.bounds];
    view.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
    [host addSubview:view];

    if (![view startWithOptions:options loadUserConfig:loadUserConfig]) {
        [view removeFromSuperview];
        return NULL;
    }

    [host.window makeFirstResponder:view];
    return (__bridge_retained void *)view;
}

void *ghostshell_terminal_attach(void *host_nsview, const char *working_directory) {
    const ghostshell_terminal_options_v1 options = {
        .struct_size = sizeof(options),
        .version = GHOSTSHELL_TERMINAL_OPTIONS_VERSION_1,
        .working_directory = working_directory,
    };
    return gs_terminal_attach_impl(host_nsview, &options, YES);
}

void *ghostshell_terminal_attach_v1(
    void *host_nsview,
    const ghostshell_terminal_options_v1 *options) {
    return gs_terminal_attach_impl(host_nsview, options, NO);
}

bool ghostshell_terminal_confirm_close(void *terminal) {
    if (terminal == NULL) return true;
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil) return true;

    __block bool approved = false;
    void (^confirm)(void) = ^{
        ghostty_surface_t surface = view.surface;
        if (surface == NULL || ghostty_surface_process_exited(surface)) {
            approved = true;
            return;
        }

        NSAlert *alert = [[NSAlert alloc] init];
        alert.messageText = @"Close GhostSHELL?";
        alert.informativeText =
            @"The terminal still has a running process. Closing GhostSHELL will kill that process.";
        [alert addButtonWithTitle:@"Close GhostSHELL"];
        [alert addButtonWithTitle:@"Cancel"];
        approved = [alert runModal] == NSAlertFirstButtonReturn;
    };
    if (NSThread.isMainThread) confirm(); else dispatch_sync(dispatch_get_main_queue(), confirm);
    return approved;
}

bool ghostshell_terminal_needs_close_confirmation(void *terminal) {
    if (terminal == NULL) return false;
    GhostShellTerminalView *view = gs_view(terminal);
    return view != nil
        && view.surface != NULL
        && ghostty_surface_needs_confirm_quit(view.surface);
}

bool ghostshell_terminal_reparent(void *terminal, void *host_nsview) {
    if (terminal == NULL || host_nsview == NULL) return false;
    GhostShellTerminalView *view = gs_view(terminal);
    NSView *host = (__bridge NSView *)host_nsview;
    if (view == nil || host == nil) return false;

    void (^reparent)(void) = ^{
        [view removeFromSuperview];
        view.frame = host.bounds;
        view.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        [host addSubview:view];
        [host.window makeFirstResponder:view];
    };
    if (NSThread.isMainThread) reparent(); else dispatch_sync(dispatch_get_main_queue(), reparent);
    return true;
}

void ghostshell_terminal_detach_view(void *terminal) {
    if (terminal == NULL) return;
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil) return;

    void (^detachView)(void) = ^{
        if (view.surface != NULL) ghostty_surface_set_focus(view.surface, false);
        [view removeFromSuperview];
    };
    if (NSThread.isMainThread) detachView(); else dispatch_sync(dispatch_get_main_queue(), detachView);
}

void ghostshell_terminal_detach(void *terminal) {
    if (terminal == NULL) return;

    GhostShellTerminalView *view = (__bridge_transfer GhostShellTerminalView *)terminal;
    void (^detach)(void) = ^{
        [view shutdown];
        [view removeFromSuperview];
    };
    if (NSThread.isMainThread) detach(); else dispatch_async(dispatch_get_main_queue(), detach);
}

bool ghostshell_terminal_set_host_key_interceptor_v1(
    void *terminal,
    ghostshell_terminal_host_key_interceptor_v1 interceptor,
    void *userdata) {
    if (terminal == NULL) return false;
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil) return false;

    void (^setInterceptor)(void) = ^{
        view.hostKeyInterceptor = interceptor;
        view.hostKeyInterceptorUserdata = userdata;
    };
    if (NSThread.isMainThread) {
        setInterceptor();
    } else {
        dispatch_sync(dispatch_get_main_queue(), setInterceptor);
    }
    return true;
}

bool ghostshell_terminal_set_physical_input_gate_v1(
    void *terminal,
    ghostshell_terminal_physical_input_gate_v1 gate,
    void *userdata) {
    if (terminal == NULL) return false;
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil) return false;

    void (^setGate)(void) = ^{
        view.physicalInputGate = gate;
        view.physicalInputGateUserdata = userdata;
    };
    if (NSThread.isMainThread) {
        setGate();
    } else {
        dispatch_sync(dispatch_get_main_queue(), setGate);
    }
    return true;
}

uint64_t ghostshell_terminal_input_epoch_v1(void *terminal) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil) return UINT64_MAX;

    __block uint64_t epoch = UINT64_MAX;
    void (^readEpoch)(void) = ^{
        epoch = view.physicalInputEpoch;
    };
    if (NSThread.isMainThread) {
        readEpoch();
    } else {
        dispatch_sync(dispatch_get_main_queue(), readEpoch);
    }
    return epoch;
}

void ghostshell_terminal_focus(void *terminal) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view != nil) [view.window makeFirstResponder:view];
}

void ghostshell_terminal_resize(void *terminal, double width, double height, double scale) {
    GhostShellTerminalView *view = gs_view(terminal);
    [view updateSurfaceGeometryWithWidth:width height:height scale:scale];
}

bool ghostshell_terminal_resize_grid_v1(
    void *terminal,
    uint32_t columns,
    uint32_t rows) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil || columns < 2 || columns > 1000 || rows < 1 || rows > 1000) {
        return false;
    }

    __block bool resized = false;
    void (^resizeGrid)(void) = ^{
        ghostty_surface_t surface = view.surface;
        if (surface == NULL) return;

        ghostty_surface_size_s current = ghostty_surface_size(surface);
        if (current.columns == 0 || current.rows == 0 ||
            current.cell_width_px == 0 || current.cell_height_px == 0) {
            return;
        }

        uint64_t usedWidth =
            (uint64_t)current.columns * (uint64_t)current.cell_width_px;
        uint64_t usedHeight =
            (uint64_t)current.rows * (uint64_t)current.cell_height_px;
        uint64_t horizontalRemainder =
            current.width_px > usedWidth ? current.width_px - usedWidth : 0;
        uint64_t verticalRemainder =
            current.height_px > usedHeight ? current.height_px - usedHeight : 0;
        uint64_t targetWidth =
            (uint64_t)columns * (uint64_t)current.cell_width_px
            + horizontalRemainder;
        uint64_t targetHeight =
            (uint64_t)rows * (uint64_t)current.cell_height_px
            + verticalRemainder;
        if (targetWidth == 0 || targetWidth > UINT32_MAX ||
            targetHeight == 0 || targetHeight > UINT32_MAX) {
            return;
        }

        ghostty_surface_set_size(
            surface,
            (uint32_t)targetWidth,
            (uint32_t)targetHeight);
        ghostty_surface_size_s updated = ghostty_surface_size(surface);
        resized = updated.columns == columns && updated.rows == rows;
    };
    if (NSThread.isMainThread) {
        resizeGrid();
    } else {
        dispatch_sync(dispatch_get_main_queue(), resizeGrid);
    }
    return resized;
}

static bool gs_send_programmatic_text_at_epoch(
    void *terminal,
    const char *utf8,
    size_t length,
    uint64_t expectedEpoch,
    bool enforceEpoch) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view.surface == NULL || utf8 == NULL || length == 0) return false;

    __block bool sent = false;
    void (^send)(void) = ^{
        if (view.surface == NULL ||
            (enforceEpoch && view.physicalInputEpoch != expectedEpoch)) {
            return;
        }
        gs_send_programmatic_text(view.surface, utf8, length);
        sent = true;
    };
    if (NSThread.isMainThread) send(); else dispatch_sync(dispatch_get_main_queue(), send);
    return sent;
}

void ghostshell_terminal_send_text(void *terminal, const char *utf8, size_t length) {
    (void)gs_send_programmatic_text_at_epoch(terminal, utf8, length, 0, false);
}

bool ghostshell_terminal_send_text_at_epoch_v1(
    void *terminal,
    const char *utf8,
    size_t length,
    uint64_t expected_epoch) {
    return gs_send_programmatic_text_at_epoch(
        terminal,
        utf8,
        length,
        expected_epoch,
        true);
}

static bool gs_paste_programmatic_text_at_epoch(
    void *terminal,
    const char *utf8,
    size_t length,
    uint64_t expectedEpoch,
    bool enforceEpoch) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view.surface == NULL || utf8 == NULL || length == 0) return false;

    __block bool pasted = false;
    void (^paste)(void) = ^{
        if (view.surface == NULL ||
            (enforceEpoch && view.physicalInputEpoch != expectedEpoch)) {
            return;
        }
        ghostty_surface_text(view.surface, utf8, length);
        pasted = true;
    };
    if (NSThread.isMainThread) paste(); else dispatch_sync(dispatch_get_main_queue(), paste);
    return pasted;
}

void ghostshell_terminal_paste_text(void *terminal, const char *utf8, size_t length) {
    (void)gs_paste_programmatic_text_at_epoch(terminal, utf8, length, 0, false);
}

bool ghostshell_terminal_paste_text_at_epoch_v1(
    void *terminal,
    const char *utf8,
    size_t length,
    uint64_t expected_epoch) {
    return gs_paste_programmatic_text_at_epoch(
        terminal,
        utf8,
        length,
        expected_epoch,
        true);
}

static bool gs_dispatch_programmatic_key(
    GhostShellTerminalView *view,
    gs_programmatic_key_spec spec,
    uint32_t modifiers,
    unichar character,
    bool semanticCharacter) {
    if (semanticCharacter) {
        char semanticText[] = {(char)spec.character, '\0'};
        ghostty_input_key_s key = {
            .action = GHOSTTY_ACTION_PRESS,
            .mods = gs_modifiers(gs_ns_modifiers(modifiers)),
            .consumed_mods = GHOSTTY_MODS_NONE,
            .keycode = spec.key_code,
            .text = semanticText,
            .unshifted_codepoint = spec.character,
            .composing = false,
        };

        // GhostSHELL's character chord is already semantic input. AppKit would
        // otherwise reinterpret the physical key code through the active input
        // source, which can turn ASCII into composed or non-Latin text. Submit
        // the complete key event directly to libghostty so its live terminal
        // modes still own both press and release encoding.
        ghostty_surface_key(view.surface, key);
        key.action = GHOSTTY_ACTION_RELEASE;
        key.text = NULL;
        ghostty_surface_key(view.surface, key);
        return true;
    }

    NSString *characters = [NSString stringWithCharacters:&character length:1];
    NSString *charactersIgnoringModifiers =
        [NSString stringWithCharacters:&spec.character length:1];
    NSEventModifierFlags flags = gs_ns_modifiers(modifiers);
    NSInteger windowNumber = view.window == nil ? 0 : view.window.windowNumber;
    NSTimeInterval timestamp = NSProcessInfo.processInfo.systemUptime;
    NSEvent *keyDown = [NSEvent
        keyEventWithType:NSEventTypeKeyDown
                location:NSZeroPoint
           modifierFlags:flags
               timestamp:timestamp
            windowNumber:windowNumber
                 context:nil
              characters:characters
 charactersIgnoringModifiers:charactersIgnoringModifiers
               isARepeat:NO
                 keyCode:spec.key_code];
    NSEvent *keyUp = [NSEvent
        keyEventWithType:NSEventTypeKeyUp
                location:NSZeroPoint
           modifierFlags:flags
               timestamp:timestamp
            windowNumber:windowNumber
                 context:nil
              characters:characters
 charactersIgnoringModifiers:charactersIgnoringModifiers
               isARepeat:NO
                 keyCode:spec.key_code];
    if (keyDown == nil || keyUp == nil) return false;

    // Reuse the exact path used by human input. libghostty therefore owns
    // application-cursor/keypad, modifyOtherKeys, and kitty-keyboard modes.
    BOOL previousBypass = view.bypassHostKeyInterceptor;
    view.bypassHostKeyInterceptor = YES;
    @try {
        [view keyDown:keyDown];
        [view keyUp:keyUp];
    } @finally {
        view.bypassHostKeyInterceptor = previousBypass;
    }
    return true;
}

static bool gs_send_programmatic_key_at_epoch(
    void *terminal,
    uint32_t key,
    uint32_t modifiers,
    uint64_t expectedEpoch,
    bool enforceEpoch) {
    GhostShellTerminalView *view = gs_view(terminal);
    gs_programmatic_key_spec spec = {0};
    if (view.surface == NULL ||
        !gs_terminal_modifiers_are_valid(modifiers) ||
        !gs_get_programmatic_key_spec(key, &spec)) {
        return false;
    }

    __block bool sent = false;
    void (^send)(void) = ^{
        if (view.surface == NULL ||
            (enforceEpoch && view.physicalInputEpoch != expectedEpoch)) {
            return;
        }

        sent = gs_dispatch_programmatic_key(
            view,
            spec,
            modifiers,
            spec.character,
            false);
    };
    if (NSThread.isMainThread) send(); else dispatch_sync(dispatch_get_main_queue(), send);
    return sent;
}

bool ghostshell_terminal_send_key(void *terminal, uint32_t key, uint32_t modifiers) {
    return gs_send_programmatic_key_at_epoch(terminal, key, modifiers, 0, false);
}

bool ghostshell_terminal_send_key_at_epoch_v1(
    void *terminal,
    uint32_t key,
    uint32_t modifiers,
    uint64_t expected_epoch) {
    return gs_send_programmatic_key_at_epoch(
        terminal,
        key,
        modifiers,
        expected_epoch,
        true);
}

bool ghostshell_terminal_send_chord_at_epoch_v1(
    void *terminal,
    uint32_t character,
    uint32_t modifier,
    uint64_t expected_epoch) {
    GhostShellTerminalView *view = gs_view(terminal);
    gs_programmatic_key_spec spec = {0};
    uint32_t keyModifiers = GHOSTSHELL_KEY_MODIFIER_NONE;
    if (view.surface == NULL ||
        !gs_get_programmatic_character_spec(character, &spec) ||
        !gs_get_character_chord_modifiers(modifier, &keyModifiers)) {
        return false;
    }

    __block bool sent = false;
    void (^send)(void) = ^{
        if (view.surface == NULL ||
            view.physicalInputEpoch != expected_epoch) {
            return;
        }

        sent = gs_dispatch_programmatic_key(
            view,
            spec,
            keyModifiers,
            spec.character,
            true);
    };
    if (NSThread.isMainThread) send(); else dispatch_sync(dispatch_get_main_queue(), send);
    return sent;
}

static bool gs_send_programmatic_mouse_at_epoch(
    void *terminal,
    uint32_t button,
    uint32_t event_kind,
    uint32_t column,
    uint32_t row,
    uint32_t modifiers,
    uint64_t expectedEpoch,
    bool enforceEpoch) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view.surface == NULL ||
        button > GHOSTSHELL_MOUSE_BUTTON_WHEEL_DOWN ||
        event_kind > GHOSTSHELL_MOUSE_EVENT_WHEEL_DOWN ||
        !gs_terminal_modifiers_are_valid(modifiers)) {
        return false;
    }

    __block bool sent = false;
    void (^send)(void) = ^{
        ghostty_surface_t surface = view.surface;
        if (surface == NULL ||
            (enforceEpoch && view.physicalInputEpoch != expectedEpoch) ||
            !ghostty_surface_mouse_captured(surface)) {
            return;
        }

        ghostty_surface_size_s size = ghostty_surface_size(surface);
        if (size.columns == 0 || size.rows == 0 ||
            size.cell_width_px == 0 || size.cell_height_px == 0 ||
            column >= size.columns || row >= size.rows) {
            return;
        }

        double scale = view.contentScale > 0 ? view.contentScale : 1;
        double horizontalRemainder = (double)size.width_px -
            ((double)size.columns * size.cell_width_px);
        double verticalRemainder = (double)size.height_px -
            ((double)size.rows * size.cell_height_px);
        double x = (MAX(horizontalRemainder, 0) / 2 +
            ((double)column + 0.5) * size.cell_width_px) / scale;
        double y = (MAX(verticalRemainder, 0) / 2 +
            ((double)row + 0.5) * size.cell_height_px) / scale;
        ghostty_input_mods_e ghosttyModifiers = gs_programmatic_mouse_modifiers(modifiers);
        ghostty_surface_mouse_pos(surface, x, y, ghosttyModifiers);

        switch (event_kind) {
            case GHOSTSHELL_MOUSE_EVENT_MOVE:
            case GHOSTSHELL_MOUSE_EVENT_DRAG:
                sent = true;
                return;
            case GHOSTSHELL_MOUSE_EVENT_WHEEL_UP:
                ghostty_surface_mouse_scroll(surface, 0, 1, 0);
                sent = true;
                return;
            case GHOSTSHELL_MOUSE_EVENT_WHEEL_DOWN:
                ghostty_surface_mouse_scroll(surface, 0, -1, 0);
                sent = true;
                return;
            case GHOSTSHELL_MOUSE_EVENT_DOWN:
            case GHOSTSHELL_MOUSE_EVENT_UP:
                break;
            default:
                return;
        }

        ghostty_input_mouse_button_e ghosttyButton = GHOSTTY_MOUSE_UNKNOWN;
        switch (button) {
            case GHOSTSHELL_MOUSE_BUTTON_LEFT: ghosttyButton = GHOSTTY_MOUSE_LEFT; break;
            case GHOSTSHELL_MOUSE_BUTTON_MIDDLE: ghosttyButton = GHOSTTY_MOUSE_MIDDLE; break;
            case GHOSTSHELL_MOUSE_BUTTON_RIGHT: ghosttyButton = GHOSTTY_MOUSE_RIGHT; break;
            default: return;
        }

        ghostty_input_mouse_state_e state = event_kind == GHOSTSHELL_MOUSE_EVENT_DOWN
            ? GHOSTTY_MOUSE_PRESS
            : GHOSTTY_MOUSE_RELEASE;
        sent = ghostty_surface_mouse_button(
            surface,
            state,
            ghosttyButton,
            ghosttyModifiers);
    };
    if (NSThread.isMainThread) send(); else dispatch_sync(dispatch_get_main_queue(), send);
    return sent;
}

bool ghostshell_terminal_send_mouse(
    void *terminal,
    uint32_t button,
    uint32_t event_kind,
    uint32_t column,
    uint32_t row,
    uint32_t modifiers) {
    return gs_send_programmatic_mouse_at_epoch(
        terminal,
        button,
        event_kind,
        column,
        row,
        modifiers,
        0,
        false);
}

bool ghostshell_terminal_send_mouse_at_epoch_v1(
    void *terminal,
    uint32_t button,
    uint32_t event_kind,
    uint32_t column,
    uint32_t row,
    uint32_t modifiers,
    uint64_t expected_epoch) {
    return gs_send_programmatic_mouse_at_epoch(
        terminal,
        button,
        event_kind,
        column,
        row,
        modifiers,
        expected_epoch,
        true);
}

bool ghostshell_terminal_read_screen_state_v1(
    void *terminal,
    ghostshell_terminal_screen_state_v1 *state) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view.surface == NULL || state == NULL ||
        state->struct_size < sizeof(*state) ||
        state->version != GHOSTSHELL_TERMINAL_SCREEN_STATE_VERSION_1) {
        return false;
    }

    __block bool read = false;
    void (^readState)(void) = ^{
        ghostty_surface_t surface = view.surface;
        if (surface == NULL) return;

        ghostty_surface_screen_state_s nativeState = ghostty_surface_screen_state(surface);
        state->rows = nativeState.rows;
        state->columns = nativeState.columns;
        state->cursor_row = nativeState.cursor_row;
        state->cursor_column = nativeState.cursor_column;
        state->alternate_screen = nativeState.alternate_screen;
        state->bracketed_paste = nativeState.bracketed_paste;
        state->mouse_captured = ghostty_surface_mouse_captured(surface) ? 1 : 0;
        read = true;
    };
    if (NSThread.isMainThread) readState(); else dispatch_sync(dispatch_get_main_queue(), readState);
    return read;
}

size_t ghostshell_terminal_read_working_directory(void *terminal, char *buffer, size_t capacity) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view == nil) {
        if (buffer != NULL && capacity > 0) buffer[0] = '\0';
        return 0;
    }

    __block size_t required = 0;
    void (^read)(void) = ^{
        NSString *workingDirectory = view.workingDirectory ?: @"";
        const char *utf8 = workingDirectory.UTF8String ?: "";
        required = [workingDirectory lengthOfBytesUsingEncoding:NSUTF8StringEncoding];
        if (buffer != NULL && capacity > 0) {
            size_t copied = MIN(required, capacity - 1);
            memcpy(buffer, utf8, copied);
            buffer[copied] = '\0';
        }
    };
    if (NSThread.isMainThread) read(); else dispatch_sync(dispatch_get_main_queue(), read);
    return required;
}

size_t ghostshell_terminal_read_screen(void *terminal, char *buffer, size_t capacity) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view.surface == NULL) {
        if (buffer != NULL && capacity > 0) buffer[0] = '\0';
        return 0;
    }

    __block size_t required = 0;
    void (^read)(void) = ^{
        ghostty_surface_t surface = view.surface;
        if (surface == NULL) {
            if (buffer != NULL && capacity > 0) buffer[0] = '\0';
            return;
        }

        ghostty_text_s text = {0};
        ghostty_selection_s selection = {
            .top_left = {
                .tag = GHOSTTY_POINT_SCREEN,
                .coord = GHOSTTY_POINT_COORD_TOP_LEFT,
            },
            .bottom_right = {
                .tag = GHOSTTY_POINT_SCREEN,
                .coord = GHOSTTY_POINT_COORD_BOTTOM_RIGHT,
            },
            .rectangle = false,
        };

        if (!ghostty_surface_read_text(surface, selection, &text)) {
            if (buffer != NULL && capacity > 0) buffer[0] = '\0';
            return;
        }

        required = text.text_len;
        if (buffer != NULL && capacity > 0) {
            size_t copied = MIN(required, capacity - 1);
            memcpy(buffer, text.text, copied);
            buffer[copied] = '\0';
        }
        ghostty_surface_free_text(surface, &text);
    };
    if (NSThread.isMainThread) read(); else dispatch_sync(dispatch_get_main_queue(), read);
    return required;
}

bool ghostshell_terminal_process_exited(void *terminal) {
    GhostShellTerminalView *view = gs_view(terminal);
    if (view.surface == NULL) return true;

    __block bool exited = false;
    void (^read)(void) = ^{
        exited = view.surface == NULL || ghostty_surface_process_exited(view.surface);
    };
    if (NSThread.isMainThread) read(); else dispatch_sync(dispatch_get_main_queue(), read);
    return exited;
}
