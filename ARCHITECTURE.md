# DupFree - Architecture

## System Overview

DupFree is a .NET 8 WPF desktop application organized into three layers:

```
┌─────────────────────────────────────────────┐
│                   Views                      │
│  MainWindow.xaml/cs  SimilarImagesPanel.xaml/cs │
├─────────────────────────────────────────────┤
│                  Services                    │
│  DuplicateSearchService  SimilarImageService │
│  GpuSsim  PhashIndex  ImagePreviewService    │
│  SettingsService                             │
├─────────────────────────────────────────────┤
│                  Models                      │
│  FileItemViewModel  DuplicateGroupViewModel  │
│  SimilarImageGroupViewModel                  │
└─────────────────────────────────────────────┘
```

- **Views** — WPF UI layer (XAML + code-behind). Handles user interaction, display, and navigation.
- **Services** — Business logic and data processing. No direct UI dependencies (communicate via events and callbacks).
- **Models** — ViewModels with `INotifyPropertyChanged` for data binding.

---

## Component Diagram

```
User Input
    │
    ▼
┌──────────────┐     ┌──────────────────────┐
│  MainWindow  │────▶│ DuplicateSearchService│
│  (Browse,    │     │ (name+size grouping)  │
│   Scan,      │     └──────────────────────┘
│   Results)   │
└──────┬───────┘
       │
       ▼
┌──────────────────┐     ┌──────────────┐     ┌──────────────┐
│ SimilarImages    │────▶│ SimilarImage │────▶│   GpuSsim    │
│ Panel            │     │ Service      │     │ (D3D11/SIMD) │
│ (scan, preview)  │     │ (pHash+SSIM) │     └──────────────┘
└──────────────────┘     │              │
                         │              │────▶┌──────────────┐
                         └──────────────┘     │  PhashIndex   │
                                              │ (BK-tree,    │
                                              │  JSON cache)  │
                                              └──────────────┘
Settings ◀──────── SettingsService ──────▶ %AppData%/DupFree/settings.json
Thumbnails ◀────── ImagePreviewService
```

---

## Data Flow

### Duplicate Detection

```
Directories → BFS Scan → Filter (hidden/system) → Group by (name, size)
    → Filter groups with 2+ files → DuplicateGroupViewModel → UI Display
```

1. **Input**: List of directory paths from folder browser
2. **BFS traversal**: Queue-based recursive scan (max depth 100), skips hidden/system/reparse files
3. **Grouping**: Files grouped by `(filename, filesize)` composite key
4. **Output**: List of `DuplicateFileGroup` objects, each containing 2+ matching files
5. **Display**: Flat list of `FileItemViewModel` in DataGrid or Grid view

### Similar Image Detection

```
Directories → Enumerate images → Compute perceptual hashes (parallel)
    → BK-tree index → Query neighbors within Hamming distance
    → SSIM verification (GPU or CPU) → Composite scoring → Group results
    → Stream groups to UI progressively
```

1. **Enumeration**: Find all image files (JPG, PNG, BMP, GIF, WebP, TIFF, ICO)
2. **Hashing (Phase 1)**:
   - Load from `PhashIndex` cache if unchanged (by length + last-write time)
   - Compute DCT-based perceptual hash (64-bit) for new/modified files
   - Compute color histogram (64-bin, 4×4×4 RGB)
   - Compute spatial histogram (3×3 tile grid)
   - All computation parallelized across CPU cores
3. **Indexing**: Insert hashes into BK-tree for efficient range queries
4. **Candidate Selection**: Query BK-tree for pairs within `HashDistanceThreshold`
5. **SSIM Verification (Phase 2)**:
   - Resize images to 128×128 grayscale
   - Compute SSIM via GPU (D3D11 compute shader) or CPU (SIMD vectors)
   - Calculate composite score: `0.65×SSIM + 0.15×hash_sim + 0.10×hist_sim + 0.10×comp_sim`
   - Pairs exceeding threshold are confirmed as similar
6. **Grouping**: Merge confirmed pairs into `SimilarImageGroup` objects
7. **Streaming**: Groups emitted progressively via events as they form

---

## GPU SSIM Architecture

