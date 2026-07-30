# GhostSHELL design QA

This is the living visual-verification record for the desktop implementation. The editable Pencil sources `design/design.pen` and `untitled.pen` are byte-identical; the exported PNGs in `design/` are the visual comparison references. The user-provided HTML exports in `design/html-export/` are the structural source for exact hierarchy, labels, dimensions, spacing, radii, colors, and typography when the Pencil connector is unavailable.

The HTML source confirms the shared baseline used by the build: a 1440 × 900 shell, 44 px top chrome, 244 px launcher/settings sidebar, 526 px Quick Terminal panel, predominantly 8 px control/card radii, SF Pro-style application chrome, JetBrains Mono terminal/data surfaces, `#111111`/`#18181B`/`#2E2E2E` neutral layers, `#B8B9B6` muted text, and `#FF8400` as the supplied example accent. Platform adaptation and host-accent following remain intentional product requirements.

## File Viewer ForkLift-style pass — updated 2026-07-30

- Source visual truth: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-4bc0ab7f-9180-48c2-9614-17109c0a426b.png` (2864 × 1808).
- Previous GhostSHELL state: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-a77fa415-a7d5-483d-aed9-5f85890111bb.png` (2026 × 1618).
- Intended state: a loaded remote location in details view with no selected file.
- Density normalization: not applicable yet; the supplied images have different window crops and this pass intentionally borrows ForkLift's visual hierarchy rather than reproducing its sidebar and dual-location workflow.
- Implementation screenshot: blocked. The running GhostSHELL process predates the rebuilt XAML and owns active terminal/remote state, so it was not terminated merely to obtain a screenshot.
- Full-view evidence: the source and previous state were opened at original resolution. The implementation adopts the source's grouped compact toolbar, location-summary row, calm same-tone list and preview surfaces, dense full-width rows, system UI typography for filenames and metadata, and restrained column headers.
- Focused-region evidence: code-level only until restart. The file-list region removes the raised gray slab and rounded inset rows; the toolbar region groups navigation and mutation controls without changing their commands.
- Interaction coverage: existing connection selection, path entry, filtering, hidden-files toggle, sorting, view switching, navigation, transfer, create/rename/delete, upload/download, selection, preview, retry, pagination, and loading-state bindings are preserved. Build and automated contracts pass.
- Iteration 2 evidence: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-89d840ac-3a46-4e18-ab3f-f62e7a3f2b0d.png` (2002 × 200) showed that the first implementation still exposed three large selector fields and text actions as a second form-like row. `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-baf57388-0cda-444f-9512-454948ea7a79.png` (596 × 108) also showed the favourites control clipped at its upper antialiased edge.
- Iteration 2 fixes: sort, order, layout, and hidden-file choices now live in one compact view-options flyout; path, grouped navigation, grouped semantic file actions, and search occupy one ForkLift-shaped toolbar; the following row is only the location and item summary. Shortcut controls now opt out of clipping at both component containers and reserve the shared one-pixel top clearance inside the scroll viewport. Architecture contracts preserve both decisions.
- Iteration 3 evidence: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-a32e0915-22a0-47b9-a9bc-0f8dac0a47e6.png` (2002 × 320) showed that disabled actions still inherited separate filled capsules, so the toolbar read as a sequence of unrelated generic buttons rather than ForkLift's grouped control surface. `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-028557f5-c617-4147-9a40-f337a2cb898b.png` (600 × 102) confirmed that child-level margin and clip settings had not protected shortcut strokes at the actual scroll-viewport boundary. `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-e7a859fc-c7f3-40c6-9f98-232f3071f182.png` (298 × 70) identified the redundant `FILES` superlabel and terminal-style panel-title typography.
- Iteration 3 fixes: file actions now share explicit clipped toolbar capsules with transparent disabled presenters; view controls and common mutations form one compact group, while open/transfer/rename move to a single overflow menu. The shortcut strip now owns asymmetric internal viewport padding instead of asking each child control to escape clipping. Runtime panel titles and saved-connection labels use the platform UI face, redundant panel-kind superlabels are removed, and the application `Monospace` presentation class resolves to the platform UI family so terminal typography cannot leak into application chrome. Native terminal rendering remains profile-controlled.
- Regression evidence: the desktop build succeeds with zero warnings, 190 architecture tests pass, and 816 app tests pass.
- Iteration 4 evidence: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-83fa3412-b1f9-41b1-8d2e-dc99d06e8dc5.png` (1348 × 152) confirms that the toolbar grouping and shortcut clipping corrections are materially improved. It also shows undersized file-list chrome, a location-summary band using a different surface, and a fixed preview allocation.
- Iteration 4 fixes: all scalable application text now has a per-platform system-body-size floor while larger hierarchy and host accessibility scaling remain intact. The location summary uses the same solid surface as its surrounding file chrome. Preview is now an explicit toolbar toggle and a true three-column layout with a draggable divider; hiding it collapses both its column and splitter, while reopening restores the last visible width and retains already-loaded preview data.
- Regression evidence after iteration 4: the desktop build succeeds with zero warnings, 190 architecture tests pass, and 817 app tests pass.
- Iteration 5 evidence: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-73768eba-afe8-44e5-8241-134813a95d74.png` (548 × 114) showed that the stock split-button template still painted its child halves across the favourite's upper border. `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-7ccb7870-aee7-4417-a202-7dc80d4802ae.png` (462 × 216) showed exposed gutter background between the file-list and preview header surfaces.
- Iteration 5 fixes: saved-connection favourites now use one owned rounded border with plain primary and menu-button hit regions, eliminating the competing split-button chrome. The file-list and preview headers paint edge-to-edge solid surfaces, the redundant preview qualifier is removed, upload disappears when the active provider cannot upload, and two draggable column dividers share their live widths with every details row.
- Regression evidence after iteration 5: the desktop build succeeds with zero warnings, 190 architecture tests pass, and 818 app tests pass.
- Iteration 6 evidence: `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-e88a1ff8-abc3-4a98-8dac-4e4d60549364.png` showed that the location summary still carried a horizontal separator through an otherwise continuous solid surface.
- Iteration 6 fix: the location summary no longer draws its own top or bottom border; the active panel outline remains the only accent boundary.
- Remaining P2 verification: capture the rebuilt loaded local and SFTP states at the same window size and check favourite strokes, upload visibility, header continuity, preview resizing, and table-column resizing visually.

final result: blocked

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

## Shell design polish pass — updated 2026-07-26

### Comparison setup

This pass introduces a reusable capture harness,
[`tools/GhostShell.DesignQa`](tools/GhostShell.DesignQa/README.md), and records
20 shell routes at a 1440 × 900 logical viewport, three full-height variants,
one focused state, two appearance-extreme variants, and 6 modal editors and
confirmations at their own arranged size, in `design/qa/current/`. The harness
renders the real `MainWindow`, the real `MainWindowViewModel`, and the compiled
application styles in-process through `RenderTargetBitmap`, so captures need no
macOS Screen Recording or Accessibility grant and never include other windows.

The structural reference is `design/html-export/` (exact px, colour, and type
per node) cross-checked against the exported PNGs. `design/design.pen` could not
be read: the Pencil MCP server is bound to a VS Code transport and returned
`transport not connected to app: visual_studio_code`; before that it resolved a
different document entirely and ignored the `filePath` argument.

Harness collaborators are deterministic and in-memory. Nothing touches SQLite,
the OS vault, terminal sessions, or the user's profile, and `QaData` sample
content is never presented as the user's real records.

### Defects found and fixed

Shell-wide, at the style layer so every call site improves together:

- `Button` does not default to stretch alignment, so list rows, command-palette
  results, and panel-chooser cards all sized to their content. Trailing badges
  drifted to mid-row and chooser cards left large gaps.
- `ComboBox` sized to its widest item, leaving settings grids with ragged control
  widths that shifted with the selection. Options now fill their cell.
- Option lists bind straight to enum values, so menus read `DiscardAndShowHint`,
  `ConfirmBeforeOpen`, and `ProtectUnsafe` — the identifier rather than the
  choice. An `EnumDisplayConverter` renders them as prose while keeping vendor
  spellings such as OpenAI intact; lists with their own item template are
  untouched.
- Keybinding shortcuts showed the stored comparison form, `Ctrl+B, ARROWLEFT`.
  A presentation formatter renders `Ctrl+B, ←` without weakening the domain's
  upper-case invariant.
- Destructive icon buttons carried a permanent red outline, so every row of a
  settings list read as an error state. They are quiet at rest and take their
  danger tint on hover or focus.
- The solid destructive button competed with the accent primary action for the
  eye; it now uses the soft danger surface.
- "Nothing here yet" was drawn on the accent-tinted notice surface, so pages of
  empty sections looked like pages of warnings. Empty states moved to a neutral
  `EmptyStateCard`, and the missing stored-credentials empty state was added.
- 50 hard-coded hex colours across 18 views bypassed the theme and could not
  follow light mode or high contrast; they now resolve through theme resources.
  The four remaining literals are deliberate semi-transparent scrims.
- Single-line `TextBox` and `ComboBox` content sat against the top edge whenever
  the control was taller than its line height.
- Avalonia's default `FocusAdorner` drew a hard high-contrast rectangle outside
  the control, ignoring its shape and reading as damage on dark chrome. It is
  suppressed on interactive controls; focus is now an accent ring on the control
  itself, captured as `settings-appearance-focused.png`.
- macOS content began 76px from the window edge, leaving the leading control
  crowded against the traffic lights; it now clears them at 92px.

Per surface:

- Launcher connection cards had no status dot, no kind badge, and no tags, and
  showed the transport as shouted `SSH`/`LOCAL`/`DOCKER`/`WSL` text. They now
  follow the source: status dot, name, cased kind badge, monospace host, tag row.
