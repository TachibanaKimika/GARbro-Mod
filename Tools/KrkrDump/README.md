# KrkrDump Runtime

Prebuilt x86 KrkrDump runtime files are bundled here for release packaging:

```text
x86/KrkrDumpLoader.exe
x86/KrkrDump.dll
```

If a future KrkrDump build provides a working x64 DLL, place the matching x64
runtime beside it:

```text
x64/KrkrDumpLoader.exe
x64/KrkrDump.dll
```

The GUI project copies this directory to the application output folder. The
runtime itself is not built by `GARbro.sln`.
