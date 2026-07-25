# ADR 0015: Durable Quick Terminal behavior and runtime fallback

- Status: Accepted
- Date: 2026-07-22

## Context

Quick Terminal is a process-wide desktop surface, but its hotkey, display placement, size, visual treatment, motion, focus dismissal, and session reuse are user choices that must survive restart. Keeping these choices in Avalonia window state would bypass SQLite export/import, make headless inspection impossible, and split the behavior from the durable definition model used by the rest of GhostSHELL.

Global shortcut registration can also fail independently of persistence because another application owns the gesture, the platform adapter rejects it, or the desktop backend does not support global shortcuts. A failed custom shortcut must not silently look active or unnecessarily make a previously working default shortcut inaccessible.

## Decision

`QuickTerminalSettings` is a schema-versioned durable definition stored by the common SQLite definition repository and closed `KnownDefinitionRegistry`. The catalog seeds one named default profile with Command + grave, the display containing the main GhostSHELL window, 55% height, 82% opacity, compositor blur requested at 24 px, slide animation, session reuse, and focus-loss dismissal.

The desktop controller observes catalog changes and applies them on the Avalonia UI thread. Height is resolved from the selected monitor's working area, so the payload is independent of scale and pixel dimensions. Explicit reduced motion disables slide animation. Focus-loss and reuse choices are enforced by the window/controller: when reuse is disabled, hiding detaches the native renderer, closes the session through the session host, and creates a fresh session on the next opening.

Shortcut registration is diagnostic state, not inferred from the saved value. The UI shows the configured gesture and the actual registration result. If a non-default configured gesture fails, the controller attempts Command + grave as an explicit fallback and reports which gesture, if any, remains active. Unsupported desktops continue to report a typed unavailable state.

The composition root selects Carbon on macOS, a dedicated `RegisterHotKey` message loop on Windows,
and an isolated `XGrabKey` connection on X11. Windows preserves Win32 conflict/error codes at the
adapter boundary. X11 grabs Caps Lock and Num Lock variants and maps `BadAccess` to a conflict.
Wayland is explicitly unavailable: an X11 grab through XWayland is not compositor-global, and no
portal implementation is claimed until its registration and activation lifecycle is verified across
the supported compositors. Escape uses a separate transient registration only while Quick Terminal
is active; its callback follows the same pending-paste cancellation path as a window Escape key.

Blur radius is durable intent. Avalonia currently exposes compositor blur as a capability hint on some backends rather than a portable numeric radius, so the controller requests blur when the radius is nonzero and the settings screen explains that the compositor may apply the nearest supported treatment.

## Consequences

- Quick Terminal behavior survives application restart and participates in guarded definition export/import.
- Settings changes update registration, placement, sizing, opacity, blur intent, motion, focus loss, and hidden-session reuse without restarting GhostSHELL.
- Meta/Command + grave remains the safe platform-mapped fallback when a custom gesture is invalid or conflicts and the default remains available.
- Windows and real X11 desktops expose typed shortcut conflicts; Wayland and headless Linux sessions expose typed unsupported diagnostics.
- Current monitor choices are `MainWindow` and `Primary`; cursor-following placement and native virtual-space policy require dedicated platform adapters.
- Session reuse is implemented across hide/show cycles. Restoring a Quick Terminal process after an application crash remains part of the broader runtime-snapshot recovery work.
- System-wide reduced-motion detection is not yet exposed by Avalonia as a cross-platform application port; the explicit reduced-motion setting always wins today and can later be ORed with host accessibility state.

## Alternatives rejected

- Storing values only in XAML or process memory would lose them on restart and exclude future CLI/ACP clients.
- Treating a persisted hotkey as proof of registration would hide conflicts and unsupported backends.
- Silently replacing a failed custom gesture in SQLite with the default would destroy user intent and prevent guided recovery.
- Adding platform pixel coordinates to the durable payload would make the settings invalid when monitors or display scale change.
