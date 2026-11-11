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

## Documentation images

Regenerate the README gallery without building or launching the application:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\design\screenshots.ps1
```

This renders the current source XAML at 2× resolution using synthetic text,
image, and device fixtures. The drop-to-shelf scene inserts the real drop-target
XAML into an illustrated desktop gesture. It is labeled as an illustration in
the README, not as a live desktop capture. No clipboard or user files are read.

## Packaging a release

Install the .NET 10 SDK and [Inno Setup 7](https://jrsoftware.org/isinfo.php), then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

Use `-IsccPath "C:\path\to\ISCC.exe"` if the compiler is not discovered.
The package script reads `Version` and `FileVersion` from the project, publishes
one self-contained x64 payload, and creates matching Setup and Portable packages
plus `SHA256SUMS.txt` under `artifacts/releases/<version>/`. Existing output is not
overwritten; use a new `-OutputRoot` for a rebuild.

The installer uses a stable AppId for upgrades, runs per-user, and checks the
application mutex before install/uninstall. It must never delete the separate
Local AppData directory. Startup is opt-in through application Preferences.

Publish both packages as assets of the **same release tag**. Mark beta versions
as GitHub prereleases and include known limitations and signing status in the notes.
This script only builds local packages; it does not upload or publish a release.
