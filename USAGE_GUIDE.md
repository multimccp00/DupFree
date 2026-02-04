# DupFree - Duplicate File Finder

A modern Windows desktop application for finding and managing duplicate files with advanced features like image preview, multiple view modes, and side-by-side comparison.

## 🎯 Key Features

### Duplicate Detection
- **Fast SHA256-based hashing** to find exact duplicates
- **Quick pre-filtering** by file size to optimize scanning
- **Wasted space calculation** showing potential disk recovery per group
- **Recursive directory scanning** for comprehensive coverage

### Image Preview & Visualization
- **Multiple format support**: JPG, PNG, BMP, GIF, WebP, TIFF, ICO
- **Thumbnail generation** for quick visual identification
- **Three view modes**:
  - **Icon View**: Compact thumbnails with file size
  - **Large Icon View**: Larger previews with file details
  - **List View**: Detailed information (name, path, size, date)

### Duplicate Management
- **Side-by-side display** of duplicate files for easy comparison
- **Expandable/collapsible** groups for better organization
- **Sort options**: Name, Size, Modified Date, Path
- **Real-time progress tracking** during scanning

## 🚀 Getting Started

### Requirements
- Windows 10 or later
- .NET 8.0 Runtime or SDK
- Minimum 4GB RAM recommended

### Installation & Running

1. **Clone or extract the project**
   ```bash
   git clone <repository-url>
   cd DupFree
   ```

2. **Build the project**
   ```bash
   dotnet build
   ```

3. **Run the application**
   ```bash
   dotnet run
   ```

4. **Or build a release version**
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained
   ```

## 📖 Usage Guide

### Basic Workflow

1. **Select a Folder**
   - Click the "📁 Browse" button
   - Choose a directory to scan (folders will be scanned recursively)

2. **Start Scanning**
   - Click "🔍 Scan" to begin duplicate detection
   - Watch the progress bar and status messages
   - The scan may take time depending on folder size

3. **View Results**
   - Results are organized in groups (each group = one set of duplicates)
   - Each group shows the number of duplicates and wasted space
   - Files in each group are identical copies

4. **Change View Mode**
   - Click view mode buttons to switch between views
   - **Icon View (🗷)**: Compact thumbnails
   - **Large Icon View (⊞)**: Larger previews with more info
   - **List View (☰)**: Detailed spreadsheet-like view

5. **Sort Results**
   - Use the dropdown menu to sort by:
     - **Name**: Alphabetical order
     - **Size**: Largest to smallest
     - **Modified Date**: Newest first
     - **Path**: Directory path order

### Understanding the UI

**Header Colors:**
- 🔵 Light Blue: Duplicate group header
- Shows count of duplicates and total wasted space

**File Display:**
- Image files show visual thumbnails
- Non-image files show a generic document icon
- Size shown in human-readable format (B, KB, MB, GB)
- Modification date helps identify which copy to keep

## 🏗️ Project Structure

```
DupFree/
├── Services/
│   ├── FileHashService.cs           # SHA256 hashing utilities
│   ├── DuplicateSearchService.cs    # Core duplicate detection engine
│   └── ImagePreviewService.cs       # Thumbnail generation & file info
├── Models/
│   └── FileItemViewModel.cs         # Data models for UI binding
├── Views/
│   ├── MainWindow.xaml              # UI layout
│   └── MainWindow.xaml.cs           # Event handlers & logic
├── App.xaml & App.xaml.cs           # Application entry point
├── DupFree.csproj                   # Project configuration
├── app.manifest                     # Windows application manifest
└── README.md                        # This file
```

## 🔧 How It Works

### Duplicate Detection Algorithm

1. **Collection Phase**
   - Recursively scans all directories
   - Collects FileInfo for each file

2. **Size Grouping** (Quick Filter)
   - Groups files by size
   - Only processes groups with 2+ files
   - Eliminates most files before expensive operations

3. **Hash Computation**
   - Computes SHA256 hash for files in size groups
   - Uses full file for accuracy

4. **Duplicate Identification**
   - Groups files by identical hashes
   - Creates duplicate groups (minimum 2 files per group)

5. **UI Rendering**
   - Displays results grouped with wasted space info
   - Generates thumbnails for image files
   - Applies user's chosen sorting

### Performance Optimizations

- **Two-pass approach**: Size check before hashing
- **Asynchronous operations**: Non-blocking UI during scanning
- **Efficient memory usage**: Processes files in batches
- **Smart thumbnail generation**: Max 256x256 with caching

## 🎨 View Modes Explained

### Icon View (🗷)
- Compact 120x120 px display
- Best for: Quick visual scanning
- Shows: Thumbnail/icon, filename, size

### Large Icon View (⊞)
- Spacious 180x180 px display
- Best for: Photo/image collections
- Shows: Large thumbnail, name, size, full path

### List View (☰)
- Traditional spreadsheet layout
- Best for: Detailed analysis
- Shows: Name, Path, Size, Modified Date
- Columns sortable by view option

## 📊 Tips for Best Results

1. **Start with one folder** - Easier to understand results
2. **Check modified dates** - Helps identify which file to keep
3. **Preview images** - Visual thumbnails help confirm duplicates
4. **Sort by size** - Large duplicates mean more space recovery
5. **Sort by date** - Newer/older versions are more important

## 🛠️ Future Enhancement Ideas

- [ ] Delete/move duplicate files safely with confirmation
- [ ] File comparison viewer (binary diff)
- [ ] Export duplicate report to CSV/PDF
- [ ] Custom file type filters
- [ ] Scheduled scanning
- [ ] Duplicate file safe removal wizard
- [ ] Cloud storage support
- [ ] Custom hash algorithm selection
- [ ] Multi-threaded hash computation for faster scanning
- [ ] Undo/recovery mechanism

## ⚙️ System Requirements

- **OS**: Windows 10 or later
- **Framework**: .NET 8.0
- **CPU**: Multi-core recommended
- **RAM**: 4GB+ recommended
- **Storage**: Free space for temporary operations

## 📝 Troubleshooting

### Application won't start
- Ensure .NET 8.0 runtime is installed
- Check Windows Firewall isn't blocking the app
- Run with administrator privileges if access denied

### Scanning is very slow
- Large folders with millions of files take time
- Check disk for bottlenecks
- Close other applications to free resources

### No duplicates found
- Ensure folder contains more than one copy of files
- Check file permissions
- Try with a known duplicate file folder

### Thumbnails not showing
- Ensure image files are in supported formats
- Check file permissions
- Verify file isn't corrupted

## 📄 License

This project is provided as-is for educational and personal use.

## 🤝 Contributing

Feel free to fork and submit pull requests for improvements!

## 📧 Support

For issues, questions, or suggestions, please create an issue in the repository.
