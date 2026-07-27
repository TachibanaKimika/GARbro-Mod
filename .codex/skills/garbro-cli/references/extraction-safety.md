# Extraction Safety

Read this reference before archive extraction or any broad write.

## Required sequence

1. Run `probe` on unknown input.
2. Run `archive list` and inspect entry names, sizes, types, and count.
3. Select only entries authorized by the user.
4. Choose an explicit destination.
5. Set limits appropriate to the request.
6. Run `archive extract --dry-run`.
7. Review planned count, paths, conflicts, and limits.
8. Remove `--dry-run` only when the plan matches the request.

Do not broaden one entry, one extension, or one directory into full-archive
extraction without authorization.

## Default policy

Discover defaults through `capabilities`. Protocol v1 currently reports:

| Setting | Default |
| --- | ---: |
| overwrite | `never` |
| maximum files | 10,000 |
| maximum total bytes | 4 GiB |
| maximum bytes per entry | 1 GiB |
| maximum path depth | 32 |

Set tighter values when the requested scope is smaller.

```powershell
& $cli archive extract $archive `
  --destination $destination `
  --entry "scenario\*.ks" `
  --overwrite never `
  --max-files 1000 `
  --max-total-bytes 1073741824 `
  --max-entry-bytes 268435456 `
  --max-depth 24 `
  --dry-run `
  --output json `
  --non-interactive
```

## Path protections

GARbro rejects:

- empty entry names;
- rooted, drive-qualified, or UNC paths;
- `..` traversal and normalized destination escape;
- invalid or ambiguous Windows names;
- reserved device names;
- excessive path depth;
- case-insensitive destination collisions.

The CLI resolves each final path and proves it remains below
`--destination`. There is no unsafe-path bypass option; do not invent one.

## Size protections

GARbro checks declared entry sizes before writing and counts actual bytes while
decompression or conversion runs. This protects against archives whose metadata
understates expanded size.

`max-files`, `max-total-bytes`, `max-entry-bytes`, and `max-depth` are hard
budgets. A failure can charge `observedBytes` even when no final file is
committed.

## Atomic writes

Each output is written to a unique `.partial` file in the target directory.
GARbro moves or replaces it only after the writer completes. Cancellation and
failure remove the temporary file when possible.

`--dry-run` performs selection, path validation, declared-size checks, and
conflict checks without creating the destination.

## Overwrite modes

- `never`: default. Reject an existing destination before extraction starts.
- `skip`: preserve existing files. Any skip makes the final result
  `partial_success`.
- `replace`: explicit authorization only. Replace through a same-volume
  temporary file.

Do not choose `replace` merely to make a command finish.

## Result review

For JSONL stdout, wait for the terminal event. Review:

```text
selected
planned
written
skipped
failed
bytesWritten
observedBytes
destination
```

Report individual failures and warnings. If `failed` or `skipped` is nonzero,
do not describe the extraction as fully successful.