```
                    ┌─────────────────────────────┐
                    │     GpuSsim.ComputeSsim()   │
                    └──────────┬──────────────────┘
                               │
              ┌────────────────┴────────────────┐
              │                                 │
    ┌─────────▼──────────┐           ┌──────────▼─────────┐
    │   GPU Path (D3D11) │           │  CPU Path (SIMD)   │
    │                    │           │                    │
    │ 1. Create textures │           │ 1. Load grayscale  │
    │    (R32_Float)     │           │    float arrays    │
    │ 2. Set SRVs + UAV  │           │ 2. Compute μ, σ²   │
    │ 3. Dispatch HLSL   │           │    using Vector<T> │
    │    compute shader  │           │ 3. Calculate SSIM  │
    │ 4. Read staging    │           │    per-window      │
    │    buffer result   │           │ 4. Return average  │
    └────────────────────┘           └────────────────────┘
```

- **GPU path**: Preferred when D3D11 device initializes successfully
- **CPU fallback**: Automatic when GPU unavailable or initialization fails
- **Thread safety**: GPU path uses `lock` for device access
- **SSIM formula**: Standard with K1=0.01, K2=0.03, L=1.0

---

## BK-Tree Index

The `PhashIndex` class maintains a BK-tree (Burkhard-Keller tree) for efficient nearest-neighbor queries on perceptual hashes:

- **Distance metric**: Hamming distance on 64-bit packed hashes
- **Query operation**: Range query returns all hashes within distance threshold
- **Time complexity**: O(log n) average case per query vs O(n) linear scan
- **Persistence**: Serialized to `phash_index.json` alongside the scanned directory
- **Incremental updates**: Only recomputes hashes for files with changed length or modification time

---

## Settings Architecture

```
App.OnStartup() → SettingsService.LoadFromFile()
    ↓
SettingsService (static properties)
    ↓ OnSettingsChanged event
UI Controls update
    ↓
App.OnExit() → SettingsService.SaveToFile()
```

- All settings stored as static properties in `SettingsService`
- JSON serialization via `System.Text.Json`
- File location: `%AppData%/DupFree/settings.json`
- `OnSettingsChanged` event notifies listeners of changes

---

## UI Architecture

### Panel Navigation
The main window uses a **panel-switching** pattern rather than page navigation:

```csharp
private void ShowPanel(UIElement targetPanel)
{
    // Hide all panels, show target
    ScanPanel.Visibility = Visibility.Collapsed;
    ResultsPanel.Visibility = Visibility.Collapsed;
    // ... etc
    targetPanel.Visibility = Visibility.Visible;
}
```

Sidebar toggle buttons control which panel is visible. Only one panel is shown at a time.

### View Modes
The duplicate results support two display modes:
- **List view**: `DataGrid` with bound columns and row selection
- **Grid view**: `WrapPanel` inside `ScrollViewer` with programmatically created `Border` cards

For large datasets (>1000 items), a virtual grid using `Canvas` provides smooth scrolling by only rendering visible items.

### Theme System
All colors and styles are defined as resources in `App.xaml`:
- 30+ `SolidColorBrush` resources for consistent theming
- Custom `ControlTemplate` definitions for all interactive controls
- Dark color palette: backgrounds (#121827, #1F2937), accents (#6366F1, #3664EF)
- Dark title bar via Windows DWM API (`DwmSetWindowAttribute` with `DWMWA_USE_IMMERSIVE_DARK_MODE`)

---

## Error Handling

### Global Exception Handlers (App.xaml.cs)
Three handlers catch unhandled exceptions at different levels:
1. `AppDomain.CurrentDomain.UnhandledException` — CLR-level
2. `TaskScheduler.UnobservedTaskException` — Async task exceptions
3. `Application.DispatcherUnhandledException` — WPF UI thread exceptions

All write crash details to `%TEMP%/dupfree_crash.log`.

### Service-Level
- Image processing operations wrapped in try-catch (graceful skip on corrupt files)
- GPU SSIM falls back to CPU on any D3D11 failure
- Settings load/save silently fail if file is inaccessible

---

## Performance Characteristics

| Operation | Approach | Notes |
|-----------|----------|-------|
| Directory scan | BFS queue | Max depth 100, skips special files |
| Perceptual hashing | Parallel (all cores) | DCT-based, 64-bit packed |
| Hash neighbor search | BK-tree | O(log n) vs O(n) linear |
| SSIM computation | GPU (D3D11) or CPU (SIMD) | 128×128 grayscale comparison |
| Hash caching | JSON file | Only recomputes modified files |
| UI rendering | Virtual grid for >1000 items | Canvas with on-demand rendering |
| Thumbnail loading | Async with semaphore (4 concurrent) | BitmapImage with Freeze() |
