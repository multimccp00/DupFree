# DupFree - Development & Contribution Guide

## Development Setup

### Environment Requirements
- **OS**: Windows 10 or later
- **IDE**: Visual Studio Code or Visual Studio 2022
- **.NET**: SDK 8.0 or later
- **Git**: For version control (optional)

### Initial Setup
```powershell
# Clone repository
git clone <repo-url>
cd DupFree

# Restore NuGet packages
dotnet restore

# Build project
dotnet build

# Run tests (if available)
dotnet test

# Run application
dotnet run
```

---

## Project Architecture

### Layered Architecture
```
┌─────────────────────────────────────┐
│   Presentation (XAML/WPF)           │
│   - Views/MainWindow.xaml(.cs)      │
└────────────────┬────────────────────┘
                 ↓
┌─────────────────────────────────────┐
│   ViewModel Layer                   │
│   - Models/FileItemViewModel.cs     │
└────────────────┬────────────────────┘
                 ↓
┌─────────────────────────────────────┐
│   Service Layer                     │
│   - DuplicateSearchService          │
│   - FileHashService                 │
│   - ImagePreviewService             │
└────────────────┬────────────────────┘
                 ↓
┌─────────────────────────────────────┐
│   Data Layer                        │
│   - File I/O & System APIs          │
└─────────────────────────────────────┘
```

---

## Code Organization

### Services/ Directory
**Purpose**: Business logic and core functionality

**Files**:
- `DuplicateSearchService.cs`: Core duplicate detection algorithm
- `FileHashService.cs`: Cryptographic hashing operations
- `ImagePreviewService.cs`: Image handling and thumbnails

**Adding a New Service**:
1. Create `IMyService.cs` interface (optional but recommended)
2. Create `MyService.cs` implementing the interface
3. Register in MainWindow.xaml.cs
4. Inject where needed

### Models/ Directory
**Purpose**: Data structures and ViewModels

**Files**:
- `FileItemViewModel.cs`: UI data model for individual files and groups

**ViewModel Pattern**:
```csharp
public class FileItemViewModel : INotifyPropertyChanged
{
    private string _fileName;
    
    public string FileName
    {
        get => _fileName;
        set
        {
            if (_fileName != value)
            {
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
            }
        }
    }
    
    public event PropertyChangedEventHandler PropertyChanged;
    
    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

### Views/ Directory
**Purpose**: UI presentation and user interaction

**Files**:
- `MainWindow.xaml`: UI markup
- `MainWindow.xaml.cs`: Code-behind with event handlers

**View Modes** in `MainWindow.xaml.cs`:
- `CreateIconView()`: 120×120 compact view
- `CreateLargeIconView()`: 180×180 detailed view
- `CreateListView()`: Spreadsheet-style view

---

## Key Algorithms

### Duplicate Detection Algorithm
```
Input: List of directories
Output: List of duplicate groups

1. COLLECT_FILES(directories)
   for each directory:
       recursively add all files to collection

2. GROUP_BY_SIZE(files)
   filter groups with count >= 2
   (optimization: eliminates most files)

3. COMPUTE_HASHES(size_groups)
   for each file in size groups:
       hash = SHA256(file_content)
       add to hash_map[hash].Add(file)

4. CREATE_GROUPS(hash_map)
   for each hash with count >= 2:
       create DuplicateFileGroup
       
5. RETURN duplicate_groups
```

### Time Complexity Analysis
- Directory traversal: O(n) where n = file count
- Size grouping: O(n log n)
- Hashing: O(m × s) where m = duplicate candidates, s = file size
- Overall: O(n log n + m × s)

### Optimization Strategies
1. **Two-pass approach**: Size filter before expensive hashing
2. **Early filtering**: Skip size groups with only 1 file
3. **Error handling**: Continue on access denied
4. **Async operations**: Non-blocking UI during scanning

---

## Code Style Guidelines

### Naming Conventions
```csharp
// Classes: PascalCase
public class DuplicateSearchService { }

