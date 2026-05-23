# Dark Mode Adaptation Plan

This document defines the full dark mode migration for the GARbro-Mod-Onachi
WPF GUI and the rules future UI changes must follow.

## Goals

- Support full light and dark themes across every in-process WPF UI surface.
- Persist a user preference with three modes: `System`, `Light`, and `Dark`.
- Allow runtime theme switching without restarting when practical.
- Keep archive, image, audio, script, and scheme behavior unchanged.
- Make future UI changes theme-aware by default.

## Non-Goals

- Do not migrate the application to a new UI framework.
- Do not introduce a large theme package unless local WPF styling proves
  insufficient.
- Do not theme native Windows dialogs owned by the OS, such as file pickers.
- Do not modify generated `*.Designer.cs` files directly.

## Current State

The GUI project is a legacy .NET Framework 4.8.1 WPF application:

- `GUI/App.xaml` has application resources but no merged theme dictionaries.
- `GUI/MainWindow.xaml` owns most custom UI styling and hard-coded colors.
- `GUI/SettingsWindow.xaml` contains custom TreeView templates and hard-coded
  selection gradients.
- Several dialogs use `SystemColors.*` dynamic resources for the base
  background, which is a useful starting point but is not enough for dark mode
  on .NET Framework WPF.
- `GUI/TextViewer.xaml` already binds text viewer backgrounds to a dynamic
  window brush but still needs foreground and selection verification.
- `ArcFormats/**/Widget*.xaml` and `Create*Widget.xaml` files are embedded into
  `GUI/ArcParameters.xaml`; most are simple WPF controls, but validation
  adorners in a few widgets hard-code red.

At the time this plan was written, the main theme blockers were:

- `MainWindow.xaml`: list row brushes, input background, list foreground,
  preview background, splitter and preview borders, sort arrows, stop icon.
- `SettingsWindow.xaml`: section title gradients, TreeView item selection,
  expander glyph colors, content border.
- `AboutBox.xaml` and `UpdateDialog.xaml`: black borders and light content
  backgrounds.
- `ArcFormats` widgets: validation red should become a semantic error brush.

## Theme Architecture

Add a small local theming layer inside `GUI/`:

```text
GUI/
  Themes/
    Theme.Shared.xaml
    Theme.Light.xaml
    Theme.Dark.xaml
  ThemeManager.cs
```

`Theme.Shared.xaml` should contain implicit control styles and resources that do
not differ by theme. `Theme.Light.xaml` and `Theme.Dark.xaml` should contain
only theme values and theme-specific image resources.

Load `Theme.Shared.xaml` once from `App.xaml`. Load either `Theme.Light.xaml` or
`Theme.Dark.xaml` through `ThemeManager`, replacing only the active theme
dictionary in `Application.Current.Resources.MergedDictionaries`.

Use `DynamicResource` for every theme-dependent brush, pen, and image source so
open windows can update when the theme changes.

## Theme Preference

Add a user-scoped setting in `GUI/Properties/Settings.settings`:

```text
Name: appTheme
Type: System.String
Default: System
Allowed values by convention: System, Light, Dark
```

Update `GUI/Properties/Settings.settings` and keep
`GUI/Properties/Settings.Designer.cs` synchronized through the settings
generator. If the generator is unavailable in the local environment, a manual
generated-file sync is allowed only in the same change as the `.settings`
source update.

Expose the setting in `SettingsWindow` as a fixed-set option under the viewer or
general UI settings section. The visible labels should be localizable:

- Follow system
- Light
- Dark

Use existing settings patterns such as `GuiResourceSetting` and
`FixedSetSetting` when they fit. If the current settings abstraction cannot
represent the theme option cleanly, add the smallest local setting wrapper
needed.

## System Theme Detection

