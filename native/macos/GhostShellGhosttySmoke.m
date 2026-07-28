#import <AppKit/AppKit.h>

#include "GhostShellGhostty.h"
#include <limits.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>

static const char agentCommand[] =
    "printf 'GHOSTSHELL_AGENT_INPUT_OK TERM=%s PWD=%s\\n' \"$TERM\" \"$PWD\"; "
    "if infocmp \"$TERM\" >/dev/null 2>&1; then "
    "printf 'GHOSTSHELL_TERMINFO_OK\\n'; fi";
static const char exitCommand[] = "exit\n";
static const char enterAlternateCommand[] =
    "printf '\\033[?1049h\\033[?2004h'; printf 'GHOSTSHELL_ALT_SCREEN_OK\\n'";
static const char bracketedPasteCommand[] = "printf 'GHOSTSHELL_BRACKETED_PASTE_OK\\n'";
static const char leaveAlternateCommand[] = "printf '\\033[?2004l\\033[?1049l'";
static const char staleAgentCommand[] = "printf 'GHOSTSHELL_STALE_AGENT_BAD\\n'\n";
static const char guardedPasteCommand[] =
    "printf 'GHOSTSHELL_GUARDED_PASTE_OK\\n'";
static const char stalePasteCommand[] =
    "printf 'GHOSTSHELL_STALE_PASTE_BAD\\n'";
static const char chordCaptureCommand[] =
    "saved=$(stty -g); stty -echo -icanon -isig min 0 time 10; "
    "printf 'GHOSTSHELL_CHORD_READY\\n'; "
    "bytes=$(dd bs=1 count=3 2>/dev/null | od -An -tx1 | tr -d ' \\n'); "
    "stty \"$saved\"; printf 'GHOSTSHELL_CHORD_BYTES_%s\\n' \"$bytes\"";
static const char kittyChordCaptureCommand[] =
    "saved=$(stty -g); stty -echo -icanon -isig min 0 time 10; "
    "printf '\\033[>1uGHOSTSHELL_KITTY_CHORD_READY\\n'; "
    "bytes=$(dd bs=1 count=8 2>/dev/null | od -An -tx1 | tr -d ' \\n'); "
    "printf '\\033[<u'; stty \"$saved\"; "
    "printf 'GHOSTSHELL_KITTY_CHORD_BYTES_%s\\n' \"$bytes\"";
static const char injectionMarker[] = "/tmp/gs-inject";
static const char optionsMarker[] =
    "GHOSTSHELL_OPTIONS_OK ENV=<structured value> ARG=<q'$(touch /tmp/gs-inject)>";
static const char shellPrompt[] = "GHOSTSHELL_SMOKE_READY> ";
static int hostKeyInterceptState;
static int hostKeyCallbackCount;
static int hostKeyRepeatCallbackCount;
static ghostshell_terminal_host_key_event_v1 lastHostKeyEvent;

typedef struct {
    bool allow;
    uint64_t last_epoch;
    int callback_count;
    int kind_counts[GHOSTSHELL_PHYSICAL_INPUT_MOUSE_SCROLL + 1];
} physical_input_gate_state;

static physical_input_gate_state primaryPhysicalInputGate = {.allow = true};
static physical_input_gate_state secondaryPhysicalInputGate = {.allow = true};
static physical_input_gate_state idlePhysicalInputGate = {.allow = true};
static atomic_bool staleAgentSendCompleted;
static atomic_bool staleAgentSendResult;

static dispatch_time_t smoke_after_milliseconds(int64_t milliseconds) {
    // A freshly linked libghostty process can take longer to expose its first
    // shell prompt while macOS warms native resources. Keep the relative test
    // sequence tight, but do not type into a PTY that is still starting.
    const int64_t coldStartWarmupMilliseconds = 1000;
    return dispatch_time(
        DISPATCH_TIME_NOW,
        (milliseconds + coldStartWarmupMilliseconds) * NSEC_PER_MSEC);
}

static dispatch_time_t smoke_after_interrupt_milliseconds(int64_t milliseconds) {
    // Interrupt readiness is bounded to two seconds below. Keep every later
    // input outside that window so a loaded host cannot interleave another
    // smoke phase while zsh is still resetting its line editor.
    const int64_t interruptReadinessWindowMilliseconds = 2200;
    return smoke_after_milliseconds(
        milliseconds + interruptReadinessWindowMilliseconds);
}

static size_t substring_count(const char *text, const char *expected) {
    size_t count = 0;
    size_t expectedLength = strlen(expected);
    if (expectedLength == 0) return 0;

    for (const char *match = strstr(text, expected);
         match != NULL;
         match = strstr(match + expectedLength, expected)) {
        count++;
    }
    return count;
}

static bool intercept_host_key(
    void *userdata,
    const ghostshell_terminal_host_key_event_v1 *event) {
    (void)userdata;
    if (event == NULL ||
        event->struct_size < sizeof(*event) ||
        event->version != GHOSTSHELL_TERMINAL_HOST_KEY_EVENT_VERSION_1) {
        return false;
    }
    hostKeyCallbackCount++;
    if (event->is_repeat != 0) {
        hostKeyRepeatCallbackCount++;
        return false;
    }
    lastHostKeyEvent = *event;

    if (hostKeyInterceptState == 0 &&
        event->physical_key == 11 &&
        event->codepoint == 'b' &&
        event->modifiers == GHOSTSHELL_KEY_MODIFIER_CONTROL) {
        hostKeyInterceptState = 1;
        return true;
    }
    if (hostKeyInterceptState == 1 &&
        event->physical_key == 23 &&
        event->codepoint == '%' &&
        event->modifiers == GHOSTSHELL_KEY_MODIFIER_SHIFT) {
        hostKeyInterceptState = 2;
        return true;
    }
    if (hostKeyInterceptState == 2 &&
        event->physical_key == 105 &&
        event->codepoint == NSF13FunctionKey &&
        event->modifiers == GHOSTSHELL_KEY_MODIFIER_NONE) {
        hostKeyInterceptState = 3;
        return true;
    }

    return false;
}

static bool accept_physical_input(
    void *userdata,
    const ghostshell_terminal_physical_input_event_v1 *event) {
    physical_input_gate_state *state = userdata;
    if (state == NULL ||
        event == NULL ||
        event->struct_size < sizeof(*event) ||
        event->version != GHOSTSHELL_TERMINAL_PHYSICAL_INPUT_EVENT_VERSION_1 ||
        event->reserved != 0 ||
        event->kind > GHOSTSHELL_PHYSICAL_INPUT_MOUSE_SCROLL ||
        event->authority_epoch == 0 ||
        event->authority_epoch <= state->last_epoch) {
        return false;
    }

    state->last_epoch = event->authority_epoch;
    state->callback_count++;
    state->kind_counts[event->kind]++;
    return state->allow;
}

static bool screen_has_line(const char *screen, const char *expected) {
    size_t expectedLength = strlen(expected);
    const char *line = screen;
    while (*line != '\0') {
        const char *end = strchr(line, '\n');
        size_t lineLength = end == NULL ? strlen(line) : (size_t)(end - line);
        if (lineLength == expectedLength && memcmp(line, expected, expectedLength) == 0) return true;
        if (end == NULL) break;
        line = end + 1;
    }
    return false;
}

