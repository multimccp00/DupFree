# DupFree - Usage Guide

## Table of Contents
1. [Application Layout](#application-layout)
2. [Browsing and Scanning](#browsing-and-scanning)
3. [Viewing Results](#viewing-results)
4. [Deleting Files](#deleting-files)
5. [Similar Image Detection](#similar-image-detection)
6. [Recycle Bin](#recycle-bin)
7. [Settings](#settings)
8. [Help Panel](#help-panel)

---

## Application Layout

DupFree uses a sidebar-based navigation layout with the following panels:

### Sidebar Buttons
| Button | Panel | Description |
|--------|-------|-------------|
| Browse | Scan Panel | Select directories and run duplicate scan |
| Duplicate Files | Results Panel | View and manage duplicate file results |
| Similar Images | Similar Images Panel | Find visually similar images |
| Recycle Bin | Recycle Bin Panel | View and restore deleted files |
| Settings | Settings Panel | Configure application preferences |
| Help | Help Panel | In-app usage instructions |

### Footer
The bottom of the main area displays four statistics cards:
- **Files Checked** — Total files scanned
- **Duplicates** — Number of duplicate groups found
- **Space Wasted** — Total disk space consumed by duplicates
- **Space Saved** — Disk space recovered by deleting duplicates

### Storage Indicator
The sidebar footer shows the selected drive's volume label, letter, and a usage progress bar.

---

## Browsing and Scanning

### Selecting Directories
1. Click **Browse** in the sidebar
2. A native Windows folder browser dialog opens
3. Select a directory — all subdirectories are scanned recursively
4. The selected path appears in the sidebar

### Running a Scan
1. After selecting a folder, click the **Scan** button in the action bar
2. The progress bar shows overall scan progress
3. Scanning can be cancelled at any time with the **Cancel** button
4. Status text shows the current scanning phase

### Filtering Options
- **Search box**: Type to filter displayed results by filename
- **Min Size**: Filter by minimum file size (All, 1MB, 10MB, 100MB)
- **Limit**: Cap the number of files processed (All, 100, 1000, 100000)

---

## Viewing Results

### List View (DataGrid)
- Sortable columns: checkbox, Name, Path, Size, Modified Date
- Click column headers to sort
- Click row checkboxes to select files for deletion

### Grid View (Thumbnails)
- Thumbnail cards with file name and optional path
- Click cards to select/deselect
- Thumbnail size is configurable in Settings (100–300px)
- Toggle between views using the view button in the action bar

### Navigation
- **Arrow keys**: Navigate between grid items
- **Enter**: Open selected file in Windows Explorer
- **Delete**: Delete selected files
- **Double-click**: Open file in default application

---

## Deleting Files

### Selecting Files
- Click individual checkboxes in list view
- Click thumbnail cards in grid view
- Use **Select All** to select everything
- The **Delete Selected (n)** button shows the current selection count

### Deletion Process
1. Click **Delete Selected (n)**
2. If confirm-delete is enabled (default), a confirmation dialog appears
3. Files are moved to the Windows Recycle Bin
4. Deleted files appear in the in-app Recycle Bin panel
5. Results update automatically after deletion

---

## Similar Image Detection

### Opening the Panel
Click **Similar Images** in the sidebar to open the dedicated panel.

### Configuring the Scan
- **Similarity threshold slider** (75–99%): Lower values find more matches, higher values are stricter
- The panel uses the same directories selected via Browse

### Running the Scan
1. Click **Scan** to start
2. Progress bar shows overall progress (hashing + SSIM verification)
3. An optional timer shows elapsed scan time (enable in Settings)
4. Groups appear progressively as they are discovered

### Reviewing Results
- **Left panel**: Scrollable list of similar image groups with 80×80 thumbnails
- Click any thumbnail to select it — selected images have a red border
- **Right panel**: Large preview (350×250) of the selected image with:
  - File name and path
  - File size and image dimensions
  - Open Image button to view in default viewer

### Auto-Select Options
Checkboxes at the top of the panel allow automatic selection of lower-quality versions:
- **Keep Uncompressed**: Prefer uncompressed formats (BMP, TIFF)
- **Keep Higher Resolution**: Prefer images with more pixels
- **Keep Larger Filesize**: Prefer larger files

### Deleting Similar Images
- Select images to delete (manually or via auto-select)
- Click **Delete Selected (n)** to remove them
- Deleted files go to the Windows Recycle Bin

---

## Recycle Bin

### Viewing Deleted Files
Click **Recycle Bin** in the sidebar. A DataGrid shows:
- File name
- Original path
- File size
- Deletion timestamp

### Restoring Files
- Right-click a file and select **Restore** to move it back to its original location
- The in-app bin stores up to 30 recent deletions

### Clearing the Bin
Use the **Clear** button to remove all entries from the in-app recycle bin list.

---

## Settings

Click **Settings** in the sidebar to access configuration options:

| Setting | Description | Default |
|---------|-------------|---------|
| Size Unit | Display file sizes in Auto/Bytes/KB/MB/GB/TB | Auto |
| Grid Picture Size | Thumbnail size in grid view (100–300px slider) | 150px |
| Show File Path | Display file paths under thumbnails in grid view | On |
| Confirm Delete | Show confirmation dialog before deleting | On |
| Show Scan Timer | Display elapsed time during similar image scans | Off |
| Auto-Select: Keep Uncompressed | Prefer uncompressed image formats | Off |
| Auto-Select: Keep Higher Resolution | Prefer higher resolution images | On |
| Auto-Select: Keep Larger Filesize | Prefer larger files | Off |

All settings are saved automatically and persist between sessions.

---

## Help Panel

Click **Help** in the sidebar for in-app usage instructions covering all major features.
