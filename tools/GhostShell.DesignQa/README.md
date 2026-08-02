# GhostSHELL design QA harness

Renders the product's real `MainWindow`, styles, and view models at a 1440 × 900
logical viewport and writes one PNG per route. Use it to compare the running UI
against `design/` without hand-driving the app.

```sh
./.dotnet/dotnet run --project tools/GhostShell.DesignQa -- design/qa/current
```

It captures 20 shell routes plus 6 modal editors and confirmations. Dialogs are
shown off-screen and rendered at their own arranged size, so a capture reflects
the dialog's real geometry rather than a fixed frame.

Some routes vary the appearance rather than the shell route. A route may carry a
`ThemePreference`, which is republished through the product's own appearance
mapper before the capture. `appearance-corners-tight` and
`appearance-corners-round` use this to pin that the corner-radius and density
settings actually reshape the interface: the two themes differ only in those two
values, so if either setting stops reaching the styles the pair becomes two
identical images.

Pass route names to capture a subset:

```sh
./.dotnet/dotnet run --project tools/GhostShell.DesignQa -- design/qa/current launcher-home settings-appearance
```

## Why it exists

Screenshotting the real window needs macOS Screen Recording permission for the
host process, and driving it needs Accessibility permission. This harness
renders in-process through `RenderTargetBitmap`, so it needs neither, never
captures unrelated windows, and produces byte-stable output for diffing.

It runs on Avalonia's headless platform with real Skia drawing: no window ever
appears on screen and nothing steals focus, while text, layout, and colour
render exactly as the desktop platform would draw them offscreen.

## What it does and does not prove

It uses the compiled application resources, the real `MainWindow`, the real
`MainWindowViewModel`, and the shipped styles, so layout, typography, spacing,
and colour are faithful. It replays the app's own appearance-resource mapping so
`ShellFontSize*` and the platform metrics resolve exactly as they do at runtime;
without that step every `DynamicResource` size silently falls back and captures
misreport the typography.

Collaborators are deterministic and in-memory. The harness never touches SQLite,
the OS vault, terminal sessions, or the user's profile, and it writes nothing to
any store. `QaData` is sample content shaped to the density of the Pencil
reference frames — it is not, and must not be presented as, the user's real
connections, screens, or sessions.

The agent runs offline (`QaOfflineAgentRuntime`), so the workspace route shows
the product's genuine "no provider configured" boundary rather than a simulated
conversation. Routes that need a live PTY render an empty canvas; this harness
does not replace live-terminal or interaction acceptance.

The reference frames were drawn with `#FF8400` as the example host accent, so
the harness supplies that accent to keep captures directly comparable. The
product's own bronze fallback still applies whenever a host reports no accent.
