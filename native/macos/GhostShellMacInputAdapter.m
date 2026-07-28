#import "GhostShellMacInputAdapter.h"

#include <limits.h>

static ghostty_input_mods_e gs_input_modifiers(NSEventModifierFlags flags) {
    uint32_t mods = GHOSTTY_MODS_NONE;
    if ((flags & NSEventModifierFlagShift) != 0) mods |= GHOSTTY_MODS_SHIFT;
    if ((flags & NSEventModifierFlagControl) != 0) mods |= GHOSTTY_MODS_CTRL;
    if ((flags & NSEventModifierFlagOption) != 0) mods |= GHOSTTY_MODS_ALT;
    if ((flags & NSEventModifierFlagCommand) != 0) mods |= GHOSTTY_MODS_SUPER;
    if ((flags & NSEventModifierFlagCapsLock) != 0) mods |= GHOSTTY_MODS_CAPS;
    return (ghostty_input_mods_e)mods;
}

static uint32_t gs_input_host_key_modifiers(NSEventModifierFlags flags) {
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

static uint32_t gs_input_first_codepoint(NSString *text) {
    if (text.length == 0) return 0;

    NSData *utf32 = [text dataUsingEncoding:NSUTF32LittleEndianStringEncoding];
    if (utf32.length < sizeof(uint32_t)) return 0;

    uint32_t value = 0;
    [utf32 getBytes:&value length:sizeof(value)];
    return value;
}

@interface GhostShellMacInputAdapter ()

@property(nonatomic, weak) NSView *view;
@property(nonatomic, strong) NSMutableAttributedString *markedText;
@property(nonatomic, strong, nullable) NSMutableArray<NSString *> *keyTextAccumulator;
@property(nonatomic, assign, nullable) ghostshell_terminal_host_key_interceptor_v1
    hostKeyInterceptor;
@property(nonatomic, assign, nullable) void *hostKeyInterceptorUserdata;
@property(nonatomic, assign, nullable) ghostshell_terminal_physical_input_gate_v1
    physicalInputGate;
@property(nonatomic, assign, nullable) void *physicalInputGateUserdata;
@property(nonatomic, assign, readwrite) uint64_t inputEpoch;
@property(nonatomic, strong) NSMutableIndexSet *interceptedPhysicalKeys;
@property(nonatomic, assign) BOOL bypassHostKeyInterceptor;
@property(nonatomic, assign) BOOL deliveringPhysicalKey;
@property(nonatomic, assign) BOOL physicalInputDeniedDuringInterpretation;

@end

@implementation GhostShellMacInputAdapter

- (instancetype)initWithView:(NSView *)view {
    self = [super init];
    if (self == nil) return nil;

    self.view = view;
    self.imeEnabled = YES;
    self.markedText = [[NSMutableAttributedString alloc] init];
    self.interceptedPhysicalKeys = [[NSMutableIndexSet alloc] init];
    return self;
}

- (void)reset {
    self.surface = NULL;
    self.hostKeyInterceptor = NULL;
    self.hostKeyInterceptorUserdata = NULL;
    self.physicalInputGate = NULL;
    self.physicalInputGateUserdata = NULL;
    self.keyTextAccumulator = nil;
    self.physicalInputDeniedDuringInterpretation = NO;
    self.bypassHostKeyInterceptor = NO;
    self.deliveringPhysicalKey = NO;
    [self.interceptedPhysicalKeys removeAllIndexes];
    [self.markedText.mutableString setString:@""];
}

- (void)setHostKeyInterceptor:(ghostshell_terminal_host_key_interceptor_v1)interceptor
                     userdata:(void *)userdata {
    self.hostKeyInterceptor = interceptor;
    self.hostKeyInterceptorUserdata = userdata;
}

- (void)setPhysicalInputGate:(ghostshell_terminal_physical_input_gate_v1)gate
                     userdata:(void *)userdata {
    self.physicalInputGate = gate;
    self.physicalInputGateUserdata = userdata;
}

- (BOOL)acceptPhysicalInput:(ghostshell_terminal_physical_input_kind_v1)kind {
    if (self.bypassHostKeyInterceptor) return YES;
    if (self.inputEpoch == UINT64_MAX) return NO;

    self.inputEpoch++;
    if (self.physicalInputGate == NULL) return NO;

    const ghostshell_terminal_physical_input_event_v1 event = {
        .struct_size = sizeof(event),
        .version = GHOSTSHELL_TERMINAL_PHYSICAL_INPUT_EVENT_VERSION_1,
        .kind = (uint32_t)kind,
        .reserved = 0,
        .authority_epoch = self.inputEpoch,
    };
    return self.physicalInputGate(self.physicalInputGateUserdata, &event);
}

- (ghostty_input_key_s)keyEvent:(NSEvent *)event
                         action:(ghostty_input_action_e)action
           translationModifiers:(NSEventModifierFlags)translationModifiers {
    ghostty_input_key_s key = {0};
    key.action = action;
    key.keycode = event.keyCode;
    key.mods = gs_input_modifiers(event.modifierFlags);
    key.consumed_mods = gs_input_modifiers(
        translationModifiers & ~(NSEventModifierFlagControl | NSEventModifierFlagCommand));
    key.unshifted_codepoint =
        gs_input_first_codepoint([event charactersByApplyingModifiers:0]);
    return key;
}

- (ghostty_input_key_s)keyEvent:(NSEvent *)event
                         action:(ghostty_input_action_e)action {
    return [self keyEvent:event
                   action:action
     translationModifiers:event.modifierFlags];
}

- (NSString *)textForKeyEvent:(NSEvent *)event {
    NSString *text = event.characters;
    if (text.length != 1) return text;

    uint32_t codepoint = gs_input_first_codepoint(text);
    if (codepoint < 0x20) {
        return [event charactersByApplyingModifiers:
            event.modifierFlags & ~NSEventModifierFlagControl];
    }
    if (codepoint >= 0xF700 && codepoint <= 0xF8FF) return nil;
    return text;
}

- (NSEvent *)translationEventForKeyEvent:(NSEvent *)event {
    ghostty_input_mods_e translatedGhostty =
        ghostty_surface_key_translation_mods(
            self.surface,
            gs_input_modifiers(event.modifierFlags));
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
    if (event.isARepeat &&
        [self.interceptedPhysicalKeys containsIndex:event.keyCode]) {
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
    uint32_t codepoint = gs_input_first_codepoint(characters);
    if (codepoint > 0 && codepoint < 0x20 &&
        (event.modifierFlags &
            (NSEventModifierFlagControl |
             NSEventModifierFlagOption |
             NSEventModifierFlagCommand)) != 0) {
        characters = [event charactersByApplyingModifiers:
            event.modifierFlags & NSEventModifierFlagShift];
        codepoint = gs_input_first_codepoint(characters);
    }
    ghostshell_terminal_host_key_event_v1 keyEvent = {
        .struct_size = sizeof(keyEvent),
        .version = GHOSTSHELL_TERMINAL_HOST_KEY_EVENT_VERSION_1,
        .physical_key = event.keyCode,
        .codepoint = codepoint,
        .modifiers = gs_input_host_key_modifiers(event.modifierFlags),
        .is_repeat = event.isARepeat ? 1U : 0U,
    };
    if (!self.hostKeyInterceptor(self.hostKeyInterceptorUserdata, &keyEvent)) {
        return NO;
    }

    [self.interceptedPhysicalKeys addIndex:event.keyCode];
    return YES;
}

- (void)keyDown:(NSEvent *)event {
    if (self.surface == NULL) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_KEY_DOWN]) return;
    if ([self interceptHostKeyDown:event]) return;

    NSEvent *translationEvent = [self translationEventForKeyEvent:event];
    ghostty_input_action_e action =
        event.isARepeat ? GHOSTTY_ACTION_REPEAT : GHOSTTY_ACTION_PRESS;
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
    [self.view interpretKeyEvents:@[translationEvent]];
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
    ghostty_surface_key(
        self.surface,
        [self keyEvent:event action:GHOSTTY_ACTION_RELEASE]);
}

