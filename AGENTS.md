# BetterQuickSettings development

- All application UI, tooltips, accessibility labels, notifications, errors, and new documentation must be in English. User files, device names, and media metadata keep their original text.
- Preserve the compact glass design. Avoid branding, slogans, decorative copy, and persistent diagnostic text in the panel.
- The user is iterating on the source. Do not build, publish, run compilation-based checks, or launch the native app until asked. Use `design/preview.ps1` for non-activating, source-XAML previews.
- File shelf operations must copy incoming files, preserve originals, persist across restarts, and export real file-drop data. Removing a shelf entry must never delete its original source.
- Keep slow file, image, and hardware operations off the UI and input-hook threads. Do not read ordinary clipboard content as screenshot history.
- Preserve the glass implementation and its native `NoResize` window with zero resize border. Height changes use the client-area grip. Local reference builds and recovery records, when present, stay under ignored `artifacts/`.
