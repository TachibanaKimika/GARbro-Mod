---
name: garbro-format-authoring
description: Use when adding or modifying GARbro archive, image, audio, script, encryption, scheme, or supported-format handlers under GameRes, ArcFormats, Legacy, or Experimental. Guides placement, MEF exports, old-style csproj updates, recognizer precision, sample verification, and docs synchronization.
---

# GARbro Format Authoring

Use this skill for resource format work. Prefer nearby precedent over new
abstractions.

## Required Read Order

1. `docs/architecture/project-structure.md`
2. Relevant base contracts:
   - `GameRes/ArchiveFormat.cs`
   - `GameRes/Image.cs`
   - `GameRes/Audio.cs`
   - `GameRes/FormatCatalog.cs`
3. Two or three nearby handlers in the target directory.
4. `references/format-implementation-checklist.md`
5. For `Formats.dat` edits or merge conflicts,
   `references/scheme-database-merge.md`

## Placement

- Use `ArcFormats/<EngineOrVendor>/` for maintained or primary support.
- Use `Legacy/<EngineOrVendor>/` for old isolated visual novel formats when
  nearby precedent exists there.
- Use `Experimental/<Topic>/` for unstable handlers, optional dependencies, or
  support that still needs proof.
- Put shared helpers near the narrowest owning format group unless existing code
  already has a reusable helper.

## Implementation Rules

- Export handlers with MEF attributes:
  `[Export(typeof(ArchiveFormat))]`, `[Export(typeof(ImageFormat))]`, or
  `[Export(typeof(AudioFormat))]`.
- Define `Tag`, `Description`, `Signature`, and `Extensions` deliberately. For
  weak signatures or extension-only detection, keep recognizers narrow.
- Validate archive counts with existing helpers such as
  `ArchiveFormat.IsSaneCount`.
- Bound all offsets, sizes, counts, and decompressed lengths before reading.
- Preserve entry names but prevent traversal-like names from escaping expected
  extraction behavior.
- Prefer `ArcView`, `IBinaryStream`, `BinaryStream`, and existing endian helpers
  over ad hoc byte parsing.
- Do not load whole files into memory unless format structure makes streaming
  impractical and the size is bounded.
- For image handlers, return accurate `ImageMetaData` before decoding pixels.
- For audio handlers, preserve codec metadata and wrap to WAV only where local
  precedent does so.
- Add new source files to the owning old-style `.csproj`.

## Verification

1. Restore/build with `$garbro-build-verify` when toolchain permits.
2. Run `Onachi-GARbro.Console.exe -l` or `Onachi-GARbro.Image.Convert.exe -l`
   after successful build to confirm MEF discovery.
3. With samples, verify listing, extraction, metadata, and conversion paths.
4. If samples are unavailable, document the missing sample limitation.
5. Use `$docs-sync` when support coverage, behavior, or prerequisites changed.

## Documentation

- Update `docs/reference/**` for new prerequisites, known limitations, or sample
  verification notes.
- Update `docs/supported.html` only when supported-format documentation is in
  scope or the update workflow is known.
- If a format requires game-specific keys or schemes, document the boundary and
  update scheme data only when authorized.
- Never resolve `ArcFormats/Resources/Formats.dat` by choosing an opaque binary
  side when both branches changed it. Use the semantic three-way analysis,
  Agent review, report-hash approval, round-trip inspection, and E2E workflow in
  `references/scheme-database-merge.md`.
