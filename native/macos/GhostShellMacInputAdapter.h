#import <AppKit/AppKit.h>

#include "GhostShellGhostty.h"
#include "ghostty.h"

NS_ASSUME_NONNULL_BEGIN

// Owns the stateful macOS keyboard and text-composition boundary for one
// embedded Ghostty surface. The NSView remains AppKit's first responder and
// forwards its NSResponder/NSTextInputClient callbacks here.
@interface GhostShellMacInputAdapter : NSObject

@property(nonatomic, assign, nullable) ghostty_surface_t surface;
@property(nonatomic, assign, getter=isImeEnabled) BOOL imeEnabled;
@property(nonatomic, assign, readonly) uint64_t inputEpoch;

- (instancetype)initWithView:(NSView *)view NS_DESIGNATED_INITIALIZER;
- (instancetype)init NS_UNAVAILABLE;

- (void)reset;
- (void)setHostKeyInterceptor:
             (nullable ghostshell_terminal_host_key_interceptor_v1)interceptor
                         userdata:(nullable void *)userdata;
- (void)setPhysicalInputGate:
             (nullable ghostshell_terminal_physical_input_gate_v1)gate
                     userdata:(nullable void *)userdata;

- (BOOL)acceptPhysicalInput:(ghostshell_terminal_physical_input_kind_v1)kind;

- (void)keyDown:(NSEvent *)event;
- (void)keyUp:(NSEvent *)event;
- (void)flagsChanged:(NSEvent *)event;
- (void)deliverProgrammaticKeyDown:(NSEvent *)keyDown keyUp:(NSEvent *)keyUp;

- (BOOL)hasMarkedText;
- (NSRange)markedRange;
- (NSRange)selectedRange;
- (void)setMarkedText:(id)string
        selectedRange:(NSRange)selectedRange
      replacementRange:(NSRange)replacementRange;
- (void)clearMarkedText;
- (void)unmarkText;
- (NSArray<NSAttributedStringKey> *)validAttributesForMarkedText;
- (nullable NSAttributedString *)attributedSubstringForProposedRange:(NSRange)range
                                                         actualRange:(nullable NSRangePointer)actualRange;
- (NSUInteger)characterIndexForPoint:(NSPoint)point;
- (NSRect)firstRectForCharacterRange:(NSRange)range
                         actualRange:(nullable NSRangePointer)actualRange;
- (void)insertText:(id)string replacementRange:(NSRange)replacementRange;
- (void)doCommandBySelector:(SEL)selector;

@end

NS_ASSUME_NONNULL_END
