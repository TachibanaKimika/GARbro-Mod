# Scheme Database Semantic Merge

Use this workflow when Git reports a conflict for
`ArcFormats/Resources/Formats.dat`. The file is a versioned `GARbroDB` payload:
zlib-compressed `SchemeDataBase` data serialized by .NET Framework
`BinaryFormatter`. A byte-level choice of ours or theirs can silently discard
valid schemes from the other side.

## Safety Boundary

- Deserialize only reviewed artifacts from this repository or a trusted
  upstream. `BinaryFormatter` data can execute unsafe deserialization paths.
- Keep the conflict's Git stages intact until analysis is complete. Do not
  convert the blobs through a text pipeline.
- Never approve a report with unresolved semantic conflicts.
- The analysis report contains paths, types, counts, and hashes. The Agent must
  relate each changed top-level scheme to the corresponding source changes; a
  clean algorithmic result is necessary but not sufficient approval.
- The workflow never stages the result. Build and inspect it first, then use an
  explicit `git add ArcFormats/Resources/Formats.dat`.

## Standard Git-Conflict Workflow

1. Fetch both remotes and inspect the merge base and changed paths.
2. Start the source merge with
   `git merge --no-commit --no-ff upstream/master`. Preserve unrelated user
   work.
3. Resolve source conflicts semantically. Leave `Formats.dat` unmerged in the
   index. If a build needs a worktree copy, use
   `git checkout --ours -- ArcFormats/Resources/Formats.dat` without staging
   it.
4. Build `SchemeTool` with Visual Studio MSBuild.
5. Generate a deterministic report from conflict stages 1, 2, and 3:

   ```powershell
   .\scripts\Merge-FormatsDatabase.ps1 -Mode Analyze -Configuration Debug
   ```

6. Agent-review every entry in `changes` and `conflicts`, plus the top-level
   `schemes` inventory. Confirm:

   - `ours` decisions preserve intended fork-only data;
   - `theirs` decisions correspond to upstream source or database changes;
   - additions and deletions match current handler tags and serialized types;
   - counts and result version are plausible;
   - `summary.conflicts` is zero.

7. Record the report SHA-256 printed by analysis. Approval is bound to those
   exact Git blobs and the deterministic report. Produce the database only
   after review:

   ```powershell
   .\scripts\Merge-FormatsDatabase.ps1 `
     -Mode Merge `
     -Configuration Debug `
     -ApprovedReportSha256 <reviewed-report-sha256>
   ```

   Merge mode reruns analysis and refuses to write when its hash differs from
   the reviewed hash.

8. Inspect the output, confirm its semantic hash equals
   `result.semanticHash`, build the affected projects/solution, and run the
   scheme merge E2E test. Only then stage the binary and resolved source files.

   ```powershell
   bin\Debug\Onachi-GARbro.SchemeTool.exe database inspect `
     --input ArcFormats\Resources\Formats.dat `
     --report .git\garbro-formats-inspect.json `
     --trusted-inputs --overwrite

   .\tests\SchemeTool\Invoke-SchemeDatabaseMergeTests.ps1 `
     -Configuration Debug
   ```

9. Before committing, verify `git diff --name-only --diff-filter=U` is empty,
   review staged versus unstaged files separately, and run the normal release
   review and documentation sync.

## Explicit Three-File Workflow

For files outside an active Git conflict, pass all three paths and choose a
separate output:

```powershell
.\scripts\Merge-FormatsDatabase.ps1 `
  -Mode Analyze `
  -BasePath C:\review\base.dat `
  -OursPath C:\review\ours.dat `
  -TheirsPath C:\review\theirs.dat `
  -ReportPath C:\review\merge-report.json

.\scripts\Merge-FormatsDatabase.ps1 `
  -Mode Merge `
  -BasePath C:\review\base.dat `
  -OursPath C:\review\ours.dat `
  -TheirsPath C:\review\theirs.dat `
  -OutputPath C:\review\merged.dat `
  -ReportPath C:\review\merge-report.json `
  -ApprovedReportSha256 <reviewed-report-sha256>
```

The semantic merger applies standard three-way rules recursively to scheme and
game dictionaries and serializable scheme fields. Independent edits are
combined. Different edits to the same scalar, list, array, or set; delete versus
modify; type changes; and unsafe reconstruction cases are reported as conflicts
and produce no output.

## Exit Codes

- `0`: clean analysis or successful output.
- `2`: invalid command, missing trust opt-in, or unsafe output selection.
- `3`: semantic conflicts; no merged database was written.
- `4`: database validation, deserialization, or filesystem failure.
- `9`: unexpected failure requiring investigation.
