# GhostSHELL design QA

This is the living visual-verification record for the desktop implementation. The editable Pencil sources `design/design.pen` and `untitled.pen` are byte-identical; the exported PNGs in `design/` are the visual comparison references. The user-provided HTML exports in `design/html-export/` are the structural source for exact hierarchy, labels, dimensions, spacing, radii, colors, and typography when the Pencil connector is unavailable.

The HTML source confirms the shared baseline used by the build: a 1440 × 900 shell, 44 px top chrome, 244 px launcher/settings sidebar, 526 px Quick Terminal panel, predominantly 8 px control/card radii, SF Pro-style application chrome, JetBrains Mono terminal/data surfaces, `#111111`/`#18181B`/`#2E2E2E` neutral layers, `#B8B9B6` muted text, and `#FF8400` as the supplied example accent. Platform adaptation and host-accent following remain intentional product requirements.

## M1 durable desktop shell — updated 2026-07-23

### Comparison setup

The workspace, appearance, and layout-designer checks place the Pencil reference and the running desktop build in a single comparison artifact. Each side is normalized to the same 2880 × 1800 review frame at 2× display scale; the combined files add a narrow label strip above those frames. Terminal settings and diagnostics are standalone app-window captures because the exported Pencil set has no dedicated frame for either route.

| Surface | Pencil reference | Running build / comparison |
| --- | --- | --- |
| Workspace editor | `design/screen-workspaces.png` | `.codex-audit/m1-workspace-editor-combined.png` |
| Appearance settings | `design/screen-settings-appearance.png` | `.codex-audit/m1-appearance-combined.png` |
| Layout designer | `artifacts/design-qa/layout-designer-reference-1440x900.png` (from `design/html-export/layiutdesign.html`) | `artifacts/design-qa/layout-designer-comparison-1440x900.png` |
| Terminal settings | No dedicated exported frame | `.codex-audit/m1-terminal-settings-keymap-live.png` |
| Data and diagnostics | No dedicated exported frame | `.codex-audit/m1-diagnostics-success-fixed.png` |

The running build uses the user's durable local definitions and real settings state. It does not reproduce fictional connection records or pretend that deferred agent features are available.

### Visual and interaction checks

- [x] Workspace, settings, and overlay surfaces consistently use the source's compact dark chrome, bronze accent, neutral borders, rounded cards, and UI/monospace type hierarchy.
- [x] The workspace editor exposes identity, reusable connection and saved-screen references, launch order, validation, reset, cancel, and save states without presenting runtime sessions as durable data.
- [x] Appearance settings expose automatic color mode, platform treatment, host-accent following, the currently stored profile, and an explicit save action.
- [x] Terminal settings expose renderer typography, palette, cursor, scrollback, input-safety, keymap, link, IME, shell-integration, bell, and compatibility choices in a readable full-window layout.
- [x] Diagnostics makes its bounded, secret-safe export contract visible before export and reports the created archive, artifact count, digest, open, and reveal actions afterward.
- [x] The layout designer now follows the exported 1000 × 648 composition: 60 px header/footer, full-width name and 12 × 8 grid controls, 560 × 324 stage, 300 px panel rail, colored panel regions, paint action, pointer hint, explanatory note, and footer summary.
- [x] Dragging across empty cells paints one snapped rectangular panel; dragging a selected panel edge previews and commits an exact snapped resize. Both gestures capture one initiating pointer, recompute at release, cancel safely on capture loss or Escape, and never apply a stale delta over a newer keyboard edit.
- [x] Pointer interaction remains additive to the keyboard surface: panel selection, Alt+arrows movement, Shift+arrows growth, Ctrl+arrows shrink, Page Up/Down traversal, visible buttons, focus trapping, and live operation status remain available.
- [x] Rejected paint/resize geometry is validated as one complete candidate and does not partially mutate the layout. The gesture surface is not a duplicate automation focus stop.
- [x] Native menus, exact-modifier shortcuts, command-palette actions, and new-item intents now converge on the same confirmation-safe action path and restore focus to the active route or panel.
- [x] Host reduced-motion, reduced-transparency, and text-scale preferences are observed through event-driven platform sources and reapplied live; Quick Terminal removes slide animation and blur when requested.
- [x] All 265 explicit `TextBlock`/`TextBox` sizes now resolve through live semantic scale resources, text-bearing search/chooser controls can grow beyond their design minima, and a repository convention prevents literal visible-text sizes from returning.
- [x] The command palette search/list/close controls and both terminal presentation paths expose stable accessibility names; terminal lifecycle status is a polite live value.

### Intentional differences and remaining acceptance work