// Methods: PascalCase
public async Task<List<DuplicateFileGroup>> FindDuplicatesAsync() { }

// Properties: PascalCase
public string FileName { get; set; }

// Private fields: _camelCase
private List<string> _selectedDirectories;

// Local variables: camelCase
var fileCount = files.Count;

// Constants: UPPER_SNAKE_CASE
private const int DEFAULT_TIMEOUT = 5000;
```

### Code Structure
```csharp
// Order of class members:
// 1. Fields (private, protected, public)
// 2. Properties
// 3. Constructors
// 4. Public methods
// 5. Private methods
// 6. Events
```

### Comments & Documentation
```csharp
/// <summary>
/// Finds duplicate files in the specified directories.
/// </summary>
/// <param name="directories">List of directory paths to scan</param>
/// <param name="progress">Optional progress reporter</param>
/// <returns>List of duplicate file groups</returns>
public async Task<List<DuplicateFileGroup>> FindDuplicatesAsync(
    List<string> directories, 
    IProgress<(int current, int total)> progress = null)
{
    // Implementation
}
```

---

## Common Development Tasks

### Adding a New View Mode

1. **Add button in MainWindow.xaml**:
```xaml
<Button Name="NewViewButton" Click="NewViewButton_Click" Padding="8,5">
    🆕
</Button>
```

2. **Create render method in MainWindow.xaml.cs**:
```csharp
private FrameworkElement CreateNewView(FileItemViewModel file)
{
    var panel = new StackPanel { /* ... */ };
    // Layout logic here
    return panel;
}
```

3. **Update DisplayResults() method**:
```csharp
if (_currentViewMode == "new_mode")
{
    foreach (var file in group.Files)
        itemsPanel.Children.Add(CreateNewView(file));
}
```

4. **Add button handler**:
```csharp
private void NewViewButton_Click(object sender, RoutedEventArgs e)
{
    _currentViewMode = "new_mode";
    DisplayResults();
}
```

### Adding Image Format Support

1. **Update ImagePreviewService.cs**:
```csharp
private static readonly string[] ImageExtensions = 
{ 
    ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".ico",
    ".svg", ".webm"  // Add new formats here
};
```

2. Test with sample files

### Adding Sort Option

1. **Update XAML ComboBox**:
```xaml
<ComboBox Name="SortComboBox">
    <ComboBoxItem>Name</ComboBoxItem>
    <ComboBoxItem>Size</ComboBoxItem>
    <ComboBoxItem>New Option</ComboBoxItem>
</ComboBox>
```

2. **Add case in SortComboBox_SelectionChanged()**:
```csharp
case "New Option":
    group.Files.Sort((a, b) => a.NewProperty.CompareTo(b.NewProperty));
    break;
