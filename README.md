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
- .NET 8 SDK
- GPU with DirectX 11 support (optional, for accelerated SSIM)

## Getting Started

```bash
dotnet restore
dotnet build
dotnet run
```

Or open `Dupfree.sln` in Visual Studio 2022 and press F5.

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
| [QUICKSTART.md](QUICKSTART.md) | Quick start guide for new users |
| [USAGE_GUIDE.md](USAGE_GUIDE.md) | Comprehensive usage instructions |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Development guide for contributors |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System architecture and design decisions |

## Version

**v1.2** — In Development  
**Author**: Miguel Campos

## License

All rights reserved.