- Launcher card grids used a fixed-width `WrapPanel` that fitted three cards per
  row against the source's four and left a ragged trailing gap. A new
  `CardGridPanel` stretches cards to a gap-consistent grid with flush edges.
- Saved-screen cards truncated their meta line because inline actions competed
  for the row. Card bodies are now the primary target, secondary actions reveal
  on hover or keyboard focus, and the meta reads `3 panels · prod-api,
  staging-web` as in the source.
- `ScrollViewer`s over the New Tab lists measured with unbounded width, so rows
  never filled their card. Constrained to the viewport.
- The connection editor clipped its keep-alive description mid-word; the row now
  stacks the caption under its checkbox and wraps.
- Layout rows reported only "8 rows", dropping the column count; they now read
  the full grid extent.
- Recent-session and New Tab lists drew a trailing divider against their
  container's rounded bottom edge. Rows now carry a leading rule instead.
- The settings sidebar search overflowed its box; the command-palette and
  settings search fields did not vertically centre their text.
- The disabled connection card painted the Fluent disabled background as a light
  rectangle inside the card; card presenters are now transparent in every state.
- The active navigation item did not carry the source's accent icon.

### Intentional differences and remaining work

- **The Appearance route now carries the reference's whole composition.** Earlier
  passes split it across Appearance and Terminal on the argument that each page
  should own the definition it saves. That was the wrong call: the reference puts
  Theme, Colors, Font, Corners & UI, and Tabs & Workspaces on one page, and the
  split left Appearance looking unrelated to the design. Theme presets, Colors,
  the ANSI palette, and Font moved onto Appearance; Terminal keeps behaviour
  — clipboard, keymap, links, bell, compatibility, scrollback.
- Both routes were rebuilt in the reference's control vocabulary rather than left
  as lists of drop-downs:
  - Appearance gained colour-mode preset tiles that **are** the control: each
    previews the chrome its mode produces and carries the selection with an
    accent border, accent name, and check, as the reference's preset row does.
    The first attempt drew those tiles as decoration above a separate drop-down,
    which duplicated the choice and read as two controls for one setting; the
    drop-down is gone and the tiles are one exclusive radio group.
  - Appearance also gained an accent swatch bound to the live accent and a
    Preview card rendering the shell's own chrome from the saved profile.
  - Terminal gained swatch cards for background/foreground/cursor/selection, the
    ANSI palette as a labelled swatch row, a font row with a line-height slider,
    a code preview drawn in the profile's own colours and typeface, and the
    cursor blink setting as a switch card.
  This needed three new converters — hex→brush for swatches, on/off for switch
  captions, and the enum display converter — plus swatch, segmented, and accent
  slider styles.
- **Theme presets are now built rather than deferred.** `TerminalPalette` gained
  Midnight, Solarized, Dracula, Nord, and Light beside the existing GhostSHELL
  Dark, plus a `Presets` list and a `Matches` comparison. The Terminal page shows
  them as a preset row where each tile renders a terminal in that palette's own
  colours, and choosing one rewrites the whole palette — all sixteen ANSI entries
  included, not just the four fields the page displays. Selection is derived from
  the colours, so hand-editing onto a preset's exact values reselects it, editing
  away marks the palette Custom, and a half-typed hex leaves no preset selected
  instead of guessing. Twelve tests cover this, including that a saved profile
  carries the full preset.
- **Corners & UI and Tabs & Workspaces are now built.** `ThemePreference` moved to
  schema version 2 with a corner-radius override, interface density,
  show-tab-bar, show-workspaces-panel, tab placement, and workspace-panel
  placement. (Window opacity and background blur were added here too, and later
  removed; see below.) Every field is optional with a defined default,
  so the document shape itself stays readable across the change; the stored row
  is still rejected on its schema version and discarded, per the policy below.
  - Radius and density reach the real chrome through the appearance resource
    mapper: the override replaces the platform profile's radius, and density
    scales that profile's own padding and control height rather than replacing
    them with fixed numbers.
  - Publishing those resources was not enough on its own — the shell's own styles
    were carrying literal corner radii and paddings, so both settings changed the
    resources and nothing visible. Card-shaped surfaces now bind
    `ShellCardCornerRadius`, controls bind `ShellControlCornerRadius`, and the
    primary, secondary, and destructive buttons take `ShellControlPadding`
    instead of fixed insets. Pill shapes keep their literal 999. Two captures,
    `appearance-corners-tight` (0 px, compact) and `appearance-corners-round`
    (20 px, comfortable), differ only in those two settings, so a regression that
    disconnects them again shows up as two identical images.
  - **Window opacity and background blur were built, then removed.** They are not
    in the product; this record keeps the account because the removal is the
    conclusion, not an absence of work.

    The sequence was: content opacity replaced with real `NSWindow.alphaValue`;
    blur made visible at all by giving the shell root a translucent fill; a
    constant flicker traced to re-requesting an unchanged transparency level,
    which re-ran the host's native path and re-entered the appearance pipeline
    through a colour-values notification; a colour shift traced to the host
    compositing a vibrancy material rather than the desktop, corrected by solving
    the fill's luminance against that material; and finally the discovery that
    Avalonia's macOS acrylic layer blends *within* the window, so the desktop was
    never blurred at any point — only made visible through a translucent window.
    A behind-window layer was installed and pinned to the active state to stop the
    interface re-tinting on focus loss.

    It still did not read as blur, and the setting was cut. The judgement was the
    user's and it was the right one: a whole-window translucency effect on a
    terminal shell is decoration, and this one had absorbed four rounds of native
    debugging without ever looking like what it claimed to be. Blur belongs to the
    Quick Terminal, where a panel overlaying the desktop has an actual reason to
    show through it; that surface already has its own opacity and blur settings.

    Removed with it: `ThemePreference.WindowOpacity` and `.BackgroundBlur` and
    their validation, `EffectiveTheme` and `EffectiveAppearanceResources`
    carriers, `BackdropFill` and its material model, `ShellBackdropBrush`,
    `INativeWindowChrome` with its macOS implementation, the transparency latch,
    and the two Appearance cards. The shell window is an ordinary opaque window
    again. The stored theme keeps schema version 2 — an older document simply
    carries two properties nothing reads, which deserialization ignores. The
    native alpha, appearance, and visual-effect-view work is in the history for
    whoever wires up the Quick Terminal.

  - The three-way density control is a templated `ToggleButton`, not a styled
    one. `ToggleButton` derives from `Button`, so it had been inheriting the base
    button's padding, border, and corner geometry and overflowing its own 3 px
    track; it now carries an explicit template and the track clips to bounds.
  - `SaveThemeAsync` takes the chrome settings as an optional group, so a caller
    that does not present them leaves the stored values alone instead of
    resetting them to defaults.
- **Schema policy while the definitions are still moving: no migrations, discard
  legacy.** Raising the theme schema to version 2 initially made the whole
  profile fail to load with `UnsupportedSchema` — saved connections and
  workspaces included — because the registry required an exact version match and
  nothing dropped the stale row. Rather than add migration machinery for a
  work-in-progress schema, the store now discards what it cannot read: a row
  whose schema or payload the current build rejects is deleted and skipped, the
  remaining definitions load normally, and whatever seeds defaults recreates the
  missing ones. The exact-version match is unchanged, so this is a data policy
  rather than a compatibility promise.
  Five tests cover it: an outdated row does not fail the list, it is removed so
  the next start is clean, a corrupt payload is discarded the same way, readable
  rows beside an unreadable one survive, and discarding one kind leaves the
  others untouched.
- Two more bugs surfaced only because the tests were written: renaming the enum to
  `TabStripPlacement` (Avalonia already defines `TabPlacement`) left the
  constructor parameter no longer matching its property, which breaks
  `System.Text.Json` construction and would have silently broken theme
  persistence; and a bundle-import test pinned `schemaVersion:1`, so raising the
  version turned its tampering step into a no-op and the assertion inverted. The
  test now derives the version from the definition.
- **Appearance settings were largely inert, and the page lied about it.** Two
  defects, both mine:
  - "Save appearance" only saved the theme. Every palette, ANSI, font, and
    cursor field moved onto the page binds to the terminal profile, so those
    edits were silently discarded. The page now saves both definitions.
  - `ShowTabBar`, `ShowWorkspacesPanel`, tab placement, and workspace-panel
    placement persisted with no consumer anywhere in the shell — four controls
    that stored a value and changed nothing. They are now bound to the real
    layout: the rail's dock edge and visibility, and a tab strip that is hosted
    either in the title bar or in its own row above the status bar.
- **Appearance no longer has a save button.** It applies as you edit. Changes are
  coalesced for ~220 ms before they reach the store, because dragging a slider
  would otherwise write once per pixel and each write reapplies the whole theme.
  Reloading the controls after a save is fenced so the reload cannot echo back as
  a fresh change. Text fields commit on Enter or when focus leaves.
- All five colour fields — the four palette colours and the accent — now carry
  the same three controls: a picker, a hex box, and an eyedropper. The accent had
  been left as a bare hex box because it lives on the theme rather than the
  terminal profile, and the earlier pass only walked the palette fields. Its
  picker and hex box mirror each other, and all three controls disable together
  while the accent source is not Custom.
- **Every colour field has a working, screen-wide eyedropper.** On macOS it opens
  AppKit's own `NSColorSampler` — the system loupe — so a colour can be lifted
  from anywhere on screen, not merely from this window. That API is used in
  preference to capturing the screen ourselves because `CGDisplayCreateImage`
  needs the screen-recording permission and returns nothing useful without it,
  whereas the sampler is system-mediated and needs no permission. Where a host
  offers no such picker the shell falls back to sampling its own window: click to
  arm, click again to lift a pixel, Escape to cancel.
  The interop was verified against the live runtime — the `NSColorSampler` and
  `NSColorSpace` classes resolve once AppKit is loaded, and the
  `NSColor` → sRGB → components conversion returns exactly `1, 0, 0` for
  `redColor`. The window fallback was verified by sampling the shell canvas and
  getting back its background, `#111111`. The interactive loupe itself needs a
  person to click, so that step is not machine-verified.