```

### Adding Async Task

```csharp
// Always use async pattern for I/O operations
private async void ScanButton_Click(object sender, RoutedEventArgs e)
{
    var results = await _searchService.FindDuplicatesAsync(_selectedDirectories);
    DisplayResults();
}
```

---

## Error Handling Best Practices

### File Operations
```csharp
try
{
    using (var fileStream = File.OpenRead(filePath))
    {
        var hash = sha256.ComputeHash(fileStream);
        return Convert.ToHexString(hash);
    }
}
catch (UnauthorizedAccessException)
{
    // Log and skip this file
    return null;
}
catch (IOException)
{
    // File locked or busy
    return null;
}
catch (Exception ex)
{
    // Unexpected error
    Debug.WriteLine($"Error hashing {filePath}: {ex.Message}");
    return null;
}
```

### UI Event Handlers
```csharp
private void Button_Click(object sender, RoutedEventArgs e)
{
    try
    {
        // Button logic
    }
    catch (Exception ex)
    {
        MessageBox.Show($"Error: {ex.Message}", "Error", 
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

---

## Testing Strategy

### Unit Test Example
```csharp
[TestClass]
public class FileHashServiceTests
{
    [TestMethod]
    public async Task GetFileHashAsync_ReturnsSameHash_ForIdenticalFiles()
    {
        // Arrange
        var file1 = "test1.bin";
        var file2 = "test2.bin";
        
        // Act
        var hash1 = await FileHashService.GetFileHashAsync(file1);
        var hash2 = await FileHashService.GetFileHashAsync(file2);
        
        // Assert
        Assert.AreEqual(hash1, hash2);
    }
}
```

### Integration Test Example
```csharp
[TestMethod]
public async Task FindDuplicatesAsync_FindsCorrectDuplicates()
{
    // Arrange
    var service = new DuplicateSearchService();
    var testDir = "TestData/";
    
    // Act
    var results = await service.FindDuplicatesAsync(
        new List<string> { testDir });
    
    // Assert
    Assert.IsTrue(results.Count > 0);
    Assert.IsTrue(results[0].Files.Count >= 2);
}
```

---

## Performance Optimization Tips

### Profiling
```powershell
# Build in Release mode for accurate profiling
dotnet build -c Release

# Use Visual Studio Performance Profiler
# Debug → Performance Profiler → CPU Usage
```

### Memory Optimization
- Use `using` statements for file streams
- Clear thumbnail cache periodically
- Dispose of BitmapImage when not needed

### Speed Optimization
- Pre-filter by file size before hashing
- Use async/await for non-blocking operations
- Consider caching hash results
- Parallelize hash computation with Parallel.ForEach

---

## Dependencies

### Current NuGet Packages
```xml
<ItemGroup>
    <PackageReference Include="System.Drawing.Common" Version="8.0.0" />
    <PackageReference Include="Ookii.Dialogs.Wpf" Version="3.0.0" />
</ItemGroup>
```

### Why These Packages?
- **System.Drawing.Common**: Image processing APIs
- **Ookii.Dialogs.Wpf**: Native Windows folder picker dialog

### Adding New Dependencies
```powershell
dotnet add package PackageName --version 1.0.0
```

---

## Build & Deployment

### Debug Build
```powershell
dotnet build -c Debug
```

### Release Build
```powershell
dotnet build -c Release
```

### Publish Self-Contained
```powershell
dotnet publish -c Release -r win-x64 --self-contained
```

### Create Installer (Future)
Can use tools like:
- WiX Toolset
- Advanced Installer
- NSIS

---

## Git Workflow

### Feature Development
```bash
# Create feature branch
git checkout -b feature/new-view-mode

# Make changes
git add .
git commit -m "Add new view mode"

# Push and create PR
git push origin feature/new-view-mode
```

### Commit Message Format
```
feat: Add new feature
fix: Fix bug description
docs: Update documentation
refactor: Restructure code
test: Add tests
perf: Improve performance
```

---

## Future Enhancement Ideas

- [ ] Delete files with safety confirmation
- [ ] Comparison viewer for duplicate files
- [ ] Export results to CSV/PDF
- [ ] Custom file type filters
- [ ] Scheduled scanning
- [ ] Multi-threaded hashing
- [ ] Database backend for results
- [ ] Cloud storage support
- [ ] Undo/recovery mechanism
- [ ] Settings/preferences UI

---

## Troubleshooting Development

### Build Fails
```powershell
# Clean and rebuild
dotnet clean
dotnet build

# Or clear NuGet cache
dotnet nuget locals all --clear
dotnet restore
```

### Dependencies Not Found
```powershell
# Update packages
dotnet package update

# Or manually restore
dotnet restore
```

### IDE Not Showing IntelliSense
- Restart Visual Studio / VS Code
- Reload the workspace
- Run `dotnet restore`

---

## Release Checklist

- [ ] All tests passing
- [ ] Code reviewed
- [ ] Documentation updated
- [ ] Performance tested
- [ ] Version number bumped
- [ ] Release notes written
- [ ] Build succeeds in Release mode
- [ ] Installer created (if applicable)

---

**Happy coding! 💻**
