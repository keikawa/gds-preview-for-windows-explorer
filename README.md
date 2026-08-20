# GDS Preview for Windows Explorer

Preview `.gds` and `.gdsii` layout files directly in the Windows Explorer preview pane.

![GDSII preview in Windows Explorer](https://github.com/user-attachments/assets/5471cd90-893c-45f6-a198-6b6ad712e110)

## Install a release

1. Download `GDS-Preview-for-Windows-Explorer-<version>-x64.zip` from
   [Releases](https://github.com/keikawa/gds-preview-for-windows-explorer/releases).
2. Right-click the downloaded ZIP, open **Properties**, select **Unblock** if shown, and extract it.
3. Run `install.cmd`. The default installation applies only to the current user.
4. Close all Explorer windows, reopen Explorer, and enable the preview pane with `Alt+P`.

Run `uninstall.cmd` from the same package to remove it.

Files marked as downloaded from the Internet may show Windows' security warning instead of a
preview. This is Mark of the Web behavior enforced by Explorer before the preview handler starts.
For a trusted GDS file, use **Properties > Unblock**.

## Features

- GDSII `BOUNDARY`, `BOX`, `PATH`, `TEXT`, `SREF`, and `AREF`
- Cell hierarchy, translation, rotation, magnification, reflection, and arrays
- Automatic single-top-cell display and tiled overview for multiple top-level cells
- Layer/datatype coloring
- Dedicated `prevhost.exe` surrogate and isolated renderer process
- Cancellation, parser limits, hierarchy limits, and a six-second renderer timeout
- Per-user installation without administrator privileges

OASIS is not supported. A file with an OASIS payload and a `.gds` extension is reported as invalid
GDSII.

## Requirements

- Windows 10 or Windows 11, x64
- [.NET 8 Desktop Runtime, x64](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build from source

Build requirements:

- .NET 8 SDK
- Zig 0.15 or newer

From PowerShell at the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build runs parser, load, COM isolation, timeout, and initial-resize regression tests. Output is
written under `artifacts\GdsPreview`.

Create a release ZIP after a successful build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Version 0.1.0-beta.1
```

Create a self-contained x64 MSIX for Microsoft Store submission:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 `
  -Version 0.1.1.0
```

This requires the Windows 10/11 SDK in addition to the normal build dependencies. Store identity,
sideload signing, certification, and test instructions are in
[packaging/STORE-SUBMISSION.md](packaging/STORE-SUBMISSION.md).

## Architecture

- `src/GdsPreview.Core` — dependency-free GDSII parser and hierarchical scene builder
- `native/GdsPreview.Native.cpp` — minimal native COM preview handler loaded by `prevhost.exe`
- `src/GdsPreview.Renderer` — isolated .NET rasterizer process
- `tests/GdsPreview.Core.Tests` — regression and load tests without an external test framework
- `tools/GdsPreview.Sample` — deterministic GDSII sample generator
- `scripts` — build, package, install, verification, and uninstall commands
- `packaging` — MSIX manifest and Microsoft Store submission instructions

The handler implements `IInitializeWithFile`, `IPreviewHandler`, `IObjectWithSite`, `IOleWindow`, and
`IPreviewHandlerVisuals`. A dedicated AppID with `DllSurrogate=Prevhost.exe` isolates it from other
preview handlers. Parsing and GDI+ are loaded only by `GdsPreview.Renderer.exe`.

## Safety limits

Defaults are intentionally conservative to protect Explorer:

- File size: 2 GiB
- GDSII records: 10,000,000
- Retained geometry: 30,000 total and 4,000 per cell
- Retained vertices: 1,000,000 while parsing and 300,000 after hierarchy expansion
- Rendered primitives: 8,000
- Expanded instances: 8,000
- Hierarchy depth: 48
- Text labels: 200
- Independent top-level cells in an overview: 16
- Renderer time: 6 seconds

Large arrays are sampled. Omitted cell geometry is represented by its bounds, and the status line
shows `simplified` whenever limits affected the preview.

## License

[MIT](LICENSE)

## Security

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Privacy

GDS Preview processes files locally and does not collect or transmit personal data. See
[PRIVACY.md](PRIVACY.md).