- **Several appearance controls silently did nothing, and the font field was
  never a selector.** The edits that were meant to add live-commit handlers and
  turn the font box into a drop-down had been written against the indentation the
  markup had *before* those sections moved onto this page, so they matched
  nothing and were applied to a file that no longer looked like that. Font
  family, font size, line height, and blink cursor were all left inert. Each
  control is now verified by reading the markup back rather than by trusting the
  edit or by checking the view model behind it.
- **Colour pickers and a font selector are now real controls.** Each palette
  field pairs an Avalonia `ColorPicker` with its hex box, both editing the same
  value, so picking and typing stay in step; the picker's alpha is dropped
  because the palette stores six-digit RGB. Font family is a drop-down of the
  host's installed families — 189 on the development machine — with the profile's
  own family kept in the list even when it is not installed, so a profile written
  on another machine cannot silently lose its font. The editor is also
  constructed where no UI platform is running, and the font manager only exists
  once one is; that case falls back to the stored family rather than throwing.
- **Tab placement offers all four edges and each one works.** The tab strip was
  extracted into one reusable `RuntimeTabStripView` that renders horizontally or
  vertically and re-raises every interaction, so the shell hosts a single control
  at whichever edge the profile selects instead of copying a sixty-line template
  per edge. Top sits in the title bar, Bottom in its own row above the status
  bar, and Left/Right dock a vertical strip beside the workspace canvas.
  Activation, closing, drag reordering, and the reorder live region are unchanged
  because they remain the shell's handlers.
- Launcher cards use the design's 16 px shape rather than the platform card
  radius, which stays reserved for window chrome and settings surfaces.
- Connection cards keep `Container …`/`Distribution …` for Docker and WSL. The
  reference shows a `user@host` string for those rows, which the model does not
  carry; the SSH default port is now hidden to match the reference's density.
- The status dot maps to whether a connection can be opened on this platform.
  The launcher tracks no liveness, so it is not presented as a live indicator.
- The workspace route renders the genuine "no provider configured" agent
  boundary. Routes needing a live PTY render an empty canvas; this harness does
  not replace live-terminal or interaction acceptance.
- Known remaining nit: the Session History search field's placeholder still sits
  about 8 px above centre; entered text is unaffected.

### Result

**passed for the reviewed shell surfaces** — the launcher, all eleven settings
routes, the command palette, New Tab, panel chooser, layout designer, and the
reviewed modal editors are coherent with the supplied design language, and
`./scripts/check.sh` is green (3,576 tests, 17 assemblies, clean build and
format). The two new presentation formatters carry their own regression tests.
This is a visual and structural result captured from a deterministic fixture; it
is not live-session, cross-platform, or accessibility acceptance.

### Per-view pass against the remaining reference frames

A second sweep took each implemented view back to its own reference frame, rather
than only the Appearance page.

**Home is a summary again.** The reference pairs each section with "View all" and
carries a Recent Sessions list; the implementation had "Add connection" on Home
and no destination for a "View all" to reach, because the sidebar's Saved
Connections and Screens entries only scrolled Home to a heading.
- Saved Connections and Screens are now real launcher pages, with their own
  heading, count, and the create action next to the list it adds to. Home shows a
  bounded preview — eight connections, four screens, the same counts the
  reference shows — so a profile with a hundred saved definitions cannot push
  every later section off the page.
- The card markup for both kinds moved into shared templates, so Home and the
  dedicated page cannot drift apart.
- Recent Sessions rows were showing the panel kind and outcome behind a fixed
  terminal glyph. A row is read as "what would I reconnect to", so it now carries
  the transport's own glyph, the endpoint, and the transport badge. The endpoint
  is resolved from the saved definition rather than stored in history, so the row
  stays truthful after the connection is edited, and falls back to the definition
  kind for a session whose definition has since been deleted.
- The getting-started card rendered as an empty panel whenever onboarding was not
  composed: `IsVisible` bound through a null view model is unset, and unset falls
  back to visible. It now falls back to hidden.

**The workspace editor's identity controls matched none of the reference.** The
accent was a hex text box and the icon a combo box of nine names.
- Accent is a row of eight presets, plus the custom colour picker and the same
  screen eyedropper the palette uses — the editor raises an intent and the host
  samples, because screen capture is a host capability.
- Icons are a searchable grid over a catalog of 36, searchable by purpose as well
  as name ("prod" finds the rocket, "db" the database). The identifier is what a
  workspace stores, so the catalog is a persistence contract: tests assert every
  identifier is storable and unique, and that an identifier this build has never
  seen falls back to the default glyph instead of drawing nothing.
- The icon-to-glyph mapping had been duplicated between the shell and the editor;
  it now lives in one place, so a workspace cannot show one icon in the picker
  and another in the tab.

**A new panel could only become a blank adapter.** The reference offers saved
connections and screens in the same chooser, so opening a saved connection into a
panel is now a real path rather than something reachable only by opening a whole
tab first.

**Counts bound through `.Count` can render nothing, silently.** Avalonia resolves
a binding path against the value's runtime type, and an `IReadOnlyList<T>` backed
by an array publishes `Length`, not `Count` — the interface implementation is
explicit and invisible to reflection. The layout designer's panel count had been
shipping as an empty pill for exactly this reason. Both offenders now publish an
explicit count property, and an architecture test rejects any `.Count` binding
that does not go through a collection whose declared type publishes it. The test
found two further candidates on its first run, both of which turned out to be
`ReadOnlyObservableCollection` and safe.

**The layout designer told the user to do something it then ignored.** Printed
under the grid: "Drag across empty cells to paint a panel". Dragging did nothing,
because painting was gated behind a mode that had to be armed from a "Paint new
panel" toggle in the rail first — a state with no representation on the canvas
the instruction was printed on.
- The mode is gone. Dragging an empty cell paints, always, which is what the
  reference's canvas describes and what the hint already promised. "Paint new
  panel" now adds a panel outright, so the same thing is reachable without a
  pointer rather than being the only way in.
- With the mode went its label, its status line, its toggle result, and the
  Escape branch that cancelled it. Escape still abandons a drag in progress
  before it closes the overlay; there is simply nothing between those two now.
- **Adding a panel was a dead end on a full grid.** A finished layout normally
  covers the grid, and `AddSlot` refused whenever there was no gap — so the only
  way to add a panel was to shrink an existing one by hand first, which nothing on
  screen said. Adding now halves the largest panel and gives the freed half to the
  new one, splitting along its longer axis. The one case still refused is a grid
  of single-cell panels, which has nothing left to divide.
- **A panel could not be moved with a pointer.** Dragging a panel's middle did
  nothing; moving existed only as keyboard buttons in the rail. Dragging now
  translates the panel by whole cells, clamped inside the grid. The commit gate
  had to change with it: it required an edge, which a move does not have, so a
  move preview would have followed the pointer and then silently discarded itself
  on release.
- **The edge grab band was 14 px, not 9.** The resize maths was correct — tests
  confirmed the edge hit test and the snap both worked — but a 9 px target is hard
  enough to hit that it reads as resizing not working at all. It stays capped at a
  third of the panel's shorter side, or a one-cell panel would be edge the whole
  way through and could never be moved.
- **Losing the selection crashed the window.** `SelectedIndex` threw
  `InvalidOperationException` on a missing selection, and seven geometry
  operations called it. They now decline. `MinimumCanvasWidth`/`Height` called
  `Max()` on the slot list, which throws on an empty sequence; the equivalent
  property on the runtime screen view model had already been guarded, this one had
  not.
- Panels showed "Column 1, row 1, 6 by 4". Grid coordinates say where a panel is
  anchored, not how big it will look, and reading a span against the grid size is
  arithmetic the reader should not be doing while dragging. Both the canvas and
  the rail now show the panel's share — "½ × ½" — as the reference does, with the
  exact coordinates kept as the accessible name and in the rail's second line. A
  ratio with no familiar glyph is shown as a plain ratio rather than rounded to a
  near one, which would state a size the panel is not.

**A lone card was stretched across the whole row.** `CardGridPanel` clamped its
column count to the number of children, so one saved screen became a card the
full width of the content area — a metre-wide bar with a single panel preview
smeared across it. Column count now follows the available width alone; a partial
last row simply leaves trailing space. Tests assert a card's width does not depend
on how many cards there are, which is the property that was actually broken.

**Workspace chrome:**
- The connection pills in the workspace header stretched to the full row height,
  so their rounded corners met the row's edges and read as corners cut off. They
  are centred now.
- The rail's add button was pinned to the bottom of the rail by a `*`-sized row,
  which stranded it most of a screen below the workspace chips it belongs with.
  It sits directly under them.
- The agent panel's capability shield was vertically centred against a wrapped
  two-line notice, so it floated in the middle of the card away from the heading
  it labels. It aligns to the first line.
- The agent panel's expanders drew Fluent's own filled, bordered chrome inside
  cards that already have a surface and a border, so each read as a second card
  nested in the first. Expanders are flat now, at the style layer, since the
  header keeps its own affordance.
- The provider picker rendered as an empty box when no provider is configured. It
  says so.

**The terminal is a native view, and three separate defects came from treating it
like an Avalonia one.**
- It painted straight through the rounded corners its panel card draws, because
  Avalonia clips its own visuals and a native child view is not one. The card's
  radius is now handed across the boundary and the view rounds its own layer.
- Focus moving into it was invisible to Avalonia, so the panel it belongs to
  stayed marked inactive and could only be activated from its title bar. The view
  reports first-responder changes back, and the shell activates the panel from
  that.
