# Dark Mode Adaptation

## Context

The user requested a complete implementation of the full dark mode adaptation
described in `docs/reference/dark-mode-adaptation.md`.

Relevant facts:

- The GUI is a legacy .NET Framework 4.8.1 WPF project:
  `GUI/GARbro.GUI.csproj`.
- Existing uncommitted changes in `GUI/MainWindow.xaml`,
  `GUI/MainWindow.xaml.cs`, and `GUI/Strings/guiStrings.*.resx` predate this
  implementation and must be preserved.
- The migration must cover every in-process WPF UI surface in `GUI/*.xaml` and
  WPF option widgets under `ArcFormats/**`.
- Future UI changes must remain compatible with light and dark themes through
  `AGENTS.md` and `$ui-dark-mode`.

## Acceptance Criteria

- `System`, `Light`, and `Dark` theme preferences exist, persist, and can be
  changed from the Settings window.
- The effective theme is applied before the main window is shown.
- Existing windows update when the theme changes at runtime.
- Native title bars use dark chrome when supported.
- Main window, all GUI dialogs, TextViewer, SettingsWindow, menus, context
  menus, toolbar, status bar, lists, and `ArcFormats` option widgets are
  readable in both light and dark themes.
- Hard-coded theme-dependent color scans have no unexplained matches outside
  theme dictionaries and allowed exceptions.
- GUI builds successfully with Visual Studio MSBuild when the local toolchain is
  available.
- Documentation and repo-local skills remain aligned with the implementation.

## Implementation Checklist

- [x] Add WPF theme dictionaries and project items.
- [x] Add `ThemeManager` with theme preference resolution, dictionary swapping,
  system theme detection, and dark title bar support.
- [x] Add persisted theme setting and localized Settings UI labels.
- [x] Apply theme resources to `MainWindow.xaml`.
- [x] Apply theme resources to every `GUI/*.xaml` surface.
- [x] Apply inherited/semantic theme resources to `ArcFormats/**/*.xaml`
  widgets.
- [x] Audit and adapt toolbar/dialog image resources where necessary.
- [x] Update docs and skill guidance if implementation choices differ from the
  reference plan.
- [x] Build and run focused smoke checks.
- [x] Run hard-coded color scans and document allowed exceptions.

## Validation Checklist

- [x] Validate the `$ui-dark-mode` skill metadata.
- [x] Run hard-coded XAML/C# color scans.
- [x] Restore packages if needed.
- [x] Build `GUI/GARbro.GUI.csproj` in Debug.
- [x] Launch the GUI briefly for a startup smoke check.
- [x] Manually inspect light mode.
- [x] Manually inspect dark mode.
- [x] Manually inspect `System` mode behavior when possible.

## Progress

- 2026-05-24: Created execution plan and loaded `$ui-dark-mode`,
  `$docs-sync`, and `$garbro-build-verify`.
- 2026-05-24: Added theme dictionaries, `ThemeManager`, persisted theme
  preference, Settings UI, and first full XAML/resource pass.
- 2026-05-24: Fixed visual smoke findings for dark `GridViewColumnHeader`,
  `ScrollBar`, and `ComboBox` rendering.
- 2026-05-24: Completed builds, hard-coded color scans, skill validation,
  startup smoke, and Light/Dark screenshot checks.
- 2026-05-24: Fixed dark menu popup rendering by replacing the inherited WPF
  `MenuItem`, `ContextMenu`, and `Separator` templates with theme-aware
  templates.

## Decision Log

- Use local WPF ResourceDictionaries rather than adding a third-party theme
  framework, matching the reference plan and the legacy project structure.
- Preserve existing uncommitted user changes and apply dark mode work on top of
  them.
- Theme both non-editable and editable `ComboBox` paths so Settings, toolbar
  selectors, ArcFormats option widgets, and editable mask input inherit dark
  compatible rendering.

## Outcomes

- Implemented full in-process WPF light/dark theme support with persisted
  `System`, `Light`, and `Dark` preferences.
- Added durable dark-mode guidance to `AGENTS.md`,
  `docs/reference/dark-mode-adaptation.md`, and `$ui-dark-mode`.
- Validation passed with Visual Studio MSBuild for `GUI/GARbro.GUI.csproj` and
  `ArcFormats/ArcFormats.csproj`.
- Hard-coded theme-dependent XAML/C# color scans have no matches outside theme
  dictionaries.
- Visual smoke checks verified the main window and Settings window in dark
  mode, plus the main window in light mode. `System` mode was startup-checked
  against the current OS preference; live OS theme switching was not forced.
- A follow-up visual smoke check verified the dark View menu popup no longer
  renders bright default WPF borders.
