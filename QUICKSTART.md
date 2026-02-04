# DupFree Quick Start Guide

## 5-Minute Setup

### Step 1: Prerequisites
Ensure you have .NET 8.0 SDK installed:
```powershell
dotnet --version
```

If not installed, download from: https://dotnet.microsoft.com/en-us/download/dotnet/8.0

### Step 2: Build the Application
```powershell
cd e:\Personal_Stuff\Dupfree
dotnet build
```

### Step 3: Run the Application
```powershell
dotnet run
```

The application window will open automatically.

---

## Basic Usage

### Finding Duplicates

1. **Select a Folder**
   - Click "📁 Browse" button
   - Choose any folder (e.g., Downloads, Pictures)
   - The folder and all subfolders will be scanned

2. **Start Scanning**
   - Click "🔍 Scan"
   - Wait for the progress bar to complete
   - Status messages show progress

3. **Review Results**
   - Duplicate groups appear as collapsible sections
   - Each group shows:
     - Number of duplicate files
     - Total wasted disk space
   - Click group header to expand/collapse

### Viewing Duplicates

**Three View Modes:**

| Mode | Button | Use Case |
|------|--------|----------|
| Icon View | 🗷 | Quick visual scan (small thumbnails) |
| Large Icon | ⊞ | Photo comparison (large previews) |
| List View | ☰ | Detailed file information |

### Sorting Options

Use the dropdown to organize by:
- **Name**: Alphabetical (A-Z)
- **Size**: Largest first
- **Modified Date**: Newest first
- **Path**: Directory order

---

## Key Features Explained

### 📊 Icon View
- Small thumbnails (120×120 px)
- Shows: Picture, filename, size
- Best for: Browsing many duplicates quickly

### 📷 Large Icon View
- Large thumbnails (180×180 px)
- Shows: Picture, name, size, full path
- Best for: Comparing photos/images

### 📝 List View
- Spreadsheet-style layout
- Columns: Name | Path | Size | Modified Date
- Best for: Detailed file analysis

### 🖼️ Image Preview
- Automatic thumbnails for:
  - JPG, PNG, BMP, GIF, WebP, TIFF, ICO
- Non-image files show generic icon
- No preview = file type not supported

### 💾 Wasted Space Calculator
Shows for each group:
```
Wasted Space = (Number of Duplicates - 1) × File Size

Example:
3 copies of 10MB file = 2 × 10MB = 20MB wasted space
```

---

## Common Scenarios

### Scenario 1: Clean Up Downloads Folder
1. Browse → Downloads
2. Scan → Wait for completion
3. Change to Large Icon View
4. Sort by Size (descending)
5. Review largest duplicate groups first

### Scenario 2: Analyze Photo Library
1. Browse → Pictures folder
2. Scan → Full recursive scan
3. Use Large Icon View to see thumbnails
4. Sort by Modified Date to find old versions
5. Compare side-by-side which to keep

### Scenario 3: Find All Duplicates on Disk
1. Browse → Drive root (C:\)
2. Scan → This will take time (perhaps 30min-1hr)
3. Use List View for detailed information
4. Sort by Size to find largest waste

---

## Pro Tips

✅ **DO:**
- Start with specific folders first
- Sort by size to find most space to recover
- Use large icons for visual comparison
- Note modification dates before deleting
- Take note of paths before cleanup

❌ **DON'T:**
- Scan entire system on first run (use specific folders)
- Trust only size - always verify hash
- Delete files without backup
- Rely solely on filename similarity
- Forget file permissions matter

---

## Performance Tips

**For Faster Scanning:**
1. Close other applications
2. Disable antivirus temporarily (may slow hashing)
3. Scan SSD drives (much faster than HDD)
4. Start with specific folders
5. Avoid network drives initially

**Typical Speeds:**
- Small folder (100 files): < 1 second
- Medium folder (10,000 files): 5-30 seconds
- Large folder (100,000 files): 2-5 minutes
- Entire drive: 30 minutes - 2 hours

---

## Troubleshooting

### "No duplicates found"
- ✓ Try a folder you know has duplicates
- ✓ Create test files: `copy file.txt file_copy.txt`
- ✓ Check file permissions

### Application runs slowly
- ✓ Close other programs
- ✓ Try smaller folder first
- ✓ Restart computer if persistent
- ✓ Check disk usage (may be at capacity)

### Thumbnails not showing
- ✓ Normal for non-image files
- ✓ Check file extensions (.jpg, .png, etc.)
- ✓ Verify file isn't corrupted
- ✓ Restart application

### Application crashes
- ✓ Update .NET: `dotnet --version`
- ✓ Rebuild: `dotnet clean` then `dotnet build`
- ✓ Try smaller folder
- ✓ Run as Administrator

---

## Next Steps

### Basic Usage
- [ ] Practice with Downloads folder
- [ ] Try all three view modes
- [ ] Use each sort option

### Advanced Usage
- [ ] Scan Pictures folder
- [ ] Compare with multiple drives
- [ ] Note wasted space amounts

### File Management
- [ ] Create backup of duplicates
- [ ] Move to "Archive" folder instead of deleting
- [ ] Document which files you kept

---

## File Structure Reference

```
DupFree/
├── bin/Debug/net8.0-windows/
│   └── DupFree.exe (Run this directly)
├── Services/
│   ├── DuplicateSearchService.cs (Detection logic)
│   ├── FileHashService.cs (SHA256 hashing)
│   └── ImagePreviewService.cs (Thumbnails)
├── Views/
│   └── MainWindow.xaml(.cs) (User interface)
├── Models/
│   └── FileItemViewModel.cs (Data objects)
├── README.md (Full documentation)
├── USAGE_GUIDE.md (Detailed user guide)
├── ARCHITECTURE.md (Technical design)
└── QUICKSTART.md (This file)
```

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| `Enter` | Confirm dialog |
| `Esc` | Close dialog |
| `Tab` | Navigate buttons |
| `Alt+B` | Browse (if set) |
| `Alt+S` | Scan (if set) |

---

## File Size Examples

Understanding the display:

| Bytes | Display |
|-------|---------|
| 512 | 512 B |
| 1,024 | 1 KB |
| 1,048,576 | 1 MB |
| 1,073,741,824 | 1 GB |
| 1,099,511,627,776 | 1 TB |

---

## FAQ

**Q: Is my data safe?**
A: Yes! DupFree only reads files. It never modifies or deletes anything.

**Q: Why does scanning take so long?**
A: Hashing all files is computationally intensive. Large drives take time.

**Q: Can I scan network drives?**
A: Yes, but it will be slower than local drives.

**Q: Will it work on external drives?**
A: Yes! Connect USB drive or external HDD and browse to it.

**Q: How does it find duplicates?**
A: Uses SHA256 cryptographic hash. Same hash = identical content.

**Q: Can I delete files from the app?**
A: Current version shows results only. Delete manually via Explorer.

---

## Support & Feedback

Found a bug? Have a suggestion?
- Create an issue in the repository
- Include your Windows version
- Describe the exact steps to reproduce

---

## Version History

**v1.0 (Current)**
- Initial release
- Duplicate detection with SHA256
- Three view modes
- Image preview support
- Multiple sort options
- Progress tracking

**Future v1.1 (Planned)**
- Direct file deletion (with safety checks)
- File comparison viewer
- Export results to CSV

---

**Happy cleaning! 🧹**