- Its typography could only be set at surface creation; see below.

**The corner radius reached the view and still did nothing.** Setting
`layer.cornerRadius` and `masksToBounds` rounds a layer, but the terminal draws
through a Metal layer and Metal content is not clipped by a corner radius — the
rounding was applied to a layer whose drawable painted square straight through it.
The view now carries a `CAShapeLayer` mask built from a rounded-rect path, which
clips whatever the layer contains, rebuilt on resize so a stale mask crops nothing.
That needed QuartzCore linked, which meant recording a new framework in the
reviewed build options as well as the usual digests.

**The agent panel's empty state was inside the transcript's ScrollViewer**, in a
vertical StackPanel — where `VerticalAlignment="Center"` does nothing at all. It
was top-aligned in a scroll region, which is why it sat in the upper third with the
rest of the panel empty beneath it, and why aligning insets around it changed
nothing. It is now the panel's own centred content, and the transcript stands down
entirely when there is no provider. The scope line under the title also dropped the
monospace face: it is prose, and that face is for paths and endpoints.

**The corner radius never reached the native view.** The value was assigned after
the session request in the same method, and setting the request starts the attach
that captures it — so it always arrived one attach too late. Presentation is
assigned before the request now.

**The command palette still rebuilt every row.** The comparison added to stop it
used record equality on the result's target, and a command target holds its
arguments in a dictionary, which records compare by reference — so two command
rows built from the same source were never equal and the guard never fired.
`Command.InvocationKey` exists precisely to identify one; targets are compared
through it now. That is the third defect in this pass caused by a record holding a
collection, after the terminal palette and the search terms.

**Then every list flickered on hover, not just the palette.** Home's saved
connections, saved screens, and recent sessions all flashed under the pointer while
the pointer sat still. Same shape as the palette, one level up: every launcher list
was cleared and refilled on every catalog notification, and refilling destroys the
realized rows — so the row under the pointer lost its hover state and immediately
regained it. Most notifications come from something the list does not show, so the
work was almost always for nothing.

The rebuild could not be avoided by comparing the projections, because none of them
can compare equal. `LauncherConnectionViewModel` holds its tags as a list,
`LauncherScreenViewModel` holds its preview panels as one, and
`RecentSessionHistoryItemViewModel` stamps a fresh `ObservedAt` on every projection
— that is the fourth, fifth, and sixth instance of the same trap in this pass. Each
now states what it means to look identical, and the lists replace their contents only
when that comparison says something moved. `RefreshRecentSessionAvailability` was
the worst offender: it refilled the recent-sessions list unconditionally, even when
its own loop had found nothing to change.

**The New Tab overlay.** The reference frame is a search box, five things to
start, workspace chips, and two lists. The implementation added an eight-column
inline form along the bottom for naming a workspace or a screen, and stretched
both lists to the full height of the overlay so a single saved screen sat at the
top of a card the height of the window. The form is gone — creating a screen opens
the screen editor from the page that owns screens, and creating a workspace lives
in settings — and the lists hug their contents.

**The agent panel with no provider** showed an empty provider picker, a scope
picker, and a capability card describing a run that cannot happen, all stacked
above an empty state explaining how to get a provider. None of it was usable and
four idioms competed for one space. Only the empty state remains until a provider
exists.

**The agent panel with a provider** was the same problem one state later. Above
the first message sat a two-row labelled form, then a capability card carrying the
exact target, the context, two always-open inspectors and the YOLO escalation —
and under some scopes a terminal chooser as well. The conversation started below
all of it, in a bubble on each side that halved the usable width of a panel that
is already narrow.

It is five bands now, each owning one kind of thing. The heading says what the
panel is and how the run is doing. Below it stand the notices that must not move:
a live YOLO grant and the capability boundary, which used to be the first items
inside the transcript and so scrolled out of sight the moment the run said
anything. Then the conversation — what you said is a bubble, what the agent said
is the page. Then the composer, which is the field itself rather than a card
around a smaller card. Then the settings that shape the next run, stated the way
the run states itself: model, boundary, policy, as chips beside the status rather
than a form above the heading. The capability detail and the terminal chooser open
from where they belong instead of standing open forever.

Two conventions moved with it. One asserted the notices were inside the transcript
scroller; they are anchored to the panel now, and the assertion says why. The
other required the first two rows to carry minima, which the footer now does.

The connected panel had never been captured. The harness has no provider and
reaches no endpoint, so every route could only render the empty state — the layout
this pass changed most was the one nothing could see. `workspace-agent` publishes a
sample transcript through the same offline runtime, and the harness resets it
before every route so no other capture can imply a connected agent.

**The tab strip.** The activator button was not stretched, so the chip shrank to
its label and the close button that belongs to that tab rendered outside it —
sitting on the bar between two tabs, next to neither. And there was no way to open
a tab from the strip at all; the strip now carries its own add control, in every
orientation.

**Clicking a terminal did nothing, and typing went nowhere.** Both came from one
line. The native view's `mouseDown:` consulted the physical-input gate *before*
taking first responder, and returned early when the gate declined — so a click on
the terminal body neither focused the panel nor reached the shell, and a terminal
that never took focus never received a keystroke either. Focus is the host's
business: it decides which panel owns the keyboard. The gate decides whether input
reaches the shell. The view now takes focus first and gates only the delivery.

**Gutters were uneven by construction.** The panel canvas carried an 8 px margin
and every panel added 8 px on its right and bottom *only*, so the left and top
edges sat at 8 px, the right and bottom at 16, and two panels sat 8 apart. Canvas
and panels now take half the gutter each, which makes the gap between two panels
the same as the gap to any edge. A test states that as arithmetic so the intent
survives a change to the number.

**Panel corners were drawn round and filled square.** The card carried the corner
radius but did not clip, so the header and the terminal squared off the corners
the border drew round. The card clips its content now, and the header's own radius
— a literal 9 that had stopped matching the card once the corner-radius setting
could change it — is gone, since the card's clip is what shapes it.

**The agent panel's rows were inset at 14, 10, 12, and 18 px**, so nothing in it
lined up vertically. One inset throughout.

**The terminal font ignored its own setting — twice.**

The first fix wired the managed terminal surface only. macOS renders through the
native Ghostty host, so on the platform the report came from, it changed nothing.
That was the wrong half of the problem, and the report that followed was correct.

Making it work on macOS needed a path that did not exist. The render profile was
applied to `ghostty_surface_config_s` at surface creation and nowhere else, so the
only way to change a font was to relaunch the shell. `ghostty_surface_update_config`
reconfigures a live surface, which is what the chain now uses:
- A native entry point, `ghostshell_terminal_update_render_profile_v1`, rebuilds
  the configuration and applies it to the running surface. Rebuilding replaces the
  whole configuration, so the launch keymap is retained on the view and reapplied
  — without that, changing the font would clear the terminal's keybindings.
- Font size travels differently in the two cases: it is a surface-config field at
  creation and a configuration key on update, so the profile writer emits it only
  for the update path and creation behaviour is untouched.
- The operation is on `ITerminalRendererAttachment` beside attach and detach,
  because it acts on the renderer rather than on the process behind it, and it
  returns false where an engine cannot reconfigure in place so the caller leaves
  the terminal alone instead of restarting it.
- Changing the shim required re-recording three reviewed digests in
  `licenses/native-macos-components.json`: the header, the source, and the built
  artifact and payload manifests. That file is a release-provenance record and it
  still reports `releaseReadiness: BLOCKED`; the digests were re-recorded because
  the source changed deliberately, not to clear a gate.

Six contract tests pin the chain. It crosses four projects with no single test
that can exercise it end to end, and the failure mode is silence — the setting
saves, and nothing moves.

**The original defect.** A panel captured its render
profile when it launched, so saving a new size changed the stored definition and
nothing on screen: every open panel kept rendering at the size it started with
until it was closed and reopened. The panel now exposes a live render profile
that the presentation host binds separately from the session request — separately,
because restarting the session to change a font would throw the scrollback away
with it.
- The obvious equality guard on that setter would never have fired.
  `TerminalRenderProfileSnapshot` is a record, but its palette holds ANSI colours
  in an array, so two snapshots built from the same profile are never equal. Left
  that way, every catalog refresh would have looked like a typography change and
  relaid out the terminal. The snapshot now answers `RendersSameAs`, comparing
  what the renderer actually reads.

**The interface font was never the host's.** The platform profiles named font
stacks — "SF Pro Text, Inter, sans-serif" — but macOS does not resolve SF Pro Text
by name, so the stack fell through to the bundled Inter and the application looked
subtly foreign on every platform it claimed to match. A host-native profile now
takes `FontFamily.Default`, which is the host's own interface font. GhostSHELL's
own profile still names Inter deliberately: that one is meant to look the same
everywhere.

**Icon buttons inherited the shell's stretch alignment**, so each glyph rested
wherever its own metrics put it rather than in the centre of its button — a row of
them read as misaligned. They centre explicitly now, at the style layer.

**Layout defects found by looking rather than by reasoning:**
- Centred empty states left-aligned their shorter children. A `StackPanel` is only
  as wide as its widest child, so a long wrapped sentence set the width and the
  heading, icon, and action all sat against its left edge. Fixed at the style
  layer with an `EmptyState` class, since the same shape occurs in seven places.
- The side tab strip drew each tab's label against the top of the tab: a
  horizontal `StackPanel` stretches its children vertically, so the text sat above
  its own icon.
- Two overlay lists were sized so the last visible row was sliced in half, which
  reads as a clipped list rather than a scrollable one. The new-item lists now
  fill the overlay's own height; the panel chooser's are an exact five rows.
- The AI provider credential card offered a provider picker with nothing in it
  when no profile existed. The card still explains the ordering, but its controls
  are inert rather than inviting a keystroke that cannot go anywhere.
