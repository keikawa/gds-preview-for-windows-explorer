# Microsoft Store listing draft

## Product identity

- Product name: GDS Preview for Windows Explorer
- Publisher display name: keikawa
- Package identity name: keikawa.GDSPreviewforWindowsExplorer
- Category: Utilities & tools
- Pricing: Free

## Short description

Preview GDSII layout files directly in the Windows Explorer preview pane.

## Description

GDS Preview for Windows Explorer adds fast, local previews for `.gds` and `.gdsii` integrated-
circuit layout files to the Windows Explorer preview pane.

Select a GDSII file in Explorer and press Alt+P to inspect its geometry without opening a full
layout editor. The preview supports cell hierarchy, references, arrays, transformations, paths,
boxes, and boundaries. Multiple top-level cells are shown in a tiled overview.

Parsing and rendering run in a separate, time-limited process to protect Explorer from malformed
or unusually complex files. Files remain on the device: the app has no telemetry, advertising,
accounts, or network communication.

GDSII is supported. OASIS files are not supported.

## Feature bullets

- Explorer preview-pane integration for `.gds` and `.gdsii`
- Hierarchical cells, SREF, AREF, rotation, reflection, and magnification
- BOUNDARY, BOX, and PATH geometry
- Layer and datatype coloring
- Multiple-top-cell overview
- Isolated renderer with conservative complexity and time limits
- Fully local processing with no telemetry or network communication

## URLs

- Website: https://github.com/keikawa/gds-preview-for-windows-explorer
- Support: https://github.com/keikawa/gds-preview-for-windows-explorer/issues
- Privacy policy: https://github.com/keikawa/gds-preview-for-windows-explorer/blob/main/PRIVACY.md

## What's new in 0.1.1.0

Improved previews for dense and high-vertex GDSII layouts. Fine structures remain visible at
overview scale, and complete polygon vertex sequences are now preserved to prevent incorrect
diagonal edges and distorted geometry.

## Search terms

GDSII, GDS, layout, preview, Explorer, semiconductor, IC design

## Certification notes

This package installs a documented Windows Explorer preview handler. It does not replace Explorer
or register itself as the default application for GDSII files.

Test procedure:

1. Launch **GDS Preview for Windows Explorer** from Start.
2. Choose **Yes** to copy and select the included `demo.gds` file.
3. In the Explorer window, enable the preview pane with Alt+P.
4. Select `demo.gds`; its colored layout should appear in the preview pane.
5. If Explorer was already running during installation, close and reopen its windows before testing.

The `runFullTrust` capability is required because the product is a native in-process COM preview
handler hosted by Windows PreviewHost and it launches a separate local renderer process. The
renderer has a six-second timeout and conservative parser/geometry limits.

Restricted-capability justification (copy into the `runFullTrust` field):

> GDS Preview for Windows Explorer requires runFullTrust because it is a packaged Win32 shell
> extension. Windows Explorer activates its native in-process COM preview-handler DLL through the
> documented Windows PreviewHost system surrogate. The handler launches only the bundled local
> GdsPreview.Renderer.exe process to parse and rasterize the selected GDSII file outside Explorer.
> It does not elevate, install a service or driver, access the network, collect data, or launch
> downloaded executables. The renderer is terminated after six seconds and enforces conservative
> file, geometry, hierarchy, and memory limits.

The app does not collect data and does not use the network. GDSII files are parsed locally only.

## Assets still entered in Partner Center

- Use `docs/images/demo-preview.png` as the source for the Store screenshot.
- Package logos are generated during the MSIX build under `artifacts/msix/staging/Assets`.
- Add English and Japanese Store listings if both languages will be supported at launch.
