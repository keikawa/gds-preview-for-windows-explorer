# GDS Preview for Windows Explorer

Preview `.gds` and `.gdsii` layout files directly in the Windows Explorer preview pane.

<img src="docs/images/store-logo-150x150.png" alt="GDS Preview for Windows Explorer icon" width="128">

![GDSII preview in Windows Explorer](https://github.com/user-attachments/assets/5471cd90-893c-45f6-a198-6b6ad712e110)

## Install from Microsoft Store

**[Get GDS Preview for Windows Explorer from Microsoft Store](https://apps.microsoft.com/detail/9nm0tj3pnk6j?cid=DevShareMCLPCS)**

This is the recommended installation method. Microsoft Store installs the self-contained app and
delivers future updates automatically.

## Install a GitHub release manually

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

- GDSII `BOUNDARY`, `BOX`, `PATH`, `SREF`, and `AREF`
- Cell hierarchy, translation, rotation, magnification, reflection, and arrays
- Automatic single-top-cell display and tiled overview for multiple top-level cells
- Layer/datatype coloring
- Dedicated `prevhost.exe` surrogate and isolated renderer process
- Cancellation, parser limits, hierarchy limits, and a six-second renderer timeout
- Per-user installation without administrator privileges

OASIS is not supported. A file with an OASIS payload and a `.gds` extension is reported as invalid
GDSII. GDSII `TEXT` labels are intentionally ignored and do not affect the fitted geometry bounds.

## Requirements

- Windows 10 or Windows 11, x64
- The Microsoft Store package is self-contained.
- Manual GitHub release installation requires the
  [.NET 8 Desktop Runtime, x64](https://dotnet.microsoft.com/download/dotnet/8.0).

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
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1 -Version 0.2.1
```

Create a self-contained x64 MSIX for Microsoft Store submission:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 `
  -Version 0.2.1.0
```

This requires the Windows 10/11 SDK in addition to the normal build dependencies. Store identity,
sideload signing, certification, and test instructions are in
[packaging/STORE-SUBMISSION.md](packaging/STORE-SUBMISSION.md).

## Architecture

- `src/GdsPreview.Core` — dependency-free GDSII parser and cell hierarchy model
- `native/GdsPreview.Native.cpp` — minimal native COM preview handler loaded by `prevhost.exe`
- `src/GdsPreview.Renderer` — isolated .NET final-grid coverage rasterizer process
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
- Cells: 100,000
- Retained geometry: 300,000 total across all cells
- Retained vertices: 8,000,000
- References: 1,000,000 (the preview fails instead of showing an incomplete hierarchy if exceeded)
- Hierarchy depth: 512
- Cached path outlines: 8,000,000 points; reusable coordinate buffers: 1,000,000 points
- Preview canvas: up to 1600 × 1200 pixels (larger panes fit this bounded canvas)
- Renderer time: 6 seconds

Cell and array transforms are composed in double precision, then polygons and physical-width
paths are drawn directly on one final pixel grid. Geometry is traversed without building a
flattened instance list; only vector outlines and scratch buffers are reused. There are no
per-cell bitmap caches, outward polygon strokes, or minimum-pixel path widths.
Preview latency and bounded memory are primary design constraints, not just reasons
to stay below the timeout. Fill and outlines share streamed coverage rows; the
rasterizer does not allocate full-canvas coverage scratch or traverse geometry twice.

The preview follows Windows' **app** light/dark mode (white / dark grey respectively),
including cell panels, loading/error screens and resize margins. High-contrast mode uses
system window/text colours. Legacy host colour hints cannot override Windows' theme.
Windows theme notifications reuse the existing debounced redraw, without polling,
extra threads or per-shape theme work. There is no theme setting in this app.
Layer colours stay fixed across files; light backgrounds use a slightly
darker variant of the same palette, while dark backgrounds retain the original colours.
Antialiasing integrates polygon edge areas within each pixel. Polygons and paths share a
subdued fill and a brighter, approximately half-pixel inward boundary accent. Fill and boundary contributions are
accumulated separately; fill opacity is capped at 32/255 even with many overlaps, and
boundaries are composited afterwards at up to 240/255. A later enclosing fill therefore
cannot paint over internal outlines. The accent is extracted inside the true pixel coverage,
not by expanding or imposing a minimum width on the geometry. This display style is not a
physical transparency simulation. A
fixed 256-color categorical palette is selected by a deterministic layer/datatype hash;
the same pair has the same base color across files, regardless of other layers or cell order.
Different pairs can still share
a color, and color differences can be difficult to perceive in dense overlaps. See
[the palette design and validation notes](docs/LAYER-COLORS.md). Color blending takes place
in linear light, with sRGB encoding only at final output. Contributions are weighted by
covered area, and outlines never extend into uncovered pixels. Subpixel structures can
contribute less than a whole pixel;
they can naturally become faint or indistinguishable at preview resolution. The preview is
not a substitute for inspecting dimensions and connectivity in a layout editor. PATH caps
0 (flat), 1 (round), and 2 (extended square) are supported; round caps are polygonally
approximated and joins use a 10-half-width miter limit. Custom PATH extensions and absolute
negative-WIDTH semantics are not interpreted by the current parser. Self-intersecting
boundaries are not repaired by the rasterizer.

The native host renders at the pane size within the canvas limit and re-renders after resizing
settles. The previous frame may be temporarily scaled during resizing. The status line shows
`simplified` whenever parser retention limits affected the preview.

## License

[MIT](LICENSE)

## Security

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## Privacy

GDS Preview processes files locally and does not collect or transmit personal data. See
[PRIVACY.md](PRIVACY.md).