For `System` mode on Windows, detect the app theme with:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
AppsUseLightTheme
```

Interpret missing or unreadable values as light theme. Subscribe to
`Microsoft.Win32.SystemEvents.UserPreferenceChanged` and re-resolve the theme
when the preference category indicates a visual/theme change.

All registry and event handling must be best-effort. Theme detection failure
must not block startup. Dispatch theme application back to the WPF dispatcher
when the system event is raised off the UI thread, and unsubscribe on
application exit.

## Window Chrome

Apply dark title bar chrome when the effective theme is dark:

- Use `DwmSetWindowAttribute` best-effort from `ThemeManager` or a small helper.
- Try `DWMWA_USE_IMMERSIVE_DARK_MODE` attribute `20`, then fallback to `19`.
- Apply after each window source is initialized.
- Reapply when the effective theme changes.
- Wrap all calls in `try/catch`; unsupported OS versions must continue normally.

Do not replace the native window chrome with a custom title bar unless native
DWM theming is demonstrably insufficient.

## Resource Keys

Use semantic resource keys instead of literal color names. Recommended minimum
set:

```text
Gar.Brush.WindowBackground
Gar.Brush.WindowText
Gar.Brush.ControlBackground
Gar.Brush.ControlText
Gar.Brush.ControlBorder
Gar.Brush.ControlDisabledText
Gar.Brush.MenuBackground
Gar.Brush.MenuText
Gar.Brush.ToolbarBackground
Gar.Brush.StatusBarBackground
Gar.Brush.ListBackground
Gar.Brush.ListText
Gar.Brush.ListAlternateBackground1
Gar.Brush.ListAlternateBackground2
Gar.Brush.ListHoverBackground
Gar.Brush.ListSelectedBackground
Gar.Brush.ListSelectedText
Gar.Brush.ListInactiveSelectedBackground
Gar.Brush.PreviewBackground
Gar.Brush.Splitter
Gar.Brush.SectionHeaderBackground
Gar.Brush.SectionHeaderText
Gar.Brush.Accent
Gar.Brush.AccentText
Gar.Brush.Error
Gar.Brush.Warning
Gar.Brush.FocusOutline
Gar.Brush.ScrollbarTrack
Gar.Brush.ScrollbarThumb
```

Add keys only when a real semantic distinction exists. Do not create one-off
keys named after a file or a single control unless the control is genuinely
special.

## Control Styling

The migration is full-scope. Standard controls must remain readable in both
themes, not only custom panels. `Theme.Shared.xaml` should define implicit or
keyed styles for at least:

- `Window`
- `TextBlock`
- `TextBox`
- `PasswordBox` if introduced later
- `Button`
- `ToggleButton`
- `CheckBox`
- `RadioButton`
- `ComboBox`
- `ListView`
- `ListViewItem`
- `GridViewColumnHeader`
- `TreeView`
- `TreeViewItem`
- `Menu`
- `ContextMenu`
- `MenuItem`
- `StatusBar`
- `TabControl`
- `TabItem`
- `Expander`
- `ScrollViewer`
- `ToolTip`
- `Separator`

Prefer targeted implicit styles over copying entire default control templates.
Use custom templates only where WPF default rendering cannot be made readable
with setters.

## Main Window Work

`GUI/MainWindow.xaml` is the highest priority surface.

Required changes:

- Move `AlternateColor1`, `AlternateColor2`, and `InactiveInputBackground` into
  theme dictionaries.
- Replace `Foreground="Black"` on `CurrentDirectory` with
  `Gar.Brush.ListText`.
- Replace `ImageView` `LightGray` with `Gar.Brush.PreviewBackground`.
- Replace splitter and preview border black with `Gar.Brush.Splitter` or
  `Gar.Brush.ControlBorder`.
- Replace sort arrow `Gray` with a theme glyph brush.
- Replace the playback stop rectangle fill with a semantic icon brush.
- Make toolbar/menu/status bar backgrounds and foregrounds come from theme
  resources.
- Ensure selected, inactive-selected, hover, and alternating list rows are
  readable in both themes.

## Dialog and Secondary Window Work

Every WPF file in `GUI/*.xaml` must be audited and updated:

- `AboutBox.xaml`
- `ArcParameters.xaml`
- `ConvertMedia.xaml`
- `CreateArchive.xaml`
- `EnterMaskDialog.xaml`
- `ExtractArchive.xaml`
- `ExtractFile.xaml`
- `FileErrorDialog.xaml`
- `FileExistsDialog.xaml`
- `KrkrDumpAssistDialog.xaml`
- `SettingsWindow.xaml`
- `TextViewer.xaml`
- `TroubleShootingDialog.xaml`
- `UpdateDialog.xaml`

Rules:

- Replace black borders with `Gar.Brush.ControlBorder`.
- Replace light content backgrounds with `Gar.Brush.WindowBackground` or
  `Gar.Brush.ControlBackground`.
- Ensure read-only transparent text boxes inherit theme foreground.
- Ensure hyperlinks are visible in both themes.
- Ensure text viewer foreground, caret, selection, and virtualized row text are
  readable.
- Ensure settings sections, expanders, and selected tree items have sufficient
  contrast.

## ArcFormats Widget Work

All WPF widgets under `ArcFormats/**` that can be hosted by
`ArcParametersDialog` are in scope.

Rules:

- Keep simple widgets simple; most should inherit default theme styles without
  local changes.
- Replace validation red literals with `Gar.Brush.Error`.
- Avoid local `TextBox`, `ComboBox`, or `CheckBox` templates unless needed for
  format-specific behavior.
- When a widget requires a special background or foreground, define and consume
  a semantic resource key.

## Icons and Bitmap Assets

Existing toolbar icons are bitmap resources and may lose contrast on dark
backgrounds.

Required approach:

- Audit every image used in `GUI/*.xaml`.
- Keep colorful bitmap icons only when they are readable in both themes.
- For monochrome/glyph-like icons, prefer theme-specific resources:
  `Gar.Image.Back`, `Gar.Image.Forward`, etc.
- Bind image sources through `DynamicResource` when the source changes by theme.
- Do not recolor bitmap files at runtime unless no cleaner asset path exists.
- Cursor assets may remain unchanged if visible against preview backgrounds.

## Coding Rules

For UI changes after this migration:

- Do not introduce theme-dependent literal colors in XAML or C#.
- Use `DynamicResource` for theme-dependent values.
- Add new brush/image keys to both light and dark dictionaries in the same
  change.
- Verify new UI in both themes before completion.
- Update this document when new theme architecture or validation rules are
  introduced.
- Use `$ui-dark-mode` for GUI, XAML, WPF dialog, icon, theme, or visual styling
  work.

Allowed literal color exceptions:

- `Transparent` and fully transparent colors.
- Data colors that represent file or media content rather than application
  chrome.
- Semantic warning/error colors only inside theme dictionaries.
- Test fixtures or documentation examples that are not loaded as UI resources.

## Implementation Checklist

1. Create `GUI/Themes/Theme.Shared.xaml`, `Theme.Light.xaml`, and
   `Theme.Dark.xaml`.
2. Add the theme dictionaries to `GUI/GARbro.GUI.csproj`.
3. Add `GUI/ThemeManager.cs` with effective-theme resolution, dictionary
   swapping, system preference detection, and dark title bar support.
4. Add the `appTheme` user setting and localizable labels.
5. Add a settings UI control for theme selection.
6. Apply theme resources to `MainWindow.xaml`.
7. Apply theme resources to every `GUI/*.xaml` dialog and secondary window.
8. Apply inherited or semantic theme resources to all `ArcFormats/**/*.xaml`
   widgets.
9. Audit bitmap icon contrast and add dark variants where needed.
10. Update `AGENTS.md`, `.codex/skills/ui-dark-mode`, and this document if the
    implementation deviates from the plan.
11. Build the GUI project with Visual Studio MSBuild when available.
12. Manually verify the full UI checklist in light, dark, and system modes.

## Validation Checklist

Use the smallest checks that prove the changed surface, but full dark mode
migration requires both automated scans and manual UI verification.

Automated scans:

```powershell
rg -n '#[0-9A-Fa-f]{3,8}|Foreground="Black"|Background="White"|Background="LightGray"|BorderBrush="Black"|Fill="Black"|Stroke="Black"|Fill="Gray"' GUI ArcFormats -g "*.xaml" -g "!GUI/Themes/*.xaml"
rg -n 'Brushes\.|new SolidColorBrush|Colors\.' GUI -g "*.cs"
```

Every match must be either removed, moved into a theme dictionary, or documented
as an allowed exception.

Build:

```powershell
nuget restore GARbro.sln
msbuild GUI\GARbro.GUI.csproj /p:Configuration=Debug /p:Platform="Any CPU"
```

Use the exact local build command from `docs/reference/build-and-verify.md` and
`$garbro-build-verify` when toolchain details are uncertain.

Manual UI checks:

- Launch the GUI in light mode.
- Launch the GUI in dark mode.
- Launch with `System` mode and toggle the Windows app theme if available.
- Open a normal directory.
- Open an archive if a sample is available.
- Preview image, text, and audio entries when samples are available.
- Open About, Settings, Update, Troubleshooting, extraction, conversion, create
  archive, mask, file exists, file error, and archive parameter dialogs.
- Open at least one parameter widget from `ArcFormats` that uses validation.
- Check menus, context menus, toolbar disabled icons, status bar, scrollbars,
  selection states, inactive selection states, focus outlines, hyperlinks, and
  text selection.

## Acceptance Criteria

Dark mode is complete only when:

- Every in-process WPF window and dialog is readable in light and dark themes.
- `System`, `Light`, and `Dark` preferences work and persist.
- Runtime switching updates existing windows or has a documented limitation with
  an explicit reason.
- Native title bars use dark chrome when supported by the OS.
- The hard-coded color scan has no unexplained theme-dependent matches.
- New theme resources exist in both light and dark dictionaries.
- Existing user workflows for browsing, previewing, extracting, converting,
  creating archives, changing settings, checking updates, and opening format
  parameter dialogs still work.
- Future UI change rules are present in `AGENTS.md` and
  `.codex/skills/ui-dark-mode`.
