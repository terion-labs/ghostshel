// The terminal's corner mask, tested without a window.
//
// This geometry has been got wrong four times: once by rounding a layer whose
// Metal content ignores cornerRadius, once by rounding all four corners of a view
// that sits below a header, once by assuming AppKit's default orientation, and
// once by running each edge into its corner so the arc had no room and came out
// square. It is a pure function so the answer can be checked rather than argued
// about, and it is compiled from the shim's own source so the two cannot drift.
#import <CoreGraphics/CoreGraphics.h>
#import <Foundation/Foundation.h>
#include <stdbool.h>

CGPathRef gs_host_corner_path(
    CGRect bounds,
    double topLeft,
    double topRight,
    double bottomRight,
    double bottomLeft,
    bool flipped) {
    CGFloat limit = MIN(CGRectGetWidth(bounds), CGRectGetHeight(bounds)) / 2;
    CGFloat tl = MIN(MAX(0, topLeft), limit);
    CGFloat tr = MIN(MAX(0, topRight), limit);
    CGFloat br = MIN(MAX(0, bottomRight), limit);
    CGFloat bl = MIN(MAX(0, bottomLeft), limit);
    if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0) return NULL;

    CGFloat minX = CGRectGetMinX(bounds), maxX = CGRectGetMaxX(bounds);
    // Which edge is visually the top depends on the view's orientation, not on
    // AppKit's default. A path built against the wrong one rounds exactly the two
    // corners it was meant to leave square.
    CGFloat topY = flipped ? CGRectGetMinY(bounds) : CGRectGetMaxY(bounds);
    CGFloat bottomY = flipped ? CGRectGetMaxY(bounds) : CGRectGetMinY(bounds);

    // Each edge stops a radius short of the corner it is heading for, then the
    // arc turns through it. Running the line all the way into the corner first
    // leaves the arc no room and the corner comes out square.
    CGFloat down = (bottomY > topY) ? 1 : -1;

    CGMutablePathRef path = CGPathCreateMutable();
    CGPathMoveToPoint(path, NULL, minX + tl, topY);
    CGPathAddLineToPoint(path, NULL, maxX - tr, topY);
    if (tr > 0) CGPathAddArcToPoint(path, NULL, maxX, topY, maxX, topY + down * tr, tr);
    CGPathAddLineToPoint(path, NULL, maxX, bottomY - down * br);
    if (br > 0) CGPathAddArcToPoint(path, NULL, maxX, bottomY, maxX - br, bottomY, br);
    CGPathAddLineToPoint(path, NULL, minX + bl, bottomY);
    if (bl > 0) CGPathAddArcToPoint(path, NULL, minX, bottomY, minX, bottomY - down * bl, bl);
    CGPathAddLineToPoint(path, NULL, minX, topY + down * tl);
    if (tl > 0) CGPathAddArcToPoint(path, NULL, minX, topY, minX + tl, topY, tl);
    CGPathCloseSubpath(path);
    return path;
}

static int failures = 0;

static void expect(const char *what, bool actual, bool wanted) {
    if (actual != wanted) {
        printf("FAIL %s (got %d, wanted %d)\n", what, actual, wanted);
        failures++;
    }
}

static bool inside(CGPathRef p, CGFloat x, CGFloat y) {
    return CGPathContainsPoint(p, NULL, CGPointMake(x, y), false);
}

int main(void) {
    CGRect b = CGRectMake(0, 0, 200, 100);
    CGFloat r = 12;

    // A terminal below a panel header: only the bottom corners are at the
    // panel's edge, so only they may be carved away.
    CGPathRef up = gs_host_corner_path(b, 0, 0, r, r, false);
    expect("unflipped bottom-left carved",  !inside(up, 1, 1),   true);
    expect("unflipped bottom-right carved", !inside(up, 199, 1), true);
    expect("unflipped top-left square",      inside(up, 1, 99),  true);
    expect("unflipped top-right square",     inside(up, 199, 99),true);

    CGPathRef flip = gs_host_corner_path(b, 0, 0, r, r, true);
    expect("flipped bottom-left carved",  !inside(flip, 1, 99),  true);
    expect("flipped bottom-right carved", !inside(flip, 199, 99),true);
    expect("flipped top-left square",      inside(flip, 1, 1),   true);
    expect("flipped top-right square",     inside(flip, 199, 1), true);

    // A full-bleed surface rounds all four.
    CGPathRef all = gs_host_corner_path(b, r, r, r, r, false);
    expect("uniform bottom-left carved", !inside(all, 1, 1),    true);
    expect("uniform top-right carved",   !inside(all, 199, 99), true);
    expect("uniform centre kept",         inside(all, 100, 50), true);

    expect("square asks for no mask", gs_host_corner_path(b, 0, 0, 0, 0, false) == NULL, true);

    if (failures == 0) printf("corner mask geometry: ok\n");
    return failures == 0 ? 0 : 1;
}
