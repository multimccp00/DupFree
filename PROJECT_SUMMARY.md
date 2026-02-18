# DupFree - Project Summary

## Project Overview

**DupFree** is a Windows desktop application built with C# (.NET 8) and WPF that finds duplicate files and visually similar images. It features GPU-accelerated SSIM comparison, perceptual hashing, a dark-themed modern UI, and an in-app recycle bin.

**Version**: v1.2  
**Author**: Miguel Campos  
**Status**: In Development

---

## Key Features

### Duplicate File Detection
- Groups files by matching **name + size** pairs
- Recursive directory scanning via BFS (max depth 100)
- Filters hidden, system, and reparse-point files
- Configurable minimum file size and scan limits
- Progress reporting with cancellation support

### Similar Image Detection
- **Perceptual hashing** (DCT-based, 64-bit packed hashes)
- **BK-tree index** for fast Hamming-distance neighbor queries
- **Tile hashing** (3×3 sub-image grid) for spatial similarity
- **SSIM verification** with composite scoring: 65% SSIM + 15% hash + 10% histogram + 10% composition
- **GPU-accelerated SSIM** via Direct3D 11 compute shaders (HLSL)
- **CPU SIMD fallback** using `System.Numerics.Vector<float>`
- Persistent hash cache (`phash_index.json`) — only recomputes for modified files
- Configurable similarity threshold (75–99%)
- Progressive streaming results
- Auto-select logic: prefer uncompressed, higher resolution, or larger filesize

### Recycle Bin
- In-app recycle bin (max 30 items)
- Files sent to Windows recycle bin via `Microsoft.VisualBasic.FileIO`
- Restore deleted files from context menu
- Clear bin functionality

### Settings (persisted to `%AppData%/DupFree/settings.json`)
- Size unit display (Auto/Bytes/KB/MB/GB/TB)
- File size filter limits (min/max)
- Duplicate display limit
- Grid thumbnail size (100–300px)
- Show/hide file paths in grid view
- Confirm delete dialog toggle
- Similar images auto-select preferences
- Scan timer visibility

### UI
- **Dark theme** with custom WPF styles
- **Dark title bar** via Windows DWM API
- **Collapsible sidebar** with 6 panels: Browse, Duplicate Files, Similar Images, Recycle Bin, Settings, Help
- **Two view modes**: List (DataGrid) and Grid (thumbnail cards)
- **Search/filter** by filename
- **Footer statistics**: Files Checked, Duplicates, Space Wasted, Space Saved
- **Storage drive indicator** with volume label and usage bar
- **Keyboard navigation** (arrow keys, Delete, Enter)
- **Similar Images panel** with preview pane, similarity slider, auto-select options

---

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Framework | .NET 8 (net8.0-windows) |
| UI | WPF (Windows Presentation Foundation) |
| Image Processing | Magick.NET (ImageMagick), System.Drawing.Common |
| GPU Compute | Vortice.Direct3D11, Vortice.D3DCompiler (HLSL shaders) |
| Dialogs | Ookii.Dialogs.Wpf |
| Serialization | System.Text.Json |

---

## Project Structure

```
DupFree/
├── App.xaml / App.xaml.cs          # Application entry, themes, global exception handling
├── DupFree.csproj                  # Project configuration and NuGet references
├── app.manifest                    # Windows application manifest
├── Models/
│   └── FileItemViewModel.cs        # ViewModels: FileItem, DuplicateGroup, SimilarGroup
├── Services/
│   ├── DuplicateSearchService.cs   # Duplicate detection engine (name+size grouping)
│   ├── SimilarImageService.cs      # Similar image detection (pHash + SSIM composite)
│   ├── GpuSsim.cs                  # GPU SSIM via D3D11 compute shaders + CPU SIMD fallback
│   ├── PhashIndex.cs               # Persistent perceptual hash cache with BK-tree
│   ├── ImagePreviewService.cs      # Thumbnail generation and format detection
│   └── SettingsService.cs          # Settings persistence (%AppData%/DupFree/)
└── Views/
    ├── MainWindow.xaml / .cs       # Main application window and all duplicate-file UI
    └── SimilarImagesPanel.xaml / .cs  # Similar images UserControl with preview pane
```

---

## NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Magick.NET-Q16-AnyCPU | 14.10.2 | ImageMagick for SSIM comparison and thumbnails |
| System.Drawing.Common | 8.0.0 | GDI+ image APIs (tile hashing, pixel operations) |
| Ookii.Dialogs.Wpf | 2.1.0 | Native Windows folder browser dialog |
| Vortice.Direct3D11 | 3.2.0 | D3D11 device/context for GPU SSIM |
| Vortice.DXGI | 3.2.0 | DXGI support for Vortice |
| Vortice.D3DCompiler | 3.2.0 | HLSL shader compilation at runtime |
| Vortice.Mathematics | 2.1.0 | Math types for Vortice |

---

## How It Works

### Duplicate Detection Flow
1. User selects one or more directories via folder browser
2. `DuplicateSearchService` recursively scans directories (BFS, max depth 100)
3. Files are grouped by **(filename, size)** pairs
4. Groups with 2+ files are reported as duplicates
5. Results displayed in DataGrid (list) or WrapPanel (grid) view

### Similar Image Detection Flow
1. User opens Similar Images panel and clicks Scan
2. `SimilarImageService` computes perceptual hashes for all images
3. Hashes indexed in a **BK-tree** for fast neighbor lookup
4. Candidate pairs within Hamming distance threshold identified
5. Each pair undergoes **SSIM verification** (GPU or CPU):
   - Composite score = 0.65×SSIM + 0.15×hash + 0.10×histogram + 0.10×composition
6. Pairs exceeding the composite threshold are grouped
7. Groups stream progressively to the UI

### GPU SSIM Pipeline
1. Images resized to 128×128 grayscale
2. Uploaded to GPU as `R32_Float` textures
3. HLSL compute shader calculates SSIM per-pixel
4. Results read back from GPU staging buffer
5. Falls back to CPU SIMD (`Vector<float>`) if GPU unavailable
