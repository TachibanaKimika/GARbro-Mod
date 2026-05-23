---
name: ui-dark-mode
description: Use when adding, modifying, reviewing, or documenting GARbro-Mod-Onachi GUI, WPF, XAML, visual styling, icons, theme resources, dialogs, toolbar/menu/status UI, TextViewer UI, SettingsWindow UI, or ArcFormats WPF option widgets. Ensures every UI change remains compatible with full light and dark themes.
---

# UI Dark Mode

Use this skill for any UI-facing work in `GUI/` or WPF option widgets under
`ArcFormats/**`.

## Required Read Order

1. `docs/reference/dark-mode-adaptation.md`
2. The nearest existing XAML or code-behind implementation.
3. `GUI/Themes/*.xaml` and `GUI/ThemeManager.cs` if they exist.

## Rules

- Treat light and dark mode support as mandatory for every UI change.
- Do not add theme-dependent literal colors in XAML or C#.
- Use semantic resources and `DynamicResource` for theme-dependent brushes,
  images, borders, glyphs, backgrounds, foregrounds, and selection states.
- Add or update both light and dark theme dictionary values in the same change.
- Keep `ArcFormats` widgets simple and let them inherit app styles unless they
  need format-specific behavior.
- Replace local validation colors with semantic resources such as
  `Gar.Brush.Error`.
- Do not edit generated `*.Designer.cs` files by hand.
- Use `$docs-sync` when UI behavior, theme architecture, AGENTS rules, or this
  skill changes.

## Workflow

1. Identify every UI surface affected by the request:
   `GUI/*.xaml`, code-behind, theme dictionaries, bitmap icons, and embedded
   `ArcFormats/**/*.xaml` widgets.
2. Check current theme keys before adding new ones. Prefer existing semantic
   keys.
3. Make scoped UI edits using theme resources.
4. Run a hard-coded color scan:

   ```powershell
   rg -n '#[0-9A-Fa-f]{3,8}|Foreground="Black"|Background="White"|Background="LightGray"|BorderBrush="Black"|Fill="Black"|Stroke="Black"|Fill="Gray"' GUI ArcFormats -g "*.xaml" -g "!GUI/Themes/*.xaml"
   rg -n 'Brushes\.|new SolidColorBrush|Colors\.' GUI -g "*.cs"
   ```

5. Build with `$garbro-build-verify` when the local toolchain permits.
6. Manually verify each touched surface in both light and dark themes. Include
   menus, context menus, disabled states, focus, hover, selected and inactive
   selected states, hyperlinks, text selection, and scrollbars when relevant.

## Review Checklist

- New UI text and controls are readable in both themes.
- No new theme-dependent literal colors were introduced.
- New theme keys are present in both light and dark dictionaries.
- Bitmap icons have enough contrast or use theme-specific resources.
- WPF option widgets hosted by `ArcParametersDialog` inherit compatible colors.
- Documentation and AGENTS rules remain aligned with the implementation.