- The workspace editor is a validation-first form and launch-order editor rather than a pixel-for-pixel copy of the reference's denser icon browser and draggable tab list. It preserves the same information hierarchy while keeping durable references explicit.
- The layout rail uses stable panel order and exact grid coordinates instead of adding a new durable panel-name field that is absent from the current layout schema. It also retains the keyboard move/resize controls below the source-shaped panel list, so that longer rail scrolls rather than hiding an accessible alternative.
- The layout comparison uses the real XAML and `LayoutDesignerViewModel` in an isolated presentation-only native window with a deterministic four-panel fixture. It does not initialize SQLite, the OS vault, single-instance coordination, terminal sessions, or the running user profile, and the fixture is not presented as live application state.
- Appearance settings currently expose the stored host/profile controls required by M1; the reference's full theme preset, ANSI palette, and font-customization composition is not reproduced on that route.
- Automated coverage now proves host-preference mapping/lifecycle, semantic text scaling, terminal accessibility metadata, and the primary shell focus policies, but these captures do not prove complete physical keyboard-only traversal, high-text-scale clipping/reflow, focus order, or VoiceOver, Narrator, and Orca announcements. The [named-host accessibility runner](docs/platform-accessibility-acceptance.md) now packages the fixed physical observation matrix, exact build/screen-reader boundaries, stable descendant cleanup, and strict sanitized evidence validation; no physical VoiceOver, Narrator, or Orca run has been relabeled as complete. The complementary [macOS AX acceptance probe](scripts/acceptance/mac-accessibility/README.md) currently returns a sanitized `BLOCKED / SCREEN_LOCKED` receipt before making AX calls.

### Result

**passed for the implemented M1 product surface** — the reviewed workspace, appearance, pointer-capable layout designer, terminal-settings, and diagnostics states are coherent with the Pencil design language. The refreshed layout comparison is same-viewport and source/implementation side-by-side; the clipped numeric value found during review was corrected before the final capture. This is a visual and targeted interaction result, not completion of M1 accessibility or cross-platform acceptance.

## M2 desktop terminal and launcher — updated 2026-07-23

### Comparison setup

Both checks place the Pencil reference and the running desktop build in one image. Each half uses the same 2880 × 1800 pixel viewport at 2× display scale.

| Surface | Pencil reference | Running build | Combined comparison |
| --- | --- | --- | --- |
| Launcher | `design/screen-connections-screens.png` | `.codex-audit/m2-launcher-recent-window.png` | `.codex-audit/m2-launcher-combined.png` |
| Terminal workspace | `design/screen-terminal-workspace.png` | `.codex-audit/m2-terminal-latest.png` | `.codex-audit/m2-terminal-combined.png` |

The launcher render uses persisted user data: one local connection, one saved screen, and metadata-only recent sessions. It does not fill the screen with the fictional production connections from the reference. The workspace render uses a real local PTY through libghostty and shows the agent as intentionally offline because no provider is configured.

### Visual checks

- [x] Compact dark chrome, restrained bronze accent, fine neutral borders, and rounded-card language match the source.
- [x] Launcher sidebar width, navigation order, section order, search/primary-action placement, card hierarchy, and recent-session container preserve the source composition.
- [x] Sparse persisted launcher data remains intentionally left-aligned instead of being stretched or replaced with fake records.
- [x] Workspace top tabs, connection strip, workspace rail, terminal canvas, docked agent surface, and bottom status strip preserve the source hierarchy.
- [x] Main-canvas/agent width balance and dense spacing remain legible at the reference viewport, including a live two-panel split.
- [x] Terminal and data surfaces use the intended monospace hierarchy; application chrome uses the host UI font.
- [x] Icons come from FluentIcons rather than text glyphs or hand-drawn assets.
- [x] Focus, live/error/offline state, and destructive actions have text or icon labels in addition to color.
- [x] Native macOS chrome is retained as a platform adaptation without changing the content hierarchy.
- [x] The agent area truthfully explains that a provider is required. The forward-looking populated transcript and action cards are not presented as implemented behavior.

### Interaction checks

