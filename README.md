# DupFree - Duplicate File Finder

> **Work in Progress**: This project is actively being developed.

A modern Windows desktop application for finding and managing duplicate files with image preview capabilities and multiple view modes.

## Features

- **Duplicate Detection**: Fast SHA256-based file hashing to find exact duplicates
- **Image Preview**: Preview images, GIFs, WebPs, and other image formats
- **Multiple View Modes**:
  - Icon View (small thumbnails)
  - Large Icon View (with file info)
  - List View (detailed file information)
- **Sorting Options**: Sort by Name, Size, Modified Date, or Path
- **Side-by-Side Display**: Duplicate files displayed together for easy comparison
- **Wasted Space Calculation**: Shows how much disk space can be freed per duplicate group
- **Progress Tracking**: Real-time status updates during scanning

## Installation

1. Ensure you have .NET 8.0 or later installed
2. Clone or extract the project files
3. Build the project:
   ```
   dotnet build
   ```

## Usage

1. Launch the application
2. Click "Browse" to select a folder to scan for duplicates
3. Click "Scan" to start the duplicate detection process
4. Use the view buttons to change between Icon, Large Icon, and List views
5. Use the Sort dropdown to organize results
6. Click group headers to expand/collapse duplicate groups

## Project Structure

```
DupFree/
├── Services/
│   ├── FileHashService.cs       - File hashing utilities
│   ├── DuplicateSearchService.cs - Duplicate detection logic
│   └── ImagePreviewService.cs   - Image thumbnail generation
├── Models/
│   └── FileItemViewModel.cs     - Data models for files and groups
├── Views/
│   └── MainWindow.xaml(.cs)     - Main UI
├── App.xaml(.cs)                - Application entry point
└── DupFree.csproj               - Project configuration
```

## How It Works

1. **File Collection**: Recursively collects all files from selected directories
2. **Size Grouping**: Groups files by size (first optimization)
3. **Hash Computation**: Computes SHA256 hash for files with duplicate sizes
4. **Duplicate Detection**: Groups files with identical hashes
5. **Visualization**: Displays duplicates with previews and file information

## Performance

- Quick pre-filtering by file size reduces unnecessary hash computations
- Asynchronous operations keep UI responsive during scanning
- Efficient thumbnail generation for image previews
- Progress reporting for user feedback

## Future Enhancements

- Compare files before deletion
- Custom file filter options
- Export duplicate reports
- Settings for hash algorithm selection
- Recycle Bin support to undo delete operations
- Continued UI polish for a clean, modern experience
- Additional user options and customization