- The About page's open-source section promised "key runtime components are shown
  here" and then rendered nothing when the build has no component catalogue. It
  now says so.

**Switch versus checkbox.** The design language uses a switch for a setting you
turn on and a checkbox for something you are asked to acknowledge or select, and
the two had drifted: keep-alive, provider-enabled, server-enabled, the saved
screen's agent override, IME input, and the repeatable prefix were all checkboxes.
They are switches now. The genuine checkboxes stay: the insecure-transport and
untrusted-server acknowledgements, the YOLO confirmation, the mutually exclusive
authentication mode, the multi-select panel list, and the file panel's hidden
filter. A contract test holds the line over settings surfaces and definition
editors, with each exception listed and reasoned.

Every switch also had to clear Avalonia's built-in On/Off labels by hand, which
one instance had forgotten — so it showed its state twice, once in the row's own
status text and once beside the switch. The style sheet clears them now, and a
second test rejects restating them per instance.

**Capture fidelity.** The harness had been rendering several views in states that
hid their own defects: no recent sessions, an unpopulated layout designer, and two
workspaces with identical default icons. It now supplies a fixed recent-session
history with a pinned clock, opens the designer on a real layout, and gives the
two fixture workspaces distinct icons and accents.

### Intentional differences from the reference

- The reference's AI Providers frame shows a catalogue of ten named providers with
  OAuth sign-in, per-provider model lists, and connection state. Those describe
  functionality this build does not have; the page keeps its own add-a-profile
  flow rather than presenting controls that would do nothing.
- Home's search says "Search everything…" where the reference says "Search
  connections…". The control opens the command palette, which searches commands,
  screens, workspaces, and session history as well, so the reference's label would
  understate it.
- Connection cards show a transport-appropriate descriptor — "Container
  edge-proxy", "Distribution Ubuntu" — where the reference shows `user@host` for
  every kind. A Docker container is not reached at a host address.


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


## Design system

The interface had drifted apart in a way nobody chose. Measured before touching
anything:

| | literals | tokens | distinct values |
|---|---:|---:|---:|
| `Spacing` | 880 | 0 | 20 |
| `Margin` | 376 | 2 | 100 |
| `ColumnSpacing` | 261 | 0 | 13 |
| `CornerRadius` | 74 | 3 | 14 |
| `Padding` | 64 | 2 | 26 |

Plus ninety-six bordered `Border` elements carrying their own fill, radius, and
inset inline across thirty-five distinct shapes, eight card classes, and five
kinds of small rounded label. The density, text-scale, and corner-radius settings
could reach a control's own height and nothing around it: turning density up moved
the controls and left the layout exactly where it was.

None of that was a bad decision. It was the absence of a place to put the
decision, so every view answered again, slightly differently. The font-size axis
was the counter-example and the proof the approach works — it was already fully
tokenised behind a convention test, with zero literals outside icon glyphs.

**Tokens.** A spacing scale of six steps, derived from one host grid step — 8 on
Aqua and Fluent, 6 on Adwaita and Breeze — scaled by density and text size, and
published in both the forms the framework needs: a number for `Spacing`, a
`Thickness` for `Margin` and `Padding`. Publishing only one is why the markup fell
back to literals; half the properties could not consume the token. Radii gained a
pill and an inner step so a shape nested in a card never reads as a second card.
Adapting to a desktop that lays out on a different step is now one number.

**Components.** Four, defined once in terms of tokens and nothing else.
`SurfaceCard` replaced the eight card classes: they differed in exactly two
things, which surface they sat on and whether they cast a shadow, so those are its
two settings and a view now says what a card *is* rather than what it looks like.
`SettingRow` and `SettingsGroup` replaced the hand-built settings shape, where the
group owns the two things rows cannot decide for themselves — the control column
width and where the separators go. `EmptyStatePanel` centres on both axes with a
Grid, because a `StackPanel` ignores a vertical centre; getting that wrong by hand
is what put the agent panel's empty state in the top third of a blank panel
through three separate attempts. `StatusChip` replaced five kinds of pill.

**A gallery.** Every component on one page, captured three times: default, and at
both density extremes with the corner radius at 0 and 20. If a token quietly goes
back to being a literal the two extremes become the same image, which is the only
way anyone would notice. It earned its keep immediately.

**What it found.** `DangerButton` was not a duplicate of `DestructiveButton` — it
was a fixed 32×30 icon button wearing a text button's name. Ten of its eleven uses
were icons; the eleventh was the trust-host-key button in the file-provider editor,
a labelled action whose text the fixed width cut off in the shipping application.
Shape and tone are separate axes, so it is `IconButton Danger` now and the labelled
one is destructive. `TextButton` carried a literal inset and no minimum height, so
it sat short of every control beside it and no row of actions lined up. And
`SettingsGroup`'s control width never applied: a `ColumnDefinition` is not a visual
and has no templated parent, so the `TemplateBinding` resolved to nothing and both
columns fell back to an even split — the row still rendered, just never at the
width it was asked for.

**Holding the line.** The design system may contain no literal size, gap, radius,
or colour at all — that is a hard rule, since a literal there is a setting that has
silently stopped working for every view built on the component. The rest of the
markup is a ratchet: **1,685 literals at the start, 15 now**, and the test fails if
that number goes up.

The gap axes went first — `Spacing`, `ColumnSpacing`, `RowSpacing` — 845 values
mapped onto the nearest step, twenty distinct gaps down to six. The margins and
paddings followed. Those needed something the tokens alone could not express: a
margin is four numbers and the markup used fifty-one distinct combinations of them,
far too many to name, and naming them is how a scale turns back into a list of
magic values. `{controls:Inset Horizontal=Lg, Bottom=Sm}` composes them instead,
resolving to a multi-binding over the published tokens rather than to a fixed
number — a markup extension that returned a `Thickness` outright would be no better
than the literal it replaced: correct once, and frozen from then on.

The fifteen that remain are the ones that were never spacing. Five `Margin="1"`
hairlines, four deliberately asymmetric corner shapes (a chat bubble's tail, a
drop-down, the ends of a segmented control), and six page-level geometry offsets
like `Margin="90,54"`. The first sweep absorbed those too, which was wrong — a 1px
hairline is not a 4px gap and a 90px offset is not a 32px one — and the contract
tests caught it. They are literals because they are geometry, not scale.

Three view contracts asserted the literals they were protecting: `Spacing="20"` on
Quick Terminal and `Spacing="22"` on Appearance, the same intent written two ways,
and the workspace gutter's `"4"`. All now assert the token, since a literal pinned
in a test is the drift made permanent.

**Every repeated shape is a component now.** The card was the biggest but not the
only one. `StatusChip` replaced the status pill, the badge, and the tag — three
classes using three radii and two fills to say the same kind of thing. They differ
in one way that matters, which the reference frames show: a state is a pill, a
label naming a kind (SSH, Docker, Local) is a rounded rectangle. That is one
property, not three classes. Tone is also available as a class, because several
chips take their state from a binding and a property cannot be switched that way.

`EmptyStatePanel` replaced ten hand-built empty states in three different shapes —
tile-and-glyph, bare glyph, and heading-only. `LabeledField` replaced seventeen
vertical fields, each restating the label style and the hint's size, which is how
two fields in the same grid ended up with different gaps. It is deliberately not
`SettingRow`: a row names the control beside it and belongs in a list, a field
names it above and belongs in a grid, and merging them behind a flag would make
every use site declare which of the two it meant.

**Retiring the eight cards.** All 194 uses moved to the one control, and a ninth
turned up during the move — the agent's progress card, a raised surface with a
notice border and a literal radius, which had never been counted. The style classes
are gone; what survives is the one behaviour the surface does not model, a launch
card revealing its actions on hover, which is a statement about the content rather
than about the surface.

The bulk rewrite was where this pass went wrong. Transforming nested cards with
index arithmetic corrupted fourteen files — the close-tag writes landed at stale
offsets and overwrote nine characters of real content each time, turning
`Classes="` and the word `designer` into fragments of a tag. Reverting was not
available, because five of those files held hand-written work from earlier in the
pass. The repair used the one thing that was reliable: each bad write destroyed
exactly nine characters, so the original text was recoverable by finding the
surrounding context in `HEAD` and reading back what had sat between — with the
swept attributes masked, since those no longer matched. That recovered all but a
handful, which were repaired individually and checked against an XML parser.

Nineteen contract tests failed afterwards, every one of them because it identified
a card by the style class it used to carry. They now identify it by its element and
its stated configuration — `Tone="Sunken"`, `Elevation="Overlay"` — which is the
same change the markup made: naming what a thing is rather than what it looks like.
Two of them asserted that a focus ring existed in the theme file for a particular
class; the ring is the component's guarantee now, stated once for every card.

**Where it landed.** Six components carry the interface: `SurfaceCard` (195 uses),
`StatusChip` (25), `LabeledField` (17), `EmptyStatePanel` (10), and
`SettingRow`/`SettingsGroup` (17). Fifteen style classes were retired outright.

What is left is deliberately left. `Muted`, `Monospace`, `Eyebrow` and
`SectionHeading` are typography roles, not components — they say what a piece of
text is and a control would add nothing. The five button classes are the button
set. `IconTile` and `Keycap` are single-purpose shapes with one use each. A design
system is not the absence of style classes; it is the absence of a second answer
to the same question.

**Buttons.** Two things were wrong at once. A button took the same inset as a text
input, because one `ShellControlPadding` token was doing both jobs — the reference
frames draw a primary button at 10x16 and an input at about 9x10, so every button
in the product was wearing the input's. The floor on control height then hid the
difference by holding the button open anyway, which is why it read as a squat pill
with a radius too large for it. Buttons have their own inset now.