- [x] Launcher, workspace, settings, command palette, new-item chooser, panel chooser, and layout designer are reachable from visible controls and the native application menu.
- [x] The command palette opened over a live libghostty surface and executed **Split panel**.
- [x] The panel chooser added a second real local terminal; `.codex-audit/m2-terminal-split-real.png` records the resulting live split.
- [x] **New Terminal Tab** in the native application menu opens the default local connection directly from the launcher; `.codex-audit/m2-native-menu-terminal-live.png` records the resulting live libghostty session.
- [x] The application prefix created a second real terminal tab while the native terminal owned focus; `.codex-audit/m2-native-keybridge-final.png` records the final rebuilt app with both live tabs.
- [x] The native bridge consumes application keys before libghostty, keeps held-key repeat/release ownership, replays configured pass-through sequences exactly once, and keeps programmatic agent key injection on the PTY path.
- [x] The native bridge's reverse callback has process-lifetime identity and weak per-session registration; GC/finalizer, deterministic disposal, in-flight disposal, and exception-boundary cases are covered without blocking the AppKit thread.
- [x] Returning to the launcher exposed recent sessions from the durable metadata-only store; `.codex-audit/m2-launcher-recent-window.png` records the populated state.
- [x] App regressions cover recent-session start/completion mapping, reopening durable connection/screen/workspace definitions, missing-definition disabling, confirmation-safe clearing, and shutdown flushing.
- [x] Closing live terminal scope uses the running-process confirmation path and explicitly restores the active route after cancellation; workspace and saved-screen deletion also require confirmation.

### Intentional differences and remaining acceptance work

- The Pencil terminal reference is deliberately forward-looking. Command blocks remain M5 research, and a connected governed agent remains M3; the current build shows the correct offline boundary instead of simulating either feature.
- The reference contains dense fictional connection, screen, session, terminal, and agent data. The build renders only persisted user records and live session state.
- Platform-native title-bar treatment differs slightly from the borderless composition and is an accepted platform adaptation.
- Visual QA does not replace physical backend acceptance. Windows and physical Linux X11 terminal evidence are still outstanding and must not be inferred from this macOS pass.

### Result

**passed** — the implemented M2 launcher and live-terminal scope passes same-viewport structural and interaction visual QA against the Pencil source. Deferred agent/command-block behavior and outstanding physical Windows/Linux terminal acceptance remain explicitly outside this result.

## M3 intrinsic agent clarification — updated 2026-07-24

### Comparison setup

The closest exported source is the terminal-workspace frame. It is 2880 × 1800 pixels at 2× display scale and was normalized to the same 1440 × 900 logical viewport as the running implementation. The source does not contain an awaiting-question state; this comparison therefore verifies the new card against the supplied workspace's visual language and containment, not exact same-state fidelity.

| Surface | Pencil/export reference | Running implementation | Combined comparison |
| --- | --- | --- | --- |
| Governed agent question | `design/screen-terminal-workspace.png` | `artifacts/design-qa/current/ask-user-live-1440x900.png` | `artifacts/design-qa/current/ask-user-visual-language-comparison-2880x936.png` |

The implementation capture comes from a presentation-only native Avalonia harness using the compiled application resources, real `MainWindow`, real `AgentChatViewModel`, actual bindings, and actual button routing. Its deterministic in-memory collaborators do not initialize SQLite, the OS vault, terminal sessions, or the user's provider configuration, and they do not modify user data.

### Visual and interaction checks

- [x] The reference and implementation were inspected together in one comparison input after viewport normalization.
- [x] The question card uses the supplied workspace's dark neutral surfaces, fine borders, orange status accent, compact rounded-card language, muted metadata, and UI/monospace type hierarchy.
- [x] The authenticated `INPUT NEEDED` status, agent question, no-credentials warning, answer field, **Skip**, and **Send** actions are visible without clipping inside the 352 px docked agent pane.
- [x] At 1440 × 900, the transcript viewport measured 350 × 480 logical pixels and fully contained the 330 × 260 question card; its answer field and both decision buttons remained inside the card.
- [x] Typing `Inspect staging only` through the actual `TextBox` binding enabled **Send**.
- [x] Raising the actual **Skip** click submitted the typed runtime question as `Declined`, cleared the pending question from the real view model, and closed the harness cleanly.
- [x] The card does not present clarification as an action approval and does not ask for credentials or other secret-shaped input.
- [x] No new image asset, approximate icon, fictional connection, or simulated terminal/provider state was introduced for the capture.

### Intentional differences and blocker

- The implementation retains the product's existing 352 px docked agent pane, while the forward-looking reference uses an approximately 410 × 536 floating panel. This is an existing shell-level structural choice rather than a change introduced by the clarification card.
- The exported PNG and HTML provide the surrounding agent-panel language but no pending clarification, approval, expiry, answer-input, or declined state. Pencil MCP continued to return `Transport closed`, so an authoritative same-state source could not be captured from the editable design.
- No P0, P1, or P2 visual or interaction defect specific to the implemented question card was found. Exact source-state comparison remains unavailable and is not relabeled as passed.

### Result

The live native question card is visually coherent with the supplied terminal-workspace design and passes the bounded binding/decision/containment checks above. Exact same-state fidelity cannot be verified until an awaiting-question source state is available.

final result: blocked

## M3 run-local capability request — updated 2026-07-24