- (void)flagsChanged:(NSEvent *)event {
    if (self.surface == NULL || self.markedText.length > 0) return;
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_MODIFIERS_CHANGED]) {
        return;
    }

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
    key.mods = gs_input_modifiers(event.modifierFlags);
    ghostty_surface_key(self.surface, key);
}

- (void)deliverProgrammaticKeyDown:(NSEvent *)keyDown keyUp:(NSEvent *)keyUp {
    BOOL previousBypass = self.bypassHostKeyInterceptor;
    self.bypassHostKeyInterceptor = YES;
    @try {
        [self keyDown:keyDown];
        [self keyUp:keyUp];
    } @finally {
        self.bypassHostKeyInterceptor = previousBypass;
    }
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

    NSRange range =
        NSMakeRange((NSUInteger)text.offset_start, (NSUInteger)text.offset_len);
    ghostty_surface_free_text(self.surface, &text);
    return range;
}

- (void)setMarkedText:(id)string
        selectedRange:(NSRange)selectedRange
      replacementRange:(NSRange)replacementRange {
    (void)selectedRange;
    (void)replacementRange;
    if (!self.imeEnabled) {
        if (self.markedText.length > 0) {
            [self.markedText.mutableString setString:@""];
        }
        if (self.surface != NULL) {
            ghostty_surface_preedit(self.surface, NULL, 0);
        }
        return;
    }

    NSAttributedString *attributedValue = nil;
    if ([string isKindOfClass:NSAttributedString.class]) {
        attributedValue = (NSAttributedString *)string;
    } else if ([string isKindOfClass:NSString.class]) {
        attributedValue =
            [[NSAttributedString alloc] initWithString:(NSString *)string];
    } else {
        return;
    }
    if (![self acceptPhysicalInput:GHOSTSHELL_PHYSICAL_INPUT_IME_PREEDIT]) {
        self.physicalInputDeniedDuringInterpretation = YES;
        return;
    }

    self.markedText = [[NSMutableAttributedString alloc]
        initWithAttributedString:attributedValue];

    if (self.keyTextAccumulator == nil) {
        [self syncPreeditClearingIfEmpty:YES];
    }
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
    if (actualRange != NULL) {
        *actualRange = NSMakeRange(NSNotFound, 0);
    }
    return nil;
}

