# Contributing

Issues and focused pull requests are welcome.

Before submitting a change:

1. Keep parsing and rasterization out of the Explorer-loaded native DLL.
2. Preserve the renderer timeout and existing resource limits unless the change includes evidence
   that a new limit is safe.
3. Add a regression case for parser, hierarchy, sizing, or isolation changes.
4. Run `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1` on Windows x64.

Do not commit proprietary or confidential GDSII layouts. Use the deterministic sample writer for
test fixtures whenever possible.