### Comparison setup

The capability-decision card was rendered through the same isolated native Avalonia harness and compiled application resources as the clarification card. The exported terminal-workspace source contains no capability-request state, so the 1440 × 900 comparison again verifies visual-language fit, containment, and the actual decision binding rather than exact same-state fidelity.

| Surface | Pencil/export reference | Running implementation | Combined comparison |
| --- | --- | --- | --- |
| Run-local Off-to-Ask request | `design/screen-terminal-workspace.png` | `artifacts/design-qa/current/capability-request-live-1440x900.png` | `artifacts/design-qa/current/capability-request-visual-language-comparison-2880x936.png` |

### Visual and interaction checks

- [x] The final reference and implementation were inspected together after normalizing both halves to the same 1440 × 900 logical viewport.
- [x] The distinct `CAPABILITY REQUEST` card uses trusted capability, exact-target, affected-tool, expiry, and policy-mode presentation rather than model prose.
- [x] The `OFF → ASK` status, warning surface, orange primary action, neutral decline action, compact spacing, rounded borders, and UI/monospace hierarchy remain coherent with the supplied workspace design.
- [x] The 330 × 291 card is fully contained by the 350 × 480 transcript viewport at the reference size; both decision controls remain inside the card.
- [x] The exact **Enable Ask for this run** and **Keep Off** labels are visible without clipping.
- [x] The actual awaiting-decision state disables the main prompt while keeping **Stop** available.
- [x] Raising the actual **Keep Off** button submitted a separate `KeepOff` capability decision and cleared the pending card through the real view-model binding.
- [x] The visible warning states that enabling Ask grants no action and that later terminal, file, browser, or process operations still require their ordinary exact approval.
- [x] The first combined inspection exposed a clipped final character in the primary button's exact label. The equal-width action grid was corrected to size the short decline action automatically and give the primary action the remaining width; the capture and combined comparison were regenerated and re-inspected after that fix.

### Intentional differences and blocker

- The implementation retains the existing docked 352 px agent pane and native desktop chrome. The reference's approximately 410 px floating panel remains a forward-looking shell composition.
- The source PNG and HTML have no authenticated capability-decision, Off-to-Ask, affected-tools, or later-action-approval state. Pencil MCP remained unavailable with `Transport closed`, so there is no authoritative same-state frame against which to claim pixel/state fidelity.
- The final native state has no observed P0, P1, or P2 layout or interaction defect. The only visible issue found in the first pass was corrected before this final capture.

### Result

The live run-local capability card passes the bounded native containment, exact-copy, command-routing, prompt-lock, Stop-availability, and supplied-design-language checks. Exact same-state fidelity remains unverifiable until an authoritative capability-request source state is available.

final result: blocked

## Quick Terminal settings shell — updated 2026-07-25

### Comparison setup

| Surface | Pencil/export reference | Running implementation | Combined comparison |
| --- | --- | --- | --- |
| Quick Terminal settings | `design/screen-settings-quick-terminal.png` | `design/qa/quick-terminal-polished.png` | `design/qa/quick-terminal-comparison.png` |

The reference is a 2880 × 4190 pixel full-page export. The implementation was
captured from a live isolated macOS profile at a 1440 × 900 logical viewport
(3016 × 1936 capture pixels). The combined artifact normalizes the reference's
top viewport against the complete live application viewport.

### Visual and interaction checks

- [x] The implemented global hotkey, registration state, display policy, panel
  height, opacity, blur, animation, reduced-motion, animation-duration,
  focus-loss, session-retention, and explicit-save controls are present.
- [x] The implementation follows the source's page introduction, sentence-case
  section labels, compact grouped rows, right-aligned controls, dividers, and
  orange state accents.
- [x] Boolean behavior settings use toggle switches instead of checkbox-form
  controls.
- [x] All editable controls expose explicit automation names, and registration
  feedback is a polite live region.
- [x] The captured shortcut conflict is truthful: the user's already-running
  GhostSHELL instance owns the default global shortcut.
- [x] No P0, P1, or P2 visual issue remained after the final comparison.

### Intentional differences and remaining product work

- Launch at login, origin, tab placement, workspace-panel placement, and desktop
  dimming exist only in the forward-looking reference and remain future product
  scope. This pass does not simulate them.
- The application keeps its current cross-platform settings sidebar and uses
  denser rows so more implemented controls remain visible in a 900 px-high
  viewport.
- The reference is a long-form design export, while the desktop implementation
  uses the shell's scrolling settings route.

### Result

**passed for the implemented Quick Terminal settings surface** — the live page
is coherent with the supplied design language and preserves the behavior and
save flow of every existing Quick Terminal setting.

final result: passed
