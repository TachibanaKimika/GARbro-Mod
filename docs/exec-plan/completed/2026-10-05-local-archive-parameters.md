# Context

The resource-library task exposed unknown MELLOW YPF schemes and Crescendo ISF
tables. Parameters were recovered by static inspection of original local EXEs.
The user authorized repository fixes, push, packaging, and Skill synchronization.

# Acceptance Criteria

Scoped parameter input works without private plugins; full-width YPF offsets
are retained; sample extraction and synthetic regression checks pass; docs and
Skills match; commit pushed; installer produced.

# Implementation Checklist

- [x] Add bounded, directory-scoped parameter JSON loading.
- [x] Support explicit YPF and ISF parameters without publishing game secrets.
- [x] Preserve 64-bit offsets and reject invalid directory entries.
- [x] Build Debug and extract selected entries from four real samples.
- [x] Synthetic regression checks, final review, commit/push, release package.
- [x] Sync installed/workspace Skill copies.

# Validation Checklist

MSBuild restore and Debug solution build passed. Existing Experimental unresolved
Microsoft.Win32.Primitives warning remains. Real sample lists/extractions passed:
306/295/301 YPF entries, 19 ISF entries. No game execution or media hashes.

# Progress

2026-10-05: implementation and real sample extraction verified. Synthetic valid index, out-of-range 64-bit offset rejection, zero-key Snappy extraction, exact-directory scope and duplicate-swap rejection all pass.

# Decision Log

Use explicit scoped local parameters, not embedded title-specific secrets.
No Formats.dat changes. Environment parameters currently exclude manifest resume;
document this limit until a typed fingerprinted CLI interface is implemented.
Preserve pre-existing AssemblyInfo changes outside the feature commit.

# Outcomes

GO: focused diff review found no blocking issue. Debug CLI E2E passed 2,289 assertions. Debug/Release synthetic parameter tests passed. Release installer and CLI/console/image smoke passed; Skill package passed 98 assertions and contains the new reference. Code commit 9a336bea pushed to origin/master. Installer: bin/Package/Onachi-GARbro-setup.exe. Existing AssemblyInfo edits restored byte-for-byte after release stamping. No installation was performed, so active extraction processes remain undisturbed. See docs/reference/local-archive-parameters.md for the manifest-resume limitation.
