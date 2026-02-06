using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DupFree.Models;
using DupFree.Services;
using System.Windows.Data;
using System.ComponentModel;
using System.Collections.Generic;
using Microsoft.VisualBasic.FileIO;
using Ookii.Dialogs.Wpf;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DupFree.Views
{
    public partial class MainWindow : Window
    {
        // Windows API for dark title bar
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private DuplicateSearchService _searchService;
        private List<string> _selectedDirectories;
        private List<DuplicateGroupViewModel> _groupViewModels;
        private string _currentViewMode = "list";
        private string _currentSortBy = "Name";
        private string _searchText = string.Empty;
        private List<FileItemViewModel> _currentGridFiles = new();
        private readonly Dictionary<int, FrameworkElement> _realizedGridItems = new();
        private bool _isVirtualGridActive = false;
        private double _virtualItemWidth = 156;
        private double _virtualItemHeight = 196;
        private int _virtualColumns = 1;
        private long _totalDeletedSize = 0;  // Track space saved from deletions
        private int _totalFilesScanned = 0;  // Track total files scanned during duplicate search
        private bool _hasScannedOnce = false;
        private int _selectedGridIndex = -1;
        private int _gridColumns = 0;
        private System.Threading.CancellationTokenSource _scanCancellation;
        private int _filesRendered = 0;
        private const int FILES_PER_BATCH = 500;  // Render 500 files at a time
        private readonly SemaphoreSlim _thumbnailSemaphore = new SemaphoreSlim(4);
        private readonly HashSet<string> _thumbnailLoading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isScanning = false;  // Track if a scan is currently in progress
        
        // Recycle Bin functionality
        private readonly ObservableCollection<DeletedFileItem> _recycleBin = new ObservableCollection<DeletedFileItem>();
        private readonly List<DeletedFileItem> _selectedRecycleBinItems = new List<DeletedFileItem>();
        private readonly List<FileItemViewModel> _selectedGridItems = new List<FileItemViewModel>(); // For scanned files grid selection
        private FileItemViewModel _lastSelectedGridItem = null; // For Shift+Click range selection
        private const int MAX_RECYCLE_BIN_SIZE = 30;

        public MainWindow()
        {
            InitializeComponent();
            
            // Set sidebar button to static light blue color
            SidebarCollapseButton.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)); // BlueBrush
            
            // Apply dark title bar
            SourceInitialized += (s, e) => ApplyDarkTitleBar();
            
            // Handle window size changes to refresh grid layout
            SizeChanged += MainWindow_SizeChanged;
            
            // Load saved settings
            SettingsService.LoadFromFile();
            
            // Initialize grid dimensions from settings
            int gridSize = SettingsService.GridPictureSize;
            _virtualItemWidth = gridSize + 56;  // size + panel padding + margins
            _virtualItemHeight = gridSize + (SettingsService.ShowGridFilePath ? 104 : 84); // adjust based on path display setting
            
            _searchService = new DuplicateSearchService();
            // Ensure service events update UI on dispatcher thread
            _searchService.OnStatusChanged += (status) => Dispatcher.Invoke(() => StatusText.Text = status);
            _searchService.OnProgressChanged += (progress) => Dispatcher.Invoke(() => ProgressBar.Value = progress);
            _selectedDirectories = new List<string>();
            _groupViewModels = new List<DuplicateGroupViewModel>();
            // Show large-icon grid by default (after collections are initialized)
            DisplayResults();

            // Initialize unit combobox and force dark theme
            UnitComboBox.SelectedIndex = (int)Services.SettingsService.CurrentSizeUnit;
            ApplyTheme("dark");

            Services.SettingsService.OnSettingsChanged += () =>
            {
                // Refresh sizes and theme when settings change
                RefreshSizes();
                ApplyTheme("dark");
            };
        }

        private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            // Ignore delete when focus is in editable controls
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox)
                return;

            // Only handle delete in scan panel
            if (ScanPanel.Visibility != Visibility.Visible)
                return;

            e.Handled = true;
            DeleteSelectedButton_Click(sender, e);
        }

        private System.Windows.Threading.DispatcherTimer _resizeTimer;

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Throttle the resize event to avoid excessive redraws
            if (_resizeTimer == null)
            {
                _resizeTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _resizeTimer.Tick += (s, args) =>
                {
                    _resizeTimer.Stop();
                    
                    // Don't refresh during scan to prevent duplication
                    if (_isScanning)
                        return;
                    
                    // Refresh grid layout if in grid view mode
                    if (_currentViewMode != "list" && ResultsScrollViewer.Visibility == Visibility.Visible && _currentGridFiles.Count > 0)
                    {
                        DisplayResults();
                    }
                };
            }
            
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int darkMode = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }
            }
            catch
            {
                // Silently fail if API not available (older Windows versions)
            }
        }

        private void RefreshSizes()
        {
            // Don't refresh during an active scan to prevent duplication
            if (_isScanning)
                return;
                
            foreach (var g in _groupViewModels)
            {
                foreach (var f in g.Files)
                {
                    f.SizeFormatted = Services.ImagePreviewService.FormatFileSize(f.FileSize, Services.SettingsService.CurrentSizeUnit);
                }
            }
            // Refresh list view binding
            if (_currentViewMode == "list")
            {
                ResultsListView.Items.Refresh();
            }
            else
            {
                DisplayResults();
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = SearchTextBox.Text ?? string.Empty;
            DisplayResults();
        }

        private void ResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDeleteCount();
        }

        private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDeleteCount();
        }

        private void ResultsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem is FileItemViewModel file)
            {
                OpenFile(file);
            }
        }

        private void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsListView.SelectedItem is FileItemViewModel file)
            {
                OpenFile(file);
            }
        }

        private void OpenFile(FileItemViewModel file)
        {
            try
            {
                if (File.Exists(file.FilePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                    {
                        FileName = file.FilePath,
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show($"File not found: {file.FilePath}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateSelectedCount()
        {
            int selectedCount = 0;
            
            if (RecycleBinPanel.Visibility == Visibility.Visible && RecycleBinDataGrid != null)
            {
                selectedCount = RecycleBinDataGrid.SelectedItems.Count;
                DeleteSelectedButton.Content = $"Recover Selected ({selectedCount})";
            }
            else if (ResultsDataGrid.Visibility == Visibility.Visible)
            {
                selectedCount = ResultsDataGrid.SelectedItems.Count;
                DeleteSelectedButton.Content = $"Delete Selected ({selectedCount})";
            }
            else if (ResultsListView.Visibility == Visibility.Visible)
            {
                selectedCount = ResultsListView.SelectedItems.Count;
                DeleteSelectedButton.Content = $"Delete Selected ({selectedCount})";
            }
            else if (_selectedGridIndex >= 0)
            {
                selectedCount = 1;
                DeleteSelectedButton.Content = $"Delete Selected ({selectedCount})";
            }
        }

        private void UpdateResultsCountText(int shown, int total)
        {
            // Results count text has been removed from UI
        }

        private List<FileItemViewModel> FilterFiles(IEnumerable<FileItemViewModel> files)
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                return files.ToList();
            }

            var query = _searchText.Trim();
            return files.Where(f =>
                    (!string.IsNullOrEmpty(f.FileName) && f.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(f.FilePath) && f.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        private void ApplyTheme(string theme)
        {
            var appResources = Application.Current.Resources;
            if (theme == "dark")
            {
                appResources["AppBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 18, 24, 39));
                appResources["TopBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 56, 65, 82));
                appResources["ActionBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 32, 41, 56));
                appResources["PanelBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 31, 41, 55));
                appResources["WindowForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                appResources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 99, 102, 241));
                appResources["ScanButtonBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 54, 100, 239));
                appResources["ControlBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 55, 65, 81));
                appResources["ControlForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 156, 163, 175));
                appResources["BorderBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 75, 85, 99));
                appResources["HeaderBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 31, 41, 55));
                appResources["ScrollBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 31, 41, 55));
                appResources["ScanButtonBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 37, 99, 235));
                appResources["SeparatorBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 75, 85, 99));
                appResources["AlternatingRowBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 31, 41, 55));
                appResources["SidebarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 11, 24, 55));
                appResources["SidebarHover"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 30, 58, 138));
                appResources["DangerBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 239, 68, 68));
                appResources["SuccessBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 16, 185, 129));
                appResources["MutedForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 156, 163, 175));
                appResources["OrangeBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 245, 158, 11));
            }
            
            // Force refresh ComboBox styles
            LimitComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            SortComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            UnitComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            
            // Update Scan button style separately
            ScanButton.Background = appResources["ScanButtonBrush"] as System.Windows.Media.Brush;
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog();
            if (dialog.ShowDialog() == true)
            {
                _selectedDirectories.Clear();
                _selectedDirectories.Add(dialog.SelectedPath);
                ScanButton.IsEnabled = true;
                StatusText.Text = $"Selected: {dialog.SelectedPath}";
                
                // Pass directories to similar images panel
                SimilarImagesPanelControl.SetDirectories(_selectedDirectories);
                
                // Uncheck the browse button after selection
                BrowseButton.IsChecked = false;

                // Auto-scan only the first time a folder is selected
                if (!_hasScannedOnce)
                {
                    _hasScannedOnce = true;
                    ScanButton_Click(ScanButton, new RoutedEventArgs());
                }
            }
            else
            {
                // Uncheck the browse button if the dialog is canceled
                BrowseButton.IsChecked = false;
            }
        }
        private int? GetSelectedLimit()
        {
            // Map SelectedIndex to limit values: All=null, 100=100, 1000=1000, 100000=100000
            return LimitComboBox.SelectedIndex switch
            {
                1 => 100,
                2 => 1000,
                3 => 100000,
                _ => null  // All
            };
        }
        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            // If no directory selected, trigger browse first
            if (_selectedDirectories == null || _selectedDirectories.Count == 0)
            {
                BrowseButton_Click(sender, e);
                
                // Check again after browse
                if (_selectedDirectories == null || _selectedDirectories.Count == 0)
                {
                    return; // User cancelled browse
                }
            }
            
            ScanButton.IsEnabled = false;
            _isScanning = true;  // Mark scan as in progress
            CancelButton.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            ScanProgressBar.Value = 0;
            ProgressPanel.Visibility = Visibility.Visible;
            ViewControlPanel.Visibility = Visibility.Collapsed;
            
            // Comprehensively clear all UI display elements
            ResultsPanel.Children.Clear();
            _realizedGridItems.Clear();  // Clear virtualized grid cache
            ResultsDataGrid.ItemsSource = null; // Clear data grid source
            ResultsListView.ItemsSource = null; // Clear list view source
            NoResultsPlaceholder.Visibility = Visibility.Collapsed;  // Hide placeholder
            ResultsDataGrid.Visibility = Visibility.Collapsed;
            ResultsListView.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;
            
            // Clear data collections
            _groupViewModels.Clear(); // Clear previous scan results before starting new scan
            _currentGridFiles.Clear(); // Clear grid files as well
            _selectedGridItems.Clear();  // Clear any selections
            _lastSelectedGridItem = null;

            // Create cancellation token source for this scan
            _scanCancellation = new System.Threading.CancellationTokenSource();

            // Progress callback updates UI with current/total hashed files
            var progress = new Progress<(int current, int total)>((p) =>
            {
                if (p.total > 0)
                {
                    double percentage = (p.current * 100.0) / p.total;
                    ProgressBar.Value = percentage;
                    ScanProgressBar.Value = percentage;
                    StatusText.Text = $"Hashing {p.current}/{p.total}";
                    ProgressStatusText.Text = $"Scanning... {p.current}/{p.total} files ({percentage:F0}%)";
                }
            });

            // Read optional limit from UI using SelectedIndex mapping
            int? limit = LimitComboBox.SelectedIndex switch
            {
                1 => 100,
                2 => 1000,
                3 => 100000,
                _ => (int?)null
            };

            StatusText.Text = $"Scanning with limit: {(limit.HasValue ? limit.Value.ToString() : "All")} files";
            ProgressStatusText.Text = "Starting scan...";

            var duplicates = await _searchService.FindDuplicatesAsync(_selectedDirectories, progress, limit, _scanCancellation.Token);

            _totalFilesScanned = _searchService.TotalFilesScanned;
            
            System.Diagnostics.Debug.WriteLine($"Scan complete: Found {duplicates.Count} groups from search service");
            
            // After scan, always load in list mode - no thumbnails needed
            foreach (var dupGroup in duplicates)
            {
                var groupVM = new DuplicateGroupViewModel
                {
                    GroupHash = dupGroup.FileHash,
                    IsExpanded = true
                };

                foreach (var file in dupGroup.Files)
                {
                    groupVM.Files.Add(FileItemViewModel.FromFileInfo(file, dupGroup.FileHash, loadThumbnail: false));
                }

                _groupViewModels.Add(groupVM);
            }
            
            System.Diagnostics.Debug.WriteLine($"After adding to _groupViewModels: {_groupViewModels.Count} groups");

            ApplySorting();
            
            // Count total files
            int totalFiles = 0;
            foreach (var group in _groupViewModels)
                totalFiles += group.Files.Count;
            
            System.Diagnostics.Debug.WriteLine($"Total files in all groups: {totalFiles}");
            
            // Clear grid selections after scan
            _selectedGridItems.Clear();
            UpdateDeleteCount();
            
            // Keep current view mode - don't reset to list
            EnableGridViewButtons();
            
            DisplayResults();
            
            _isScanning = false;  // Mark scan as complete
            ScanButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 100;
            ScanProgressBar.Value = 100;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ViewControlPanel.Visibility = Visibility.Visible;
        }

        private void DisableGridViewButtons()
        {
            // No-op: keep grid view available
        }

        private void EnableGridViewButtons()
        {
            // No-op: view toggles are always available
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _scanCancellation?.Cancel();
            StatusText.Text = "Scan cancelled";
            _isScanning = false;  // Mark scan as complete
            ScanButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }

        private void ApplySorting()
        {
            foreach (var group in _groupViewModels)
            {
                switch (_currentSortBy)
                {
                    case "Name":
                        group.Files.Sort((a, b) => a.FileName.CompareTo(b.FileName));
                        break;
                    case "Size":
                        group.Files.Sort((a, b) => b.FileSize.CompareTo(a.FileSize));
                        break;
                    case "Modified Date":
                        group.Files.Sort((a, b) => b.ModifiedDate.CompareTo(a.ModifiedDate));
                        break;
                    case "Path":
                        group.Files.Sort((a, b) => a.FilePath.CompareTo(b.FilePath));
                        break;
                }
            }
        }

        private void UpdateFooterStats()
        {
            if (FooterFilesChecked == null || FooterDuplicates == null || FooterSpaceWasted == null || FooterSpaceSaved == null)
                return;
            
            int totalDuplicateFiles = 0;
            long wastedSpace = 0;
            
            foreach (var group in _groupViewModels)
            {
                if (group.Files.Count == 0)
                {
                    continue;
                }

                totalDuplicateFiles += group.Files.Count;

                // Count only extra copies (keep one per group)
                var fileSize = group.Files[0].FileSize;
                wastedSpace += (group.Files.Count - 1) * fileSize;
            }
            
            FooterFilesChecked.Text = _totalFilesScanned.ToString();
            FooterDuplicates.Text = totalDuplicateFiles.ToString();
            FooterSpaceWasted.Text = FormatFileSize(wastedSpace);
            FooterSpaceSaved.Text = FormatFileSize(_totalDeletedSize);
        }

        private void UpdateStorageIndicator()
        {
            if (_selectedDirectories.Count == 0 || StorageIndicator == null || StorageText == null) return;
            
            try
            {
                var driveInfo = new System.IO.DriveInfo(_selectedDirectories[0]);
                long used = driveInfo.TotalSize - driveInfo.AvailableFreeSpace;
                double percentage = (double)used / driveInfo.TotalSize * 100;
                
                // Update storage indicator width (max 200 to match Grid width)
                double indicatorWidth = (percentage / 100) * 200;
                StorageIndicator.Width = indicatorWidth;
                
                // Change color based on percentage
                System.Windows.Media.Brush indicatorColor;
                if (percentage < 75)
                    indicatorColor = (System.Windows.Media.Brush)Application.Current.Resources["BlueBrush"];
                else if (percentage < 90)
                    indicatorColor = (System.Windows.Media.Brush)Application.Current.Resources["OrangeBrush"];
                else
                    indicatorColor = (System.Windows.Media.Brush)Application.Current.Resources["DangerBrush"];
                
                StorageIndicator.Background = indicatorColor;
                
                // Update storage text
                StorageText.Text = $"{FormatFileSize(used)} used of {FormatFileSize(driveInfo.TotalSize)}";
            }
            catch { }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private async void DisplayResults()
        {
            _filesRendered = 0;  // Reset batch counter
            
            // Count total files first
            int totalFiles = 0;
            foreach (var group in _groupViewModels)
            {
                totalFiles += group.Files.Count;
            }

            // Show/hide placeholder based on whether we have results
            if (totalFiles == 0)
            {
                NoResultsPlaceholder.Visibility = Visibility.Visible;
                ResultsDataGrid.Visibility = Visibility.Collapsed;
                ResultsListView.Visibility = Visibility.Collapsed;
                ResultsScrollViewer.Visibility = Visibility.Collapsed;
                UpdateResultsCountText(0, 0);
                UpdateSelectedCount();
                UpdateFooterStats();
                UpdateStorageIndicator();
                return;
            }
            else
            {
                NoResultsPlaceholder.Visibility = Visibility.Collapsed;
            }

            if (_currentViewMode == "list")
            {
                _isVirtualGridActive = false;
                ResultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
                ResultsScrollViewer.SizeChanged -= ResultsScrollViewer_SizeChanged;

                ResultsScrollViewer.Visibility = Visibility.Collapsed;
                ResultsListView.Visibility = Visibility.Collapsed;
                ResultsDataGrid.Visibility = Visibility.Visible;
                
                var flat = new List<FileItemViewModel>();
                var seenPathsListView = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in _groupViewModels)
                {
                    var dupCount = group.Files?.Count ?? 0;
                    var dupSpace = group.TotalWastedSpaceFormatted;
                    foreach (var f in group.Files)
                    {
                        // Skip duplicates based on file path
                        if (!seenPathsListView.Add(f.FilePath))
                            continue;
                            
                        f.DupCount = dupCount;
                        f.DupSpace = dupSpace;
                        flat.Add(f);
                    }
                }
                
                // Apply duplicate limit from settings
                if (SettingsService.MaxDuplicatesToShow > 0 && flat.Count > SettingsService.MaxDuplicatesToShow)
                {
                    flat = flat.Take(SettingsService.MaxDuplicatesToShow).ToList();
                }
                
                var filtered = FilterFiles(flat);
                // Use DataGrid for proper column binding
                ResultsDataGrid.ItemsSource = filtered;
                UpdateResultsCountText(filtered.Count, totalFiles);
                UpdateSelectedCount();
                
                StatusText.Text = $"Displaying {flat.Count} files in list view";
                UpdateFooterStats();
                UpdateStorageIndicator();
            }
            else if (_currentViewMode == "icons" || _currentViewMode == "large_icons" || _currentViewMode == "xlarge_icons")
            {
                ResultsListView.Visibility = Visibility.Collapsed;
                ResultsDataGrid.Visibility = Visibility.Collapsed;
                ResultsScrollViewer.Visibility = Visibility.Visible;
                ResultsPanel.Children.Clear();

                // Flatten all files
                _currentGridFiles.Clear();
                var allGridFiles = new List<FileItemViewModel>();
                foreach (var group in _groupViewModels)
                {
                    allGridFiles.AddRange(group.Files);
                }
                
                System.Diagnostics.Debug.WriteLine($"DisplayResults: Total groups: {_groupViewModels.Count}, Total flattened files: {allGridFiles.Count}");
                
                // Apply duplicate limit from settings
                if (SettingsService.MaxDuplicatesToShow > 0 && allGridFiles.Count > SettingsService.MaxDuplicatesToShow)
                {
                    allGridFiles = allGridFiles.Take(SettingsService.MaxDuplicatesToShow).ToList();
                }
                
                var filteredFiles = FilterFiles(allGridFiles);
                System.Diagnostics.Debug.WriteLine($"DisplayResults: After filtering: {filteredFiles.Count} files");
                
                // Deduplicate based on file path (in case of duplicate entries)
                var uniqueFiles = new List<FileItemViewModel>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in filteredFiles)
                {
                    if (seenPaths.Add(file.FilePath))
                    {
                        uniqueFiles.Add(file);
                    }
                }
                
                _currentGridFiles.AddRange(uniqueFiles);
                System.Diagnostics.Debug.WriteLine($"DisplayResults: After dedup: {uniqueFiles.Count} files, now _currentGridFiles contains {_currentGridFiles.Count} files");
                UpdateResultsCountText(_currentGridFiles.Count, totalFiles);
                UpdateSelectedCount();

                // For smaller sets, render with WrapPanel to avoid virtualization gaps
                if (_currentGridFiles.Count <= 1000)
                {
                    _isVirtualGridActive = false;
                    ResultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
                    ResultsScrollViewer.SizeChanged -= ResultsScrollViewer_SizeChanged;

                    ResultsPanel.Children.Clear();
                    
                    var wrap = new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };

                    // Ensure WrapPanel has a constrained width for proper wrapping
                    double wrapWidth = ResultsScrollViewer.ViewportWidth;
                    if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                        wrapWidth = ResultsScrollViewer.ActualWidth;
                    if (!double.IsNaN(wrapWidth) && wrapWidth > 0)
                        wrap.Width = wrapWidth;

                    ResultsPanel.Children.Add(wrap);
                    
                    // Add click handler to WrapPanel for deselecting when clicking empty space
                    wrap.MouseLeftButtonDown += (s, e) =>
                    {
                        // Only deselect if clicking directly on the WrapPanel, not on a child
                        if (s == e.OriginalSource)
                        {
                            _selectedGridItems.Clear();
                            _lastSelectedGridItem = null;
                            RefreshGridItemSelection();
                            UpdateDeleteCount();
                            e.Handled = true;
                        }
                    };
                    
                    System.Diagnostics.Debug.WriteLine($"DisplayResults: About to add {_currentGridFiles.Count} items to WrapPanel");
                    int addedCount = 0;
                    foreach (var file in _currentGridFiles)
                    {
                        wrap.Children.Add(GetViewModeCreateFunc()(file));
                        addedCount++;
                    }
                    System.Diagnostics.Debug.WriteLine($"DisplayResults: Successfully added {addedCount} items to WrapPanel");

                    StatusText.Text = $"Displaying {_currentGridFiles.Count} files (grid)";
                }
                else
                {

                    ResultsPanel.Children.Clear();
                    
                    // Create canvas for virtualized rendering
                    var gridCanvas = new Canvas();
                    ResultsPanel.Children.Add(gridCanvas);
                    _isVirtualGridActive = true;
                    SetupVirtualGrid(gridCanvas);

                    StatusText.Text = $"Displaying {_currentGridFiles.Count} files (virtualized grid)";
                }
            }
        }

        private void SetupVirtualGrid(Canvas canvas)
        {
            _realizedGridItems.Clear();

            // Determine item size based on view mode (includes margin)
            if (_currentViewMode == "xlarge_icons")
            {
                _virtualItemWidth = 384;
                _virtualItemHeight = 444;
            }
            else if (_currentViewMode == "large_icons")
            {
                _virtualItemWidth = 240;
                _virtualItemHeight = 300;
            }
            else
            {
                int gridSize = SettingsService.GridPictureSize;
                _virtualItemWidth = gridSize + 56;  // size + panel padding + margins
                _virtualItemHeight = gridSize + (SettingsService.ShowGridFilePath ? 104 : 84); // adjust based on path display setting
            }

            ResultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
            ResultsScrollViewer.ScrollChanged += ResultsScrollViewer_ScrollChanged;
            ResultsScrollViewer.SizeChanged -= ResultsScrollViewer_SizeChanged;
            ResultsScrollViewer.SizeChanged += ResultsScrollViewer_SizeChanged;

            RecalculateVirtualGrid(canvas);
            UpdateVirtualGrid(canvas);
        }

        private void ResultsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ResultsPanel.Children.Count == 0)
                return;

            if (_isVirtualGridActive)
            {
                if (ResultsPanel.Children[0] is Canvas canvas)
                {
                    RecalculateVirtualGrid(canvas);
                    UpdateVirtualGrid(canvas);
                }
            }
            else
            {
                if (ResultsPanel.Children[0] is WrapPanel wrap)
                {
                    double wrapWidth = ResultsScrollViewer.ViewportWidth;
                    if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                        wrapWidth = ResultsScrollViewer.ActualWidth;
                    if (!double.IsNaN(wrapWidth) && wrapWidth > 0)
                        wrap.Width = wrapWidth;
                }
            }
        }

        private void ResultsScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!_isVirtualGridActive || ResultsPanel.Children.Count == 0)
                return;

            if (ResultsPanel.Children[0] is Canvas canvas)
                UpdateVirtualGrid(canvas);
        }

        private void RecalculateVirtualGrid(Canvas canvas)
        {
            double viewportWidth = ResultsScrollViewer.ViewportWidth;
            if (double.IsNaN(viewportWidth) || viewportWidth <= 0)
                viewportWidth = ResultsScrollViewer.ActualWidth;

            _virtualColumns = Math.Max(1, (int)(viewportWidth / _virtualItemWidth));
            _gridColumns = _virtualColumns;

            int rows = (int)Math.Ceiling((double)_currentGridFiles.Count / _virtualColumns);
            canvas.Width = _virtualColumns * _virtualItemWidth;
            canvas.Height = rows * _virtualItemHeight;
        }

        private void UpdateVirtualGrid(Canvas canvas)
        {
            if (_currentGridFiles.Count == 0)
                return;

            double verticalOffset = ResultsScrollViewer.VerticalOffset;
            double viewportHeight = ResultsScrollViewer.ViewportHeight;

            int firstRow = Math.Max(0, (int)(verticalOffset / _virtualItemHeight));
            int visibleRows = Math.Max(1, (int)Math.Ceiling(viewportHeight / _virtualItemHeight) + 1);
            int overscan = 2;

            int startRow = Math.Max(0, firstRow - overscan);
            int endRow = firstRow + visibleRows + overscan;

            int startIndex = startRow * _virtualColumns;
            int endIndex = Math.Min(_currentGridFiles.Count - 1, (endRow * _virtualColumns) - 1);

            // Remove items outside range
            var toRemove = _realizedGridItems.Keys.Where(i => i < startIndex || i > endIndex).ToList();
            foreach (var idx in toRemove)
            {
                if (_realizedGridItems.TryGetValue(idx, out var elem))
                {
                    canvas.Children.Remove(elem);
                    _realizedGridItems.Remove(idx);
                }
            }

            // Add items in range
            for (int i = startIndex; i <= endIndex; i++)
            {
                if (!_realizedGridItems.ContainsKey(i))
                {
                    var elem = GetViewModeCreateFunc()(_currentGridFiles[i]);
                    if (elem is FrameworkElement fe)
                    {
                        fe.Margin = new Thickness(0);
                    }

                    int row = i / _virtualColumns;
                    int col = i % _virtualColumns;
                    Canvas.SetLeft(elem, col * _virtualItemWidth);
                    Canvas.SetTop(elem, row * _virtualItemHeight);

                    canvas.Children.Add(elem);
                    _realizedGridItems[i] = elem;
                }
            }

            // Reapply selection highlight if visible
            if (_selectedGridIndex >= 0 && _realizedGridItems.TryGetValue(_selectedGridIndex, out var selectedElem))
            {
                if (selectedElem is Border selectedBorder)
                {
                    selectedBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 120, 215));
                    selectedBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                    selectedBorder.BorderThickness = new Thickness(2);
                }
            }
        }

        private async Task RenderGridItemsProgressivelyAsync(WrapPanel gridPanel)
        {
            // Render all items progressively with long delays to keep UI responsive
            Func<FileItemViewModel, FrameworkElement> fileCreateFunc = GetViewModeCreateFunc();
            
            int batchSize = 25; // Small batches
            int delayMs = 100;  // Long delay between batches to keep UI responsive
            
            while (_filesRendered < _currentGridFiles.Count)
            {
                int endIdx = Math.Min(_filesRendered + batchSize, _currentGridFiles.Count);
                
                for (int i = _filesRendered; i < endIdx; i++)
                {
                    try
                    {
                        gridPanel.Children.Add(fileCreateFunc(_currentGridFiles[i]));
                    }
                    catch { }
                }
                _filesRendered = endIdx;
                
                if (_filesRendered % 200 == 0)
                    StatusText.Text = $"Rendering... {_filesRendered}/{_currentGridFiles.Count}";
                
                await Task.Delay(delayMs);
            }
        }

        private void OnScrollChanged(WrapPanel gridPanel)
        {
            // Removed - scroll event was causing UI freeze
        }

        private Func<FileItemViewModel, FrameworkElement> GetViewModeCreateFunc()
        {
            if (_currentViewMode == "xlarge_icons")
                return CreateXLargeIconView;
            else if (_currentViewMode == "large_icons")
                return CreateLargeIconView;
            else
                return CreateIconView;
        }

        private void ComputeGridColumns(WrapPanel gridPanel)
        {
            try
            {
                if (gridPanel.Children.Count > 0)
                {
                    var firstChild = gridPanel.Children[0] as FrameworkElement;
                    if (firstChild != null)
                    {
                        double firstTop = firstChild.TransformToAncestor(gridPanel).Transform(new System.Windows.Point(0, 0)).Y;
                        int countInFirstRow = 0;
                        foreach (var child in gridPanel.Children)
                        {
                            if (child is FrameworkElement fe)
                            {
                                double top = fe.TransformToAncestor(gridPanel).Transform(new System.Windows.Point(0, 0)).Y;
                                if (Math.Abs(top - firstTop) < 2.0)
                                    countInFirstRow++;
                                else
                                    break;
                            }
                        }
                        if (countInFirstRow > 0)
                            _gridColumns = Math.Max(1, countInFirstRow);
                    }
                }
            }
            catch { }

            if (_selectedGridIndex < 0 && _currentGridFiles.Count > 0)
                _selectedGridIndex = 0;

            Dispatcher.BeginInvoke(() =>
            {
                ResultsPanel.Focus();
                if (_selectedGridIndex >= 0 && _currentGridFiles.Count > 0)
                    HighlightSelectedGridFile();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private FrameworkElement CreateGroupPanel(DuplicateGroupViewModel group)
        {
            var mainPanel = new StackPanel { Margin = new Thickness(10) };

            // Group Header
            var headerButton = new Button
            {
                Content = $"📦 {group.Files.Count} duplicates | Wasted: {group.TotalWastedSpaceFormatted}",
                Padding = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            headerButton.SetResourceReference(Button.BackgroundProperty, "HeaderBackground");
            headerButton.SetResourceReference(Button.ForegroundProperty, "WindowForeground");

            var itemsPanel = new WrapPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Visible
            };

            headerButton.Click += (s, e) =>
            {
                itemsPanel.Visibility = itemsPanel.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };

            mainPanel.Children.Add(headerButton);

            // Display files based on view mode
            if (_currentViewMode == "icons")
            {
                itemsPanel.Orientation = Orientation.Horizontal;
                foreach (var file in group.Files)
                {
                    itemsPanel.Children.Add(CreateIconView(file));
                }
            }
            else if (_currentViewMode == "large_icons" || _currentViewMode == "xlarge_icons")
            {
                itemsPanel.Orientation = Orientation.Horizontal;
                foreach (var file in group.Files)
                {
                    itemsPanel.Children.Add(CreateLargeIconView(file));
                }
            }
            else if (_currentViewMode == "list")
            {
                itemsPanel.Orientation = Orientation.Vertical;
                itemsPanel.Width = double.NaN;
                foreach (var file in group.Files)
                {
                    itemsPanel.Children.Add(CreateListView(file));
                }
            }

            mainPanel.Children.Add(itemsPanel);

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Child = mainPanel,
                Margin = new Thickness(0, 0, 0, 10),
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");

            return border;
        }

        private FrameworkElement CreateIconView(FileItemViewModel file)
        {
            int pictureSize = SettingsService.GridPictureSize;
            int panelWidth = pictureSize + 24;
            // Adjust height based on whether file path will be shown
            int panelHeight = pictureSize + (SettingsService.ShowGridFilePath ? 72 : 52);
            
            var panel = new StackPanel
            {
                Width = panelWidth,
                Height = panelHeight,
                Margin = new Thickness(0),  // Remove margin from panel, apply to border instead
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = file,
                IsHitTestVisible = true
            };

            // Add click handler for selection
            panel.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    // Double-click - open file
                    OpenFile(file);
                    e.Handled = true;
                    return;
                }
                e.Handled = false;  // Allow event to bubble to border
            };

            // Always show full path on tooltip for quick location visibility
            panel.ToolTip = file.FilePath;

            // Thumbnail or icon (lazy-loaded)
            panel.Children.Add(CreatePreviewElement(file, pictureSize));

            // Name under the image
            var nameBlock = new TextBlock
            {
                Text = file.FileName,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(2, 5, 2, 0),
                MaxHeight = 30
            };
            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "WindowForeground");
            panel.Children.Add(nameBlock);

            // Path below name (truncated) - skip for large counts and if setting is disabled
            if (SettingsService.ShowGridFilePath)
            {
                var pathSmall = new TextBlock
                {
                    Text = file.FilePath,
                    FontSize = 9,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(2, 2, 2, 0),
                    MaxHeight = 20
                };
                pathSmall.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");
                panel.Children.Add(pathSmall);
            }

            // Context menu for delete
            var cm = new ContextMenu();
            var del = new MenuItem { Header = "Delete (Recycle Bin)", Tag = file };
            del.Click += OnDeleteMenuItem_Click;
            cm.Items.Add(del);
            panel.ContextMenu = cm;

            // Add double-click handler to open file
            panel.MouseDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenFile(file);
                    e.Handled = true;
                }
            };

            // Wrap in border for selection highlighting and full click area
            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Tag = file,
                Margin = new Thickness(12),  // Apply margin to border
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Apply initial selection state if file is already selected
            if (_selectedGridItems.Contains(file))
            {
                border.Background = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));
                border.BorderThickness = new Thickness(2);
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
            }

            // Add click handler on border to capture entire area
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    // Double-click - open file
                    OpenFile(file);
                    e.Handled = true;
                    return;
                }
                
                var clickedBorder = s as Border;
                var clickedFile = clickedBorder.Tag as FileItemViewModel;
                
                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool isShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                
                if (isShiftPressed && _lastSelectedGridItem != null)
                {
                    int lastIndex = _currentGridFiles.IndexOf(_lastSelectedGridItem);
                    int currentIndex = _currentGridFiles.IndexOf(clickedFile);
                    
                    if (lastIndex >= 0 && currentIndex >= 0)
                    {
                        int start = Math.Min(lastIndex, currentIndex);
                        int end = Math.Max(lastIndex, currentIndex);
                        
                        for (int i = start; i <= end; i++)
                        {
                            if (!_selectedGridItems.Contains(_currentGridFiles[i]))
                            {
                                _selectedGridItems.Add(_currentGridFiles[i]);
                            }
                        }
                        
                        RefreshGridItemSelection();
                    }
                }
                else if (isCtrlPressed)
                {
                    if (_selectedGridItems.Contains(clickedFile))
                    {
                        _selectedGridItems.Remove(clickedFile);
                    }
                    else
                    {
                        _selectedGridItems.Add(clickedFile);
                    }
                    
                    RefreshGridItemSelection();
                }
                else
                {
                    _selectedGridItems.Clear();
                    _selectedGridItems.Add(clickedFile);
                    
                    RefreshGridItemSelection();
                }
                
                _lastSelectedGridItem = clickedFile;
                UpdateDeleteCount();
                e.Handled = true;
            };

            return border;
        }

        private FrameworkElement CreateLargeIconView(FileItemViewModel file)
        {
            var panel = new StackPanel
            {
                Width = 220,
                Height = 280,
                Margin = new Thickness(0),  // Remove margin from panel, apply to border
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = file,
                IsHitTestVisible = true
            };

            // Add click handler for double-click only on panel
            panel.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenFile(file);
                    e.Handled = true;
                    return;
                }
                e.Handled = false;  // Allow event to bubble to border
            };

            panel.ToolTip = file.FilePath;

            panel.Children.Add(CreatePreviewElement(file, 160));

            var nameBlock = new TextBlock
            {
                Text = file.FileName,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var sizeBlock = new TextBlock
            {
                Text = file.SizeFormatted,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            var pathBlock = new TextBlock
            {
                Text = file.FilePath,
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };

            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "WindowForeground");
            sizeBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");
            pathBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");

            panel.Children.Add(nameBlock);
            panel.Children.Add(sizeBlock);
            panel.Children.Add(pathBlock);

            // Context menu
            var cm = new ContextMenu();
            var del = new MenuItem { Header = "Delete (Recycle Bin)", Tag = file };
            del.Click += OnDeleteMenuItem_Click;
            cm.Items.Add(del);
            panel.ContextMenu = cm;

            // Wrap in border for selection highlighting
            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Tag = file,
                Margin = new Thickness(10),  // Apply margin to border
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Apply initial selection state
            if (_selectedGridItems.Contains(file))
            {
                border.Background = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));
                border.BorderThickness = new Thickness(2);
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
            }

            // Add click handler on border to capture entire area
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenFile(file);
                    e.Handled = true;
                    return;
                }
                
                var clickedBorder = s as Border;
                var clickedFile = clickedBorder.Tag as FileItemViewModel;
                
                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool isShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                
                if (isShiftPressed && _lastSelectedGridItem != null)
                {
                    int lastIndex = _currentGridFiles.IndexOf(_lastSelectedGridItem);
                    int currentIndex = _currentGridFiles.IndexOf(clickedFile);
                    
                    if (lastIndex >= 0 && currentIndex >= 0)
                    {
                        int start = Math.Min(lastIndex, currentIndex);
                        int end = Math.Max(lastIndex, currentIndex);
                        
                        for (int i = start; i <= end; i++)
                        {
                            if (!_selectedGridItems.Contains(_currentGridFiles[i]))
                            {
                                _selectedGridItems.Add(_currentGridFiles[i]);
                            }
                        }
                        
                        RefreshGridItemSelection();
                    }
                }
                else if (isCtrlPressed)
                {
                    if (_selectedGridItems.Contains(clickedFile))
                    {
                        _selectedGridItems.Remove(clickedFile);
                    }
                    else
                    {
                        _selectedGridItems.Add(clickedFile);
                    }
                    
                    RefreshGridItemSelection();
                }
                else
                {
                    _selectedGridItems.Clear();
                    _selectedGridItems.Add(clickedFile);
                    
                    RefreshGridItemSelection();
                }
                
                _lastSelectedGridItem = clickedFile;
                UpdateDeleteCount();
                e.Handled = true;
            };

            return border;
        }

        private FrameworkElement CreateXLargeIconView(FileItemViewModel file)
        {
            var panel = new StackPanel
            {
                Width = 360,
                Height = 420,
                Margin = new Thickness(0),  // Remove margin from panel, apply to border
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = file,
                IsHitTestVisible = true
            };

            // Add click handler for double-click only on panel
            panel.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenFile(file);
                    e.Handled = true;
                    return;
                }
                e.Handled = false;  // Allow event to bubble to border
            };

            panel.ToolTip = file.FilePath;

            panel.Children.Add(CreatePreviewElement(file, 300));

            var nameBlock = new TextBlock
            {
                Text = file.FileName,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var sizeBlock = new TextBlock
            {
                Text = file.SizeFormatted,
                FontSize = 13,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var pathBlock = new TextBlock
            {
                Text = file.FilePath,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };

            panel.Children.Add(nameBlock);
            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "WindowForeground");
            sizeBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");
            pathBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");
            panel.Children.Add(sizeBlock);
            panel.Children.Add(pathBlock);

            // Context menu
            var cm = new ContextMenu();
            var del = new MenuItem { Header = "Delete (Recycle Bin)", Tag = file };
            del.Click += OnDeleteMenuItem_Click;
            cm.Items.Add(del);
            panel.ContextMenu = cm;

            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Tag = file,
                Margin = new Thickness(12),  // Apply margin to border
                Cursor = System.Windows.Input.Cursors.Hand
            };

            // Apply initial selection state
            if (_selectedGridItems.Contains(file))
            {
                border.Background = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));
                border.BorderThickness = new Thickness(2);
                border.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
            }

            // Add click handler on border to capture entire area
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    OpenFile(file);
                    e.Handled = true;
                    return;
                }
                
                var clickedBorder = s as Border;
                var clickedFile = clickedBorder.Tag as FileItemViewModel;
                
                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool isShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                
                if (isShiftPressed && _lastSelectedGridItem != null)
                {
                    int lastIndex = _currentGridFiles.IndexOf(_lastSelectedGridItem);
                    int currentIndex = _currentGridFiles.IndexOf(clickedFile);
                    
                    if (lastIndex >= 0 && currentIndex >= 0)
                    {
                        int start = Math.Min(lastIndex, currentIndex);
                        int end = Math.Max(lastIndex, currentIndex);
                        
                        for (int i = start; i <= end; i++)
                        {
                            if (!_selectedGridItems.Contains(_currentGridFiles[i]))
                            {
                                _selectedGridItems.Add(_currentGridFiles[i]);
                            }
                        }
                        
                        RefreshGridItemSelection();
                    }
                }
                else if (isCtrlPressed)
                {
                    if (_selectedGridItems.Contains(clickedFile))
                    {
                        _selectedGridItems.Remove(clickedFile);
                    }
                    else
                    {
                        _selectedGridItems.Add(clickedFile);
                    }
                    
                    RefreshGridItemSelection();
                }
                else
                {
                    _selectedGridItems.Clear();
                    _selectedGridItems.Add(clickedFile);
                    
                    RefreshGridItemSelection();
                }
                
                _lastSelectedGridItem = clickedFile;
                UpdateDeleteCount();
                e.Handled = true;
            };

            return border;
        }

        private FrameworkElement CreatePreviewElement(FileItemViewModel file, double size)
        {
            var grid = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var placeholder = new TextBlock
            {
                Text = "📄",
                FontSize = Math.Max(18, size * 0.6),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            grid.Children.Add(placeholder);

            var image = new Image
            {
                Width = size,
                Height = size,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Bind to the thumbnail so updates propagate even if loaded later
            var binding = new Binding("Thumbnail") { Source = file };
            image.SetBinding(Image.SourceProperty, binding);

            grid.Children.Add(image);

            if (file.Thumbnail != null)
                placeholder.Visibility = Visibility.Collapsed;

            PropertyChangedEventManager.AddHandler(file, (_, args) =>
            {
                if (args.PropertyName == nameof(FileItemViewModel.Thumbnail) && file.Thumbnail != null)
                {
                    placeholder.Visibility = Visibility.Collapsed;
                }
            }, nameof(FileItemViewModel.Thumbnail));

            grid.Loaded += (_, __) =>
            {
                if (!file.IsPreviewable)
                    return;

                if (file.Thumbnail == null)
                {
                    EnsureThumbnailAsync(file, placeholder, (int)size);
                }
            };

            return grid;
        }

        private bool TryBeginThumbnailLoad(string filePath)
        {
            lock (_thumbnailLoading)
            {
                if (_thumbnailLoading.Contains(filePath))
                    return false;
                _thumbnailLoading.Add(filePath);
                return true;
            }
        }

        private void EndThumbnailLoad(string filePath)
        {
            lock (_thumbnailLoading)
            {
                _thumbnailLoading.Remove(filePath);
            }
        }

        private async void EnsureThumbnailAsync(FileItemViewModel file, TextBlock placeholder, int size)
        {
            if (!TryBeginThumbnailLoad(file.FilePath))
                return;

            try
            {
                await _thumbnailSemaphore.WaitAsync();

                var thumb = await Task.Run(() => Services.ImagePreviewService.GetThumbnail(file.FilePath, size, size));
                if (thumb == null)
                    return;

                Dispatcher.Invoke(() =>
                {
                    file.Thumbnail = thumb;
                    placeholder.Visibility = Visibility.Collapsed;
                });
            }
            catch
            {
            }
            finally
            {
                _thumbnailSemaphore.Release();
                EndThumbnailLoad(file.FilePath);
            }
        }

        private FrameworkElement CreateListView(FileItemViewModel file)
        {
            var grid = new Grid
            {
                Height = 40,
                Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush,
                Margin = new Thickness(0, 1, 0, 0)
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var border = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var nameBlock = new TextBlock { Text = file.FileName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            Grid.SetColumn(nameBlock, 0);

            var pathBlock = new TextBlock { Text = file.FilePath, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) };
            Grid.SetColumn(pathBlock, 1);

            var sizeBlock = new TextBlock { Text = file.SizeFormatted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), TextAlignment = TextAlignment.Right };
            Grid.SetColumn(sizeBlock, 2);

            var dateBlock = new TextBlock { Text = file.ModifiedDate.ToString("yyyy-MM-dd"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0), TextAlignment = TextAlignment.Right };
            Grid.SetColumn(dateBlock, 3);

            grid.Children.Add(nameBlock);
            // Apply dynamic foregrounds
            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "WindowForeground");
            pathBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");
            sizeBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");
            dateBlock.SetResourceReference(TextBlock.ForegroundProperty, "ControlForeground");

            grid.Children.Add(pathBlock);
            grid.Children.Add(sizeBlock);
            grid.Children.Add(dateBlock);

            border.Child = grid;
            return border;
        }

        private async void OnDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FileItemViewModel file)
            {
                await DeleteFileAsync(file);
            }
        }

        private async Task DeleteFileAsync(FileItemViewModel file, bool skipConfirm = false)
        {
            try
            {
                if (!skipConfirm && Services.SettingsService.ConfirmDelete)
                {
                    var confirm = MessageBox.Show($"Delete '{file.FileName}'?", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes)
                        return;
                }

                StatusText.Text = $"Deleting {file.FileName}...";
                
                // Add to recycle bin before deleting
                AddToRecycleBin(file);
                
                await Task.Run(() =>
                {
                    try
                    {
                        FileSystem.DeleteFile(file.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    }
                    catch
                    {
                        // If recycle fails, fallback to permanent delete
                        try { File.Delete(file.FilePath); } catch { }
                    }
                });
                // Remember current selection positions
                int oldGridIndex = _selectedGridIndex;
                int oldListIndex = ResultsListView?.SelectedIndex ?? -1;
                int oldDataGridIndex = ResultsDataGrid?.SelectedIndex ?? -1;

                // Remove from view models (remove all occurrences)
                foreach (var group in _groupViewModels)
                {
                    var removed = group.Files.RemoveAll(f => f.FilePath == file.FilePath);
                    if (removed > 0)
                    {
                        _totalDeletedSize += file.FileSize;
                    }
                }

                // Remove from any cached grid selections/lists
                _selectedGridItems.RemoveAll(f => f.FilePath == file.FilePath);
                _currentGridFiles.RemoveAll(f => f.FilePath == file.FilePath);

                // Remove any empty groups
                _groupViewModels.RemoveAll(g => g.Files.Count <= 1);

                // Compute flattened list to adjust selection
                var flatAfter = _groupViewModels.SelectMany(g => g.Files).ToList();

                if (_currentViewMode != "list")
                {
                    if (flatAfter.Count == 0)
                        _selectedGridIndex = -1;
                    else if (oldGridIndex < 0)
                        _selectedGridIndex = 0;
                    else
                        _selectedGridIndex = Math.Min(oldGridIndex, flatAfter.Count - 1);
                }

                ApplySorting();
                ResultsDataGrid.ItemsSource = null;
                ResultsListView.ItemsSource = null;
                DisplayResults();
                UpdateFooterStats();

                // For ListView, restore selection to next item (same index) or previous if at end
                if (_currentViewMode == "list" && ResultsListView.Visibility == Visibility.Visible)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        var count = ResultsListView.Items.Count;
                        if (count == 0) return;
                        int sel = oldListIndex >= 0 ? Math.Min(oldListIndex, count - 1) : 0;
                        ResultsListView.SelectedIndex = sel;
                        if (ResultsListView.SelectedItem != null)
                        {
                            ResultsListView.ScrollIntoView(ResultsListView.SelectedItem);
                            // Update layout to ensure item containers are generated
                            ResultsListView.UpdateLayout();
                            // Get the ListViewItem and focus it
                            var item = ResultsListView.ItemContainerGenerator.ContainerFromIndex(sel) as ListViewItem;
                            if (item != null)
                            {
                                item.Focus();
                            }
                            else
                            {
                                ResultsListView.Focus();
                            }
                        }
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                // For DataGrid, restore selection to next item (same index) or previous if at end
                else if (ResultsDataGrid.Visibility == Visibility.Visible)
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        var count = ResultsDataGrid.Items.Count;
                        if (count == 0) return;
                        int sel = oldDataGridIndex >= 0 ? Math.Min(oldDataGridIndex, count - 1) : 0;
                        ResultsDataGrid.SelectedIndex = sel;
                        if (ResultsDataGrid.SelectedItem != null)
                        {
                            ResultsDataGrid.ScrollIntoView(ResultsDataGrid.SelectedItem);
                            // Update layout to ensure row containers are generated
                            ResultsDataGrid.UpdateLayout();
                            // Get the DataGridRow and focus it
                            var row = ResultsDataGrid.ItemContainerGenerator.ContainerFromIndex(sel) as DataGridRow;
                            if (row != null)
                            {
                                row.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                            }
                            else
                            {
                                ResultsDataGrid.Focus();
                            }
                        }
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }
                // For grid view, highlight the next item
                else if (_currentViewMode != "list")
                {
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (_selectedGridIndex >= 0)
                        {
                            HighlightSelectedGridFile();
                            ResultsPanel.Focus();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
                }

                StatusText.Text = $"Deleted {file.FileName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Delete failed: {ex.Message}";
                MessageBox.Show($"Failed to delete file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void IconViewButton_Click(object sender, RoutedEventArgs e)
        {
            _currentViewMode = "icons";
            DisplayResults();
        }

        private void RowCheckBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is CheckBox checkBox)
            {
                var row = FindAncestor<DataGridRow>(checkBox);
                if (row != null)
                {
                    row.IsSelected = !row.IsSelected;
                    e.Handled = true;
                }
            }
        }

        private static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void ViewToggleButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle between list and grid views
            if (_currentViewMode == "list")
            {
                _currentViewMode = "icons";
                // Animate sliding indicator to right
                AnimateViewToggle(36);
            }
            else
            {
                _currentViewMode = "list";
                // Clear grid selections when switching to list
                _selectedGridItems.Clear();
                // Animate sliding indicator to left
                AnimateViewToggle(0);
            }
            
            // Check if we're in RecycleBin and display accordingly
            if (RecycleBinPanel.Visibility == Visibility.Visible)
            {
                DisplayRecycleBinResults();
            }
            else
            {
                DisplayResults();
            }
            
            UpdateDeleteCount();
        }

        private void AnimateViewToggle(double targetX)
        {
            var button = ViewToggleButton;
            if (button.Template.FindName("IndicatorTransform", button) is System.Windows.Media.TranslateTransform transform)
            {
                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    To = targetX,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
                };
                transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animation);
            }
            
            // Update icon colors
            if (button.Template.FindName("Indicator", button) is Border indicator)
            {
                var grid = indicator.Parent as Grid;
                if (grid != null && grid.Children.Count > 1 && grid.Children[1] is Grid labelGrid)
                {
                    if (labelGrid.Children[0] is Viewbox listBox && labelGrid.Children[1] is Viewbox gridBox)
                    {
                        if (listBox.Child is System.Windows.Shapes.Path listPath && gridBox.Child is System.Windows.Shapes.Path gridPath)
                        {
                            listPath.Fill = targetX == 0 ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)Application.Current.Resources["ControlForeground"];
                            gridPath.Fill = targetX == 36 ? System.Windows.Media.Brushes.White : (System.Windows.Media.Brush)Application.Current.Resources["ControlForeground"];
                        }
                    }
                }
            }
        }

        private void LargeIconViewButton_Click(object sender, RoutedEventArgs e)
        {
            _currentViewMode = "large_icons";
            DisplayResults();
        }

        private void XLargeIconViewButton_Click(object sender, RoutedEventArgs e)
        {
            _currentViewMode = "xlarge_icons";
            DisplayResults();
        }

        private void ListViewButton_Click(object sender, RoutedEventArgs e)
        {
            _currentViewMode = "list";
            DisplayResults();
        }

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SortComboBox.SelectedItem is ComboBoxItem item)
            {
                _currentSortBy = item.Content.ToString();
                ApplySorting();
                DisplayResults();
            }
        }


        private void SidebarScanButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(ScanPanel);
        }

        private void SidebarRecycleBinButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(RecycleBinPanel);
            DisplayRecycleBinResults();
        }

        private void SidebarSimilarImagesButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(SimilarImagesPanelContainer);
            // Pass the selected directories to the similar images panel
            if (_selectedDirectories != null && _selectedDirectories.Count > 0)
            {
                SimilarImagesPanelControl.SetDirectories(_selectedDirectories);
            }
        }

        private void SidebarSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(SettingsPanel);
            LoadSettingsValues();
        }

        private void LoadSettingsValues()
        {
            // Load current settings into the UI
            MinFileSizeTextBox.Text = SettingsService.MinFileSizeMB.ToString();
            MaxFileSizeTextBox.Text = SettingsService.MaxFileSizeMB.ToString();
            MaxDuplicatesTextBox.Text = SettingsService.MaxDuplicatesToShow.ToString();
            GridPictureSizeSlider.Value = SettingsService.GridPictureSize;
            ShowGridFilePathCheckBox.IsChecked = SettingsService.ShowGridFilePath;
            if (ConfirmDeleteCheckBox != null)
                ConfirmDeleteCheckBox.IsChecked = SettingsService.ConfirmDelete;
        }

        private void ConfirmDeleteCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ConfirmDeleteCheckBox == null)
                return;

            SettingsService.SetConfirmDelete(ConfirmDeleteCheckBox.IsChecked == true);
            SettingsService.SaveToFile();
        }

        private void SidebarHelpButton_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(HelpPanel);
        }

        private void ShowPanel(FrameworkElement panel)
        {
            ScanPanel.Visibility = panel == ScanPanel ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = panel == SettingsPanel ? Visibility.Visible : Visibility.Collapsed;
            HelpPanel.Visibility = panel == HelpPanel ? Visibility.Visible : Visibility.Collapsed;
            RecycleBinPanel.Visibility = panel == RecycleBinPanel ? Visibility.Visible : Visibility.Collapsed;
            SimilarImagesPanelContainer.Visibility = panel == SimilarImagesPanelContainer ? Visibility.Visible : Visibility.Collapsed;

            // Show top bars only for ScanPanel and RecycleBinPanel (not for similar images)
            bool showTopBars = (panel == ScanPanel || panel == RecycleBinPanel);
            TopFiltersBar.Visibility = showTopBars ? Visibility.Visible : Visibility.Collapsed;
            ActionBar.Visibility = showTopBars ? Visibility.Visible : Visibility.Collapsed;
            
            // Hide footer stats when not viewing ScanPanel
            FooterStats.Visibility = panel == ScanPanel ? Visibility.Visible : Visibility.Collapsed;

            // Update ActionBar buttons based on panel
            if (panel == RecycleBinPanel)
            {
                DeleteSelectedButton.Content = "Recover Selected (0)";
                DeleteSelectedButton.Visibility = Visibility.Visible;
                DeleteSelectedButton.Style = (Style)Application.Current.Resources["SuccessButton"];
                RecycleBinControls.Visibility = Visibility.Visible;
                ViewControlPanel.Visibility = Visibility.Visible;
                ScanButton.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                SelectAllButton.Visibility = Visibility.Collapsed;
            }
            else if (panel == ScanPanel)
            {
                DeleteSelectedButton.Content = "Delete Selected (0)";
                DeleteSelectedButton.Visibility = Visibility.Visible;
                DeleteSelectedButton.Style = (Style)Application.Current.Resources["DangerButton"];
                RecycleBinControls.Visibility = Visibility.Collapsed;
                ViewControlPanel.Visibility = Visibility.Visible;
                ScanButton.Visibility = Visibility.Visible;
                SelectAllButton.Visibility = Visibility.Visible;
            }

            SidebarScanButton.IsChecked = panel == ScanPanel;
            SidebarRecycleBinButton.IsChecked = panel == RecycleBinPanel;
            SidebarSimilarImagesButton.IsChecked = panel == SimilarImagesPanelContainer;
            SidebarSettingsButton.IsChecked = panel == SettingsPanel;
            SidebarHelpButton.IsChecked = panel == HelpPanel;
        }

        private bool _sidebarExpanded = true;

        private void SidebarCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            _sidebarExpanded = !_sidebarExpanded;

            // Create animation for smooth transition
            var widthAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            if (_sidebarExpanded)
            {
                // Expand sidebar
                widthAnimation.To = 256;
                SidebarContainer.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
                
                // Show content after a brief delay
                Task.Delay(50).ContinueWith(_ => Dispatcher.Invoke(() =>
                {
                    SidebarContent.Visibility = Visibility.Visible;
                }));
                
                SidebarCollapseButton.ToolTip = "Collapse sidebar";
                ((TextBlock)SidebarCollapseButton.Content).Text = "‹";
            }
            else
            {
                // Collapse sidebar
                widthAnimation.To = 0;
                SidebarContent.Visibility = Visibility.Collapsed;
                SidebarContainer.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
                
                SidebarCollapseButton.ToolTip = "Expand sidebar";
                ((TextBlock)SidebarCollapseButton.Content).Text = "›";
            }

            // Refresh grid layout after animation completes
            Task.Delay(300).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (_currentViewMode != "list" && ResultsScrollViewer.Visibility == Visibility.Visible)
                {
                    // Force grid to recalculate layout by re-rendering
                    DisplayResults();
                }
            }));
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecycleBinPanel.Visibility == Visibility.Visible && RecycleBinDataGrid != null)
            {
                // Toggle: if all selected, deselect all; otherwise select all
                if (RecycleBinDataGrid.SelectedItems.Count == RecycleBinDataGrid.Items.Count)
                {
                    RecycleBinDataGrid.SelectedItems.Clear();
                }
                else
                {
                    RecycleBinDataGrid.SelectAll();
                }
            }
            else if (ResultsDataGrid.Visibility == Visibility.Visible)
            {
                // Toggle: if all selected, deselect all; otherwise select all
                if (ResultsDataGrid.SelectedItems.Count == ResultsDataGrid.Items.Count)
                {
                    ResultsDataGrid.SelectedItems.Clear();
                }
                else
                {
                    ResultsDataGrid.SelectAll();
                }
            }
            else if (ResultsListView.Visibility == Visibility.Visible)
            {
                // Toggle: if all selected, deselect all; otherwise select all
                if (ResultsListView.SelectedItems.Count == ResultsListView.Items.Count)
                {
                    ResultsListView.SelectedItems.Clear();
                }
                else
                {
                    ResultsListView.SelectAll();
                }
            }
        }

        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            // Check if we're in RecycleBinPanel
            if (RecycleBinPanel.Visibility == Visibility.Visible)
            {
                RecoverSelectedFiles();
                return;
            }

            var toDelete = new List<FileItemViewModel>();

            // First check grid selections (for grid view in scanned files)
            if (_currentViewMode != "list" && _selectedGridItems.Count > 0)
            {
                toDelete = _selectedGridItems.ToList();
            }
            else if (ResultsDataGrid.Visibility == Visibility.Visible)
            {
                toDelete = ResultsDataGrid.SelectedItems.Cast<FileItemViewModel>().ToList();
            }
            else if (ResultsListView.Visibility == Visibility.Visible)
            {
                toDelete = ResultsListView.SelectedItems.Cast<FileItemViewModel>().ToList();
            }

            if (toDelete.Count == 0)
            {
                MessageBox.Show("No files selected.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Services.SettingsService.ConfirmDelete)
            {
                var result = MessageBox.Show($"Delete {toDelete.Count} file(s)?", "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            foreach (var file in toDelete.ToList())
            {
                await DeleteFileAsync(file, skipConfirm: true);
            }
        }

        private void UnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UnitComboBox.SelectedItem is ComboBoxItem item)
            {
                var text = item.Content.ToString();
                if (Enum.TryParse<Services.SizeUnit>(text, out var unit))
                {
                    Services.SettingsService.SetSizeUnit(unit);
                }
            }
        }

        private async void ResultsListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                // Attempt to delete the selected file item or the first file in a selected group
                if (ResultsListView.SelectedItem is FileItemViewModel file)
                {
                    await DeleteFileAsync(file);
                }
                else if (ResultsListView.SelectedItem is DuplicateGroupViewModel group)
                {
                    var first = group.Files.FirstOrDefault();
                    if (first != null)
                        await DeleteFileAsync(first);
                }
            }
        }

        private async void ResultsDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                var toDelete = ResultsDataGrid.SelectedItems.Cast<FileItemViewModel>().ToList();
                if (toDelete.Count == 0)
                {
                    return;
                }

                foreach (var file in toDelete.ToList())
                {
                    await DeleteFileAsync(file);
                }
                e.Handled = true;
            }
        }

        private void ResultsPanel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle keyboard navigation on grid view - use PreviewKeyDown to capture before bubbling
            if (_currentGridFiles.Count == 0 || RecycleBinPanel.Visibility == Visibility.Visible) 
                return;

            if (e.Key == Key.Delete)
            {
                // Delete multi-selected items or the single selected item
                List<FileItemViewModel> filesToDelete;
                if (_selectedGridItems.Count > 0)
                {
                    filesToDelete = _selectedGridItems.ToList();
                }
                else if (_selectedGridIndex >= 0 && _selectedGridIndex < _currentGridFiles.Count)
                {
                    filesToDelete = new List<FileItemViewModel> { _currentGridFiles[_selectedGridIndex] };
                }
                else
                {
                    return;
                }

                foreach (var file in filesToDelete)
                {
#pragma warning disable CS4014
                    DeleteFileAsync(file);
#pragma warning restore CS4014
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                HandleGridNavigation(e.Key);
                e.Handled = true;
            }
        }

        private void RecycleBinScrollViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle Delete key in recycle bin grid view
            if (e.Key == Key.Delete && _selectedRecycleBinItems.Count > 0 && RecycleBinPanel.Visibility == Visibility.Visible)
            {
                // Perform recover on selected items
                RecoverSelectedFiles();
                e.Handled = true;
            }
        }

        private void HandleGridNavigation(Key key)
        {
            if (_currentGridFiles.Count == 0) return;

            // Initialize selection if needed
            if (_selectedGridIndex < 0)
            {
                _selectedGridIndex = 0;
                HighlightSelectedGridFile();
                return;
            }
            // Prefer cached computed columns from layout; fallback conservatively to 1 to avoid diagonal moves
            int columnsPerRow = _gridColumns > 0 ? _gridColumns : 1;

            int newIndex2 = _selectedGridIndex;
            switch (key)
            {
                case Key.Right:
                    newIndex2 = Math.Min(_currentGridFiles.Count - 1, newIndex2 + 1);
                    break;
                case Key.Left:
                    newIndex2 = Math.Max(0, newIndex2 - 1);
                    break;
                case Key.Down:
                    newIndex2 = Math.Min(_currentGridFiles.Count - 1, newIndex2 + columnsPerRow);
                    break;
                case Key.Up:
                    newIndex2 = Math.Max(0, newIndex2 - columnsPerRow);
                    break;
            }

            _selectedGridIndex = newIndex2;
            HighlightSelectedGridFile();
        }

        private void HighlightSelectedGridFile()
        {
            // Clear all highlights
            if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is WrapPanel gridPanel)
            {
                for (int i = 0; i < gridPanel.Children.Count; i++)
                {
                    if (gridPanel.Children[i] is Border border)
                    {
                        border.Background = System.Windows.Media.Brushes.Transparent;
                        border.BorderThickness = new Thickness(0);
                    }
                }
            }
            else if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is Canvas canvas)
            {
                foreach (var item in _realizedGridItems.Values)
                {
                    if (item is Border border)
                    {
                        border.Background = System.Windows.Media.Brushes.Transparent;
                        border.BorderThickness = new Thickness(0);
                    }
                }
            }

            if (_selectedGridIndex < 0)
                return;

            // Highlight selected
            if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is WrapPanel grid)
            {
                if (_selectedGridIndex < grid.Children.Count && grid.Children[_selectedGridIndex] is Border selectedBorder)
                {
                    selectedBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 120, 215));
                    selectedBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                    selectedBorder.BorderThickness = new Thickness(2);
                    if (!IsElementInView(selectedBorder))
                        selectedBorder.BringIntoView();
                }
            }
            else if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is Canvas canvas)
            {
                int row = _selectedGridIndex / Math.Max(1, _virtualColumns);
                if (!IsRowInView(row))
                {
                    double rowTop = row * _virtualItemHeight;
                    double rowBottom = rowTop + _virtualItemHeight;
                    double viewTop = ResultsScrollViewer.VerticalOffset;
                    double viewBottom = viewTop + ResultsScrollViewer.ViewportHeight;

                    if (rowTop < viewTop)
                        ResultsScrollViewer.ScrollToVerticalOffset(rowTop);
                    else if (rowBottom > viewBottom)
                        ResultsScrollViewer.ScrollToVerticalOffset(Math.Max(0, rowBottom - ResultsScrollViewer.ViewportHeight));

                    UpdateVirtualGrid(canvas);
                }

                if (_realizedGridItems.TryGetValue(_selectedGridIndex, out var elem) && elem is Border selectedBorder)
                {
                    selectedBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 0, 120, 215));
                    selectedBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                    selectedBorder.BorderThickness = new Thickness(2);
                }
            }
        }

        private bool IsRowInView(int row)
        {
            double rowTop = row * _virtualItemHeight;
            double rowBottom = rowTop + _virtualItemHeight;
            double viewTop = ResultsScrollViewer.VerticalOffset;
            double viewBottom = viewTop + ResultsScrollViewer.ViewportHeight;
            return rowTop >= viewTop && rowBottom <= viewBottom;
        }

        private bool IsElementInView(FrameworkElement element)
        {
            if (element == null || ResultsScrollViewer == null)
                return true;

            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                return true;

            try
            {
                var transform = element.TransformToAncestor(ResultsScrollViewer);
                var bounds = transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                var viewport = new Rect(0, 0, ResultsScrollViewer.ViewportWidth, ResultsScrollViewer.ViewportHeight);
                return viewport.Contains(bounds);
            }
            catch
            {
                return true;
            }
        }

        // Settings Event Handlers
        private void SaveFileSizeLimitsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                long minSize = 0;
                long maxSize = 0;
                
                if (!string.IsNullOrWhiteSpace(MinFileSizeTextBox.Text))
                {
                    if (!long.TryParse(MinFileSizeTextBox.Text, out minSize) || minSize < 0)
                    {
                        MessageBox.Show("Please enter a valid minimum file size (0 or greater).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                
                if (!string.IsNullOrWhiteSpace(MaxFileSizeTextBox.Text))
                {
                    if (!long.TryParse(MaxFileSizeTextBox.Text, out maxSize) || maxSize < 0)
                    {
                        MessageBox.Show("Please enter a valid maximum file size (0 or greater).", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                
                if (maxSize > 0 && minSize > maxSize)
                {
                    MessageBox.Show("Minimum file size cannot be greater than maximum file size.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                SettingsService.SetMinFileSizeMB(minSize);
                SettingsService.SetMaxFileSizeMB(maxSize);
                SettingsService.SaveToFile();
                
                MessageBox.Show($"File size limits applied successfully.\n\nMinimum: {(minSize == 0 ? "No limit" : minSize + " MB")}\nMaximum: {(maxSize == 0 ? "No limit" : maxSize + " MB")}", 
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving file size limits: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveDuplicateLimitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int maxDuplicates = 0;
                
                if (!string.IsNullOrWhiteSpace(MaxDuplicatesTextBox.Text))
                {
                    if (!int.TryParse(MaxDuplicatesTextBox.Text, out maxDuplicates) || maxDuplicates < 0)
                    {
                        MessageBox.Show("Please enter a valid number (0 or greater). 0 means no limit.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                
                SettingsService.SetMaxDuplicatesToShow(maxDuplicates);
                SettingsService.SaveToFile();
                
                MessageBox.Show($"Duplicate limit applied successfully: {(maxDuplicates == 0 ? "No limit" : maxDuplicates.ToString())}", 
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving duplicate limit: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GridPictureSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (GridPictureSizeValueText != null && GridSizePreviewBorder != null)
            {
                int size = (int)e.NewValue;
                GridPictureSizeValueText.Text = $"{size} px";
                GridSizePreviewBorder.Width = size;
                GridSizePreviewBorder.Height = size;
            }
        }

        private void SaveGridSizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int size = (int)GridPictureSizeSlider.Value;
                SettingsService.SetGridPictureSize(size);
                SettingsService.SaveToFile();
                
                // Update the virtual grid dimensions
                _virtualItemWidth = size + 56;  // size + panel padding + margins
                _virtualItemHeight = size + 104; // size + panel padding + text height + margins
                
                // If currently in grid view, refresh the display
                if (_currentViewMode != "list" && _groupViewModels != null && _groupViewModels.Count > 0)
                {
                    MessageBox.Show($"Grid picture size set to {size}px. Refreshing grid view...", 
                        "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                    DisplayResults();
                }
                else
                {
                    MessageBox.Show($"Grid picture size set to {size}px. The new size will be applied when you switch to grid view.", 
                        "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving grid size: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveGridFilePathButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool showPath = ShowGridFilePathCheckBox.IsChecked ?? true;
                SettingsService.SetShowGridFilePath(showPath);
                SettingsService.SaveToFile();

                // Update virtual item height based on setting
                int gridSize = SettingsService.GridPictureSize;
                _virtualItemHeight = gridSize + (showPath ? 104 : 84);

                // If currently in grid view, refresh the display
                if (_currentViewMode != "list" && _groupViewModels != null && _groupViewModels.Count > 0)
                {
                    DisplayResults();
                    MessageBox.Show(showPath ? "File path display enabled. Refreshing grid view..." : "File path display disabled. Refreshing grid view...",
                        "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(showPath ? "File path display enabled. The setting will be applied when you switch to grid view." : "File path display disabled. The setting will be applied when you switch to grid view.",
                        "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Recycle Bin Methods
        private void AddToRecycleBin(FileItemViewModel file)
        {
            var deletedItem = new DeletedFileItem
            {
                FileName = file.FileName,
                FilePath = file.FilePath,
                FileSize = file.FileSize,
                FileSizeFormatted = file.SizeFormatted,
                DeletedTime = DateTime.Now,
                OriginalViewModel = file,
                Thumbnail = file.Thumbnail
            };

            // Add to beginning of list (most recent first)
            _recycleBin.Insert(0, deletedItem);

            // Maintain max size - remove oldest items
            while (_recycleBin.Count > MAX_RECYCLE_BIN_SIZE)
            {
                _recycleBin.RemoveAt(_recycleBin.Count - 1);
            }
        }

        private void UpdateRecycleBinDisplay()
        {
            RecycleBinDataGrid.ItemsSource = _recycleBin;
            RecycleBinCountText.Text = $"({_recycleBin.Count} file{(_recycleBin.Count != 1 ? "s" : "")})";
            UpdateRecycleBinCount();
        }

        private void DisplayRecycleBinResults()
        {
            // Clear grid selections when switching views
            _selectedRecycleBinItems.Clear();
            RecycleBinDataGrid?.UnselectAll();
            UpdateRecycleBinCount();
            
            // Update count text
            RecycleBinCountText.Text = $"({_recycleBin.Count} file{(_recycleBin.Count != 1 ? "s" : "")})";
            
            // Show/hide placeholder and action bar based on whether bin is empty
            if (_recycleBin.Count == 0)
            {
                NoRecycleBinPlaceholder.Visibility = Visibility.Visible;
                RecycleBinDataGrid.Visibility = Visibility.Collapsed;
                RecycleBinScrollViewer.Visibility = Visibility.Collapsed;
                ActionBar.Visibility = Visibility.Collapsed;
                return;
            }
            else
            {
                NoRecycleBinPlaceholder.Visibility = Visibility.Collapsed;
                ActionBar.Visibility = Visibility.Visible;
            }
            
            if (_currentViewMode == "list")
            {
                // Show list view
                RecycleBinDataGrid.Visibility = Visibility.Visible;
                RecycleBinScrollViewer.Visibility = Visibility.Collapsed;
                RecycleBinDataGrid.ItemsSource = _recycleBin;
            }
            else
            {
                // Show grid view
                RecycleBinDataGrid.Visibility = Visibility.Collapsed;
                RecycleBinScrollViewer.Visibility = Visibility.Visible;
                RecycleBinResultsPanel.Children.Clear();

                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                // Ensure WrapPanel has a constrained width for proper wrapping
                double wrapWidth = RecycleBinScrollViewer.ViewportWidth;
                if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                    wrapWidth = RecycleBinScrollViewer.ActualWidth;
                if (!double.IsNaN(wrapWidth) && wrapWidth > 0)
                    wrap.Width = wrapWidth;

                RecycleBinResultsPanel.Children.Add(wrap);

                // Create grid items for each deleted file
                foreach (var deletedFile in _recycleBin)
                {
                    var item = CreateRecycleBinGridItem(deletedFile);
                    wrap.Children.Add(item);
                }
            }
        }

        private Border CreateRecycleBinGridItem(DeletedFileItem deletedFile)
        {
            int gridSize = SettingsService.GridPictureSize;
            
            var border = new Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.Resources["CardBackground"],
                BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(4),
                Padding = new Thickness(8),
                Width = gridSize + 40,
                Cursor = Cursors.Hand,
                Tag = deletedFile
            };

            // Add mouse click handler for selection (with Ctrl+Click support)
            border.MouseLeftButtonDown += (s, e) =>
            {
                var clickedBorder = s as Border;
                var file = clickedBorder.Tag as DeletedFileItem;
                
                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                
                if (isCtrlPressed)
                {
                    // Ctrl+Click for multi-select: toggle this item
                    if (_selectedRecycleBinItems.Contains(file))
                    {
                        _selectedRecycleBinItems.Remove(file);
                        clickedBorder.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"];
                        clickedBorder.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        _selectedRecycleBinItems.Add(file);
                        clickedBorder.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
                        clickedBorder.BorderThickness = new Thickness(3);
                    }
                }
                else
                {
                    // Single click: select only this item
                    _selectedRecycleBinItems.Clear();
                    _selectedRecycleBinItems.Add(file);
                    
                    // Update visual feedback for all items
                    if (s is Border border2 && border2.Parent is WrapPanel wrap)
                    {
                        foreach (var child in wrap.Children.OfType<Border>())
                        {
                            if (child.Tag == file)
                            {
                                child.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
                                child.BorderThickness = new Thickness(3);
                            }
                            else
                            {
                                child.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"];
                                child.BorderThickness = new Thickness(1);
                            }
                        }
                    }
                }
                
                // Update the count display
                UpdateRecycleBinCount();
            };

            var stack = new StackPanel { IsHitTestVisible = false };

            // File icon or image preview
            var placeholder = new TextBlock
            {
                Text = "📄",
                FontSize = Math.Max(18, gridSize * 0.6),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var image = new Image
            {
                Width = gridSize,
                Height = gridSize,
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                IsHitTestVisible = false
            };

            // Prefer stored thumbnail (original preview)
            if (deletedFile.Thumbnail != null)
            {
                image.Source = deletedFile.Thumbnail;
                placeholder.Visibility = Visibility.Collapsed;
            }
            // Try to load preview if it's an image and still exists on disk
            else if (File.Exists(deletedFile.FilePath) && Services.ImagePreviewService.IsPreviewableImage(deletedFile.FilePath))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = gridSize;
                    bitmap.UriSource = new Uri(deletedFile.FilePath);
                    bitmap.EndInit();
                    image.Source = bitmap;
                    placeholder.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    // If loading fails, show placeholder
                    image.Source = null;
                }
            }

            stack.Children.Add(placeholder);
            stack.Children.Add(image);

            // File name
            var nameText = new TextBlock
            {
                Text = deletedFile.FileName,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["WindowForeground"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            stack.Children.Add(nameText);

            // File size
            var sizeText = new TextBlock
            {
                Text = deletedFile.FileSizeFormatted,
                FontSize = 10,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedForeground"],
                TextAlignment = TextAlignment.Center
            };
            stack.Children.Add(sizeText);

            border.Child = stack;
            return border;
        }

        private void UpdateDeleteCount()
        {
            int count = 0;
            
            if (RecycleBinPanel?.Visibility == Visibility.Visible)
            {
                // In RecycleBin - don't update scan file counts
                return;
            }
            
            // Count from grid view if active
            if (_currentViewMode != "list")
            {
                count = _selectedGridItems?.Count ?? 0;
                System.Diagnostics.Debug.WriteLine($"UpdateDeleteCount - Grid view, selected items: {count}");
            }
            else
            {
                // In list view, use ResultsDataGrid (not ResultsListView)
                count = ResultsDataGrid?.SelectedItems.Count ?? 0;
                System.Diagnostics.Debug.WriteLine($"UpdateDeleteCount - List view (DataGrid), selected items: {count}");
            }
            
            System.Diagnostics.Debug.WriteLine($"UpdateDeleteCount - Final count: {count}");
            DeleteSelectedButton.Content = $"Delete Selected ({count})";
        }

        private void UpdateRecycleBinCount()
        {
            if (RecycleBinPanel.Visibility == Visibility.Visible)
            {
                int selectedCount;
                if (_currentViewMode != "list")
                {
                    selectedCount = _selectedRecycleBinItems.Count;
                }
                else
                {
                    selectedCount = RecycleBinDataGrid?.SelectedItems.Count ?? 0;
                }
                DeleteSelectedButton.Content = $"Recover Selected ({selectedCount})";
            }
        }

        private void RecycleBinDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateRecycleBinCount();
        }

        private void RecoverSelectedFiles()
        {
            // Get selected items based on current view mode
            List<DeletedFileItem> selectedItems;
            if (_currentViewMode != "list")
            {
                selectedItems = _selectedRecycleBinItems.ToList();
            }
            else
            {
                selectedItems = RecycleBinDataGrid.SelectedItems.Cast<DeletedFileItem>().ToList();
            }
            
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("No files selected. Please select files to recover from the list.", 
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var message = selectedItems.Count == 1
                ? $"Restore '{selectedItems[0].FileName}' from Recycle Bin?"
                : $"Restore {selectedItems.Count} files from Recycle Bin?";

            var result = MessageBox.Show(message, "Restore Files", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                int successCount = 0;
                var failedFiles = new List<string>();

                foreach (var item in selectedItems)
                {
                    try
                    {
                        if (RestoreFromRecycleBin(item.FilePath))
                        {
                            _recycleBin.Remove(item);
                            successCount++;
                        }
                        else
                        {
                            failedFiles.Add(item.FileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedFiles.Add($"{item.FileName}: {ex.Message}");
                    }
                }

                DisplayRecycleBinResults();

                if (failedFiles.Count > 0)
                {
                    var failedMessage = $"Restored {successCount} file(s).\n\nFailed to restore:\n" + string.Join("\n", failedFiles.Take(5));
                    if (failedFiles.Count > 5) failedMessage += $"\n...and {failedFiles.Count - 5} more";
                    MessageBox.Show(failedMessage, "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (successCount > 0)
                {
                    MessageBox.Show($"Successfully restored {successCount} file(s).", "Restore Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            
            if (result == MessageBoxResult.Yes)
            {
                foreach (var item in selectedItems)
                {
                    _recycleBin.Remove(item);
                }

                UpdateRecycleBinDisplay();
            }
        }

        private void ClearBinButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recycleBin.Count == 0)
            {
                MessageBox.Show("Recycle bin is already empty.", "Recycle Bin", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Clear {_recycleBin.Count} file(s) from tracking? Files remain in Windows Recycle Bin.", 
                "Confirm Clear", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                _recycleBin.Clear();
                UpdateRecycleBinDisplay();
            }
        }

        private bool RestoreFromRecycleBin(string filePath)
        {
            try
            {
                // Use Shell32 COM to restore from Recycle Bin (dynamic invocation)
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic recycleBin = shell.NameSpace(10); // 10 = Recycle Bin

                if (recycleBin == null)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to access Recycle Bin");
                    return false;
                }

                string fileName = Path.GetFileName(filePath);
                System.Diagnostics.Debug.WriteLine($"Looking for file: {filePath}");

                foreach (dynamic item in recycleBin.Items())
                {
                    try
                    {
                        // Get item name and path
                        string itemName = item.Name;
                        string itemPath = item.Path;
                        
                        System.Diagnostics.Debug.WriteLine($"Checking item: {itemName} at {itemPath}");

                        // Try to match by name first (since path in recycle bin is different)
                        if (itemName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine($"Found matching file by name: {itemName}");
                            
                            // Try different restore verbs
                            bool restored = DoVerbs(item, "ESTORE") || // Contains "RESTORE"
                                          DoVerbs(item, "&Restore") || // Menu text
                                          DoVerbs(item, "Restore");    // Direct name
                            
                            if (restored)
                            {
                                System.Threading.Thread.Sleep(500); // Give more time to restore
                                
                                // Check if file was restored
                                if (File.Exists(filePath))
                                {
                                    System.Diagnostics.Debug.WriteLine($"Successfully restored: {filePath}");
                                    return true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error checking item: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"File not found in recycle bin: {fileName}");
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreFromRecycleBin error: {ex.Message}");
                return false;
            }
        }

        private bool DoVerbs(dynamic item, string verb)
        {
            try
            {
                foreach (dynamic itemVerb in item.Verbs())
                {
                    string verbName = itemVerb.Name;
                    System.Diagnostics.Debug.WriteLine($"Available verb: {verbName}");
                    
                    if (verbName.ToUpper().Contains(verb.ToUpper()))
                    {
                        System.Diagnostics.Debug.WriteLine($"Executing verb: {verbName}");
                        itemVerb.DoIt();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DoVerbs error: {ex.Message}");
            }
            return false;
        }

        private void RefreshGridItemSelection()
        {
            // Update visual feedback for all grid items
            // Check if we have a WrapPanel (normal grid view)
            if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is WrapPanel wrapPanel)
            {
                foreach (var child in wrapPanel.Children.OfType<Border>())
                {
                    // Check border's Tag directly (we now store file reference there)
                    var file = child.Tag as FileItemViewModel;
                    if (file == null)
                    {
                        // Fallback to checking panel Tag for backwards compatibility
                        var panel = child.Child as StackPanel;
                        file = panel?.Tag as FileItemViewModel;
                    }
                    
                    if (file != null && _selectedGridItems.Contains(file))
                    {
                        // Apply highlight to Border
                        child.Background = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246)); // Bright blue
                        child.BorderThickness = new Thickness(2);
                        child.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246)); // Blue border
                    }
                    else
                    {
                        child.Background = new SolidColorBrush(Colors.Transparent);
                        child.BorderThickness = new Thickness(0);
                    }
                }
            }
            else if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is Canvas canvas)
            {
                // Virtualized grid view
                foreach (var child in canvas.Children.OfType<Border>())
                {
                    var file = child.Tag as FileItemViewModel;
                    if (file == null)
                    {
                        var panel = child.Child as StackPanel;
                        file = panel?.Tag as FileItemViewModel;
                    }
                    
                    if (file != null && _selectedGridItems.Contains(file))
                    {
                        child.Background = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246)); // Bright blue
                        child.BorderThickness = new Thickness(2);
                        child.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246)); // Blue border
                    }
                    else
                    {
                        child.Background = new SolidColorBrush(Colors.Transparent);
                        child.BorderThickness = new Thickness(0);
                    }
                }
            }
        }

        private void ClearAllGridItemSelection()
        {
            // Clear visual selection from all grid items
            if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is WrapPanel wrapPanel)
            {
                foreach (Border child in wrapPanel.Children.OfType<Border>())
                {
                    child.Background = new SolidColorBrush(Colors.Transparent);
                    child.BorderThickness = new Thickness(0);
                }
            }
            else if (ResultsPanel.Children.Count > 0 && ResultsPanel.Children[0] is Canvas canvas)
            {
                foreach (var child in canvas.Children.OfType<Border>())
                {
                    child.Background = new SolidColorBrush(Colors.Transparent);
                    child.BorderThickness = new Thickness(0);
                }
            }
        }

        private void RestoreFileMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = RecycleBinDataGrid.SelectedItems.Cast<DeletedFileItem>().ToList();
            if (selectedItems.Count == 0) return;

            var message = selectedItems.Count == 1
                ? $"File '{selectedItems[0].FileName}' is in Windows Recycle Bin.\n\nYou can restore it from Windows Recycle Bin if needed.\n\nRemove from tracking list?"
                : $"{selectedItems.Count} files are in Windows Recycle Bin.\n\nYou can restore them from Windows Recycle Bin if needed.\n\nRemove from tracking list?";

            var result = MessageBox.Show(message, "Remove from Bin", MessageBoxButton.YesNo, MessageBoxImage.Question);
            
            if (result == MessageBoxResult.Yes)
            {
                foreach (var item in selectedItems)
                {
                    _recycleBin.Remove(item);
                }
                
                UpdateRecycleBinDisplay();
            }
        }

        private void RemoveFromBinMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = RecycleBinDataGrid.SelectedItems.Cast<DeletedFileItem>().ToList();
            if (selectedItems.Count == 0) return;

            foreach (var item in selectedItems)
            {
                _recycleBin.Remove(item);
            }
            
            UpdateRecycleBinDisplay();
        }
    }

    // Deleted file item for recycle bin
    public class DeletedFileItem
    {
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public string FileSizeFormatted { get; set; }
        public DateTime DeletedTime { get; set; }
        public FileItemViewModel OriginalViewModel { get; set; }
        public BitmapImage Thumbnail { get; set; }
    }
}
