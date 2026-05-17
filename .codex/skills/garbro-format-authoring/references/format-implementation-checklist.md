# Format Implementation Checklist

Use this checklist after reading nearby implementations.

## Evidence

- Sample files or archive tree are available, or the lack of samples is stated.
- Extension, magic/signature, endian, header version, and count fields are known.
- Compression, encryption, palette, audio codec, and image pixel layout are
  identified before coding.

## Recognition

- `Signature` and `Signatures` do not cause broad false positives.
- `Extensions` match real files and do not steal common extensions without
  additional header validation.
- `Tag` is unique enough to avoid ambiguity; use a slash-qualified tag when a
  common extension has many engines.

## Archive Safety

- Counts use `ArchiveFormat.IsSaneCount` or an equivalent bound.
- Every offset and size is checked against archive length.
- Directory entries are sorted or preserved only when required by extraction.
- Negative, overflow, and duplicate-entry cases are handled predictably.
- Extraction opens entry streams through existing `ArcFile`/`ArchiveFormat`
  patterns.

## Image Safety

- Metadata read is cheap and bounded.
- Width, height, BPP, stride, palette size, and compressed length are validated.
- Pixel conversion follows existing `ImageData.Create` and `PixelFormats`
  patterns.

## Audio Safety

- Codec and sample metadata are validated before stream wrapping.
- WAV wrapping writes correct RIFF headers when used.
- Decoder ownership and stream lifetime follow nearby audio handlers.

## Project Integration

- New `.cs` or `.xaml` files are listed in the owning `.csproj`.
- Required embedded resources are included in the project file.
- Extra package dependencies are avoided unless there is no practical local
  implementation or existing dependency.
- GUI option widgets follow existing WPF widget placement and naming.

## Verification

- Build attempted with the expected command.
- MEF listing smoke check run after successful build.
- Sample listing/extraction/conversion run when samples exist.
- Documentation impact checked with `$docs-sync`.
