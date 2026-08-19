# Microsoft Store MSIX submission

The Store build uses manifest-based COM and file type registration. It does not run the
PowerShell installer or write the preview-handler registry keys directly.

## Prerequisites

- Windows 10 or Windows 11, x64
- .NET 8 SDK
- Zig 0.15 or newer
- Windows 10/11 SDK (`MakeAppx.exe` and, for sideload signing, `SignTool.exe`)
- A Partner Center app-name reservation

The first MSIX build downloads Microsoft's `win-x64` .NET runtime packs from NuGet.org. The normal
ZIP build remains offline after its source restore.

## Build a Store package

The repository defaults already contain the Partner Center identity assigned to this app:

- Name: `keikawa.GDSPreviewforWindowsExplorer`
- Publisher: `CN=915278F7-D39C-4A79-8E88-5A30F45250CB`
- Publisher display name: `keikawa`

Build the submission package with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 `
  -Version 0.1.0.0
```

The output is `artifacts\msix\GDS-Preview-for-Windows-Explorer-0.1.0.0-x64.msix`. The package is
left unsigned for Partner Center, which signs the certified package.

MSIX versions have four numeric parts, each from 0 through 65535. Prerelease suffixes such as
`-beta.1` are not valid in the manifest version.

The Store MSIX targets Windows 10 version 1809 (`10.0.17763.0`) or later. Microsoft Store does not
accept MSIX packages targeting Windows 10 version 1803 or earlier.

## Sideload test

For local installation, sign the package with a code-signing certificate whose subject matches the
manifest Publisher. If the certificate is already in the current user's certificate store:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-msix.ps1 `
  -Version 0.1.0.0 `
  -CertificateThumbprint '<certificate SHA-1 thumbprint>'
```

Trust the test certificate only on test machines, then install with `Add-AppxPackage`. An unsigned
package cannot be sideloaded. Uninstall the existing ZIP/script version before testing the MSIX so
that its registry registration cannot mask an MSIX problem.

Test at least these cases before submission:

- clean install and uninstall on current Windows 10 and Windows 11 x64
- `.gds` and `.gdsii` preview registration after reopening Explorer
- Store app launch and `Alt+P` instructions
- multiple top-level cells, deeply nested cells, arrays, and a large-file timeout
- files with Mark of the Web and files in read-only/network locations
- upgrade from the previous MSIX version

Run the Windows App Certification Kit against the final package, then upload the same `.msix` to
Partner Center. Store listing screenshots and descriptions are managed separately from the logo
assets embedded in the package.

The package declares the restricted `runFullTrust` capability. Copy the justification from
`packaging/STORE-LISTING.md` into the restricted-capability field on the Submission options page.

## Package architecture

- `GdsPreview.App.exe` is the small Start-menu status/instructions application.
- `GdsPreview.Native.dll` is registered as a packaged COM class under the system PreviewHost.
- `GdsPreview.Renderer.exe` is published self-contained for `win-x64`; Store users do not need to
  install the .NET Desktop Runtime separately.
- `.gds` and `.gdsii` use `desktop2:DesktopPreviewHandler`, whose CLSID must remain identical to the
  packaged COM class ID.
- `Samples\demo.gds` is copied to the user's local app-data folder only when they request the demo
  from the Start-menu app.