- (NSUInteger)characterIndexForPoint:(NSPoint)point {
    (void)point;
    return 0;
}

- (NSRect)firstRectForCharacterRange:(NSRange)range
                         actualRange:(NSRangePointer)actualRange {
    if (actualRange != NULL) *actualRange = range;
    if (self.surface == NULL || self.view.window == nil) return NSZeroRect;

    double x = 0;
    double y = 0;
    double width = 1;
    double height = 1;
    ghostty_surface_ime_point(self.surface, &x, &y, &width, &height);
    NSRect viewRect = NSMakeRect(
        x,
        self.view.bounds.size.height - y,
        MAX(width, 1),
        MAX(height, 1));
    NSRect windowRect = [self.view convertRect:viewRect toView:nil];
    return [self.view.window convertRectToScreen:windowRect];
}

- (void)insertText:(id)string replacementRange:(NSRange)replacementRange {
    (void)replacementRange;
    if (!self.imeEnabled) return;

    NSString *value = nil;
    if ([string isKindOfClass:NSAttributedString.class]) {
        // AppKit may commit the same mutable attributed string that backs our
        // marked text. Preserve its value before clearMarkedText mutates that
        // backing store, otherwise ordinary characters become an empty commit.
        value = [((NSAttributedString *)string).string copy];
    } else if ([string isKindOfClass:NSString.class]) {
        value = [(NSString *)string copy];
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
