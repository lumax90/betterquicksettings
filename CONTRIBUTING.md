# Contributing

Issues and pull requests are welcome. For UI or hardware issues, include the
Windows build, display scaling, relevant device model, and reproduction steps.
Use sample content in screenshots and diagnostics.

## Project conventions

- Keep the application UI and documentation in English.
- Use the existing WPF/Win32 architecture and compact visual design.
- Keep image, file, and hardware work off the UI and input-hook threads.
- Preserve original files when changing shelf behavior.
- Keep the native `NoResize` window and zero resize border used by the glass
  backdrop. Panel height is adjusted through a client-area grip.
- Preserve the legacy storage and clipboard protocol identifiers used for
  upgrades from early previews.

## Layout

| Directory | Purpose |
| --- | --- |
| `Core/` | Data models and routing rules |
| `Services/` | Audio, brightness, media, storage, clipboard, and shell integration |
| `Interop/` | Native Windows interop and drag feedback |
| `UI/` | WPF styling, view models, transitions, and auxiliary windows |
| `checks/` | Storage/routing checks and opt-in native diagnostics |
| `design/` | Source-XAML preview and icon generation |

Build with `dotnet build BetterQuickSettings.csproj -c Release`. The check runner
is `checks/BetterQuickSettings.Checks.csproj`.

For native debugging, the application accepts `--glass-check`,
`--interaction-check`, `--product-check`, `--external-drag-check`,
`--clipboard-check`, `--taskbar-drop-check`, `--volume-feedback-check`, or
`--cadence-check`, followed by an output directory. For example:

```powershell
dotnet bin/Release/net10.0-windows10.0.19041.0/BetterQuickSettings.dll --glass-check artifacts/glass-check
```

These are opt-in desktop diagnostics: some move the pointer, use the clipboard,
or play a sound. Run them with the normal application closed. Use isolated data
with `--data-dir` when investigating ordinary application startup.

Generated output and local recovery records belong under `artifacts/` and are
excluded from version control.