static bool ascii_key(char character,
                      unsigned short *keyCode,
                      NSEventModifierFlags *modifiers,
                      char *baseCharacter) {
    static const unsigned short letterKeyCodes[] = {
        0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46,
        45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6,
    };

    *modifiers = 0;
    if (character >= 'A' && character <= 'Z') {
        *modifiers = NSEventModifierFlagShift;
        *baseCharacter = (char)(character - 'A' + 'a');
        *keyCode = letterKeyCodes[*baseCharacter - 'a'];
        return true;
    }
    if (character >= 'a' && character <= 'z') {
        *baseCharacter = character;
        *keyCode = letterKeyCodes[character - 'a'];
        return true;
    }

    *baseCharacter = character;
    switch (character) {
        case ' ': *keyCode = 49; return true;
        case '\'': *keyCode = 39; return true;
        case '\\': *keyCode = 42; return true;
        case '-': *keyCode = 27; return true;
        case '_':
            *keyCode = 27;
            *modifiers = NSEventModifierFlagShift;
            *baseCharacter = '-';
            return true;
        default: return false;
    }
}

static void dispatch_key_event(NSApplication *application,
                               NSWindow *window,
                               NSEventType type,
                               unsigned short keyCode,
                               NSEventModifierFlags modifiers,
                               unichar character,
                               unichar baseCharacter,
                               bool isRepeat) {
    NSString *characters = [NSString stringWithCharacters:&character length:1];
    NSString *charactersIgnoringModifiers =
        [NSString stringWithCharacters:&baseCharacter length:1];
    NSEvent *event = [NSEvent
        keyEventWithType:type
                 location:NSZeroPoint
            modifierFlags:modifiers
                timestamp:NSProcessInfo.processInfo.systemUptime
             windowNumber:window.windowNumber
                  context:nil
               characters:characters
charactersIgnoringModifiers:charactersIgnoringModifiers
                 isARepeat:isRepeat
                   keyCode:keyCode];
    [application sendEvent:event];
}

static void dispatch_key(NSApplication *application,
                         NSWindow *window,
                         unsigned short keyCode,
                         NSEventModifierFlags modifiers,
                         unichar character,
                         unichar baseCharacter) {
    dispatch_key_event(
        application,
        window,
        NSEventTypeKeyDown,
        keyCode,
        modifiers,
        character,
        baseCharacter,
        false);
    dispatch_key_event(
        application,
        window,
        NSEventTypeKeyUp,
        keyCode,
        modifiers,
        character,
        baseCharacter,
        false);
}

static bool type_ascii(NSApplication *application, NSWindow *window, const char *text) {
    for (const char *cursor = text; *cursor != '\0'; cursor++) {
        unsigned short keyCode = 0;
        NSEventModifierFlags modifiers = 0;
        char baseCharacter = 0;
        if (!ascii_key(*cursor, &keyCode, &modifiers, &baseCharacter)) return false;
        dispatch_key(
            application,
            window,
            keyCode,
            modifiers,
            (unichar)(unsigned char)*cursor,
            (unichar)(unsigned char)baseCharacter);
    }
    return true;
}

static NSEvent *key_event(
    NSWindow *window,
    NSEventType type,
    unsigned short keyCode,
    NSEventModifierFlags modifiers) {
    return [NSEvent
        keyEventWithType:type
                location:NSZeroPoint
           modifierFlags:modifiers
               timestamp:NSProcessInfo.processInfo.systemUptime
            windowNumber:window.windowNumber
                 context:nil
              characters:@""
     charactersIgnoringModifiers:@""
               isARepeat:NO
                 keyCode:keyCode];
}

static NSEvent *mouse_event(
    NSWindow *window,
    NSEventType type,
    NSInteger eventNumber) {
    return [NSEvent
        mouseEventWithType:type
                  location:NSMakePoint(20, 20)
             modifierFlags:0
                 timestamp:NSProcessInfo.processInfo.systemUptime
              windowNumber:window.windowNumber
                   context:nil
               eventNumber:eventNumber
                clickCount:1
                  pressure:type == NSEventTypeLeftMouseUp ? 0 : 1];
}

static bool dispatch_pointer_and_modifier_inputs(NSWindow *window, NSView *terminalView) {
    NSEvent *modifierDown = key_event(
        window,
        NSEventTypeFlagsChanged,
        0x38,
        NSEventModifierFlagShift);
    NSEvent *modifierUp = key_event(window, NSEventTypeFlagsChanged, 0x38, 0);
    NSEvent *moved = mouse_event(window, NSEventTypeMouseMoved, 1);
    NSEvent *down = mouse_event(window, NSEventTypeLeftMouseDown, 2);
    NSEvent *dragged = mouse_event(window, NSEventTypeLeftMouseDragged, 3);
    NSEvent *up = mouse_event(window, NSEventTypeLeftMouseUp, 4);
    CGEventRef scrollEvent = CGEventCreateScrollWheelEvent(
        NULL,
        kCGScrollEventUnitPixel,
        1,
        1);
    NSEvent *scroll = scrollEvent == NULL ? nil : [NSEvent eventWithCGEvent:scrollEvent];
    if (scrollEvent != NULL) CFRelease(scrollEvent);
    if (modifierDown == nil ||
        modifierUp == nil ||
        moved == nil ||
        down == nil ||
        dragged == nil ||
        up == nil ||
        scroll == nil) {
        return false;
    }

    [terminalView flagsChanged:modifierDown];
    [terminalView flagsChanged:modifierUp];
    [terminalView mouseMoved:moved];
    [terminalView mouseDown:down];
    [terminalView mouseDragged:dragged];
    [terminalView mouseUp:up];
    [terminalView scrollWheel:scroll];
    return true;
}

static bool observed_every_physical_input_kind(void) {
    for (uint32_t kind = GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN;
         kind <= GHOSTSHELL_PHYSICAL_INPUT_MOUSE_SCROLL;
         kind++) {
        if (primaryPhysicalInputGate.kind_counts[kind] +
                secondaryPhysicalInputGate.kind_counts[kind] ==
            0) {
            return false;
        }
    }
    return true;
}

