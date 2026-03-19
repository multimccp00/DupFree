# DupFree - Development Guide

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK** — [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Visual Studio 2022** (recommended) or VS Code with C# Dev Kit
- **GPU with DirectX 11** (optional, for accelerated SSIM)

## Setup

```bash
# Clone the repository
git clone <repo-url>
cd DupFree

# Restore NuGet packages
dotnet restore

# Build the project
dotnet build

# Run in debug mode
dotnet run --configuration Debug
```

### Visual Studio
1. Open `Dupfree.sln`
2. Set DupFree as the startup project
3. Press F5 to build and run

---

## NuGet Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Magick.NET-Q16-AnyCPU | 14.10.2 | ImageMagick bindings for SSIM comparison and image processing |
| System.Drawing.Common | 8.0.0 | GDI+ APIs for tile hashing and pixel-level operations |
| Ookii.Dialogs.Wpf | 2.1.0 | Native Windows Vista-style folder browser dialog |
| Vortice.Direct3D11 | 3.2.0 | Direct3D 11 device/context for GPU compute shaders |
| Vortice.DXGI | 3.2.0 | DXGI factory/adapter support (required by Vortice.Direct3D11) |
| Vortice.D3DCompiler | 3.2.0 | Runtime HLSL shader compilation |
| Vortice.Mathematics | 2.1.0 | Math/vector types for Vortice interop |

---

## Project Structure

```
DupFree/
├── App.xaml                        # Application resources, themes, styles
├── App.xaml.cs                     # Startup/exit lifecycle, global exception handlers
├── DupFree.csproj                  # .NET 8 project file, NuGet references
├── app.manifest                    # Windows application manifest (DPI, UAC)
├── Models/
│   └── FileItemViewModel.cs        # ViewModels (FileItem, DuplicateGroup, SimilarGroup)
├── Services/
│   ├── DuplicateSearchService.cs   # Duplicate detection via name+size grouping
│   ├── SimilarImageService.cs      # Similar image detection (pHash + SSIM composite)
│   ├── GpuSsim.cs                  # GPU SSIM via D3D11 compute shaders + CPU SIMD fallback
│   ├── PhashIndex.cs               # Persistent perceptual hash cache with BK-tree
│   ├── ImagePreviewService.cs      # Thumbnail generation and format detection
│   └── SettingsService.cs          # Settings persistence (%AppData%/DupFree/settings.json)
└── Views/
    ├── MainWindow.xaml             # Main window layout (sidebar, panels, footer)
    ├── MainWindow.xaml.cs          # Main window code-behind (scan, display, navigation)
    ├── SimilarImagesPanel.xaml     # Similar images UserControl layout
    └── SimilarImagesPanel.xaml.cs  # Similar images scan logic and UI
```

---

## Key Services

### DuplicateSearchService
- Groups files by **(filename, size)** pairs
- Recursive BFS directory traversal (max depth 100)
- Filters hidden/system/reparse-point files
- Supports cancellation and progress reporting
- Configurable file count limits

### SimilarImageService
- 2-phase detection: fast perceptual hash pre-filter → SSIM verification
- Composite scoring: **65% SSIM + 15% hash + 10% histogram + 10% composition**
- BK-tree index for O(log n) Hamming-distance neighbor queries
- Tile hashing (3×3 grid) for spatial similarity
- Parallelized hash computation across all CPU cores
- Progressive streaming of grouped results via events
- Persistent `phash_index.json` cache (only recomputes for modified files)

### GpuSsim
- **GPU path**: Direct3D 11 compute shader (HLSL) for parallel SSIM
  - Images uploaded as `R32_Float` textures (128×128 grayscale)
  - Results read back via staging buffer
  - Thread-safe with lock
- **CPU fallback**: SIMD-accelerated using `System.Numerics.Vector<float>`
- SSIM constants: K1=0.01, K2=0.03, L=1.0

### PhashIndex
- Persistent cache file: `phash_index.json`
- Stores: file path, length, last-write time, hash bytes, packed hash
- BK-tree for fast Hamming-distance range queries
- Tile hash inverted index for spatial matching
- Incremental updates: only new/modified files recomputed

### SettingsService
- Static service with `OnSettingsChanged` event
- JSON persistence via `System.Text.Json`
- Settings file location: `%AppData%/DupFree/settings.json`
- Loaded in `App.OnStartup()`, saved in `App.OnExit()`

### ImagePreviewService
- Generates thumbnails (max 256×256) using `BitmapImage`
- Supports: JPG, JPEG, PNG, BMP, GIF, WebP, TIFF, ICO
- `FormatFileSize()` with configurable unit display
- Uses `OnLoad` cache mode and `Freeze()` for thread safety

---

## UI Architecture

### Themes and Styles (App.xaml)
- Full dark theme with 30+ color resources
- Custom control templates for: Button (5 variants), CheckBox, ComboBox, DataGrid, ScrollBar, TextBox, Window
- Color palette matches React/Tailwind dark design conventions

### MainWindow (Views/MainWindow.xaml.cs)
- **Sidebar + panel navigation** — `ShowPanel()` switches between Scan, Results, Similar Images, Recycle Bin, Settings, Help
- **Duplicate file grid** — `DataGrid` (list) or `WrapPanel`/`Canvas` virtual grid (grid view)
- **GIF animation** — `DispatcherTimer` at `DispatcherPriority.Background` drives frame stepping from Magick.NET-decoded frame cache. `animationActive`/`autoPlayStarted`/`gridLoaded` flags prevent double-start and slot leaks
- **Video preview** — `MediaElement` with `MediaEnded` looping, `videoFailed` guard for broken files, LRU eviction via `_videoPreviewStoppers: List<Action>` (cap: `MaxConcurrentVideoPreviews = 6`)
- **Viewport gating** — `IsTileInViewport()` via `TransformToAncestor` + `ScrollChangedEventHandler` stops off-screen media automatically
- **Keyboard navigation** — Arrow keys, Enter, Delete in grid view

### SimilarImagesPanel (UserControl)
- Similarity slider (75–99%) with custom round thumb template
- Custom progress bar (Grid with track + fill Borders)
- Optional elapsed-time timer (DispatcherTimer)
- Left: scrollable group thumbnails (80×80) with red border selection
- Right: 350×250 preview with file info and Open Image button

---

## Building for Release

```bash
dotnet publish -c Release -r win-x64 --self-contained true
```

Output will be in `bin/Release/net8.0-windows/win-x64/publish/`.

---

## Common Development Tasks

### Adding a New Setting
1. Add property and setter in `SettingsService.cs`
2. Add to the `SaveToFile()` anonymous object
3. Add `TryGetProperty` read in `LoadFromFile()`
4. Add UI control in the Settings section of `MainWindow.xaml`
5. Wire up the control event handler in `MainWindow.xaml.cs`

### Adding a New Sidebar Panel
1. Add a ToggleButton in the sidebar section of `MainWindow.xaml`
2. Create the panel content (Grid/StackPanel) in the main content area
3. Add the panel to the `ShowPanel()` method in `MainWindow.xaml.cs`
4. Wire up the sidebar button click handler

### Modifying the Similar Image Algorithm
- Threshold constants are at the top of `SimilarImageService.cs`
- Composite weights: `CompositeWeightSsim`, `CompositeWeightHash`, `CompositeWeightHist`, `CompositeWeightComp`
- Hash distance threshold: `HashDistanceThreshold`
- BK-tree query radius is derived from the threshold

---

## Debugging

### Crash Logs
Global exception handlers write crash details to:
```
%TEMP%/dupfree_crash.log
```

### Diagnostic Output
- `DuplicateSearchService` writes diagnostic logs to file during scanning
- Use `System.Diagnostics.Debug.WriteLine` statements (visible in VS Debug Output)
- Enable the scan timer in Settings to see elapsed time for similar image scans
- **Telemetry**: optional anonymous timing/metric events are logged when the corresponding setting is enabled. Look for `TELEMETRY:` entries in the normal log file.

