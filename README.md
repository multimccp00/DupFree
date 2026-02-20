# DupFree

A Windows desktop application for finding duplicate files and visually similar images, built with C# (.NET 8) and WPF.

## Features

- **Duplicate File Detection** — Finds files with matching names and sizes across directories
- **Similar Image Detection** — Perceptual hashing + GPU-accelerated SSIM comparison
- **Dark Theme UI** — Modern dark interface with collapsible sidebar navigation
- **Multiple View Modes** — List (DataGrid) and Grid (thumbnail) views
- **In-App Recycle Bin** — Delete with undo via restore functionality
- **Persistent Settings** — Preferences saved to `%AppData%/DupFree/settings.json`
- **Search & Filter** — Filter by filename, minimum size, and scan limits
- **Auto-Select** — Automatically mark lower-quality similar images for deletion

## Requirements

- Windows 10/11
- .NET 8 Runtime (SDK not required for end users)
- **Visual C++ 2015‑2022 Redistributable (x64)** – needed by native libraries such as Magick.NET; failing to install it results in a "side‑by‑side configuration is incorrect" error when the EXE is launched.
- GPU with DirectX 11 support (optional, for accelerated SSIM)

### Dependencies & updates

This project consumes several third‑party NuGet packages (Magick.NET, Ookii.Dialogs,
Vortice, etc.). Keep them current by running:

```powershell
cd e:\Personal_Stuff\Dupfree
dotnet list package --outdated
```

and then updating the `PackageReference` versions in `DupFree.csproj` or using
`dotnet add package <name> --version <x.y.z>`.

> 💡 To audit for known vulnerabilities, use the built‑in CLI command:
> 
> ```powershell
> dotnet list package --vulnerable
> ```
> 
> or integrate `dotnet list package --vulnerable --include-transitive` in
> your CI pipeline.  Periodic audits help catch security issues in dependencies.
>
> The application also provides a convenient **Check Dependencies** button in
> the Help panel which runs `dotnet list package --outdated` for you and shows
> the results in a dialog; no CLI usage required from developers that just
> want a quick look.

Keeping dependencies up‑to‑date not only provides new features/performance
improvements but also ensures any security fixes are applied.
## Getting Started

```bash
dotnet restore
dotnet build
dotnet run
```

Or open `Dupfree.sln` in Visual Studio 2022 and press F5.

### Logging

### Error handling

If the application encounters an unhandled exception (UI thread, background
task, or other) it writes details to ``%TEMP%/dupfree_crash.log`` and then
presents a friendly dialog explaining that an unexpected error occurred.
The dialog shows the path to the log file and offers **Copy Log** and **Open
Log** buttons for easy reporting.  This ensures problems are visible even if
beginning of the process never had a window.


### DPI & Scaling

The app makes itself DPI aware at runtime using the Win32 API instead of
relying on the manifest. This way we avoid embedding an unsupported
`<dpiAware>` element on older Windows releases (the SideBySide loader will
fail with exit code 1 if the operating system doesn't understand that tag).

All windows still use layout rounding and pixel snapping so the UI scales
cleanly on high‑DPI displays. The runtime check quietly falls back on older
OS versions where the API isn't available.


A runtime log of recent actions is written to:

`%AppData%\DupFree\dupfree.log`

Please include this file when reporting bugs — it contains timestamps and the last operations performed by the application.

A convenience button labeled **"Open Log File"** is available in the Help &gt; About panel; it will open Explorer with the log selected so you can copy or attach it quickly.
Additionally, a **"Report Issue"** button sits alongside it. Clicking it opens a dialog where you can type a description; pressing **Send** will open a browser pointing at a pre‑filled GitHub issue page (the repo is hard‑coded in the app). This requires no configuration and works whether the repo is public or private.

### Permissions

When choosing a folder to scan, DupFree checks access rights immediately. If the selected directory cannot be enumerated due to operating system permissions, a warning dialog explains the issue and the folder will not be added. During scanning, any subfolders that cannot be entered are skipped automatically; the status bar will note if permission errors occurred.

> ⚠️ **Privacy note:** if you want the repository to be invisible to the public, set it to **private** on GitHub. That’s controlled by your GitHub account – the application itself doesn’t expose your code.
## Project Structure

```
DupFree/
├── App.xaml / App.xaml.cs          # Application entry, themes, exception handling
├── Models/
│   └── FileItemViewModel.cs        # ViewModels for files, groups, similar images
├── Services/
│   ├── DuplicateSearchService.cs   # Duplicate detection (name+size grouping)
│   ├── SimilarImageService.cs      # Similar image detection (pHash + SSIM)
│   ├── GpuSsim.cs                  # GPU SSIM via D3D11 compute shaders
│   ├── PhashIndex.cs               # Persistent hash cache with BK-tree
│   ├── ImagePreviewService.cs      # Thumbnail generation
│   └── SettingsService.cs          # Settings persistence
└── Views/
    ├── MainWindow.xaml / .cs       # Main window and duplicate-file UI
    └── SimilarImagesPanel.xaml / .cs  # Similar images panel with preview
```

## Technology

| Component | Technology |
|-----------|-----------|
| Framework | .NET 8 (net8.0-windows), WPF |
| Image Processing | Magick.NET, System.Drawing.Common |
| GPU Compute | Vortice.Direct3D11, D3DCompiler (HLSL) |
| Dialogs | Ookii.Dialogs.Wpf |

## Documentation

| Document | Description |
|----------|-------------|
| [PROJECT_SUMMARY.md](PROJECT_SUMMARY.md) | Detailed feature overview and technical summary |

## Settings

All configuration options in the Settings panel are saved automatically when changed and persisted in `%AppData%/DupFree/settings.json`. A **Reset to Defaults** button restores the original values and overwrites the settings file.
| [QUICKSTART.md](QUICKSTART.md) | Quick start guide for new users |
| [USAGE_GUIDE.md](USAGE_GUIDE.md) | Comprehensive usage instructions |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Development guide for contributors |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System architecture and design decisions |

## Version

**v1.2** — In Development  
**Author**: Miguel Campos

## License

All rights reserved.