int main(void) {
    @autoreleasepool {
        NSApplication *application = NSApplication.sharedApplication;
        [application setActivationPolicy:NSApplicationActivationPolicyAccessory];

        NSWindow *window = [[NSWindow alloc]
            initWithContentRect:NSMakeRect(0, 0, 800, 500)
                      styleMask:NSWindowStyleMaskTitled
                        backing:NSBackingStoreBuffered
                          defer:NO];
        NSView *host = [[NSView alloc] initWithFrame:window.contentView.bounds];
        host.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        [window.contentView addSubview:host];
        [window makeKeyAndOrderFront:nil];

        unlink(injectionMarker);
        static const char startupScript[] =
            "printf 'GHOSTSHELL_OPTIONS_OK ENV=<%s> ARG=<%s>\\n' "
            "\"$GHOSTSHELL_SMOKE_ENV\" \"$1\"; exec /bin/zsh -f";
        static const char *launchArguments[] = {
            "-c",
            startupScript,
            "ghostshell-smoke",
            "q'$(touch /tmp/gs-inject)",
        };
        static const ghostshell_environment_variable_v1 environment[] = {
            {"GHOSTSHELL_SMOKE_ENV", "structured value"},
            {"PROMPT", shellPrompt},
        };
        static const uint32_t ansiPalette[] = {
            0x1F1C19, 0xD26060, 0x72B57B, 0xD1A85A,
            0x6B9BD2, 0xB17AC5, 0x66B8B2, 0xD8D2C8,
            0x69625B, 0xEE7B72, 0x91D39A, 0xEBC574,
            0x86B6EA, 0xCD98DF, 0x83D5CF, 0xFFF9F0,
        };
        const ghostshell_terminal_render_profile_v1 renderProfile = {
            .struct_size = sizeof(renderProfile),
            .font_size = 14,
            .cursor_style = GHOSTSHELL_CURSOR_STYLE_BAR,
            .cursor_blink = 0,
            .scrollback_limit_bytes = 16 * 1024 * 1024,
            .foreground_rgb = 0xE8E4DE,
            .background_rgb = 0x12100E,
            .cursor_rgb = 0xD9944D,
            .selection_background_rgb = 0x4A3828,
            .ansi_palette_rgb = ansiPalette,
            .ansi_palette_count = sizeof(ansiPalette) / sizeof(ansiPalette[0]),
            .font_family = "Menlo",
            .line_height = 1.2,
            .clipboard_read = GHOSTSHELL_CLIPBOARD_ALLOW,
            .clipboard_write = GHOSTSHELL_CLIPBOARD_DENY,
            .paste_safety = GHOSTSHELL_PASTE_PROTECT_UNSAFE_INCLUDING_BRACKETED,
            .link_policy = GHOSTSHELL_LINK_DISABLED,
            .ime_enabled = 0,
            .shell_integration = GHOSTSHELL_SHELL_INTEGRATION_DISABLED,
            .bell_mode = GHOSTSHELL_BELL_DISABLED,
            .compatibility = GHOSTSHELL_COMPATIBILITY_GHOSTTY,
        };
        static const char *terminalKeybindings[] = {
            "super+c=copy_to_clipboard",
            "super+v=paste_from_clipboard",
            "super+a=select_all",
            "alt+left=text:\\x1bb",
            "alt+right=text:\\x1bf",
            "alt+backspace=text:\\x17",
            "alt+delete=text:\\x1bd",
            "super+left=text:\\x01",
            "super+right=text:\\x05",
            "super+f=start_search",
            "super+plus=increase_font_size:1",
            "super+-=decrease_font_size:1",
            "super+0=reset_font_size",
            "super+k=clear_screen",
            "ctrl+c=text:\\x03",
            "ctrl+d=text:\\x04",
            "ctrl+l=text:\\x0c",
            "f13=text:echo GHOSTSHELL_KEYMAP_OK",
            "f14=start_search",
        };
        const ghostshell_terminal_options_v1 options = {
            .struct_size = sizeof(options),
            .version = GHOSTSHELL_TERMINAL_OPTIONS_VERSION_1,
            .working_directory = "/tmp",
            .executable = "/bin/sh",
            .arguments = launchArguments,
            .argument_count = sizeof(launchArguments) / sizeof(launchArguments[0]),
            .environment = environment,
            .environment_count = sizeof(environment) / sizeof(environment[0]),
            .render_profile = &renderProfile,
            .terminal_keybindings = terminalKeybindings,
            .terminal_keybinding_count = sizeof(terminalKeybindings) / sizeof(terminalKeybindings[0]),
            .terminal_keymap_present = 1,
        };
        void *terminal = ghostshell_terminal_attach_v1((__bridge void *)host, &options);
        if (terminal == NULL) {
            fprintf(stderr, "%s\n", ghostshell_ghostty_last_error());
            return 1;
        }
        if (!ghostshell_terminal_set_host_key_interceptor_v1(
                terminal,
                intercept_host_key,
                NULL)) {
            fprintf(stderr, "The host-key interceptor could not be installed.\n");
            ghostshell_terminal_detach(terminal);
            return 1;
        }
        if (!ghostshell_terminal_set_physical_input_gate_v1(
                terminal,
                accept_physical_input,
                &primaryPhysicalInputGate)) {
            fprintf(stderr, "The physical-input gate could not be installed.\n");
            ghostshell_terminal_detach(terminal);
            return 1;
        }

        NSView *secondaryHost = [[NSView alloc] initWithFrame:NSMakeRect(0, 0, 320, 200)];
        [window.contentView addSubview:secondaryHost];
        static const char *secondaryArguments[] = {
            "-c",
            "printf 'GHOSTSHELL_SECOND_APP_OK\\n'; exec /bin/zsh -f",
        };
        const ghostshell_terminal_render_profile_v1 legacyRenderProfile = {
            .struct_size = offsetof(ghostshell_terminal_render_profile_v1, font_family),
            .font_size = 12,
            .cursor_style = GHOSTSHELL_CURSOR_STYLE_BLOCK,
            .cursor_blink = 1,
            .scrollback_limit_bytes = 4 * 1024 * 1024,
            .foreground_rgb = 0xE8E4DE,
            .background_rgb = 0x12100E,
            .cursor_rgb = 0xD9944D,
            .selection_background_rgb = 0x4A3828,
            .ansi_palette_rgb = ansiPalette,
            .ansi_palette_count = sizeof(ansiPalette) / sizeof(ansiPalette[0]),
        };
        const ghostshell_terminal_options_v1 secondaryOptions = {
            .struct_size = offsetof(ghostshell_terminal_options_v1, terminal_keybindings),
            .version = GHOSTSHELL_TERMINAL_OPTIONS_VERSION_1,
            .working_directory = "/tmp",
            .executable = "/bin/sh",
            .arguments = secondaryArguments,
            .argument_count = sizeof(secondaryArguments) / sizeof(secondaryArguments[0]),
            .render_profile = &legacyRenderProfile,
        };
        void *secondaryTerminal = ghostshell_terminal_attach_v1(
            (__bridge void *)secondaryHost,
            &secondaryOptions);
        if (secondaryTerminal == NULL) {
            fprintf(stderr, "%s\n", ghostshell_ghostty_last_error());
            ghostshell_terminal_detach(terminal);
            return 1;
        }
        if (!ghostshell_terminal_set_physical_input_gate_v1(
                secondaryTerminal,
                accept_physical_input,
                &secondaryPhysicalInputGate)) {
            fprintf(stderr, "The secondary physical-input gate could not be installed.\n");
            ghostshell_terminal_detach(secondaryTerminal);
            ghostshell_terminal_detach(terminal);
            return 1;
        }

        NSView *idleHost = [[NSView alloc] initWithFrame:NSMakeRect(400, 0, 320, 200)];
        [window.contentView addSubview:idleHost];
        ghostshell_terminal_render_profile_v1 idleRenderProfile = renderProfile;
        idleRenderProfile.ime_enabled = 1;
        idleRenderProfile.shell_integration = GHOSTSHELL_SHELL_INTEGRATION_DETECT;
        const ghostshell_terminal_options_v1 idleOptions = {
            .struct_size = sizeof(idleOptions),
            .version = GHOSTSHELL_TERMINAL_OPTIONS_VERSION_1,
            .working_directory = "/tmp",
            .executable = "/bin/zsh",
            .environment = environment,
            .environment_count = sizeof(environment) / sizeof(environment[0]),
            .render_profile = &idleRenderProfile,
        };
        void *idleTerminal = ghostshell_terminal_attach_v1(
            (__bridge void *)idleHost,
            &idleOptions);
        if (idleTerminal == NULL) {
            fprintf(stderr, "%s\n", ghostshell_ghostty_last_error());
            ghostshell_terminal_detach(secondaryTerminal);
            ghostshell_terminal_detach(terminal);
            return 1;
        }
        if (!ghostshell_terminal_set_physical_input_gate_v1(
                idleTerminal,
                accept_physical_input,
                &idlePhysicalInputGate)) {
            fprintf(stderr, "The idle-shell physical-input gate could not be installed.\n");
            ghostshell_terminal_detach(idleTerminal);
            ghostshell_terminal_detach(secondaryTerminal);
            ghostshell_terminal_detach(terminal);
            return 1;
        }

        __block int result = 1;
        __block bool finished = false;
        __block bool alternateStatePassed = false;
        __block bool bracketedPastePassed = false;
        __block bool searchUiPassed = false;
        __block bool searchUiClosed = false;
        __block bool exactGridResizePassed = false;
        __block bool interruptReadinessPassed = false;
        __block bool idleClosePassed = false;
        void (^finish)(int) = ^(int exitCode) {
            if (finished) return;
            finished = true;
            result = exitCode;
            ghostshell_terminal_detach(idleTerminal);
            ghostshell_terminal_detach(secondaryTerminal);
            ghostshell_terminal_detach(terminal);
            [window close];
            [application stop:nil];
            NSEvent *wakeEvent = [NSEvent
                otherEventWithType:NSEventTypeApplicationDefined
                          location:NSZeroPoint
                     modifierFlags:0
                         timestamp:0
                      windowNumber:0
                           context:nil
                           subtype:0
                             data1:0
                             data2:0];
            [application postEvent:wakeEvent atStart:NO];
        };

        dispatch_after(smoke_after_milliseconds(500), dispatch_get_main_queue(), ^{
            if (!ghostshell_terminal_resize_grid_v1(terminal, 100, 30) ||
                ghostshell_terminal_resize_grid_v1(terminal, 1, 30)) {
                fprintf(stderr, "The exact terminal grid resize contract failed.\n");
                finish(1);
                return;
            }
            exactGridResizePassed = true;

            NSView *terminalView = (__bridge NSView *)terminal;
            [window makeFirstResponder:terminalView];
            __block bool interruptSent = false;
            __block size_t promptCountBeforeInterrupt = 0;
            __block uint32_t pollCount = 0;
            __block __weak dispatch_block_t weakSynchronizeInterrupt = nil;
            dispatch_block_t synchronizeInterrupt = ^{
                if (finished) return;

                char screen[65536] = {0};
                ghostshell_terminal_read_screen(terminal, screen, sizeof(screen));
                size_t promptCount = substring_count(screen, shellPrompt);
                if (!interruptSent && promptCount > 0) {
                    promptCountBeforeInterrupt = promptCount;
                    uint64_t epochBeforeDeniedInput =
                        ghostshell_terminal_input_epoch_v1(terminal);
                    primaryPhysicalInputGate.allow = false;
                    dispatch_key(application, window, 7, 0, 'x', 'x');
                    primaryPhysicalInputGate.allow = true;
                    if (ghostshell_terminal_input_epoch_v1(terminal) !=
                        epochBeforeDeniedInput + 2) {
                        fprintf(
                            stderr,
                            "Denied physical input did not advance the authority epoch.\n");
                        finish(1);
                        return;
                    }
                    if (!type_ascii(
                            application,
                            window,
                            "printf 'GHOSTSHELL_CANCELLED_BAD\\n'")) {
                        fprintf(
                            stderr,
                            "The keyboard smoke command contains an unmapped key.\n");
                        finish(1);
                        return;
                    }

                    dispatch_key(
                        application,
                        window,
                        8,
                        NSEventModifierFlagControl,
                        3,
                        'c');
                    interruptSent = true;
                } else if (interruptSent &&
                           promptCount > promptCountBeforeInterrupt) {
                    if (!type_ascii(
                            application,
                            window,
                            "printf 'GHOSTSHELL_KEY_INPUT_OK\\n'")) {
                        fprintf(
                            stderr,
                            "The keyboard smoke command contains an unmapped key.\n");
                        finish(1);
                        return;
                    }
                    dispatch_key(application, window, 36, 0, '\r', '\r');
                    interruptReadinessPassed = true;
                    return;
                }

                if (++pollCount >= 80) {
                    fprintf(
                        stderr,
                        "The shell did not acknowledge interrupt readiness "
                        "(sent=%d prompts-before=%zu prompts-now=%zu). Screen:\n%s\n",
                        interruptSent,
                        promptCountBeforeInterrupt,
                        promptCount,
                        screen);
                    finish(1);
                    return;
                }
                dispatch_block_t nextPoll = weakSynchronizeInterrupt;
                if (nextPoll == nil) {
                    fprintf(stderr, "The interrupt readiness poll was released early.\n");
                    finish(1);
                    return;
                }
                dispatch_after(
                    dispatch_time(DISPATCH_TIME_NOW, 25 * NSEC_PER_MSEC),
                    dispatch_get_main_queue(),
                    nextPoll);
            };
            weakSynchronizeInterrupt = synchronizeInterrupt;
            synchronizeInterrupt();
        });

        dispatch_after(smoke_after_interrupt_milliseconds(750), dispatch_get_main_queue(), ^{
            if (finished) return;
            id<NSTextInputClient> disabledTextInput = (__bridge id<NSTextInputClient>)terminal;
            if (!interruptReadinessPassed) {
                fprintf(
                    stderr,
                    "The post-interrupt smoke phase started before shell readiness.\n");
                finish(1);
                return;
            }
            [disabledTextInput setMarkedText:@"pending"
                               selectedRange:NSMakeRange(0, 7)
                             replacementRange:NSMakeRange(NSNotFound, 0)];
            [disabledTextInput insertText:@"printf 'GHOSTSHELL_IME_DISABLED_BAD\\n'"
                replacementRange:NSMakeRange(NSNotFound, 0)];
            if (disabledTextInput.hasMarkedText) {
                fprintf(stderr, "The disabled IME path retained marked text.\n");
                finish(1);
                return;
            }

            id<NSTextInputClient> textInput = (__bridge id<NSTextInputClient>)secondaryTerminal;
            NSString *textInputCommand =
                @"printf 'GHOSTSHELL_TEXT_INPUT_CLIENT_OK\\n'";
            [textInput setMarkedText:textInputCommand
                       selectedRange:NSMakeRange(0, textInputCommand.length)
                     replacementRange:NSMakeRange(NSNotFound, 0)];
            // AppKit may pass the mutable attributed string that also backs
            // the client's marked text. Exercise that aliasing behavior so
            // clearing preedit cannot erase the commit before it is sent.
            id inputAdapter =
                [(__bridge id)secondaryTerminal valueForKey:@"inputAdapter"];
            id aliasedMarkedText = [inputAdapter valueForKey:@"markedText"];
            [textInput insertText:aliasedMarkedText
                replacementRange:NSMakeRange(NSNotFound, 0)];
            NSView *terminalView = (__bridge NSView *)secondaryTerminal;
            [window makeFirstResponder:terminalView];
            dispatch_key(application, window, 36, 0, '\r', '\r');

            NSView *idleView = (__bridge NSView *)idleTerminal;
            [window makeFirstResponder:idleView];
            if (!type_ascii(
                    application,
                    window,
                    "printf 'GHOSTSHELL_IME_KEY_INPUT_OK\\n'")) {
                fprintf(stderr, "The IME keyboard smoke command contains an unmapped key.\n");
                finish(1);
                return;
            }
            dispatch_key(application, window, 36, 0, '\r', '\r');
        });

        dispatch_after(smoke_after_interrupt_milliseconds(800), dispatch_get_main_queue(), ^{
            if (finished) return;
            NSView *terminalView = (__bridge NSView *)terminal;
            [window makeFirstResponder:terminalView];
            uint64_t stalePasteEpoch =
                ghostshell_terminal_input_epoch_v1(terminal);
            if (!dispatch_pointer_and_modifier_inputs(window, terminalView)) {
                fprintf(stderr, "Unable to create physical pointer or modifier smoke events.\n");
                finish(1);
                return;
            }
            uint64_t advancedPasteEpoch =
                ghostshell_terminal_input_epoch_v1(terminal);
            if (advancedPasteEpoch <= stalePasteEpoch) {
                fprintf(
                    stderr,
                    "Physical input did not advance the guarded-paste authority epoch.\n");
                finish(1);
                return;
            }
            if (ghostshell_terminal_paste_text_at_epoch_v1(
                    terminal,
                    stalePasteCommand,
                    sizeof(stalePasteCommand) - 1,
                    stalePasteEpoch)) {
                fprintf(stderr, "The guarded paste API accepted a stale authority epoch.\n");
                finish(1);
                return;
            }
            if (ghostshell_terminal_send_chord_at_epoch_v1(
                    terminal,
                    'a',
                    GHOSTSHELL_CHARACTER_CHORD_CONTROL,
                    stalePasteEpoch)) {
                fprintf(stderr, "The guarded chord API accepted a stale authority epoch.\n");
                finish(1);
                return;
            }

            NSPasteboard *pasteboard = NSPasteboard.generalPasteboard;
            [pasteboard clearContents];
            if (![pasteboard
                    setString:@"echo GHOSTSHELL_PHYSICAL_PASTE_OK"
                      forType:NSPasteboardTypeString]) {
                fprintf(stderr, "Unable to prepare the physical paste smoke input.\n");
                finish(1);
                return;
            }
            dispatch_key(application, window, 9, NSEventModifierFlagCommand, 'v', 'v');
            dispatch_key(application, window, 36, 0, '\r', '\r');

            uint64_t staleEpoch = ghostshell_terminal_input_epoch_v1(terminal);
            dispatch_semaphore_t started = dispatch_semaphore_create(0);
            dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
                dispatch_semaphore_signal(started);
                bool sent = ghostshell_terminal_send_text_at_epoch_v1(
                    terminal,
                    staleAgentCommand,
                    sizeof(staleAgentCommand) - 1,
                    staleEpoch);
                atomic_store_explicit(
                    &staleAgentSendResult,
                    sent,
                    memory_order_relaxed);
                atomic_store_explicit(
                    &staleAgentSendCompleted,
                    true,
                    memory_order_release);
            });
            if (dispatch_semaphore_wait(
                    started,
                    dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC)) != 0) {
                fprintf(stderr, "The queued stale-agent smoke call did not start.\n");
                finish(1);
                return;
            }

            NSEvent *preemptingInput = mouse_event(
                window,
                NSEventTypeMouseMoved,
                5);
            if (preemptingInput == nil) {
                fprintf(stderr, "Unable to create the preempting physical input.\n");
                finish(1);
                return;
            }
            [terminalView mouseMoved:preemptingInput];
        });

        dispatch_after(smoke_after_interrupt_milliseconds(1000), dispatch_get_main_queue(), ^{
            if (finished) return;
            if (!atomic_load_explicit(
                    &staleAgentSendCompleted,
                    memory_order_acquire) ||
                atomic_load_explicit(
                    &staleAgentSendResult,
                    memory_order_relaxed)) {
                fprintf(stderr, "A physical event did not cancel the queued stale-agent input.\n");
                finish(1);
                return;
            }

            int callbackCountBeforeAgentInput = primaryPhysicalInputGate.callback_count;
            uint64_t agentEpoch = ghostshell_terminal_input_epoch_v1(terminal);
            if (!ghostshell_terminal_paste_text_at_epoch_v1(
                    terminal,
                    guardedPasteCommand,
                    sizeof(guardedPasteCommand) - 1,
                    agentEpoch)) {
                fprintf(stderr, "The guarded paste API rejected the current authority epoch.\n");
                finish(1);
                return;
            }
            if (!ghostshell_terminal_send_key_at_epoch_v1(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE,
                    agentEpoch)) {
                fprintf(stderr, "The guarded paste API could not submit its command.\n");
                finish(1);
                return;
            }
            if (!ghostshell_terminal_send_text_at_epoch_v1(
                    terminal,
                    agentCommand,
                    sizeof(agentCommand) - 1,
                    agentEpoch)) {
                fprintf(stderr, "The epoch-checked programmatic text API rejected current input.\n");
                finish(1);
                return;
            }
            if (!ghostshell_terminal_send_key_at_epoch_v1(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE,
                    agentEpoch)) {
                fprintf(stderr, "The programmatic key API rejected Enter.\n");
                finish(1);
                return;
            }
            if (ghostshell_terminal_send_mouse_at_epoch_v1(
                    terminal,
                    GHOSTSHELL_MOUSE_BUTTON_NONE,
                    GHOSTSHELL_MOUSE_EVENT_MOVE,
                    0,
                    0,
                    GHOSTSHELL_KEY_MODIFIER_NONE,
                    agentEpoch)) {
                fprintf(stderr, "Mouse input was accepted without terminal mouse capture.\n");
                finish(1);
                return;
            }
            if (primaryPhysicalInputGate.callback_count != callbackCountBeforeAgentInput ||
                ghostshell_terminal_input_epoch_v1(terminal) != agentEpoch) {
                fprintf(stderr, "Programmatic input re-entered the physical-input gate.\n");
                finish(1);
                return;
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(850), dispatch_get_main_queue(), ^{
            if (finished) return;
            NSView *terminalView = (__bridge NSView *)terminal;
            [window makeFirstResponder:terminalView];
            hostKeyCallbackCount = 0;
            hostKeyRepeatCallbackCount = 0;
            dispatch_key(application, window, 11, NSEventModifierFlagControl, 'b', 'b');
            dispatch_key(application, window, 23, NSEventModifierFlagShift, '%', '5');
            dispatch_key_event(
                application,
                window,
                NSEventTypeKeyDown,
                105,
                0,
                NSF13FunctionKey,
                NSF13FunctionKey,
                false);
            int physicalKeyDownsBeforeRepeat =
                primaryPhysicalInputGate.kind_counts[GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN];
            if (!ghostshell_terminal_set_host_key_interceptor_v1(terminal, NULL, NULL) ||
                !ghostshell_terminal_set_host_key_interceptor_v1(
                    terminal,
                    intercept_host_key,
                    NULL)) {
                fprintf(stderr, "The held-key interceptor replacement failed.\n");
                finish(1);
                return;
            }
            dispatch_key_event(
                application,
                window,
                NSEventTypeKeyDown,
                105,
                0,
                NSF13FunctionKey,
                NSF13FunctionKey,
                true);
            if (hostKeyInterceptState != 3 ||
                hostKeyCallbackCount != 3 ||
                hostKeyRepeatCallbackCount != 0 ||
                primaryPhysicalInputGate.kind_counts[GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN] !=
                    physicalKeyDownsBeforeRepeat + 1) {
                fprintf(
                    stderr,
                    "The host-key interceptor did not receive the application sequence "
                    "exactly once per physical press "
                    "(state=%d callbacks=%d repeat_callbacks=%d physical=%u "
                    "codepoint=%u modifiers=%u repeat=%u).\n",
                    hostKeyInterceptState,
                    hostKeyCallbackCount,
                    hostKeyRepeatCallbackCount,
                    lastHostKeyEvent.physical_key,
                    lastHostKeyEvent.codepoint,
                    lastHostKeyEvent.modifiers,
                    lastHostKeyEvent.is_repeat);
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(1100), dispatch_get_main_queue(), ^{
            if (finished) return;
            if (!ghostshell_terminal_send_key(
                    terminal,
                    GHOSTSHELL_KEY_F13,
                    GHOSTSHELL_KEY_MODIFIER_NONE)) {
                fprintf(stderr, "The selected native keymap did not accept its smoke binding.\n");
                finish(1);
                return;
            }
            // The programmatic F13 must bypass both intercepted key phases while
            // the human F13 remains held. Its second physical repeat is still
            // consumed, then the matching human release clears the held state.
            dispatch_key_event(
                application,
                window,
                NSEventTypeKeyDown,
                105,
                0,
                NSF13FunctionKey,
                NSF13FunctionKey,
                true);
            dispatch_key_event(
                application,
                window,
                NSEventTypeKeyUp,
                105,
                0,
                NSF13FunctionKey,
                NSF13FunctionKey,
                false);
            if (hostKeyCallbackCount != 3 || hostKeyRepeatCallbackCount != 0 ||
                !ghostshell_terminal_send_key(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE)) {
                fprintf(
                    stderr,
                    "The intercepted repeat/release path leaked or blocked programmatic PTY input "
                    "(callbacks=%d repeat_callbacks=%d).\n",
                    hostKeyCallbackCount,
                    hostKeyRepeatCallbackCount);
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(1200), dispatch_get_main_queue(), ^{
            if (finished) return;
            if (!ghostshell_terminal_send_key(
                    terminal,
                    GHOSTSHELL_KEY_F14,
                    GHOSTSHELL_KEY_MODIFIER_NONE)) {
                fprintf(stderr, "The native Find binding was rejected.\n");
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(1300), dispatch_get_main_queue(), ^{
            if (finished) return;
            NSView *terminalView = (__bridge NSView *)terminal;
            for (NSView *subview in terminalView.subviews) {
                if ([subview isKindOfClass:NSSearchField.class] && !subview.hidden) {
                    searchUiPassed = true;
                    break;
                }
            }
            if (!searchUiPassed) {
                fprintf(stderr, "The embedded native terminal search UI did not open.\n");
                finish(1);
                return;
            }
            // Exercise the actual NSSearchField responder/delegate path. A
            // surface-level Escape binding would mask a broken host search UI.
            dispatch_key(application, window, 53, 0, 0x1B, 0x1B);
        });

        dispatch_after(smoke_after_interrupt_milliseconds(1400), dispatch_get_main_queue(), ^{
            if (finished) return;
            NSView *terminalView = (__bridge NSView *)terminal;
            searchUiClosed = true;
            for (NSView *subview in terminalView.subviews) {
                if ([subview isKindOfClass:NSSearchField.class] && !subview.hidden) {
                    searchUiClosed = false;
                    break;
                }
            }
            if (!searchUiClosed) {
                fprintf(stderr, "The embedded native terminal search UI did not close.\n");
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(1500), dispatch_get_main_queue(), ^{
            if (finished) return;
            ghostshell_terminal_send_text(
                terminal,
                enterAlternateCommand,
                sizeof(enterAlternateCommand) - 1);
            if (!ghostshell_terminal_send_key(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE)) {
                fprintf(stderr, "The programmatic key API rejected alternate-screen Enter.\n");
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(2200), dispatch_get_main_queue(), ^{
            if (finished) return;
            ghostshell_terminal_screen_state_v1 state = {
                .struct_size = sizeof(state),
                .version = GHOSTSHELL_TERMINAL_SCREEN_STATE_VERSION_1,
            };
            char workingDirectory[PATH_MAX] = {0};
            ghostshell_terminal_read_working_directory(
                terminal,
                workingDirectory,
                sizeof(workingDirectory));
            alternateStatePassed = ghostshell_terminal_read_screen_state_v1(terminal, &state) &&
                state.rows > 0 &&
                state.columns > 0 &&
                state.cursor_row < state.rows &&
                state.cursor_column < state.columns &&
                state.alternate_screen == 1 &&
                state.bracketed_paste == 1 &&
                strcmp(workingDirectory, "/tmp") == 0;
            if (!alternateStatePassed) {
                fprintf(
                    stderr,
                    "Canonical state mismatch: rows=%u columns=%u cursor=%u,%u alt=%u paste=%u pwd=%s\n",
                    state.rows,
                    state.columns,
                    state.cursor_row,
                    state.cursor_column,
                    state.alternate_screen,
                    state.bracketed_paste,
                    workingDirectory);
                finish(1);
                return;
            }

            ghostshell_terminal_paste_text(
                terminal,
                bracketedPasteCommand,
                sizeof(bracketedPasteCommand) - 1);
            if (!ghostshell_terminal_send_key(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE)) {
                fprintf(stderr, "The programmatic key API rejected bracketed-paste Enter.\n");
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(3000), dispatch_get_main_queue(), ^{
            if (finished) return;
            char alternateScreen[65536] = {0};
            ghostshell_terminal_read_screen(terminal, alternateScreen, sizeof(alternateScreen));
            bracketedPastePassed = screen_has_line(
                alternateScreen,
                "GHOSTSHELL_BRACKETED_PASTE_OK");
            if (!bracketedPastePassed) {
                fprintf(stderr, "Bracketed paste marker was not rendered:\n%s\n", alternateScreen);
                finish(1);
                return;
            }

            ghostshell_terminal_send_text(
                terminal,
                leaveAlternateCommand,
                sizeof(leaveAlternateCommand) - 1);
            if (!ghostshell_terminal_send_key(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE)) {
                fprintf(stderr, "The programmatic key API rejected primary-screen Enter.\n");
                finish(1);
            }
        });

        dispatch_after(smoke_after_interrupt_milliseconds(3150), dispatch_get_main_queue(), ^{
            if (finished) return;
            int callbackCountBeforeChord = primaryPhysicalInputGate.callback_count;
            uint64_t chordEpoch = ghostshell_terminal_input_epoch_v1(terminal);
            if (ghostshell_terminal_send_chord_at_epoch_v1(
                    terminal,
                    'A',
                    GHOSTSHELL_CHARACTER_CHORD_CONTROL,
                    chordEpoch) ||
                ghostshell_terminal_send_chord_at_epoch_v1(
                    terminal,
                    'a',
                    UINT32_MAX,
                    chordEpoch)) {
                fprintf(stderr, "The guarded chord API accepted an invalid chord.\n");
                finish(1);
                return;
            }
            if (!ghostshell_terminal_send_text_at_epoch_v1(
                    terminal,
                    chordCaptureCommand,
                    sizeof(chordCaptureCommand) - 1,
                    chordEpoch) ||
                !ghostshell_terminal_send_key_at_epoch_v1(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE,
                    chordEpoch)) {
                fprintf(stderr, "Unable to start the guarded chord capture command.\n");
                finish(1);
                return;
            }

            __block uint32_t chordPollCount = 0;
            __block __weak dispatch_block_t weakSendChords = nil;
            dispatch_block_t sendChords = ^{
                if (finished) return;

                char screen[65536] = {0};
                ghostshell_terminal_read_screen(terminal, screen, sizeof(screen));
                if (screen_has_line(screen, "GHOSTSHELL_CHORD_READY")) {
                    if (!ghostshell_terminal_send_chord_at_epoch_v1(
                            terminal,
                            'c',
                            GHOSTSHELL_CHARACTER_CHORD_CONTROL,
                            chordEpoch) ||
                        !ghostshell_terminal_send_chord_at_epoch_v1(
                            terminal,
                            'a',
                            GHOSTSHELL_CHARACTER_CHORD_ALT,
                            chordEpoch)) {
                        fprintf(stderr, "The guarded chord API rejected a valid chord.\n");
                        finish(1);
                        return;
                    }
                    if (primaryPhysicalInputGate.callback_count != callbackCountBeforeChord ||
                        ghostshell_terminal_input_epoch_v1(terminal) != chordEpoch) {
                        fprintf(stderr, "Programmatic chord input re-entered the physical-input gate.\n");
                        finish(1);
                    }
                    return;
                }

                if (++chordPollCount >= 20) {
                    fprintf(
                        stderr,
                        "The terminal did not enter guarded chord capture mode. Screen:\n%s\n",
                        screen);
                    finish(1);
                    return;
                }
                dispatch_block_t nextPoll = weakSendChords;
                if (nextPoll == nil) {
                    fprintf(stderr, "The guarded chord readiness poll was released early.\n");
                    finish(1);
                    return;
                }
                dispatch_after(
                    dispatch_time(DISPATCH_TIME_NOW, 25 * NSEC_PER_MSEC),
                    dispatch_get_main_queue(),
                    nextPoll);
            };
            weakSendChords = sendChords;
            sendChords();
        });

        dispatch_after(smoke_after_interrupt_milliseconds(3500), dispatch_get_main_queue(), ^{
            if (finished) return;
            char screen[65536] = {0};
            ghostshell_terminal_read_screen(terminal, screen, sizeof(screen));
            if (!screen_has_line(screen, "GHOSTSHELL_CHORD_BYTES_031b61")) {
                fprintf(
                    stderr,
                    "The legacy guarded chord capture did not complete before Kitty mode. "
                    "Screen:\n%s\n",
                    screen);
                finish(1);
                return;
            }

            int callbackCountBeforeChord = primaryPhysicalInputGate.callback_count;
            uint64_t chordEpoch = ghostshell_terminal_input_epoch_v1(terminal);
            if (!ghostshell_terminal_send_text_at_epoch_v1(
                    terminal,
                    kittyChordCaptureCommand,
                    sizeof(kittyChordCaptureCommand) - 1,
                    chordEpoch) ||
                !ghostshell_terminal_send_key_at_epoch_v1(
                    terminal,
                    GHOSTSHELL_KEY_ENTER,
                    GHOSTSHELL_KEY_MODIFIER_NONE,
                    chordEpoch)) {
                fprintf(stderr, "Unable to start the Kitty guarded chord capture command.\n");
                finish(1);
                return;
            }

            __block uint32_t chordPollCount = 0;
            __block __weak dispatch_block_t weakSendChords = nil;
            dispatch_block_t sendChords = ^{
                if (finished) return;

                char currentScreen[65536] = {0};
                ghostshell_terminal_read_screen(
                    terminal,
                    currentScreen,
                    sizeof(currentScreen));
                if (screen_has_line(currentScreen, "GHOSTSHELL_KITTY_CHORD_READY")) {
                    if (!ghostshell_terminal_send_chord_at_epoch_v1(
                            terminal,
                            'c',
                            GHOSTSHELL_CHARACTER_CHORD_CONTROL,
                            chordEpoch) ||
                        !ghostshell_terminal_send_chord_at_epoch_v1(
                            terminal,
                            'a',
                            GHOSTSHELL_CHARACTER_CHORD_ALT,
                            chordEpoch)) {
                        fprintf(stderr, "The guarded chord API rejected a Kitty-mode chord.\n");
                        finish(1);
                        return;
                    }
                    if (primaryPhysicalInputGate.callback_count != callbackCountBeforeChord ||
                        ghostshell_terminal_input_epoch_v1(terminal) != chordEpoch) {
                        fprintf(
                            stderr,
                            "Kitty-mode programmatic chords re-entered the physical-input gate.\n");
                        finish(1);
                    }
                    return;
                }

                if (++chordPollCount >= 20) {
                    fprintf(
                        stderr,
                        "The terminal did not enter Kitty guarded chord capture mode. "
                        "Screen:\n%s\n",
                        currentScreen);
                    finish(1);
                    return;
                }
                dispatch_block_t nextPoll = weakSendChords;
                if (nextPoll == nil) {
                    fprintf(stderr, "The Kitty chord readiness poll was released early.\n");
                    finish(1);
                    return;
                }
                dispatch_after(
                    dispatch_time(DISPATCH_TIME_NOW, 25 * NSEC_PER_MSEC),
                    dispatch_get_main_queue(),
                    nextPoll);
            };
            weakSendChords = sendChords;
            sendChords();
        });

        dispatch_after(smoke_after_interrupt_milliseconds(5200), dispatch_get_main_queue(), ^{
            if (finished) return;
            char screen[65536] = {0};
            char secondaryScreen[4096] = {0};
            char idleScreen[4096] = {0};
            ghostshell_terminal_screen_state_v1 state = {
                .struct_size = sizeof(state),
                .version = GHOSTSHELL_TERMINAL_SCREEN_STATE_VERSION_1,
            };
            ghostshell_terminal_read_screen(terminal, screen, sizeof(screen));
            ghostshell_terminal_read_screen(
                secondaryTerminal,
                secondaryScreen,
                sizeof(secondaryScreen));
            ghostshell_terminal_read_screen(
                idleTerminal,
                idleScreen,
                sizeof(idleScreen));
            bool keyInputPassed =
                screen_has_line(screen, "GHOSTSHELL_KEY_INPUT_OK");
            bool cancelledInputPassed =
                !screen_has_line(screen, "GHOSTSHELL_CANCELLED_BAD");
            bool disabledImePassed =
                !screen_has_line(screen, "GHOSTSHELL_IME_DISABLED_BAD");
            bool staleAgentPassed =
                !screen_has_line(screen, "GHOSTSHELL_STALE_AGENT_BAD");
            bool stalePastePassed =
                !screen_has_line(screen, "GHOSTSHELL_STALE_PASTE_BAD");
            bool agentInputPassed = screen_has_line(
                screen,
                "GHOSTSHELL_AGENT_INPUT_OK TERM=xterm-ghostty PWD=/tmp");
            bool guardedPastePassed =
                screen_has_line(screen, "GHOSTSHELL_GUARDED_PASTE_OK");
            bool chordInputPassed =
                screen_has_line(screen, "GHOSTSHELL_CHORD_BYTES_031b61");
            bool kittyChordInputPassed = screen_has_line(
                screen,
                "GHOSTSHELL_KITTY_CHORD_BYTES_031b5b39373b3375");
            bool physicalPastePassed =
                screen_has_line(screen, "GHOSTSHELL_PHYSICAL_PASTE_OK");
            bool keymapPassed =
                screen_has_line(screen, "GHOSTSHELL_KEYMAP_OK");
            bool terminfoPassed =
                screen_has_line(screen, "GHOSTSHELL_TERMINFO_OK");
            bool optionsPassed = screen_has_line(screen, optionsMarker);
            bool secondaryStartupPassed =
                screen_has_line(secondaryScreen, "GHOSTSHELL_SECOND_APP_OK");
            bool secondaryTextInputPassed = screen_has_line(
                secondaryScreen,
                "GHOSTSHELL_TEXT_INPUT_CLIENT_OK");
            bool imeKeyInputPassed =
                screen_has_line(idleScreen, "GHOSTSHELL_IME_KEY_INPUT_OK");
            bool idleShellReady = strlen(idleScreen) > 0;
            bool idleNeedsClose =
                ghostshell_terminal_needs_close_confirmation(idleTerminal);
            idleClosePassed = idleShellReady && !idleNeedsClose;
            bool physicalKindsPassed = observed_every_physical_input_kind();
            bool stateReadPassed =
                ghostshell_terminal_read_screen_state_v1(terminal, &state);
            bool injectionPassed = access(injectionMarker, F_OK) != 0;
            if (keyInputPassed &&
                cancelledInputPassed &&
                disabledImePassed &&
                staleAgentPassed &&
                stalePastePassed &&
                agentInputPassed &&
                guardedPastePassed &&
                chordInputPassed &&
                kittyChordInputPassed &&
                physicalPastePassed &&
                keymapPassed &&
                terminfoPassed &&
                optionsPassed &&
                secondaryStartupPassed &&
                secondaryTextInputPassed &&
                imeKeyInputPassed &&
                alternateStatePassed &&
                bracketedPastePassed &&
                searchUiPassed &&
                searchUiClosed &&
                exactGridResizePassed &&
                interruptReadinessPassed &&
                idleClosePassed &&
                hostKeyInterceptState == 3 &&
                physicalKindsPassed &&
                stateReadPassed &&
                state.alternate_screen == 0 &&
                injectionPassed) {
                ghostshell_terminal_send_text(terminal, exitCommand, sizeof(exitCommand) - 1);
                dispatch_after(
                    dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC),
                    dispatch_get_main_queue(),
                    ^{
                    if (ghostshell_terminal_process_exited(terminal)) {
                        printf(
                            "Ghostty versioned launch, argv quoting, environment, full and legacy "
                            "render profiles, keyboard, text input, agent key/send/read, safe mouse, canonical "
                            "cursor/viewport/PWD, selected keymap, embedded search, alternate screen, "
                            "host-key interception, physical-input authority, stale-agent cancellation, "
                            "guarded paste, legacy and Kitty-mode guarded character chords, "
                            "stale-input rejection, bracketed paste, "
                            "idle-close detection, exact-grid resize, terminfo, and "
                            "exit smoke tests passed.\n");
                        fflush(stdout);
                        finish(0);
                    } else {
                        char exitScreen[65536] = {0};
                        ghostshell_terminal_read_screen(terminal, exitScreen, sizeof(exitScreen));
                        fprintf(
                            stderr,
                            "Ghostty terminal did not report process exit. Screen:\n%s\n",
                            exitScreen);
                        fflush(stderr);
                        finish(1);
                    }
                });
                return;
            } else {
                fprintf(
                    stderr,
                    "Ghostty terminal did not return the smoke marker "
                    "(key-input-check=%d cancelled-check=%d stale-paste-check=%d "
                    "agent-input-check=%d guarded-paste-check=%d chord-input-check=%d "
                    "kitty-chord-check=%d "
                    "physical-paste-check=%d keymap-check=%d terminfo-check=%d "
                    "options-check=%d secondary-start-check=%d secondary-input-check=%d "
                    "ime-key-input-check=%d "
                    "interrupt-ready-check=%d alt-check=%d paste-check=%d "
                    "search-open-check=%d search-close-check=%d idle-ready-check=%d "
                    "idle-needs-close=%d "
                    "physical-kinds=%d state-read=%d alt=%u paste=%u injection=%d). "
                    "Primary:\n%s\nSecondary:\n%s\nIdle:\n%s\n",
                    keyInputPassed,
                    cancelledInputPassed,
                    stalePastePassed,
                    agentInputPassed,
                    guardedPastePassed,
                    chordInputPassed,
                    kittyChordInputPassed,
                    physicalPastePassed,
                    keymapPassed,
                    terminfoPassed,
                    optionsPassed,
                    secondaryStartupPassed,
                    secondaryTextInputPassed,
                    imeKeyInputPassed,
                    interruptReadinessPassed,
                    alternateStatePassed,
                    bracketedPastePassed,
                    searchUiPassed,
                    searchUiClosed,
                    idleShellReady,
                    idleNeedsClose,
                    physicalKindsPassed,
                    stateReadPassed,
                    state.alternate_screen,
                    state.bracketed_paste,
                    injectionPassed ? 0 : 1,
                    screen,
                    secondaryScreen,
                    idleScreen);
                fflush(stderr);
            }

            finish(1);
        });

        [application run];
        return result;
    }
}