The label on a filled accent control was the other. Picking whichever of black and
white has more contrast is the accessible answer and gives near-black on the bronze
accent, which is exactly what the reference frames specify — but on a dark
interface it reads as a warning label rather than as the primary action. Dark
themes take white; light themes keep the measured choice. White on the bronze
accent is about 2.5:1, under the 4.5:1 normal text is meant to clear, so high
contrast keeps the measured choice whatever the theme — that is the one setting
where the trade is not anyone's to make on the user's behalf. A test states each of
the three cases so the decision stays a decision.

**The panel's top corners.** Three attempts had gone at this and each fixed a real
thing without fixing the symptom. The last one was the closest and the most wrong:
the native mask used `CGPathCreateWithRoundedRect`, which rounds all four corners —
but a terminal sits *below* a 36px header, so only its bottom two corners are at
the panel's edge. Those coincided with the card's own corners, which is exactly why
the bottom looked right; the top two were rounded in the middle of the panel and
read as notches. "Bottom corners ok, top still clipped" was a precise description
of a mask doing its job in the wrong place.

The mask is built corner by corner now, and the radii travel per corner from the
view down through `NativeRendererCorners` to a new `ghostshell_terminal_set_host_-
corner_radii_v1`. Which edge counts as the top is read from the view's own
`isFlipped` rather than assumed from AppKit's default, because a path built against
the wrong orientation rounds exactly the two corners it was meant to leave square —
the same symptom, from the opposite cause. The terminal names what it is: `ShellPanelBottomCornerRadius`.
Changing the shim meant re-recording the header, source, artifact-manifest and
payload-manifest digests in the reviewed catalog, which still reports
`releaseReadiness: BLOCKED` — they were re-recorded because the source changed
deliberately, not to clear a gate.

Worth recording that the harness proved the Avalonia side was already correct: a
card with a filled header renders its corner pixels following the curve exactly. It
is in the gallery now as "Panel chrome", so the next person does not spend a fourth
attempt on the half that already worked.

**The New Tab overlay, again.** Three things, one of them the whole complaint. The
chooser cards centred their icon while left-aligning their labels — a fixed-width
tile inside a vertical `StackPanel` cannot stretch, so it centres itself, and the
card read as neither centred nor aligned. The saved-screen rows showed a generic
grid glyph where the reference frames show what the screen actually looks like;
they use the layout preview the Home page already had. And the search field
stretched its content but not itself, so it stopped a third of the way across a
panel it is meant to span.

**Hovering lagged across the whole interface.** Retiring the eight card classes
left one descendant selector pointed at the component instead of at a class:
`controls|SurfaceCard:pointerover StackPanel.CardActions`. A descendant selector is
re-matched up the tree whenever anything it could match changes state, and where it
had covered six launch cards it now covered all one hundred and ninety-five, so
every pointer move over any card in the product paid for a tree walk. Both
directions lagged because both directions are a state change. The ancestor is
qualified by class again.

The lesson generalises: a `/template/` selector is scoped to one control's template
and costs nothing, but a plain descendant selector costs whatever the ancestor
matches. Widening the ancestor from a class to a component is not a rename.

**The count beside a heading rendered as a tall lozenge.** It was a bare `Border` in
a horizontal `StackPanel`, which stretches to the line height of the heading beside
it, so a single digit got a badge twice as tall as it was wide. Those counts are
chips — the component sizes to its content and centres itself — and the keycap style
now states a vertical alignment so the same thing cannot happen to a shortcut hint.

**The app pinned a core at idle, and every pointer event queued behind it.**
Measured rather than guessed: `sample` showed the main thread inside Avalonia's
per-frame run-loop observer, and a counter showed the appearance pipeline
republishing every token **233 times in 30 seconds**. HEAD idled at 0%, so it was
mine.

The cycle: the appearance page lost its Save button in favour of a debounced
auto-commit, and the commit wrote unconditionally. Writing notified the catalog;
the notification rebuilt the terminal-profile editor because its revision was now
stale; rebuilding rebound its combo boxes; each `SelectionChanged` read as a fresh
edit and re-armed the timer. Ninety commits, ninety-one editor rebuilds and three
hundred and sixty-two synthetic edits in twenty-four seconds. Every republish
rewrote sixty resources, and this pass had just multiplied the number of
`DynamicResource` consumers by an order of magnitude, so the cost of a loop that
might once have been survivable became the whole frame budget.

Two fixes, both of which are just correct behaviour. A save that would store what
is already stored does not write — for the theme that is record equality, for the
terminal profile it needs `RepresentsSameAs`, because the palette holds its ANSI
colours in a list and records compare those by reference. That is the same trap
that produced the palette flicker, the search-term rebuild, the command-target
churn, and the Home hover flicker; this time it was costing 80% of a core. And the
page ignores control changes while it is being filled in from the stored profile,
so refilling can never read as editing.

Result: 233 republishes to 1, 90 commits to 1, 80% CPU to 0.0%.

**The corner mask, fourth time.** Extracting the geometry into a pure function and
testing it found a bug that three readings had missed: each edge ran all the way
into its corner before the arc, so `CGPathAddArcToPoint` had no room and the
bottom-right corner came out square. The edges stop a radius short now.

That test is permanent — `scripts/verify-native-corner-mask.sh` compiles the
function out of the shim's own source and asserts which corners are carved, in both
view orientations, and `check.sh` runs it. No .NET test can reach this code, which
is why it kept regressing. The four ways it has been wrong: rounding a layer whose
Metal content ignores `cornerRadius`; rounding all four corners of a view that sits
below a header; assuming AppKit's orientation instead of reading it; and running
each edge into its corner.

**Then the whole design system stopped applying.** Every card lost its border and
fill, every empty state showed only its button, and the active-panel outline
vanished. One cause: while bisecting the CPU spin I reverted `App.axaml` with
`git checkout` to undo a temporary edit, and that took the file back to `HEAD`
rather than to the version a minute earlier — removing the `DesignSystem.axaml`
style include along with every token fallback. The controls kept working because
the tokens are republished at runtime; only the include lives in that file, so all
the control themes went with it and each component fell back to the default
`ContentControl` presentation, which draws its content and nothing else.

Reverting a tracked file to undo an uncommitted experiment throws away every
uncommitted change in it, not the last one. In a session where the working tree is
the only copy of the work, `git checkout <file>` is not an undo.

**The colour wells vanished, and it was the same `git checkout` again.** The
appearance page's swatches render nothing without the colour picker's own theme,
and that `StyleInclude` was uncommitted work the earlier revert destroyed. I put
back the two things I remembered adding and missed the one I had not. A revert of a
tracked file discards every uncommitted change in it — the count of things lost is
not the count of things you remember.

**Enter in the address bar stopped working, and so did back, forward, reload and
stop.** Fixing Close by removing the row's data context broke the five handlers
that resolved the browser host *from* that context. One context cannot answer two
questions: the shell asks it which panel to close, and the browser handlers asked
it which host to drive. The view names its host now and raises browser actions with
it as the sender, so neither lookup depends on what the row happens to point at.

**A no-op activation was compared against the wrong panel.** The receipt check has
a branch for the host returning an unchanged cursor, which is what activating the
panel it already holds produces — and it compares that against the client's captured
graph. With a placeholder selected the capture had no host-backed active panel to
name and reported the first one, so clicking a terminal the host already considered
active was rejected. The tab now remembers the panel the host last had, and
selecting a placeholder leaves it alone.

**Closing a panel left a hole.** Dropping empty tracks does not reclaim a cell
freed in the middle of the grid: the rows and columns it sat in are still occupied
by other panels, so nothing is empty and the gap stays. The older collapse only
handled panels it had recorded a split for, and the place-then-choose flow records
none, so closing one of those reclaimed nothing at all. A neighbour sharing the whole
edge now grows into the cell.

Merging the leftover track boundary as well was a step too far — two tests exist
because preserving saved layout geometry on close is deliberate, so the boundary
stays and the assertion is about area, not track counts.

**An error the user cannot read is the same as no error.** The banner sits against
the window edge and has been seen showing with nothing legible in it. Operation
errors are also written to standard error now; the banner is still the way it is
noticed, but the text survives it.

**Splitting a panel that already spanned two tracks tore the grid.** The split
always opened a new track, even when the panel covered more than one already — and
that track went in at the end of the panel's range, which for a full-height panel is
the end of the grid. The panel then kept the larger share of it, so splitting the
same column twice gave a half and two quarters rather than three thirds, and the
neighbouring column's stacked panels were both stretched over the new row and
overlapped each other.

When a panel spans more than one track the boundary the split needs is already
there, so dividing its span is the whole job: no new track, no other panel touched,
the tracks keep their sizes. Only a panel confined to a single track needs one
opened, and then just the panels reaching that position stretch across it — not
every panel that happened to overlap the split panel's rows. The reported sequence
is a test, and reverting either half of the fix fails it on the exact cell from the
report.

**A split pushed a column through the whole grid.** Opening a track grew only the
panels that strictly straddled the insertion point, which is right when a panel is
added against an edge of the canvas — that is new space, and the panels already
there keep their cells. A split is not new space: the track is carved out of the
panel's own cell, so every other panel covering that cell has to stretch across it.
Splitting the lower of two stacked terminals ran a full-height column down the grid
and left the cell beside the upper one empty.

The two cases are now distinguished. A grid invariant covers it — every cell
belongs to exactly one panel, so a hole and an overlap both fail — checked for all
four edges and both split orientations. Reverting the fix fails it on the exact
empty cell from the report.

**Filling a placeholder put the client's panel list out of order with the host's.**
The new panel was inserted at the placeholder's index so it would land in the right
cell — but where a panel is drawn comes from its assigned layout, not its position
in the list, and that position is compared index by index against the host's own
order, which appends. One filled placeholder was enough to desynchronise the two
lists, and every receipt after it was rejected as a mismatched graph. The panel is
appended now and still takes the placeholder's cell through its layout.

