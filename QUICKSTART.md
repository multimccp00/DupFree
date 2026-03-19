# DupFree - Quick Start Guide

## Prerequisites

- Windows 10/11
- .NET 8 SDK (for building from source)
- GPU with DirectX 11 support (optional, for accelerated SSIM)

## Build & Run

```bash
# Clone the repository
git clone <repo-url>
cd DupFree

# Restore NuGet packages and build
dotnet restore
dotnet build

# Run the application
dotnet run
```

Or open `Dupfree.sln` in Visual Studio 2022 and press F5.

---

## First Use

### 1. Select a Folder
- Click **Browse** in the sidebar to open the folder browser
- Select one or more directories to scan
- The storage indicator at the bottom of the sidebar shows drive usage

### 2. Scan for Duplicates
- Click **Scan** in the action bar to start scanning
- Progress is shown in the status bar
- Cancel at any time with the **Cancel** button
- Results appear automatically when the scan completes

### 3. Review Results
- **List view**: DataGrid with sortable columns (Name, Path, Size, Date)
- **Grid view**: Thumbnail cards — toggle with the view button in the action bar
- **Animated previews**: In grid view, GIF and video files auto-play when you hover over them, and loop seamlessly. They automatically pause when scrolled off-screen
- Use the **search box** to filter results by filename
- Footer cards show: Files Checked, Duplicates Found, Space Wasted, Space Saved

### 4. Delete Duplicates
- Select files by clicking checkboxes or using **Select All**
- Click **Delete Selected (n)** to move files to the recycle bin
- Deleted files can be restored from the **Recycle Bin** panel in the sidebar

### 5. Find Similar Images
- Click **Similar Images** in the sidebar
- Adjust the **similarity threshold** slider (75–99%)
- Click **Scan** to start the similar image analysis
- Groups of similar images appear with thumbnail previews
- Click any image to see a larger preview with file details
- Use **Auto-Select** options to automatically mark lower-quality versions

### 6. Configure Settings
- Click **Settings** in the sidebar
- Adjust size display units, grid thumbnail size, file path visibility
- Toggle delete confirmation dialog
- Enable/disable scan timer display

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Arrow keys | Navigate grid items |
| Enter | Open selected file in Explorer |
| Delete | Delete selected files |
| Ctrl+A | Select all items |

---

## FAQ

**Q: Where are deleted files sent?**  
A: Files are moved to the Windows Recycle Bin. You can also restore them from the in-app Recycle Bin panel.

**Q: Does DupFree modify my files?**  
A: DupFree only reads files for scanning. Deletion moves files to the Windows Recycle Bin — nothing is permanently destroyed.

**Q: How does similar image detection work?**  
A: Images are compared using perceptual hashing (for fast filtering) followed by SSIM verification (for accuracy). GPU acceleration is used when available.

**Q: Where are settings stored?**  
A: Settings are saved to `%AppData%/DupFree/settings.json` and persist between sessions.

**Q: What image formats are supported?**  
A: JPG, JPEG, PNG, BMP, GIF, WebP, TIFF, and ICO.
