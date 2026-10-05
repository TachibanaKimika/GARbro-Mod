# Local YPF and ISF parameters

Unknown schemes can be supplied explicitly from parameters recovered from the
user's original executable. No executable is launched and no key is guessed.
The parameter files are private inputs, not additions to `Formats.dat`.

Set `GARBRO_YPF_PARAMETERS` to an absolute JSON file path before running probe,
list, plan, dry-run and extraction. The JSON contains:

```json
{
  "archive_directory": "D:\\LocalGame\\pac",
  "name_xor": 0,
  "swap_table": [],
  "script_key": 0,
  "compression": "zlib"
}
```

The numbers above are placeholders, not a working game scheme. `swap_table`
contains disjoint byte pairs in a flat array. `name_xor` is a byte;
`script_key` is an unsigned 32-bit integer in GARbro's XOR byte order.
Compression must be `zlib` or `snappy`. Parameters apply only to archives whose
immediate directory exactly matches the absolute `archive_directory`.
This explicit path supports YPF versions 470–600 and their 64-bit entry offsets.
Other versions retain the existing scheme workflow.

Set `GARBRO_ISF_PARAMETERS` to a JSON file for encrypted IKURA/GDL scripts:

```json
{
  "archive_directory": "D:\\LocalGame",
  "archive_name": "ISF",
  "table_path": "D:\\PrivateParameters\\original-exe-table.bin"
}
```

The exact archive name and directory must match; the table must be exactly
2048 bytes. Existing installed schemes remain the fallback outside this scope.
The table is used during entry decoding, so a successful list alone does not
prove successful decryption. Test a selected script extraction too.

Keep the same parameter files and environment throughout a job. These local
parameters are not included in the CLI's existing XP3 scheme fingerprint:
**do not use extraction manifest resume with these environment parameters**.
Use a fresh destination and record the private input provenance separately.
Never include keys, executable tables, or game scripts in public commits or
release packages. Clear the environment variables after the scoped job.

Verified on local copies of 2045, Tsuki Yori.; Skychord; True Colors (306,
295, 301 script archive entries), and Crescendo Full Voice (19 ISF entries).
These are sample coverage statements, not support claims for every YU-RIS game.