**The shell took the keyboard off the terminal and never gave it back.**
`RequestInputFocus` read as: if Avalonia thinks this control is focused, focus the
surface; otherwise call `Focus()`. But Avalonia never sees focus enter a native
child view, so `IsFocused` is false even while the surface owns the keyboard — the
second branch ran every time. `Focus()` moves the keyboard to Avalonia's own
top-level view, and nothing ever handed it back, so the native view stopped being
first responder and `keyDown:` was never called. The terminal kept drawing output
and ignored typing. Every caller is a panel becoming active, so it happened
constantly.

The diagnostics are what settled it: the gate was installed and refused nothing,
which meant keystrokes were never arriving to be refused. Both steps are now taken —
Avalonia's logical focus for the shell's own tracking, then the native focus, which
is the only thing that can make the surface first responder.

**A placeholder is not part of the host's graph, and three places assumed it was.**
Activating one asked the host about an id it had never issued. Capturing the graph
counted it, so the client's tab looked a panel wider than the host's and every
receipt compared against that capture read as invalid — the failure that appeared
after a few splits. And answering a placeholder called the toolbar's "new terminal"
action, which opens a whole tab; the terminal landed in a new tab while the cell the
user had just placed stayed empty.

Placeholders are now excluded from the captured host graph and from projection
validation, applying a projection no longer drags the selection off the one being
answered, and the five choose-what-to-open handlers call the panel-level operations
instead of the tab-level ones. A failed receipt also meant the commit never ran, so
the client's revision stopped advancing and later host calls kept failing — which is
the most likely reason the keyboard stayed dead, though that is not confirmed.

**Placing a panel cost the workspace its keyboard.** The place-then-choose flow
creates a placeholder with a client-invented id and activates it. The session
host's workspace graph has never heard of that id, so activation came back
`not_found` — the error the toast was showing. The damage was downstream: the
failed activation left client and host out of step, the panel never received the
interactive attachment that grants keyboard authority, and the physical-input gate
then refused every keystroke. The terminal kept drawing output while ignoring
typing, which reads exactly like a dead surface and sent three rounds of fixes into
the native shim, where nothing was wrong.

A placeholder is local until the user fills it: activating and discarding one no
longer round-trips to the host, and the host learns about the panel when it becomes
a real one. Two tests cover the local paths; the guard in `ActivatePanelAsync`
itself is uncovered, because `RuntimeWorkspace` has a private setter and cannot be
seated from a test.

**The shell was quoted out of recognition.** libghostty takes one command string
and gives it to two readers: `/bin/sh`, which runs it, and its own argument parser,
which reads the first word to work out which shell this is and inject the matching
integration. They disagree about quoting. The parser is Zig's
`ArgIteratorGeneral`, configured without single-quote support, so `'` is emitted as
an ordinary character — the shim quoted every word, so `/bin/zsh` reached the parser
as `'/bin/zsh'` and it went looking for a shell named `zsh'`. It matched nothing,
injected nothing, and without shell integration there are no prompt markers, so
`cursorIsAtPrompt` was false forever and every close asked to confirm.

Turning on libghostty's own logging with `GHOSTTY_LOG=stderr` is what showed it:
"shell could not be detected, no automatic shell integration will be injected". On
macOS the library logs to unified logging and, in a `-Dapp-runtime=none` build,
stderr logging is off by default — which is why this had been silent through several
rounds of looking in the wrong place.

Words that cannot be expanded are now passed through bare, which both readers read
the same way. Anything else is still quoted: failing to detect the shell is a much
smaller problem than letting a path expand.

**Why the terminal could never tell idle from busy.** libghostty does handle shell
integration itself — `termio/Exec.zig` injects it when it spawns the child, and the
host writes none of it. But it first needs a resources directory, and without one
it logs "no resources dir set, shell integration disabled" and gives up. It reads
that directory from `GHOSTTY_RESOURCES_DIR`, in native code.

We set that variable with `Environment.SetEnvironmentVariable`, which on Unix
updates only the runtime's own copy — the native environment `getenv` reads is
untouched. So the variable was set, visible to us, and invisible to the engine.
No resources directory, no shell integration, no prompt markers, and
`cursorIsAtPrompt()` false forever. It is published with `setenv` now, and two
tests state the platform behaviour so nobody simplifies it back.

terminfo is fine, contrary to a note here earlier: the engine looks for it beside
the resources directory rather than inside it, and the build already stages and
copies it there. `infocmp xterm-ghostty` resolves against the packaged copy.

**Typing did nothing after the panel chrome landed.** `-hitTest:` takes a point in
the *superview's* coordinate space; `handleLocalEvent:` was converting the click
into the terminal view's own space instead. The two agree only while the view sits
at its superview's origin, which stopped being true once the panel chrome inset it.
The test then returned some other view, the branch bailed out before
`makeFirstResponder:`, and the surface never took the keyboard — so every keystroke
went somewhere else. It converts through the superview now.

**The corner mask cropped rather than rounded.** It was built once from `self.bounds`
and never rebuilt, but the radii are bound before the first layout, so it was
computed against an empty rect. It is rebuilt from `-layout`, and an empty bounds
now clears the mask instead of installing a zero-area one that would hide the whole
surface.

**The close confirmation claimed a running foreground process that was not there.**
libghostty answers `needsConfirmQuit` with `!cursorIsAtPrompt()`, and that needs the
shell to emit prompt markers. Where it does not, the answer is permanently "confirm"
— and the copy turned that into an assertion about a process. It now says what is
actually known: the session could not be confirmed idle, and closing ends it. The
detection itself is only as good as the shell integration behind it; that is worth
fixing separately rather than papering over with a claim.

**Resizing could shrink a track under the panel in it.** The drag clamped to a
constant hundred and twenty pixels, so a panel whose own minimum was larger drew
outside the space it had been given and the layout came apart. The clamp is the
largest minimum among the panels either side of the boundary now, spread over the
tracks each one spans, with the constant only as a floor.

**Adding a panel asked what before where.** A modal over the whole window asked
which adapter to open, and the layout then appended the result wherever it liked.
It is the other way round now: the plus offers the four edges, a split button on
each panel header offers the two axes, and either gesture places an empty panel
which asks what to open *in the space it will occupy*. Choosing tells the tab which
placeholder is being answered, so the created panel takes that exact cell instead
of being appended — the existing creation paths are unchanged, they just land
somewhere chosen.

Inserting a track shifts everything at or after it along, and a panel that straddles
the insertion point grows rather than being torn in two.

**Closing a panel left a hole, and the next one was appended past it.** Removing a
panel never recomputed the grid: the survivor kept the cell it had, so half the
canvas stayed empty, and because the track count never shrank the next panel was
appended after the gap — which is exactly "close one and the other does not fill,
add a third and it divides by three with empty space". They were one bug. The tab
compacts its tracks on removal now, dropping the ones nothing occupies and
renumbering the rest, with spans preserved by counting how many of the tracks a
panel covered are still occupied.

**Two regressions from the browser rewrite.** Setting a data context on the whole
header row to reach the browser host broke Close, because the shell resolves which
panel to act on from the sender's data context and the sender was no longer inside
the panel's. It also dropped the title. Browser state is bound by element name now
and the row keeps the panel as its context; the contract test says why, so the
next person does not re-point it.

**The count beside a heading, again.** Fixed once on the overlay and missed on Home,
which uses a `CountPill` component rather than an inline border — and that
component had the same defect, a `UserControl` with no vertical alignment
stretching to the heading's line height. It renders a chip now, and the alignment
that stops the stretch is on its root where it belongs.

**The corner, actually found.** It was never the native surface. `ClipsContent`
put `ClipToBounds` on the *same* `Border` that draws the card's outline, so the
border clipped its own stroke: at a rounded corner that removes the stroke's outer
half and the arc comes out as a dark diagonal notch between two straight edges.
The fill was rounded correctly the whole time, which is why every pixel probe of
the *background* said the corner was fine — I was measuring the one thing that was
never broken.

The template is two borders now: the outer draws the outline, the inner clips the
content. The card itself no longer clips either, for the same reason.

That explains the shape of the whole failure. "Top corners clipped, bottom fine"
was exact: the notch shows against the header's lighter fill at the top and hides
against the dark panel body at the bottom. Three previous fixes — the Metal-layer
mask, per-corner radii, the view orientation — were all real bugs in the native
surface, and none of them was this one. And the thing that finally found it was
cropping the corner and putting it beside the reference frame at eight times zoom,
which took a minute and should have been the first move rather than the twentieth.

**The harness finally renders panels.** It had tabs but never any panels, so the
panel card and its header — the exact chrome the corner argument was about — were
never in a capture. Two `UnavailableRuntimePanelViewModel`s and an active tab fixed
that, and the corner reads clean: the page background recedes along the curve at
every corner and the header fill follows it. The Avalonia side has been right the
whole time; whatever remains is the native surface, which no in-process render can
show. Four attempts were spent on this partly because the one view that would have
settled it was the one the harness did not draw.

**The browser panel spent a hundred pixels on chrome.** A header, a separate
navigation strip beneath it, and a status bar at the bottom repeating the address
already shown in the address box. Navigation belongs beside the title; it is one
forty-pixel row now, and the bottom bar is gone because it said nothing the address
box did not.

**Panels could not be resized, and there was nothing to resize.** The canvas
divided itself evenly — `size.Width / columns` — so the split was decided when the
layout was chosen and never again. The tab now carries a weight per track, and the
gap between two panels is the handle: the layout panel hit-tests the gutters,
shows the resize cursor, and a drag moves the boundary between the two tracks it
separates. Doing it in the layout panel rather than with splitter controls keeps
the visual tree unchanged — a splitter between every pair of tracks in a spanning
grid is a lot of controls to place, and none of them would understand spans. A drag
past the limit stops at it rather than being refused, which is what makes it feel
like a wall instead of a fault.

