# Project Structure

GARbro-Mod-Onachi is a legacy .NET Framework solution for browsing, extracting,
and converting visual novel resources. The solution is organized around a core
resource library, large sets of MEF-discovered format plugins, and several
desktop/console entry points.

## Solution

`GARbro.sln` targets Visual Studio 17 format and defines `Debug`, `Prerelease`,
and `Release` configurations for `Any CPU`. Most projects target .NET Framework
4.8.1. `Net20` targets .NET Framework 2.0 and is referenced by some format
projects for compatibility code.

All primary projects write to shared folders under `bin/<Configuration>/`.
Several projects execute `inc-revision.pl` as a pre-build step, so a working
Perl executable is part of the normal build environment.

## Core Library

`GameRes/` contains the stable abstractions used throughout the repository:

- `GameRes.cs`: base `IResource` contract, tags, signatures, extensions, and
  priority metadata. It also carries archive-parameter context and command
  bridge interfaces used when a format option widget needs host-side actions.
  The parameter context can also carry a host progress callback, allowing
  background format work to update application-owned UI without introducing a
  GUI dependency into format assemblies.
- `ArchiveFormat.cs`: archive open/create/extract contract and common archive
  safety helpers.
- `Image.cs` and `ImageDecoder.cs`: image metadata, decoder, and conversion
  contracts.
- `Audio.cs` and `AudioWAV.cs`: audio metadata, stream, and WAV wrapping.
- `FormatCatalog.cs`: MEF import, extension/signature lookup, preferred format
  mapping, and scheme serialization.
- `BinaryStream.cs`, `ArcView.cs`, and related stream helpers: bounded binary
  access primitives used by handlers.

Changes in `GameRes/` are high impact because every format and application entry
point depends on these contracts.

## Format Assemblies

`ArcFormats/` is the main format implementation assembly. It contains archive,
image, audio, script, encryption, compression, and per-engine option widget code.
Format classes are discovered by MEF attributes such as
`[Export(typeof(ArchiveFormat))]`, `[Export(typeof(ImageFormat))]`, and
`[Export(typeof(AudioFormat))]`.

`Legacy/` contains older visual novel formats, mostly late 1990s and early
2000s engines. Use it for low-traffic or historically isolated handlers when
nearby precedent exists there.

`Experimental/` contains handlers with extra dependencies, unstable behavior, or
optional support. Move code out of `Experimental/` only when it has enough
coverage and maintainability to be treated as regular support.

Old-style `.csproj` files list source files explicitly. Adding a new `.cs` or
`.xaml` file usually requires editing the owning project file.

## Applications and Tools

`GUI/` builds the WPF desktop application `Onachi-GARbro.exe`. It owns host-side
dialogs and external process integration, including the KrkrDump XP3 assistant;
format assemblies request those actions through `GameRes` interfaces instead of
referencing GUI types directly.

`Console/` builds `Onachi-GARbro.Console.exe`, a command-line archive browser
and extraction tool. The local README describes it as a testing playground and
not actively developed, but it is still useful for smoke checks.

`Image.Convert/` builds `Onachi-GARbro.Image.Convert.exe`, a command-line image
metadata and conversion utility. It is also described as a testing playground.

`SchemeTool/` edits serialized scheme data under `GameData`, especially format
schemes used by engines such as KiriKiri.

## Documentation

`docs/supported.html` is the published supported-format page. Treat it as
published/generated-style documentation: update it only when the task explicitly
changes supported format documentation or when the generation/update workflow is
known.

`docs/version.xml` contains release metadata.

Use `docs/reference/**` for build, run, configuration, and operational notes.
Use `docs/architecture/**` for stable module boundaries and dependency facts.