One ordering lesson worth keeping: the chip's shape has to be stated after its
tone in the control theme, or a rounded label's fill is undone by whatever tone it
also carries. The first attempt got this backwards and the badges rendered as bare
text — caught by a capture, not by a test, which is the case for keeping the
gallery.

The Quick Terminal settings page is the worked example: 290 lines of hand-built
cards with four disagreeing column widths, ten hand-placed separators, and a
description style restated fourteen times, down to 197 lines that say only what
each setting is.

final result: passed

## New Panel inset and deterministic panel resizing — updated 2026-07-28

### Comparison setup

- Source visual truth:
  `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-47f36bb3-4387-4b64-b1b6-c1e2d9d43d6f.png`
- Rendered implementation:
  `design-qa-artifacts/new-panel-implementation.png`
- Same-input comparison:
  `design-qa-artifacts/new-panel-comparison.png`
- Resize evidence:
  `design-qa-artifacts/panel-layout-wide.png` and
  `design-qa-artifacts/panel-layout-compressed.png`
- State: dark desktop shell, live local terminal, New Panel placeholder on the
  right, catalog scrolled to its top.
- Viewport and normalization: the source and implementation catalog regions are
  both 1014 × 398 pixels. Both are 2× Retina captures of a 507 × 199 logical
  region, so no density resampling was required. The surrounding implementation
  was captured from a 1440 × 900 logical app window at 2×.

The full source crop is already a focused component view, so a second crop would
remove the panel edge needed to judge its inset. The full-view comparison is
therefore also the focused-region comparison.

### Comparison history and fixes

- [P2] The New Panel search row reserved column spacing for a hidden close
  action, creating a visibly larger right inset than the left inset. The shared
  chooser now places the spacing on the close button itself, so the spacing is
  present only when that button is visible. The post-fix comparison shows
  balanced 50 px logical left and right search insets.
- [P1] Panel minimums were enforced twice: the canvas exceeded the finite window
  size inside a scroll host, while pointer dragging separately applied an
  approximate per-track floor. Resizing the window could therefore change which
  limiter won and make the next drag feel sticky or arbitrary. The canvas now
  always arranges to the finite viewport, and one tab-owned policy constrains
  divider movement from real panel edges, spans, and minimums.
- [P2] Pointer overshoot accumulated while a divider was at its limit, so
  reversing direction first had to traverse an invisible stale delta. Every
  pointer delta is now consumed even at a constraint, and lost pointer capture
  clears the drag state.

### Required fidelity surfaces

- Fonts and typography: the shared chooser keeps the existing family, optical
  weights, sizes, line heights, truncation, and small uppercase panel label.
- Spacing and layout rhythm: the search field has balanced side insets; header
  height, section gap, card widths, radii, and vertical rhythm remain unchanged.
- Colors and visual tokens: the implementation continues to use the existing
  dark surface, neutral border, muted copy, and orange active-border tokens.
- Image quality and asset fidelity: there are no raster assets in the compared
  region; the existing Fluent vector icons remain unchanged and sharp.
- Copy and content: all chooser labels and search guidance match the shared
  catalog; no new or placeholder product copy was introduced.

### Interaction and regression checks

- [x] A finite window constrains the panel canvas instead of producing a larger
  scrollable minimum canvas.
- [x] Divider motion is expressed in pointer pixels against the current viewport.
- [x] Minimums constrain whole panel edges, including panels spanning multiple
  grid tracks; an internal boundary does not incorrectly constrain the spanning
  panel.
- [x] Shrinking the window does not rewrite stored split weights or make the next
  drag jump.
- [x] Dragging past a limit stops at the limit; reversing begins on the next
  pointer delta.
- [x] Lost pointer capture resets hover, drag, and resize-cursor state.
- [x] Formatting verification and the full solution build pass with zero warnings
  and zero errors.
- [x] The full test suite passes: 3,760 passed and 1 explicitly skipped native
  vault integration test.

No actionable P0, P1, or P2 findings remain in the reported regions.

final result: passed

## Catalog header and browser blank-address polish — updated 2026-07-28

### Comparison setup

- Source visual truth:
  `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-d5369cf4-896e-468b-a422-acc9e02e386b.png`
  (864 × 286) and
  `/var/folders/vd/mq6r33rs1h30tgd81x06zt7w0000gp/T/codex-clipboard-6f3cd428-2d12-4076-ba9d-1ab52745b79b.png`
  (1084 × 348).
- Implementation evidence:
  `design/qa/current/catalog-header-polish-implementation.png` and
  `design/qa/current/browser-placeholder-focused-implementation.png`
  (3456 × 2234 pixels at a 1728 × 1117 CSS-point desktop viewport and 2×
  density).
- Combined comparison evidence:
  `design/qa/current/catalog-header-polish-comparison.png` (980 × 542) and
  `design/qa/current/browser-placeholder-comparison.png` (1120 × 620).
- State: a placed-panel chooser with one saved connection and one saved screen,
  followed by a live blank browser panel at rest and with the address editor
  focused.
- Density normalization: focused implementation regions were cropped at native
  2× density. The source and implementation were centered without resampling so
  typography, borders, and focus treatment remained inspectable at their native
  sharpness.

### Findings and comparison history

- [P2] The catalog heading row originally let the heading, count, and action
  measure against different vertical constraints. The shared chooser now uses
  the existing `CountPill`, centers the heading group, and removes the text
  action's unrelated minimum control height. The post-fix comparison shows both
  counts and actions on the heading's optical center.
- [P2] `ListRow` supplied a leading separator to the first generated catalog
  item, producing a second horizontal line beneath the card's own rounded top
  border. Catalog rows now defer surrounding chrome to `SurfaceCard`; the
  post-fix comparison shows one clean rounded outline and no inset top rule.
- [P2] `about:blank` was editor content rather than blank-state guidance. The
  browser host now exposes empty editor text for the blank address, the textbox
  presents `about:blank` as placeholder copy, and focus removes it. The combined
  evidence contains both the resting placeholder and focused empty editor.

### Required fidelity surfaces

- Fonts and typography: existing shell font families, weights, sizes, and
  truncation are unchanged; count chips and section actions now share the
  heading's vertical rhythm.
- Spacing and layout rhythm: header alignment and card-top chrome match the
  supplied intent; no new padding or panel geometry was introduced.
- Colors and visual tokens: all surfaces, borders, muted placeholder copy, and
  orange focus treatment continue to use existing shell tokens.
- Image quality and asset fidelity: no raster or illustrative assets are present
  in these regions; the existing Fluent icons remain sharp.
- Copy and content: catalog labels are unchanged. `about:blank` remains visible
  only as blank-state guidance and disappears while the user edits.

### Interaction and regression checks

- [x] Blank browser shows `about:blank` as muted placeholder copy.
- [x] Focusing the address field removes the placeholder and shows an empty
  caret-ready editor.
- [x] Count chips and section actions align with both saved-list headings.
- [x] The catalog's first rows have no duplicate top separator.
- [x] The user confirmed both reported defects are fixed in the running build.
- [x] Focused architecture contracts and the desktop build pass.

No actionable P0, P1, or P2 differences remain in the reported regions. Focused
regions were required because the defects were line-level alignment, one-pixel
chrome, and input-focus behavior; the full desktop captures establish that the
surrounding panel layout remains intact.

final result: passed

## Shared New Tab / New Panel catalog — updated 2026-07-28

### Comparison setup

The user-provided New Session reference and the running 1440 × 900 desktop build
were normalized and inspected together in
`design/qa/current/new-item-catalog-comparison.png`. The panel state was then
captured separately in `design/qa/current/new-panel-shared-catalog.png`, with the
same catalog rendered inside a placed panel and only the Workspaces section
disabled. `design/qa/current/home-icon-tab.png` records the icon-only Home tab,
and `design/qa/current/new-panel-browser-working.png` records a browser created
by selecting Browser from the placed-panel catalog.

### Visual and interaction checks

- [x] New Session and New Panel render one `NewItemChooserView`; the overlay and
  panel are wrappers and do not maintain separate chooser markup.
- [x] Search, the five creation cards, saved connections, and saved screens keep
  the same hierarchy, labels, icons, spacing, radii, borders, and catalog row
  treatment in both hosts.
- [x] New Panel hides Workspaces and the duplicate overlay close action, while
  retaining its own panel header and close action.
- [x] Narrow-panel chooser content is constrained by the actual card width, so
  titles and descriptions ellipsize instead of measuring to zero or disappearing.
- [x] Selecting Browser from New Session creates the first runtime workspace and
  tab; selecting Browser from a placed panel replaces that exact placeholder.
- [x] Home is a persistent predefined tab in both routes and is rendered as a
  single 32 × 32 icon with a tooltip and accessible name.
- [x] The New Session comparison preserves the supplied five-card row,
  Workspaces strip, two-column saved-catalog region, dark neutral palette, and
  orange active treatment. Differences are limited to the implementation's
  current typography metrics and the real persisted catalog data.
- [x] Native build, formatting verification, 185 architecture tests, and 788 app
  tests pass after the shared-component extraction.

### Result

The New Panel surface no longer invents or owns a second chooser design. The
shared catalog remains coherent in the overlay and in both narrow and wide panel
placements, Browser creation works from both entry points, and Home remains an
icon-only predefined tab.

final result: passed

## Latest verification status — 2026-07-28

The New Panel inset and deterministic panel-resizing report above is the latest
verification pass. Its same-input visual comparison, native resize exercise,
build, formatting check, and full solution test run have no remaining actionable
P0, P1, or P2 finding.

final result: passed
