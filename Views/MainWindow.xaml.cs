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
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Diagnostics;

namespace DupFree.Views
{
    public partial class MainWindow : Window
    {
        // Windows API for dark title bar
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private readonly DuplicateSearchService _searchService;
        private readonly List<string> _selectedDirectories;
        private readonly List<DuplicateGroupViewModel> _groupViewModels;
        private string _currentViewMode = "list";
        private readonly string _currentSortBy = "Name";
        private string _searchText = string.Empty;
        private readonly List<FileItemViewModel> _currentGridFiles = [];
        private readonly Dictionary<int, FrameworkElement> _realizedGridItems = [];
        private bool _isVirtualGridActive = false;
        private double _virtualItemWidth = 156;
        private double _virtualItemHeight = 196;
        private int _virtualColumns = 1;
        private long _totalDeletedSize = 0;  // Track space saved from deletions
        private int _totalFilesScanned = 0;  // Track total files scanned during duplicate search
        private bool _hasScannedOnce = false;
        private int _selectedGridIndex = -1;
        private int _gridColumns = 0;
        private System.Threading.CancellationTokenSource? _scanCancellation;
        private readonly SemaphoreSlim _thumbnailSemaphore = new(4);
        private readonly HashSet<string> _thumbnailLoading = new(StringComparer.OrdinalIgnoreCase);
        // Track/limit concurrent video hover previews
        private int _activeVideoPreviews = 0;
        private const int MaxConcurrentVideoPreviews = 6;
        private readonly List<Action> _videoPreviewStoppers = [];
        // Per-file media release actions — called before deletion to free OS file locks held by MediaElement / animated BitmapImage
        private readonly Dictionary<string, Action> _mediaReleaseActions = new(StringComparer.OrdinalIgnoreCase);
        private bool _isScanning = false;  // Track if a scan is currently in progress
        private bool _isDeleting = false;   // Prevent re-entrant deletion (double-click / key repeat)

        // Recycle Bin functionality
        private readonly ObservableCollection<DeletedFileItem> _recycleBin = [];
        private readonly List<DeletedFileItem> _selectedRecycleBinItems = [];
        private readonly List<FileItemViewModel> _selectedGridItems = []; // For scanned files grid selection
        private FileItemViewModel? _lastSelectedGridItem = null; // For Shift+Click range selection
        // MAX_RECYCLE_BIN_SIZE now driven by SettingsService.MaxRecycleBinSize

        public MainWindow()
        {
#if DEBUG
            // uncomment to test unexpected-error dialog
            // throw new InvalidOperationException("simulated startup failure");
#endif
            InitializeComponent();
            // Hide the dependency checker in releases (it only works with the SDK/source)
#if !DEBUG
            if (CheckDependenciesButton != null)
                CheckDependenciesButton.Visibility = Visibility.Collapsed;
#endif
            // Set sidebar button to static light blue color
            SidebarCollapseButton.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)); // BlueBrush

            // Apply dark title bar
            SourceInitialized += (s, e) => ApplyDarkTitleBar();

            // Handle window size changes to refresh grid layout
            SizeChanged += MainWindow_SizeChanged;

            // Settings already loaded in App.OnStartup — no need to reload here.

            // Initialize grid dimensions from settings
            int gridSize = SettingsService.GridPictureSize;
            _virtualItemWidth = gridSize + 36;  // size + panel padding + margins (reduced spacing)
            _virtualItemHeight = gridSize + (SettingsService.ShowGridFilePath ? 92 : 72); // adjust based on path display setting (reduced spacing)

            _searchService = new DuplicateSearchService();
            // Ensure service events update UI on dispatcher thread
            _searchService.OnStatusChanged += (status) => Dispatcher.Invoke(() => StatusText.Text = status);
            _selectedDirectories = [];
            _groupViewModels = [];
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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

        private System.Windows.Threading.DispatcherTimer? _resizeTimer;

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
                    MessageBox.Show(string.Format(Properties.Resources.FileNotFoundFormat, file.FilePath), "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Properties.Resources.CouldNotOpenFileFormat, ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handler for the "Open Log File" button added to the Help/About panel. Selects the current
        /// log file in Explorer so users can easily attach or copy it.
        /// </summary>
        private void OpenLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = Services.Log.FilePath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    // open explorer with the file selected
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else
                {
                    MessageBox.Show(Properties.Resources.LogFileNotFound, "DupFree", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        // Open GitHub issue page in browser with a minimal template and log path.
        private void ReportIssueButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                const string repo = "multimccp00/DupFree";
                const string issueTitle = "Bug report";

                string bodyText = "";
                try
                {
                    if (!string.IsNullOrEmpty(Log.FilePath) && File.Exists(Log.FilePath))
                    {
                        var logContent = File.ReadAllText(Log.FilePath);
                        // truncate if excessively large
                        if (logContent.Length > 10000)
                            logContent = logContent[^10000..]; // last 10k chars
                        bodyText = "Log snippet:" + Environment.NewLine +
                                   "```" + Environment.NewLine +
                                   logContent + Environment.NewLine +
                                   "```";
                    }
                    else
                    {
                        bodyText = "Log file not available.";
                    }
                }
                catch
                {
                    bodyText = "(failed to read log)";
                }
                var body = Uri.EscapeDataString(bodyText);
                var titleEscaped = Uri.EscapeDataString(issueTitle);
                var url = $"https://github.com/{repo}/issues/new?title={titleEscaped}&body={body}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        // show window with output from `dotnet list package --outdated`
        private async void CheckDependenciesButton_Click(object sender, RoutedEventArgs e)
        {
            bool available = false;
            try
            {
                var psi = new ProcessStartInfo("dotnet", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    await p.WaitForExitAsync();
                    available = p.ExitCode == 0;
                }
            }
            catch { }

            if (!available)
            {
                MessageBox.Show(this, "The .NET CLI (dotnet) was not found in PATH. Cannot check dependencies.",
                    "Dependency Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string result;
            try
            {
                result = await Task.Run(() => RunDotnetListOutdated());
            }
            catch (Exception ex)
            {
                result = "Failed to execute command: " + ex;
            }

            var wnd = new DependencyWindow(result) { Owner = this };
            wnd.ShowDialog();
        }

        private static string RunDotnetListOutdated()
        {
            var psi = new ProcessStartInfo("dotnet", "list package --outdated")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
            };
            using var p = Process.Start(psi);
            if (p == null) throw new InvalidOperationException("Unable to start dotnet process.");
            var output = p.StandardOutput.ReadToEnd();
            output += "\n" + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private void UpdateSelectedCount()
        {
            int selectedCount;
            if (RecycleBinPanel != null && RecycleBinPanel.Visibility == Visibility.Visible && RecycleBinDataGrid != null)
            {
                selectedCount = RecycleBinDataGrid.SelectedItems.Count;
                DeleteSelectedButton.Content = $"Recover Selected ({selectedCount})";
            }
            else if (ResultsDataGrid != null && ResultsDataGrid.Visibility == Visibility.Visible)
            {
                selectedCount = ResultsDataGrid.SelectedItems.Count;
                DeleteSelectedButton.Content = $"Delete Selected ({selectedCount})";
            }
            else if (ResultsListView != null && ResultsListView.Visibility == Visibility.Visible)
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

        private List<FileItemViewModel> FilterFiles(IEnumerable<FileItemViewModel> files)
        {
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                return [.. files];
            }

            var query = _searchText.Trim();
            return [.. files.Where(f =>
                    (!string.IsNullOrEmpty(f.FileName) && f.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(f.FilePath) && f.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase)))];
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            TrySelectDirectories(autoScan: false);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public bool TrySelectDirectories(bool autoScan)
        {
            // use the CommonOpenFileDialog from the Windows API Code Pack because
            // it supports folder multiselect, which the Ookii/Vista dialog lacks.
            var dialog = new Microsoft.WindowsAPICodePack.Dialogs.CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Multiselect = true,
                EnsurePathExists = true,
                AllowNonFileSystemItems = false
            };

            if (dialog.ShowDialog() == Microsoft.WindowsAPICodePack.Dialogs.CommonFileDialogResult.Ok)
            {
                var paths = dialog.FileNames?.ToArray() ?? Array.Empty<string>();
                if (paths.Length == 0)
                {
                    BrowseButton.IsChecked = false;
                    return false;
                }

                // ensure all selected directories are accessible
                foreach (var p in paths)
                {
                    if (!DirectoryAccessAllowed(p))
                    {
                        var td = new Ookii.Dialogs.Wpf.TaskDialog
                        {
                            WindowTitle = "Permission Denied",
                            MainInstruction = "Cannot access one or more selected folders.",
                            Content = "Please check your permissions and try a different location.",
                            MainIcon = Ookii.Dialogs.Wpf.TaskDialogIcon.Warning
                        };
                        td.ShowDialog(this);

                        BrowseButton.IsChecked = false;
                        return false;
                    }
                }

                _selectedDirectories.Clear();
                _selectedDirectories.AddRange(paths);
                ScanButton.IsEnabled = true;

                if (_selectedDirectories.Count == 1)
                    StatusText.Text = $"Selected: {_selectedDirectories[0]}";
                else
                    StatusText.Text = $"Selected: {string.Join("; ", _selectedDirectories)}";

                SimilarImagesPanelControl.SetDirectories(_selectedDirectories);
                UpdateStorageIndicator();
                BrowseButton.IsChecked = false;

                if (autoScan && !_hasScannedOnce)
                {
                    _hasScannedOnce = true;
                    ScanButton_Click(ScanButton, new RoutedEventArgs());
                }

                return true;
            }
            else
            {
                // Uncheck the browse button if the dialog is canceled
                BrowseButton.IsChecked = false;
                return false;
            }
        }

        private bool DirectoryAccessAllowed(string path)
        {
            try
            {
                // attempt to enumerate a single entry
                Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch
            {
                // ignore other errors and assume accessible
                return true;
            }
        }
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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
            ScanProgressIndicator.Width = 0;
            ProgressPanel.Visibility = Visibility.Visible;
            ViewControlPanel.Visibility = Visibility.Collapsed;

            // Comprehensively clear all UI display elements
            ResultsPanel.Children.Clear();
            _realizedGridItems.Clear();  // Clear virtualized grid cache
            if (ResultsDataGrid != null) ResultsDataGrid.ItemsSource = null; // Clear data grid source
            if (ResultsListView != null) ResultsListView.ItemsSource = null; // Clear list view source
            NoResultsPlaceholder.Visibility = Visibility.Collapsed;  // Hide placeholder
            if (ResultsDataGrid != null) ResultsDataGrid.Visibility = Visibility.Collapsed;
            if (ResultsListView != null) ResultsListView.Visibility = Visibility.Collapsed;
            ResultsScrollViewer.Visibility = Visibility.Collapsed;

            // Clear data collections
            _groupViewModels.Clear(); // Clear previous scan results before starting new scan
            _currentGridFiles.Clear(); // Clear grid files as well
            _selectedGridItems.Clear();  // Clear any selections
            _lastSelectedGridItem = null;

            // Create cancellation token source for this scan (dispose previous if any)
            _scanCancellation?.Dispose();
            _scanCancellation = new System.Threading.CancellationTokenSource();

            // Progress callback updates UI with current/total progress
            var progress = new Progress<(int current, int total)>((p) =>
            {
                if (p.total > 0)
                {
                    double percentage = (p.current * 100.0) / p.total;
                    ProgressBar.Value = percentage;
                    UpdateScanProgressBar(percentage);
                    ProgressStatusText.Text = $"Scanning... {percentage:F0}%";
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

            Log.Info($"Scan complete: Found {duplicates.Count} groups from search service");

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

            Log.Info($"After adding to _groupViewModels: {_groupViewModels.Count} groups");

            ApplySorting();

            // Count total files
            int totalFiles = 0;
            foreach (var group in _groupViewModels)
                totalFiles += group.Files.Count;

            Log.Info($"Total files in all groups: {totalFiles}");

            // Clear grid selections after scan
            _selectedGridItems.Clear();
            UpdateDeleteCount();

            // Keep current view mode - don't reset to list
            DisplayResults();

            _isScanning = false;  // Mark scan as complete
            ScanButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 100;
            UpdateScanProgressBar(100);
            ProgressPanel.Visibility = Visibility.Collapsed;
            ViewControlPanel.Visibility = Visibility.Visible;
        }

        private void UpdateScanProgressBar(double percentage)
        {
            if (percentage < 0) percentage = 0;
            if (percentage > 100) percentage = 100;
            if (ScanProgressBarContainer != null && ScanProgressIndicator != null)
            {
                var totalWidth = ScanProgressBarContainer.ActualWidth;
                if (totalWidth <= 0)
                    totalWidth = ScanProgressBarContainer.Width;
                if (double.IsNaN(totalWidth) || totalWidth <= 0)
                    totalWidth = 150;
                var availableWidth = Math.Max(0, totalWidth - 2);
                ScanProgressIndicator.Width = availableWidth * (percentage / 100.0);
            }
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
            if (_selectedDirectories.Count == 0 || StorageIndicator == null || StorageText == null || StorageDriveText == null) return;

            try
            {
                // base our display on the first selected directory; this keeps the
                // indicator simple but adds a note when multiple folders are chosen.
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
                var driveRoot = driveInfo.Name.TrimEnd('\\');
                var volumeLabel = driveInfo.VolumeLabel;
                string driveDisplay = string.IsNullOrWhiteSpace(volumeLabel)
                    ? driveRoot
                    : $"{volumeLabel} ({driveRoot})";

                if (_selectedDirectories.Count > 1)
                {
                    StorageDriveText.Text = driveDisplay + $" (+{_selectedDirectories.Count - 1} more)";
                }
                else
                {
                    StorageDriveText.Text = driveDisplay;
                }

                StorageText.Text = $"{FormatFileSize(used)} used of {FormatFileSize(driveInfo.TotalSize)}";
            }
            catch { }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void DisplayResults()
        {
            Log.Info("DisplayResults: Entered method");

            // Count total files first
            int totalFiles = 0;
            foreach (var group in _groupViewModels)
            {
                totalFiles += group.Files.Count;
            }
            Log.Info($"DisplayResults: Total files: {totalFiles}");

            // Show/hide placeholder based on whether we have results
            if (totalFiles == 0)
            {
                Log.Info("DisplayResults: No results found, showing placeholder");
                NoResultsPlaceholder.Visibility = Visibility.Visible;
                ResultsDataGrid.Visibility = Visibility.Collapsed;
                ResultsListView.Visibility = Visibility.Collapsed;
                ResultsScrollViewer.Visibility = Visibility.Collapsed;
                UpdateSelectedCount();
                UpdateFooterStats();
                UpdateStorageIndicator();
                return;
            }
            else
            {
                NoResultsPlaceholder.Visibility = Visibility.Collapsed;
            }

            Log.Info($"DisplayResults: Current view mode: {_currentViewMode}");

            if (_currentViewMode == "list")
            {
                Log.Info("DisplayResults: Rendering list view");
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
                    foreach (var f in group.Files ?? Enumerable.Empty<FileItemViewModel>())
                    {
                        // Skip duplicates based on file path
                        if (!seenPathsListView.Add(f.FilePath))
                            continue;

                        f.DupCount = dupCount;
                        f.DupSpace = dupSpace;
                        flat.Add(f);
                    }
                }

                Log.Info($"DisplayResults: Flat list contains {flat.Count} items");

                // Apply duplicate limit from settings
                if (SettingsService.MaxDuplicatesToShow > 0 && flat.Count > SettingsService.MaxDuplicatesToShow)
                {
                    flat = [.. flat.Take(SettingsService.MaxDuplicatesToShow)];
                }

                var filtered = FilterFiles(flat);
                Log.Info($"DisplayResults: Filtered list contains {filtered.Count} items");

                // Use DataGrid for proper column binding
                ResultsDataGrid.ItemsSource = filtered;
                UpdateSelectedCount();

                StatusText.Text = $"Displaying {flat.Count} files in list view";
                UpdateFooterStats();
                UpdateStorageIndicator();
            }
            else if (_currentViewMode == "grid")
            {
                Log.Info("DisplayResults: Rendering grid view");
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

                Log.Info($"DisplayResults: Total groups: {_groupViewModels.Count}, Total flattened files: {allGridFiles.Count}");

                // Apply duplicate limit from settings
                if (SettingsService.MaxDuplicatesToShow > 0 && allGridFiles.Count > SettingsService.MaxDuplicatesToShow)
                {
                    allGridFiles = [.. allGridFiles.Take(SettingsService.MaxDuplicatesToShow)];
                }

                var filteredFiles = FilterFiles(allGridFiles);
                Log.Info($"DisplayResults: After filtering: {filteredFiles.Count} files");

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
                Log.Info($"DisplayResults: After dedup: {uniqueFiles.Count} files, now _currentGridFiles contains {_currentGridFiles.Count} files");
                UpdateSelectedCount();

                // For smaller sets, render with WrapPanel to avoid virtualization gaps
                if (_currentGridFiles.Count <= 1000)
                {
                    Log.Info("DisplayResults: Using WrapPanel for rendering");
                    _isVirtualGridActive = false;
                    ResultsScrollViewer.ScrollChanged -= ResultsScrollViewer_ScrollChanged;
                    ResultsScrollViewer.SizeChanged -= ResultsScrollViewer_SizeChanged;

                    ResultsPanel.Children.Clear();

                    var wrap = new WrapPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };

                    // Try to constrain wrap width immediately (may be 0 on first layout).
                    AdjustWrapPanelWidth(wrap);
                    ResultsPanel.Children.Add(wrap);

                    // Re-subscribe SizeChanged so the WrapPanel is updated when the ScrollViewer
                    // finally reports a valid ViewportWidth (prevents first-time overlap).
                    ResultsScrollViewer.SizeChanged -= ResultsScrollViewer_SizeChanged;
                    ResultsScrollViewer.SizeChanged += ResultsScrollViewer_SizeChanged;

                    // Local helper to populate the WrapPanel (kept before LayoutUpdated usage)
                    void addChildren()
                    {
                        // guard against double-add
                        if (wrap.Children.Count > 0 && wrap.Children.OfType<FrameworkElement>().Any(c => c.Tag is FileItemViewModel))
                            return;

                        Log.Info($"DisplayResults: About to add {_currentGridFiles.Count} items to WrapPanel");
                        int addedCount = 0;
                        foreach (var file in _currentGridFiles)
                        {
                            var element = GetViewModeCreateFunc()(file);
                            if (element != null)
                            {
                                wrap.Children.Add(element);
                                addedCount++;
                            }
                            else
                            {
                                Log.Info($"DisplayResults: Failed to create element for file: {file.FilePath}");
                            }
                        }
                        Log.Info($"DisplayResults: Successfully added {addedCount} items to WrapPanel");

                        // Force layout recalculation to fix overlapping/gaps after scan or view switch
                        wrap.InvalidateMeasure();
                        wrap.InvalidateArrange();
                        ResultsPanel.InvalidateMeasure();
                        ResultsPanel.InvalidateArrange();
                        ResultsScrollViewer.InvalidateMeasure();
                        ResultsScrollViewer.InvalidateArrange();
                        wrap.UpdateLayout();
                        ResultsPanel.UpdateLayout();
                        ResultsScrollViewer.UpdateLayout();
                    }

                    // One-time LayoutUpdated handler — run after WPF finishes the next layout pass
                    // and only populate the WrapPanel when we have a stable measured width. This avoids
                    // race conditions where children are added before the WrapPanel/ScrollViewer measure
                    // is valid (causes stacking/empty-space glitches).
                    bool childrenAdded = false;
                    void layoutUpdatedHandler(object? s, EventArgs ev)
                    {
                        try
                        {
                            double wrapWidth = ResultsScrollViewer.ViewportWidth;
                            if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                                wrapWidth = ResultsScrollViewer.ActualWidth;

                            if (!double.IsNaN(wrapWidth) && wrapWidth > 1)
                            {
                                AdjustWrapPanelWidth(wrap);

                                // populate children once we have a usable width
                                if (!childrenAdded)
                                {
                                    addChildren();
                                    childrenAdded = true;
                                }

                                ResultsScrollViewer.LayoutUpdated -= layoutUpdatedHandler;
                            }
                        }
                        catch
                        {
                            ResultsScrollViewer.LayoutUpdated -= layoutUpdatedHandler;
                        }
                    }

                    ResultsScrollViewer.LayoutUpdated += layoutUpdatedHandler;

                    // Fallback timer: if LayoutUpdated doesn't run with a valid width within 250ms,
                    // add children anyway (prevents indefinite delay on some systems).
                    var fallbackTimer = new System.Windows.Threading.DispatcherTimer(System.TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Normal, (ts, te) =>
                    {
                        var timer = ts as System.Windows.Threading.DispatcherTimer;
                        if (!childrenAdded)
                        {
                            AdjustWrapPanelWidth(wrap);
                            addChildren();
                            childrenAdded = true;
                        }
                        timer?.Stop();
                        ResultsScrollViewer.LayoutUpdated -= layoutUpdatedHandler;
                    }, Dispatcher);
                    fallbackTimer.Start();

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

                    StatusText.Text = $"Displaying {_currentGridFiles.Count} files (grid)";
                }
                else
                {
                    Log.Info("DisplayResults: Using virtualized grid for rendering");
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
            if (_currentViewMode == "grid")
            {
                // Compute virtual item size from configured grid picture size so rows/columns match WrapPanel items
                int pictureSize = SettingsService.GridPictureSize;
                // panel width = pictureSize + 40, add a small gap (12) between items when virtualized
                _virtualItemWidth = pictureSize + 40 + 12;
                _virtualItemHeight = pictureSize + (SettingsService.ShowGridFilePath ? 92 : 72) + 12;
            }
            else
            {
                int gridSize = SettingsService.GridPictureSize;
                _virtualItemWidth = gridSize + 36;  // size + panel padding + margins (reduced spacing)
                _virtualItemHeight = gridSize + (SettingsService.ShowGridFilePath ? 92 : 72); // adjust based on path display setting (reduced spacing)
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
                    AdjustWrapPanelWidth(wrap);
                }
            }
        }

        // Keep the recycle-bin wrap in sync with its ScrollViewer viewport
        private void RecycleBinScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (RecycleBinResultsPanel.Children.Count == 0)
                return;

            if (RecycleBinResultsPanel.Children[0] is WrapPanel wrap)
            {
                double wrapWidth = RecycleBinScrollViewer.ViewportWidth;
                if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                    wrapWidth = RecycleBinScrollViewer.ActualWidth;
                if (!double.IsNaN(wrapWidth) && wrapWidth > 0)
                {
                    wrap.Width = wrapWidth;
                    wrap.MinWidth = wrapWidth;
                    wrap.MaxWidth = wrapWidth;
                    wrap.InvalidateMeasure();
                    wrap.UpdateLayout();
                }
            }
        }

        private void AdjustWrapPanelWidth(WrapPanel wrap)
        {
            double wrapWidth = ResultsScrollViewer.ViewportWidth;
            if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                wrapWidth = ResultsScrollViewer.ActualWidth;
            if (!double.IsNaN(wrapWidth) && wrapWidth > 0)
            {
                wrap.Width = wrapWidth;
                wrap.MinWidth = wrapWidth;
                wrap.MaxWidth = wrapWidth;
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
                        // Ensure realized element uses the same fixed height as virtual item bucket so
                        // its ActualHeight won't change later and break canvas positioning.
                        fe.Width = _virtualItemWidth - 12; // account for small spacing used in measurements
                        fe.Height = _virtualItemHeight - 12; // keep consistent with virtual row height
                    }

                    int row = i / _virtualColumns;
                    int col = i % _virtualColumns;
                    Canvas.SetLeft(elem, col * _virtualItemWidth);
                    Canvas.SetTop(elem, row * _virtualItemHeight);

                    canvas.Children.Add(elem);
                    _realizedGridItems[i] = elem;
                }
            }

            // Reposition all currently-realized items (handles _virtualColumns changes / window resize)
            foreach (var kv in _realizedGridItems.ToList())
            {
                int idx = kv.Key;
                var child = kv.Value;
                int newRow = idx / _virtualColumns;
                int newCol = idx % _virtualColumns;
                Canvas.SetLeft(child, newCol * _virtualItemWidth);
                Canvas.SetTop(child, newRow * _virtualItemHeight);
            }

            Log.Info($"UpdateVirtualGrid: repositioned {_realizedGridItems.Count} realized items (cols={_virtualColumns}, itemW={_virtualItemWidth}, itemH={_virtualItemHeight})");
            Log.Info($"UpdateVirtualGrid: repositioned {_realizedGridItems.Count} realized items (cols={_virtualColumns}, itemW={_virtualItemWidth}, itemH={_virtualItemHeight})");

            // Detect any accidental overlapping positions among realized children (debugging aid)
            try
            {
                var posMap = new Dictionary<(int left, int top), List<int>>();
                foreach (var kv in _realizedGridItems)
                {
                    int idx = kv.Key;
                    var child = kv.Value;
                    int left = (int)Math.Round(Canvas.GetLeft(child));
                    int top = (int)Math.Round(Canvas.GetTop(child));
                    var key = (left, top);
                    if (!posMap.TryGetValue(key, out var list))
                    {
                        list = [];
                        posMap[key] = list;
                    }
                    list.Add(idx);
                }

                bool hadCollision = false;
                foreach (var kv in posMap.Where(k => k.Value.Count > 1))
                {
                    hadCollision = true;
                    var indices = kv.Value;
                    var files = indices.Select(i => (i < _currentGridFiles.Count ? _currentGridFiles[i].FileName : "<out-of-range>")).ToList();
                    Log.Info($"UpdateVirtualGrid: COLLISION at ({kv.Key.left},{kv.Key.top}) -> idx=[{string.Join(',', indices)}], files=[{string.Join(',', files)}]");
                    Log.Info($"UpdateVirtualGrid: COLLISION at ({kv.Key.left},{kv.Key.top}) -> idx=[{string.Join(',', indices)}], files=[{string.Join(',', files)}]");
                }

                // Automatic recovery if collision detected — rebuild the visible canvas region
                if (hadCollision)
                {
                    Log.Info("UpdateVirtualGrid: collision detected — rebuilding visible canvas range to recover");
                    Log.Info("UpdateVirtualGrid: collision detected — rebuilding visible canvas range to recover");

                    foreach (var kv in _realizedGridItems.ToList())
                    {
                        if (kv.Value != null && canvas.Children.Contains(kv.Value))
                            canvas.Children.Remove(kv.Value);
                    }
                    _realizedGridItems.Clear();

                    for (int i = startIndex; i <= endIndex; i++)
                    {
                        if (i < 0 || i >= _currentGridFiles.Count)
                            continue;

                        var elem = GetViewModeCreateFunc()(_currentGridFiles[i]);
                        if (elem is FrameworkElement fe)
                        {
                            fe.Margin = new Thickness(0);
                            fe.Width = _virtualItemWidth - 12;
                            fe.Height = _virtualItemHeight - 12;
                        }

                        int row = i / _virtualColumns;
                        int col = i % _virtualColumns;
                        Canvas.SetLeft(elem, col * _virtualItemWidth);
                        Canvas.SetTop(elem, row * _virtualItemHeight);

                        canvas.Children.Add(elem);
                        _realizedGridItems[i] = elem;
                    }
                }
            }
            catch { }

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

        private Func<FileItemViewModel, FrameworkElement> GetViewModeCreateFunc()
        {
            if (_currentViewMode == "grid")
                return CreateGridIconView;
            else
                return CreateIconView;
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
            var delFolder = new MenuItem { Header = "Delete all duplicates in this folder", Tag = file };
            delFolder.Click += OnDeleteAllDuplicatesInFolderMenuItem_Click;
            cm.Items.Add(delFolder);
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
                Margin = new Thickness(4),  // Further reduced margin to tighten layout
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
                var clickedFile = clickedBorder?.Tag as FileItemViewModel;

                // Guard: tag may be unexpectedly null or of the wrong type
                if (clickedFile == null)
                    return;

                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool isShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                // Capture field into a local so the nullable state is tracked by the compiler
                var lastSelected = _lastSelectedGridItem;

                if (isShiftPressed && lastSelected != null)
                {
                    int lastIndex = _currentGridFiles.IndexOf(lastSelected);
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

                _selectedGridIndex = _currentGridFiles.IndexOf(clickedFile);
                _lastSelectedGridItem = clickedFile;
                UpdateDeleteCount();
                ResultsPanel.Focus();
                e.Handled = true;
            };

            return border;
        }

        private FrameworkElement CreateGridIconView(FileItemViewModel file)
        {
            int pictureSize = SettingsService.GridPictureSize;
            var panel = new StackPanel
            {
                Width = pictureSize + 40,                   // narrower container based on user size
                Height = pictureSize + (SettingsService.ShowGridFilePath ? 92 : 72), // match virtualized item height (element height only)
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

            // Use the configured grid picture size for the preview so layout stays consistent with settings
            panel.Children.Add(CreatePreviewElement(file, pictureSize));

            var nameBlock = new TextBlock
            {
                Text = file.FileName,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            var sizeBlock = new TextBlock
            {
                Text = file.SizeFormatted,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var pathBlock = new TextBlock
            {
                Text = file.FilePath,
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
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
            var delFolder = new MenuItem { Header = "Delete all duplicates in this folder", Tag = file };
            delFolder.Click += OnDeleteAllDuplicatesInFolderMenuItem_Click;
            cm.Items.Add(delFolder);
            panel.ContextMenu = cm;

            // Wrap in border for selection highlighting
            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Tag = file,
                Margin = new Thickness(4),  // Further reduced margin to tighten layout
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
                var clickedFile = clickedBorder?.Tag as FileItemViewModel;

                if (clickedFile == null)
                    return;

                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool isShiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                var lastSelected = _lastSelectedGridItem;

                if (isShiftPressed && lastSelected != null)
                {
                    int lastIndex = _currentGridFiles.IndexOf(lastSelected);
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

                // Track the selected index in the grid so deletion restores the closest item
                _selectedGridIndex = _currentGridFiles.IndexOf(clickedFile);

                _lastSelectedGridItem = clickedFile;
                UpdateDeleteCount();
                ResultsPanel.Focus();
                e.Handled = true;
            };

            // Hover highlight — pale box on mouse over (like Windows Explorer)
            border.MouseEnter += (s, e) =>
            {
                try
                {
                    if (!_selectedGridItems.Contains(file))
                    {
                        border.Background = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246));
                        border.BorderThickness = new Thickness(1);
                        border.BorderBrush = new SolidColorBrush(Color.FromArgb(120, 59, 130, 246));
                    }
                }
                catch { }
            };

            border.MouseLeave += (s, e) =>
            {
                try
                {
                    if (!_selectedGridItems.Contains(file))
                    {
                        border.Background = new SolidColorBrush(Colors.Transparent);
                        border.BorderThickness = new Thickness(0);
                        border.BorderBrush = null;
                    }
                }
                catch { }
            };

            return border;
        }

        private FrameworkElement CreatePreviewElement(FileItemViewModel file, double size)
        {
            var grid = new Grid
            {
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = System.Windows.Media.Brushes.Transparent // required for mouse hit-testing when all children have IsHitTestVisible=false
            };

            var placeholder = new TextBlock
            {
                Text = "📄",
                FontSize = Math.Max(18, size * 0.6),
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            grid.Children.Add(placeholder);

            // Let the image stretch uniformly but reserve a small top/bottom padding
            // so tall (vertical) images don't visually touch the item's border/line.
            var image = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10), // 10px top & bottom padding
                IsHitTestVisible = false
            };

            // Bind to the thumbnail so updates propagate even if loaded later
            var binding = new Binding("Thumbnail") { Source = file };
            image.SetBinding(Image.SourceProperty, binding);



            // Hover-to-play for animated GIF/WebP (swap source on hover). If the animated image
            // hasn't been prepared yet, decode it on-demand in a background task.
            var ext = Path.GetExtension(file.FilePath).ToLower();
            bool isAnimatedFile = ext == ".gif" || (ext == ".webp" && Services.ImagePreviewService.IsAnimatedWebP(file.FilePath));
            CancellationTokenSource? gifAnimCts = null;
            bool animationActive = false;
            bool gridLoaded = false; // tracks whether grid is in visual tree
            bool autoPlayStarted = false; // prevents double auto-play trigger

            // Exposed starters so we can also trigger auto-play on load
            Func<bool, Task> startAnimatedPreview = (_) => Task.CompletedTask;
            Func<bool, Task> startVideoPreview = (_) => Task.CompletedTask;

            // Safety net: if hover events are missed by WPF hit-testing in complex templates,
            // a direct interaction still attempts preview start.
            grid.PreviewMouseLeftButtonDown += async (_, __) =>
            {
                await startAnimatedPreview(true);
                await startVideoPreview(true);
            };

            bool IsTileInViewport()
            {
                // In the Canvas-based virtual grid, TransformToAncestor produces unreliable
                // coordinates because tiles are positioned via Canvas.SetLeft/SetTop on a Canvas
                // that is sized to the full content height.  The virtualisation logic already
                // guarantees that only visible tiles are realised (gridLoaded == true), and
                // grid.Unloaded fires when a tile scrolls out, so this simple check is sufficient.
                return gridLoaded && grid.IsLoaded;
            }

            async Task EnsureAnimatedPreviewAsync(bool skipViewportCheck = false)
            {
                if (!isAnimatedFile)
                    return;

                // Don't start animation on a grid that has been virtualized away
                if (!gridLoaded)
                    return;

                // Don't animate tiles outside the visible viewport.
                // Skip this check for autoplay-on-load since virtualization already ensures visibility.
                if (!skipViewportCheck && !IsTileInViewport())
                    return;

                // If a manual frame animation loop is already running, don't start another
                if (gifAnimCts != null && !gifAnimCts.IsCancellationRequested)
                    return;

                // Prevent re-entrancy if animation is already active
                if (animationActive)
                    return;

                animationActive = true;
                try
                {
                    Log.Info($"Start animated preview -> {file.FilePath}; animated present={file.AnimatedThumbnail != null}");

                    if (ext == ".gif")
                    {
                        // prefer cached manual frames for GIFs (more reliable than WPF/stream decoder)
                        if (file.AnimatedFrames != null && file.AnimatedFrames.Length > 0)
                        {
                            {
                                var localCts1 = new CancellationTokenSource();
                                gifAnimCts = localCts1;
                                var frames1 = file.AnimatedFrames;
                                if (frames1 == null || frames1.Length == 0)
                                {
                                    animationActive = false;
                                    return;
                                }
                                var delays1 = file.AnimatedFrameDelays;
                                if (delays1 == null || delays1.Length == 0)
                                    delays1 = [.. Enumerable.Repeat(80, frames1.Length)];
                                int fi1 = 0;
                                image.Source = frames1[0];
                                var animTimer1 = new System.Windows.Threading.DispatcherTimer(
                                    System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                                {
                                    Interval = TimeSpan.FromMilliseconds(Math.Max(10, delays1[0]))
                                };
                                animTimer1.Tick += (ts, te) =>
                                {
                                    if (localCts1.IsCancellationRequested || !gridLoaded)
                                    {
                                        animTimer1.Stop();
                                        gifAnimCts = null;
                                        animationActive = false;
                                        try { image.Source = file.Thumbnail; if (ext == ".gif") { try { file.AnimatedThumbnail = null; } catch { } } } catch { }
                                        return;
                                    }
                                    fi1 = (fi1 + 1) % frames1.Length;
                                    try { image.Source = frames1[fi1]; } catch { }
                                    animTimer1.Interval = TimeSpan.FromMilliseconds(Math.Max(10, delays1.Length > 0 ? delays1[fi1 % delays1.Length] : 80));
                                };
                                animTimer1.Start();
                            }

                            return;
                        }
                    }
                    else if (file.AnimatedThumbnail != null)
                    {
                        // re-assign to force animation start
                        image.Source = null;
                        await Task.Yield();
                        image.Source = file.AnimatedThumbnail;
                        return;
                    }

                    // For non-GIF (animated WebP): load bytes and create animated BitmapImage.
                    // For GIF: skip the bytes/URI paths — WPF's BitmapImage does NOT animate GIFs
                    // when assigned via code-behind (only the first frame is shown). Always use the
                    // manual frame timer below for GIF.
                    if (ext != ".gif")
                    {
                        bool trackingHoverLoad = TryBeginThumbnailLoad(file.FilePath);
                        byte[]? bytes;
                        try
                        {
                            bytes = await Task.Run(() => Services.ImagePreviewService.GetAnimatedImageBytes(file.FilePath, (int)size, (int)size));
                        }
                        finally
                        {
                            if (trackingHoverLoad) EndThumbnailLoad(file.FilePath);
                        }

                        if (bytes != null)
                        {
                            try
                            {
                                var animatedBm = Services.ImagePreviewService.CreateBitmapImageFromBytes(bytes, (int)size, freeze: false);
                                Dispatcher.Invoke(() =>
                                {
                                    file.AnimatedThumbnail = animatedBm;
                                    image.Source = null; // force swap/reload
                                    image.Source = file.AnimatedThumbnail;
                                    Log.Info($"Applied animated thumbnail for {file.FilePath} (from bytes)");
                                });
                                return;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex);
                            }
                        }
                    }

                    Log.Info($"No bytes-based animated preview for {file.FilePath} — using manual frame timer");

                    // Manual frame animation fallback (Magick -> frame PNGs -> dispatcher timer loop).
                    {
                        var localCts2 = new CancellationTokenSource();
                        gifAnimCts = localCts2;
                        bool timerStarted = false;
                        try
                        {
                            // Track this load so delete waits for the Magick.NET file handle to be released.
                            bool trackingFrameLoad = TryBeginThumbnailLoad(file.FilePath);
                            (System.Windows.Media.Imaging.BitmapSource[] Frames, int[] Delays) frameResult;
                            try
                            {
                                frameResult = await Task.Run(() => Services.ImagePreviewService.GetAnimatedFrames(file.FilePath, (int)size, (int)size));
                            }
                            finally
                            {
                                if (trackingFrameLoad) EndThumbnailLoad(file.FilePath);
                            }
                            var (Frames, Delays) = frameResult;
                            if (Frames != null && Frames.Length > 0 && !localCts2.IsCancellationRequested && gridLoaded)
                            {
                                Log.Info($"Manual GIF animator: frames={Frames.Length} for {file.FilePath}");
                                int fi2 = 0;
                                image.Source = Frames[0];
                                var animTimer2 = new System.Windows.Threading.DispatcherTimer(
                                    System.Windows.Threading.DispatcherPriority.Background, Dispatcher)
                                {
                                    Interval = TimeSpan.FromMilliseconds(Math.Max(10, Delays.Length > 0 ? Delays[0] : 80))
                                };
                                animTimer2.Tick += (ts, te) =>
                                {
                                    if (localCts2.IsCancellationRequested || !gridLoaded)
                                    {
                                        animTimer2.Stop();
                                        gifAnimCts = null;
                                        animationActive = false;
                                        try { image.Source = file.Thumbnail; if (ext == ".gif") { try { file.AnimatedThumbnail = null; } catch { } } } catch { }
                                        return;
                                    }
                                    fi2 = (fi2 + 1) % Frames.Length;
                                    try { image.Source = Frames[fi2]; } catch { }
                                    animTimer2.Interval = TimeSpan.FromMilliseconds(Math.Max(10, Delays.Length > 0 ? Delays[fi2 % Delays.Length] : 80));
                                };
                                animTimer2.Start();
                                timerStarted = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex);
                        }
                        finally
                        {
                            if (!timerStarted)
                            {
                                gifAnimCts = null;
                                // animationActive reset is handled by the outer finally
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex);
                }
                finally
                {
                    // Only reset if no DispatcherTimer animation is running.
                    // Active timers hold a non-cancelled gifAnimCts so this correctly
                    // leaves animationActive=true while a timer is ticking.
                    if (gifAnimCts == null || gifAnimCts.IsCancellationRequested)
                        animationActive = false;
                }
            }

            // assign so it can be invoked from grid.Loaded (for auto-play)
            startAnimatedPreview = (skip) => EnsureAnimatedPreviewAsync(skip);

            if (isAnimatedFile)
            {
                // Register a release action so deletion can free the file lock before the OS-level delete.
                // The action captures gifAnimCts by reference (closure) so it always cancels the live token.
                _mediaReleaseActions[file.FilePath] = () =>
                {
                    try { gifAnimCts?.Cancel(); } catch { }
                    gifAnimCts = null;
                    animationActive = false;
                    try { image.Source = null; } catch { }
                    try { file.AnimatedThumbnail = null; } catch { }
                    // Clear decoded frame cache so Magick.NET bitmaps are released
                    try { file.AnimatedFrames = []; } catch { }
                };

                grid.MouseEnter += async (_, __) => await EnsureAnimatedPreviewAsync();
                grid.PreviewMouseMove += async (_, __) => await EnsureAnimatedPreviewAsync();

                grid.MouseLeave += (_, __) =>
                {
                    // If the user has opted to auto-play previews, do not stop playback on mouse leave.
                    if (SettingsService.GetAutoPlayAnimatedPreviews())
                        return;

                    try
                    {
                        // cancel any manual animator and clear GIF-native animated cache so the tile returns to static state
                        gifAnimCts?.Cancel();
                        gifAnimCts = null;
                        animationActive = false;
                        if (ext == ".gif")
                        {
                            try { file.AnimatedThumbnail = null; } catch { }
                        }
                        image.Source = file.Thumbnail;
                        Log.Info($"MouseLeave(animated) -> {file.FilePath}");
                    }
                    catch { }
                };

                // Ensure we stop animation when the item is unloaded (e.g. virtualized away)
                grid.Unloaded += (_, __) =>
                {
                    gridLoaded = false;
                    autoPlayStarted = false;
                    _mediaReleaseActions.Remove(file.FilePath);
                    try
                    {
                        gifAnimCts?.Cancel();
                        gifAnimCts = null;
                        if (ext == ".gif")
                        {
                            try { file.AnimatedThumbnail = null; } catch { }
                        }
                        animationActive = false;
                    }
                    catch { }
                };
            }

            grid.Children.Add(image);

            // --- Video hover preview (lightweight) ---
            MediaElement? media = null;

            if (Services.ImagePreviewService.IsVideoFile(file.FilePath))
            {
                media = new MediaElement
                {
                    LoadedBehavior = MediaState.Manual,
                    UnloadedBehavior = MediaState.Manual,
                    IsMuted = true,
                    Stretch = System.Windows.Media.Stretch.UniformToFill,
                    Visibility = Visibility.Collapsed,
                    Width = size,
                    Height = size,
                    IsHitTestVisible = false,
                    // Do not set Source here to avoid preloading large files — set on first hover.
                };

                // place media on top of the static image
                grid.Children.Add(media);

                bool videoPreviewActive = false;
                bool videoFailed = false;
                bool videoOpenedSinceStart = false;
                int videoStartGeneration = 0;
                int videoAutoPlayRetryCount = 0;
                Action? myStopAction = null;

                // Loop video when it ends
                media.MediaEnded += (_, __) =>
                {
                    try
                    {
                        if (gridLoaded && media.Visibility == Visibility.Visible)
                        {
                            media.Position = TimeSpan.Zero;
                            media.Play();
                        }
                    }
                    catch { }
                };

                // Media events for diagnostics and UI fallback
                media.MediaOpened += (_, __) =>
                {
                    videoOpenedSinceStart = true;
                    videoFailed = false;
                    videoAutoPlayRetryCount = 0;
                    Log.Info($"MediaOpened: {file.FilePath} naturalDuration={media.NaturalDuration}");

                    // If the tile was evicted before WMF finished opening (e.g. via the
                    // autoplay eviction cascade), stop the background pipeline and bail.
                    // Without this, the video plays invisibly forever.
                    if (!videoPreviewActive)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { media.Source = null; } catch { }
                        }), System.Windows.Threading.DispatcherPriority.Background);
                        return;
                    }

                    // Ensure the element is visible (may have been collapsed by a prior
                    // eviction that was then reversed by a re-start before MediaOpened fired).
                    media.Visibility = Visibility.Visible;
                    try { media.Position = TimeSpan.Zero; } catch { }
                };
                media.MediaFailed += (_, e) =>
                {
                    Log.Error($"MediaFailed: {file.FilePath} - {e.ErrorException?.Message ?? e.ErrorException?.ToString()}");
                    videoFailed = true; // block immediate tight retry loops on bad files
                    try
                    {
                        // Ensure counters and active flags are released after a failed open/play.
                        StopVideoPreview();

                        // Hide the media control.  Do NOT call media.Source = null synchronously — it can
                        // hang the UI thread if the WMF pipeline is in a bad state.
                        media.Visibility = Visibility.Collapsed;

                        // Schedule async source reset so a future retry can re-open cleanly.
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { if (!videoPreviewActive) media.Source = null; } catch { }
                        }), System.Windows.Threading.DispatcherPriority.Background);

                        // Fetch a poster thumbnail on a background thread so the UI stays responsive.
                        var sz = (int)size;
                        var fp = file.FilePath;
                        Task.Run(() =>
                        {
                            try
                            {
                                var poster = Services.ImagePreviewService.GetVideoThumbnail(fp, sz, sz);
                                if (poster != null)
                                {
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        file.Thumbnail = poster;
                                        image.Source = poster;
                                    }));
                                }
                            }
                            catch { }
                        });
                    }
                    catch { }
                };

                void StopVideoPreview()
                {
                    if (!videoPreviewActive)
                        return;
                    // Do NOT call media.Pause() — it sends a synchronous command to the
                    // WMF pipeline and can hang the UI thread indefinitely when the
                    // pipeline is in a transitional state (opening/buffering/seeking).
                    // IsMuted=true prevents audio leak.  Collapse hides the visual
                    // immediately.  Source=null in grid.Unloaded (BeginInvoke) shuts
                    // WMF down without blocking.
                    try { media.Visibility = Visibility.Collapsed; } catch { }
                    Log.Info($"Media stopped for {file.FilePath}");
                    _activeVideoPreviews = Math.Max(0, _activeVideoPreviews - 1);
                    if (myStopAction != null) _videoPreviewStoppers.Remove(myStopAction);
                    videoPreviewActive = false;
                    videoOpenedSinceStart = false;
                }
                myStopAction = StopVideoPreview;

                Task EnsureVideoPreviewAsync(bool skipViewportCheck = false)
                {
                    Log.Info($"[VideoDbg] {Path.GetFileName(file.FilePath)}: skip={skipViewportCheck} active={videoPreviewActive} failed={videoFailed} loaded={gridLoaded} isLoaded={grid.IsLoaded}");
                    if (videoPreviewActive || videoFailed)
                    {
                        Log.Info($"[VideoDbg] SKIP: already active or failed");
                        return Task.CompletedTask;
                    }

                    // Don't start video preview on a grid that has been virtualized away
                    if (!gridLoaded)
                    {
                        Log.Info($"[VideoDbg] SKIP: gridLoaded=false");
                        return Task.CompletedTask;
                    }

                    // Don't play videos outside the viewport.
                    // Skip this check for autoplay-on-load since virtualization already ensures visibility.
                    if (!skipViewportCheck && !IsTileInViewport())
                    {
                        Log.Info($"[VideoDbg] SKIP: not in viewport");
                        return Task.CompletedTask;
                    }

                    Log.Info($"Start video preview -> {file.FilePath}; active={_activeVideoPreviews}");

                    // When autoplay is enabled ALL visible tiles should play — the virtual
                    // grid's grid.Unloaded handles cleanup when tiles scroll out of view.
                    // Only enforce the cap in hover mode to cap resource usage.
                    if (!SettingsService.GetAutoPlayAnimatedPreviews())
                    {
                        while (_videoPreviewStoppers.Count >= MaxConcurrentVideoPreviews && _videoPreviewStoppers.Count > 0)
                        {
                            var oldest = _videoPreviewStoppers[0];
                            _videoPreviewStoppers.RemoveAt(0); // remove before invoking to avoid re-entry
                            try { oldest(); } catch { }
                        }
                    }

                    videoPreviewActive = true;
                    videoOpenedSinceStart = false;
                    var startGen = ++videoStartGeneration;
                    _activeVideoPreviews++;
                    _videoPreviewStoppers.Add(myStopAction!);
                    try
                    {
                        // set Source lazily to avoid preloading every video
                        if (media.Source == null)
                        {
                            media.Source = new Uri(file.FilePath, UriKind.Absolute);
                            Log.Info($"Media.Source assigned for {file.FilePath}");
                        }

                        media.Visibility = Visibility.Visible;
                        media.Play();
                        Log.Info($"Media.Play invoked for {file.FilePath}");

                        // If WMF never opens (no MediaOpened), clear stale active state and perform
                        // one auto-play retry for transient pipeline stalls.
                        _ = Task.Delay(5000).ContinueWith(_ =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (startGen == videoStartGeneration && videoPreviewActive && !videoOpenedSinceStart)
                                    {
                                        Log.Info($"[VideoDbg] Watchdog reset (no MediaOpened) for {file.FilePath}");
                                        StopVideoPreview();
                                        try { media.Source = null; } catch { }
                                        videoFailed = false;

                                        if (SettingsService.GetAutoPlayAnimatedPreviews()
                                            && gridLoaded
                                            && videoAutoPlayRetryCount < 1)
                                        {
                                            videoAutoPlayRetryCount++;
                                            _ = EnsureVideoPreviewAsync(true);
                                        }
                                    }
                                }
                                catch { }
                            }), System.Windows.Threading.DispatcherPriority.Background);
                        });
                        // Video loops via MediaEnded handler. Cleanup via StopVideoPreview on mouse leave / unload.
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                        videoFailed = true;
                        StopVideoPreview();
                    }
                    return Task.CompletedTask;
                }

                // assign so it can be invoked from grid.Loaded (for auto-play)
                startVideoPreview = (skip) => EnsureVideoPreviewAsync(skip);

                ScrollChangedEventHandler? viewportStopper = null;
                viewportStopper = (_, __) =>
                {
                    // When autoplay is enabled, let grid.Unloaded handle cleanup instead.
                    // IsTileInViewport() is unreliable in the Canvas-based virtual grid and
                    // kills animations on any layout-triggered ScrollChanged (e.g. selection highlight).
                    if (SettingsService.GetAutoPlayAnimatedPreviews())
                        return;

                    if (!IsTileInViewport())
                    {
                        gifAnimCts?.Cancel();
                        StopVideoPreview();
                    }
                };
                ResultsScrollViewer.ScrollChanged += viewportStopper;

                // Register a release action so deletion can free the file lock before
                // the OS-level delete.  CRITICAL: Must be registered AFTER viewportStopper
                // so we can unsubscribe it and break the reference chain that keeps the
                // MediaElement alive (ResultsScrollViewer → viewportStopper → closure → media).
                _mediaReleaseActions[file.FilePath] = () =>
                {
                    bool wasActive = videoPreviewActive;
                    if (wasActive)
                    {
                        videoPreviewActive = false;
                        _activeVideoPreviews = Math.Max(0, _activeVideoPreviews - 1);
                        if (myStopAction != null) _videoPreviewStoppers.Remove(myStopAction);
                    }
                    videoFailed = true; // prevent any restart
                    // Unsubscribe viewport stopper to break the reference chain from
                    // the long-lived ResultsScrollViewer → closure → MediaElement.
                    // Without this, the MediaElement can never be GC'd and the WMF
                    // pipeline keeps the file handle open indefinitely.
                    if (viewportStopper != null)
                    {
                        try { ResultsScrollViewer.ScrollChanged -= viewportStopper; } catch { }
                    }
                    // Keep MediaElement in the visual tree; just clear Source asynchronously.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { media.Source = null; } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };

                grid.MouseEnter += async (_, __) =>
                {
                    await EnsureVideoPreviewAsync();
                };
                grid.PreviewMouseMove += async (_, __) =>
                {
                    await EnsureVideoPreviewAsync();
                };

                grid.MouseLeave += (_, __) =>
                {
                    // If the user has opted to auto-play previews, do not stop playback on mouse leave.
                    if (SettingsService.GetAutoPlayAnimatedPreviews())
                        return;

                    StopVideoPreview();
                };

                // Ensure we stop playback when the item is unloaded (e.g. virtualized away)
                grid.Unloaded += (_, __) =>
                {
                    gridLoaded = false; // needed so media.Source=null fires in BeginInvoke below
                    _mediaReleaseActions.Remove(file.FilePath);
                    StopVideoPreview();
                    videoFailed = false; // allow retry on next load
                    videoAutoPlayRetryCount = 0;
                    if (viewportStopper != null)
                    {
                        try { ResultsScrollViewer.ScrollChanged -= viewportStopper; } catch { }
                    }
                    // Release the WMF file handle asynchronously at Background priority so we
                    // don't block the UI thread (WMF shutdown can take time). We do NOT remove
                    // media from grid.Children here because the grid object is reused when the
                    // tile scrolls back into view — removing it would prevent the video from
                    // ever rendering again on subsequent hover.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { if (!gridLoaded) media.Source = null; } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                };
            }

            if (file.Thumbnail != null)
                placeholder.Visibility = Visibility.Collapsed;

            PropertyChangedEventManager.AddHandler(file, (_, args) =>
            {
                if (args.PropertyName == nameof(FileItemViewModel.Thumbnail) && file.Thumbnail != null)
                {
                    placeholder.Visibility = Visibility.Collapsed;
                }
            }, nameof(FileItemViewModel.Thumbnail));

            // When animated frames become available (populated by EnsureThumbnailAsync), auto-play if enabled.
            // Use a direct PropertyChanged subscription — PropertyChangedEventManager holds lambda closures
            // weakly and may drop them before EnsureThumbnailAsync completes.
            if (isAnimatedFile)
            {
                PropertyChangedEventHandler autoPlayFramesHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(FileItemViewModel.AnimatedFrames)
                        && file.AnimatedFrames?.Length > 0
                        && SettingsService.GetAutoPlayAnimatedPreviews()
                        && gridLoaded && !autoPlayStarted)
                    {
                        autoPlayStarted = true;
                        _ = startAnimatedPreview(true);
                    }
                };
                file.PropertyChanged += autoPlayFramesHandler;
                // Unsubscribe when tile is unloaded to prevent a reference cycle.
                grid.Unloaded += (_, __) => file.PropertyChanged -= autoPlayFramesHandler;
            }

            grid.Loaded += async (_, __) =>
            {
                gridLoaded = true;

                // allow video thumbnails and image thumbnails to be prepared here
                if (!file.IsPreviewable && !Services.ImagePreviewService.IsVideoFile(file.FilePath))
                    return;

                // For static images we populate thumbnails asynchronously.
                // For animated files: also ensure frames are extracted even if a static thumbnail
                // already exists (FromFileInfo pre-loads a static thumbnail, so Thumbnail is non-null,
                // but AnimatedFrames won't be populated yet — without this call, autoplay never starts).
                bool needsAnimatedFrames = isAnimatedFile && (file.AnimatedFrames == null || file.AnimatedFrames.Length == 0);
                if ((file.Thumbnail == null || needsAnimatedFrames) && !Services.ImagePreviewService.IsVideoFile(file.FilePath))
                {
                    EnsureThumbnailAsync(file, placeholder, (int)size);
                }

                // For video files try to obtain a shell/poster thumbnail (non-blocking).
                // Track the load in _thumbnailLoading so deletion can wait for it to finish
                // before attempting an OS-level delete (prevents native crash from Windows Shell
                // reading a file handle that's been invalidated mid-extraction).
                if (Services.ImagePreviewService.IsVideoFile(file.FilePath) && file.Thumbnail == null)
                {
                    if (TryBeginThumbnailLoad(file.FilePath))
                    {
                        Task.Run(() =>
                        {
                            try
                            {
                                var poster = Services.ImagePreviewService.GetVideoThumbnail(file.FilePath, (int)size, (int)size);
                                if (poster != null)
                                {
                                    // BeginInvoke instead of Invoke — avoids blocking this thread
                                    // while the UI thread is in ReleaseMediaLockAsync's polling loop.
                                    Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        file.Thumbnail = poster;
                                        placeholder.Visibility = Visibility.Collapsed;
                                    }));
                                }
                            }
                            catch { }
                            finally { EndThumbnailLoad(file.FilePath); }
                        });
                    }
                }

                // for video files we intentionally do not pre-load video content here
                // to avoid heavy I/O — media Source is assigned lazily on hover.

                // If the user has enabled auto-play previews, trigger the playback logic once when the tile is loaded.
                if (SettingsService.GetAutoPlayAnimatedPreviews())
                {
                    // Defer to allow WPF to finish layout.
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        // For animated files: only start immediately if frames are already cached.
                        // If not (the common case on first load), the PropertyChanged(AnimatedFrames)
                        // handler will trigger startAnimatedPreview once EnsureThumbnailAsync populates them.
                        // Starting before frames are ready causes EnsureAnimatedPreviewAsync to take the
                        // wrong fallback path (bytes/URI) which doesn't actually animate GIFs, and sets
                        // autoPlayStarted=true which then blocks the PropertyChanged retry.
                        if (isAnimatedFile && !autoPlayStarted && file.AnimatedFrames != null && file.AnimatedFrames.Length > 0)
                        {
                            autoPlayStarted = true;
                            await startAnimatedPreview(true);
                        }

                        // Video (if any)
                        await startVideoPreview(true);

                        // Some WMF/MediaElement pipelines are not immediately ready at grid load.
                        // Issue one delayed retry for video tiles if autoplay is still not active.
                        if (Services.ImagePreviewService.IsVideoFile(file.FilePath))
                        {
                            _ = Task.Delay(350).ContinueWith(_ =>
                            {
                                Dispatcher.BeginInvoke(new Action(async () =>
                                {
                                    if (gridLoaded)
                                    {
                                        await startVideoPreview(true);
                                    }
                                }), System.Windows.Threading.DispatcherPriority.Background);
                            });
                        }
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };

            return grid;
        }

        private bool TryBeginThumbnailLoad(string filePath)
        {
            lock (_thumbnailLoading)
            {
                return _thumbnailLoading.Add(filePath);
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
            // Acquire the semaphore FIRST so _thumbnailLoading only tracks files that are
            // ACTIVELY being decoded by Magick.NET/Shell, not those waiting in queue.
            // This keeps ReleaseMediaLockAsync's wait tight (decode time only, not queue time).
            await _thumbnailSemaphore.WaitAsync();

            // Deduplicate: if another concurrent call already started this file, bail.
            if (!TryBeginThumbnailLoad(file.FilePath))
            {
                _thumbnailSemaphore.Release();
                return;
            }

            try
            {
                // Guard: abort if the file was deleted between tile loading and getting the slot.
                if (!File.Exists(file.FilePath))
                    return;

                var ext = Path.GetExtension(file.FilePath).ToLower();

                // Animated GIF or animated WebP — create a static first-frame for default display
                // and also prepare an unfrozen animated BitmapImage for hover-play.
                if (ext == ".gif" || (ext == ".webp" && Services.ImagePreviewService.IsAnimatedWebP(file.FilePath)))
                {
                    Log.Info($"EnsureThumbnailAsync START (animated): {file.FilePath}");
                    // Extract frames once — this gives us both the static first frame and animation data.
                    var (Frames, Delays) = await Task.Run(() => Services.ImagePreviewService.GetAnimatedFrames(file.FilePath, size, size));
                    // Only fetch raw animated bytes if frame extraction failed (fallback for AnimatedThumbnail).
                    byte[]? animatedBytes = null;
                    BitmapImage? firstFrame = (Frames != null && Frames.Length > 0) ? Frames[0] as BitmapImage : null;
                    if (firstFrame == null && Frames != null && Frames.Length > 0)
                    {
                        // Frame exists but isn't BitmapImage — create one from first frame bytes
                        firstFrame = await Task.Run(() => Services.ImagePreviewService.GetFirstFrameBitmap(file.FilePath, size, size));
                    }
                    if (firstFrame == null)
                    {
                        animatedBytes = await Task.Run(() => Services.ImagePreviewService.GetAnimatedImageBytes(file.FilePath, size, size));
                        if (animatedBytes == null)
                        {
                            firstFrame = await Task.Run(() => Services.ImagePreviewService.GetFirstFrameBitmap(file.FilePath, size, size));
                        }
                    }
                    Log.Info($"EnsureThumbnailAsync: {file.FilePath} animatedBytes={(animatedBytes != null ? animatedBytes.Length.ToString() : "null")}, firstFrame={(firstFrame != null ? "ok" : "null")}, frames={(Frames != null ? Frames.Length.ToString() : "0")} ");

                    if (animatedBytes == null && firstFrame == null)
                        return;

                    Dispatcher.BeginInvoke(() =>
                    {
                        // show frozen first-frame by default (non-animated)
                        if (firstFrame != null)
                        {
                            file.Thumbnail = firstFrame;
                        }
                        else if (Frames != null && Frames.Length > 0)
                        {
                            // prefer the first extracted frame as the static thumbnail
                            file.Thumbnail = Frames[0] as BitmapImage ?? Services.ImagePreviewService.CreateBitmapImageFromBytes(Services.ImagePreviewService.GetAnimatedImageBytes(file.FilePath, size, size), size, freeze: true);
                        }
                        else if (animatedBytes != null)
                        {
                            file.Thumbnail = Services.ImagePreviewService.CreateBitmapImageFromBytes(animatedBytes, size, freeze: true);
                        }

                        // Cache manual frames (preferred for hover animation)
                        if (Frames != null && Frames.Length > 0)
                        {
                            file.AnimatedFrames = Frames;
                            file.AnimatedFrameDelays = Delays;
                            Log.Info($"EnsureThumbnailAsync: Cached {file.AnimatedFrames.Length} frames for {file.FilePath}");
                        }

                        // Keep AnimatedThumbnail only as a fallback; do NOT rely on it for GIF animation if manual frames exist.
                        if ((Frames == null || Frames.Length == 0) && animatedBytes != null)
                        {
                            if (ext == ".gif")
                            {
                                try
                                {
                                    var uriBm = new BitmapImage();
                                    uriBm.BeginInit();
                                    uriBm.UriSource = new Uri(file.FilePath, UriKind.Absolute);
                                    uriBm.CacheOption = BitmapCacheOption.OnLoad;
                                    uriBm.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                                    uriBm.DecodePixelWidth = size;
                                    uriBm.EndInit();
                                    file.AnimatedThumbnail = uriBm;
                                    Log.Info($"EnsureThumbnailAsync: URI AnimatedThumbnail assigned for GIF {file.FilePath}");
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex);
                                    file.AnimatedThumbnail = Services.ImagePreviewService.CreateBitmapImageFromBytes(animatedBytes, size, freeze: false);
                                    Log.Info($"EnsureThumbnailAsync: bytes-based AnimatedThumbnail assigned for {file.FilePath}");
                                }
                            }
                            else
                            {
                                file.AnimatedThumbnail = Services.ImagePreviewService.CreateBitmapImageFromBytes(animatedBytes, size, freeze: false);
                                Log.Info($"EnsureThumbnailAsync: AnimatedThumbnail assigned for {file.FilePath}");
                            }
                        }

                        placeholder.Visibility = Visibility.Collapsed;
                    });
                }
                else
                {
                    // Static images (including static WebP) — safe to create/freeze on background thread.
                    BitmapImage? thumb = null;
                    try
                    {
                        thumb = await Task.Run(() => Services.ImagePreviewService.GetThumbnail(file.FilePath, size, size));
                    }
                    catch { }

                    // Fallback: attempt direct WPF load if Magick fails or returns null.
                    if (thumb == null)
                    {
                        try
                        {
                            var direct = new BitmapImage();
                            direct.BeginInit();
                            direct.UriSource = new Uri(file.FilePath, UriKind.Absolute);
                            direct.CacheOption = BitmapCacheOption.OnLoad;
                            direct.DecodePixelWidth = size;
                            direct.EndInit();
                            direct.Freeze();
                            thumb = direct;
                        }
                        catch { }
                    }

                    if (thumb == null)
                    {
                        // As a last resort, try the Windows shell thumbnail API (works for many formats).
                        try
                        {
                            thumb = Services.ImagePreviewService.GetVideoThumbnail(file.FilePath, size, size);
                        }
                        catch { }
                    }

                    if (thumb == null)
                        return;

                    Dispatcher.BeginInvoke(() =>
                    {
                        file.Thumbnail = thumb;
                        placeholder.Visibility = Visibility.Collapsed;
                    });
                }
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

        private async void OnDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is FileItemViewModel file)
            {
                await DeleteFileAsync(file);
            }
        }

        private async void DataGridDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (ResultsDataGrid.SelectedItem is FileItemViewModel file)
            {
                await DeleteFileAsync(file);
            }
        }

        private async void DeleteAllDuplicatesInFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Called from DataGrid context menu
            if (ResultsDataGrid.SelectedItem is FileItemViewModel file)
            {
                await DeleteAllDuplicatesInFolderAsync(file);
            }
        }

        private async void OnDeleteAllDuplicatesInFolderMenuItem_Click(object sender, RoutedEventArgs e)
        {
            // Called from grid view context menus
            if (sender is MenuItem mi && mi.Tag is FileItemViewModel file)
            {
                await DeleteAllDuplicatesInFolderAsync(file);
            }
        }

        private async Task DeleteAllDuplicatesInFolderAsync(FileItemViewModel clickedFile)
        {
            if (_isDeleting) return;
            _isDeleting = true;
            try
            {
            string? folder = Path.GetDirectoryName(clickedFile.FilePath);
            if (string.IsNullOrEmpty(folder)) return;

            // Collect all duplicate files in the same folder (excluding originals — keep one per group)
            // Re-validate groups: only consider groups that still have 2+ files on disk
            var filesToDelete = new List<FileItemViewModel>();
            foreach (var group in _groupViewModels)
            {
                // Only process groups with actual duplicates (2+ files)
                if (group.Files.Count < 2) continue;

                // Find files in this group that are in the target folder
                var filesInFolder = group.Files.Where(f =>
                    string.Equals(Path.GetDirectoryName(f.FilePath), folder, StringComparison.OrdinalIgnoreCase)).ToList();

                if (filesInFolder.Count == 0) continue;

                // If the group has files both inside and outside the folder, delete the ones in the folder
                // If all files in the group are in the folder, keep one (the first) and delete the rest
                var filesOutsideFolder = group.Files.Where(f =>
                    !string.Equals(Path.GetDirectoryName(f.FilePath), folder, StringComparison.OrdinalIgnoreCase)).ToList();

                // Decide which files in this group should be deleted
                if (filesOutsideFolder.Count > 0)
                {
                    // If group contains files outside the folder, delete all files inside the folder
                    filesToDelete.AddRange(filesInFolder);
                }
                else
                {
                    // All files are in the folder — keep one (the first) and delete the rest
                    filesToDelete.AddRange(filesInFolder.Skip(1));
                }

            }

            // After collecting files across all groups, validate selection and prepare deletion
            if (filesToDelete.Count == 0)
            {
                MessageBox.Show(Properties.Resources.NothingToDelete, "Nothing to delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Prepare thumbnail capture, paths and progress variables
            int gridSize = SettingsService.GridPictureSize;
            var filesToCapture = SettingsService.MaxRecycleBinSize > 0 ? filesToDelete.Take(SettingsService.MaxRecycleBinSize).ToList() : [.. filesToDelete];
            var thumbData = new List<(FileItemViewModel file, BitmapImage? thumb)>();
            var pathsToDelete = filesToDelete.Select(f => f.FilePath).ToList();
            int deleted = 0;
            int failed = 0;
            var deleteProgress = new Progress<int>(p =>
            {
                if (pathsToDelete.Count > 0)
                {
                    double percentage = (p * 100.0) / pathsToDelete.Count;
                    ProgressBar.Value = percentage;
                    UpdateScanProgressBar(percentage);
                    ProgressStatusText.Text = $"Deleting... {p}/{pathsToDelete.Count}";
                }
            });

            // Show progress UI before performing deletion
            ProgressPanel.Visibility = Visibility.Visible;
            ViewControlPanel.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 0;
            ProgressStatusText.Text = $"Deleting {pathsToDelete.Count} file(s)...";

            await Task.Run(() =>
            {
                // Load thumbnails only for files that will fit in the recycle bin
                foreach (var file in filesToCapture)
                {
                    BitmapImage? thumb = file.Thumbnail;
                    if (thumb == null && File.Exists(file.FilePath) && Services.ImagePreviewService.IsPreviewableImage(file.FilePath))
                    {
                        try
                        {
                            thumb = Services.ImagePreviewService.GetThumbnail(file.FilePath, gridSize, gridSize);
                        }
                        catch { }
                    }
                    thumbData.Add((file, thumb));
                }
            });

            // Release any media/GIF locks on these files before attempting OS deletion.
            await ReleaseMediaLockAsync(pathsToDelete);

            // Add to recycle bin on UI thread after thumbnail capture but before deletion
            RecycleBinDataGrid.ItemsSource = null;
            foreach (var (file, thumb) in thumbData)
            {
                var deletedItem = new DeletedFileItem
                {
                    FileName = file.FileName,
                    FilePath = file.FilePath,
                    FileSize = file.FileSize,
                    FileSizeFormatted = file.SizeFormatted,
                    DeletedTime = DateTime.Now,
                    OriginalViewModel = file,
                    Thumbnail = thumb
                };
                _recycleBin.Insert(0, deletedItem);
            }
            // Trim recycle bin
            int maxSize = SettingsService.MaxRecycleBinSize;
            if (maxSize > 0)
            {
                while (_recycleBin.Count > maxSize)
                    _recycleBin.RemoveAt(_recycleBin.Count - 1);
            }

            // Remove deleted files from view models BEFORE file deletion so
            // grid.Unloaded fires and fully cleans up MediaElement references.
            var deletedPaths = new HashSet<string>(pathsToDelete, StringComparer.OrdinalIgnoreCase);
            foreach (var group in _groupViewModels)
            {
                group.Files.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            }
            // Accumulate deleted size
            _totalDeletedSize += filesToDelete.Where(f => deletedPaths.Contains(f.FilePath)).Sum(f => f.FileSize);

            // Clean up cached lists
            _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));

            // Collect orphan files from groups that will be disbanded (only 1 file left = no longer a duplicate)
            foreach (var g in _groupViewModels)
            {
                if (g.Files.Count <= 1)
                    foreach (var f in g.Files) deletedPaths.Add(f.FilePath);
            }
            _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _groupViewModels.RemoveAll(g => g.Files.Count <= 1);
            ApplySorting();
            ResultsDataGrid.ItemsSource = null;
            ResultsListView.ItemsSource = null;
            // Remove tiles BEFORE file deletion — triggers grid.Unloaded for full MediaElement cleanup
            RemoveDeletedItemsFromGrid(deletedPaths);
            UpdateFooterStats();
            UpdateDeleteCount();

            // Nudge GC once; avoid blocking on finalizers.
            await Task.Run(() => GC.Collect(2, GCCollectionMode.Forced));

            await Task.Run(() =>
            {
                // Delete all files
                for (int i = 0; i < pathsToDelete.Count; i++)
                {
                    if (TryDeleteFile(pathsToDelete[i]))
                        Interlocked.Increment(ref deleted);
                    else
                        Interlocked.Increment(ref failed);
                    ((IProgress<int>)deleteProgress).Report(i + 1);
                }
            });

            // Hide progress bar
            ProgressPanel.Visibility = Visibility.Collapsed;
            ViewControlPanel.Visibility = Visibility.Visible;
            UpdateScanProgressBar(100);

            StatusText.Text = $"Deleted {deleted} duplicate file(s) from {Path.GetFileName(folder)}" +
                (failed > 0 ? $" ({failed} failed)" : "");
            }
            finally
            {
                _isDeleting = false;
            }
        }

        private ScrollViewer? FindScrollViewer(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>
        /// Surgically removes only the deleted items from the current grid/icon WrapPanel
        /// (or virtual canvas) without rebuilding the entire view.  This preserves all
        /// running animations (videos, GIFs) for non-deleted files.
        /// For list view mode, falls back to a full DisplayResults() because DataGrid
        /// binding requires a new ItemsSource.
        /// </summary>
        private void RemoveDeletedItemsFromGrid(HashSet<string> deletedPaths)
        {
            if (_currentViewMode == "list")
            {
                // List view has no running animations — safe to rebuild fully
                ApplySorting();
                if (ResultsDataGrid != null) ResultsDataGrid.ItemsSource = null;
                if (ResultsListView != null) ResultsListView.ItemsSource = null;
                DisplayResults();
                return;
            }

            // Grid / icon mode — find the WrapPanel or Canvas in ResultsPanel
            if (ResultsPanel.Children.Count == 0) return;

            if (!_isVirtualGridActive && ResultsPanel.Children[0] is WrapPanel wrap)
            {
                // Non-virtual WrapPanel: iterate children and remove matching ones.
                // Iterate backwards to safely remove while iterating.
                for (int i = wrap.Children.Count - 1; i >= 0; i--)
                {
                    if (wrap.Children[i] is FrameworkElement fe
                        && fe.Tag is FileItemViewModel vm
                        && deletedPaths.Contains(vm.FilePath))
                    {
                        wrap.Children.RemoveAt(i);
                    }
                }
            }
            else if (_isVirtualGridActive && ResultsPanel.Children[0] is Canvas canvas)
            {
                // Virtual grid: remove realised items for deleted files, then re-layout.
                var toRemove = new List<int>();
                foreach (var (idx, elem) in _realizedGridItems)
                {
                    if (idx < _currentGridFiles.Count)
                    {
                        var file = _currentGridFiles[idx];
                        if (deletedPaths.Contains(file.FilePath))
                            toRemove.Add(idx);
                    }
                }
                foreach (var idx in toRemove)
                {
                    if (_realizedGridItems.Remove(idx, out var elem))
                        canvas.Children.Remove(elem);
                }

                // _currentGridFiles and _groupViewModels are already pruned by the caller.
                // Re-run the virtual layout to reindex remaining items.
                _realizedGridItems.Clear();
                canvas.Children.Clear();
                SetupVirtualGrid(canvas);
            }
        }

        /// <summary>Batch-delete multiple files with a single UI refresh at the end (used by keyboard Delete in grid view).</summary>
        private async Task BatchDeleteFilesAsync(List<FileItemViewModel> filesToDelete)
        {
            if (filesToDelete.Count == 0) return;
            if (_isDeleting) return;

            if (filesToDelete.Count == 1)
            {
                await DeleteFileAsync(filesToDelete[0]);
                return;
            }

            if (Services.SettingsService.ConfirmDelete)
            {
                var result = MessageBox.Show(string.Format(Properties.Resources.ConfirmDeleteMultipleFormat, filesToDelete.Count),
                    "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            double gridOffset = ResultsScrollViewer?.VerticalOffset ?? 0;
            int keepIndex = _selectedGridIndex;

            foreach (var file in filesToDelete) AddToRecycleBin(file);

            var paths = filesToDelete.Select(f => f.FilePath).ToList();
            // Release any media/GIF locks on these files before attempting OS deletion.
            await ReleaseMediaLockAsync(paths);

            var deletedPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (var group in _groupViewModels)
            {
                var removed = group.Files.RemoveAll(f => deletedPaths.Contains(f.FilePath));
                if (removed > 0) _totalDeletedSize += filesToDelete.Where(f => deletedPaths.Contains(f.FilePath)).Sum(f => f.FileSize);
            }
            _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));

            // Collect orphan files from groups that will be disbanded
            foreach (var g in _groupViewModels)
            {
                if (g.Files.Count <= 1)
                    foreach (var f in g.Files) deletedPaths.Add(f.FilePath);
            }
            _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _groupViewModels.RemoveAll(g => g.Files.Count <= 1);

            ApplySorting();
            if (ResultsDataGrid != null) ResultsDataGrid.ItemsSource = null;
            if (ResultsListView != null) ResultsListView.ItemsSource = null;
            // Remove tiles BEFORE file deletion so grid.Unloaded fully cleans up MediaElements
            RemoveDeletedItemsFromGrid(deletedPaths);
            UpdateFooterStats();
            UpdateDeleteCount();

            // Nudge GC once; avoid blocking on finalizers.
            await Task.Run(() => GC.Collect(2, GCCollectionMode.Forced));

            await Task.Run(() =>
            {
                foreach (var p in paths)
                    TryDeleteFile(p);
            });

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (ResultsScrollViewer != null)
                    ResultsScrollViewer.ScrollToVerticalOffset(gridOffset);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            if (_currentGridFiles.Count > 0)
            {
                _selectedGridIndex = Math.Clamp(keepIndex < 0 ? 0 : keepIndex, 0, _currentGridFiles.Count - 1);
                _selectedGridItems.Clear();
                _selectedGridItems.Add(_currentGridFiles[_selectedGridIndex]);
                _lastSelectedGridItem = _selectedGridItems.FirstOrDefault();
                RefreshGridItemSelection();
            }
            else
            {
                _selectedGridIndex = -1;
                _selectedGridItems.Clear();
                _lastSelectedGridItem = null;
            }
        }

        /// <summary>
        /// Stops and clears any active media resources (video or animated GIF) for the given file paths
        /// and waits for any in-progress background thumbnail loads to finish, so that OS-level file
        /// handles are fully released before deletion is attempted. This prevents native crashes from
        /// Magick.NET or the Windows Shell thumbnail extractor reading a file that is concurrently
        /// being deleted.
        /// </summary>
        private async Task ReleaseMediaLockAsync(IEnumerable<string> filePaths)
        {
            var paths = filePaths is ICollection<string> c ? c : filePaths.ToList();

            // 1. Invoke registered cleanup callbacks (stop playback, detach MediaElement).
            bool anyMedia = false;
            foreach (var path in paths)
            {
                if (_mediaReleaseActions.Remove(path, out var release))
                {
                    try { release(); } catch { }
                    anyMedia = true;
                }
            }

            // 2. Wait briefly for any in-progress Magick.NET/Shell decode to finish.
            //    Force-remove files from _thumbnailLoading so we never wait longer than
            //    the deadline. This guarantees we don't hang if a Shell COM call stalls.
            var deadline = DateTime.UtcNow.AddMilliseconds(800);
            bool anyLoading;
            do
            {
                anyLoading = false;
                lock (_thumbnailLoading)
                {
                    foreach (var path in paths)
                    {
                        if (_thumbnailLoading.Contains(path))
                        {
                            anyLoading = true;
                            break;
                        }
                    }
                }
                if (anyLoading)
                    await Task.Delay(60);
            } while (anyLoading && DateTime.UtcNow < deadline);

            // If thumbnail loads are still running after the deadline, force-remove them
            // so we don't block deletion. The thumbnail load will finish eventually and
            // update the UI, but we won't wait for it.
            if (anyLoading)
            {
                lock (_thumbnailLoading)
                {
                    foreach (var path in paths)
                        _thumbnailLoading.Remove(path);
                }
            }
        }

        /// <summary>
        /// Deletes a file with retries.
        /// Retries up to <paramref name="maxRetries"/> times so transient WMF handle
        /// releases don't cause silent delete failures.
        /// Must be called from a background thread.
        /// </summary>
        private static bool TryDeleteFile(string path, int maxRetries = 6, int retryDelayMs = 200)
        {
            // Use direct File.Delete retries for all files. The shell delete path can
            // hang on some systems and leave orphaned worker tasks.

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                    System.Threading.Thread.Sleep(retryDelayMs);

                // Lightweight nudge once: encourage cleanup of detached MediaElements
                // without blocking on finalizers for every file in large batches.
                if (attempt == 1)
                {
                    GC.Collect(0, GCCollectionMode.Optimized);
                }

                try
                {
                    File.Delete(path);
                    return true;
                }
                catch when (attempt < maxRetries) { /* will retry */ }
                catch { return false; }
            }
            return false;
        }

        private async Task DeleteFileAsync(FileItemViewModel file, bool skipConfirm = false)
        {
            if (_isDeleting) return;
            _isDeleting = true;
            try
            {
                if (!skipConfirm && Services.SettingsService.ConfirmDelete)
                {
                    var confirm = MessageBox.Show(string.Format(Properties.Resources.ConfirmDeleteIndividualFormat, file.FileName), "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes)
                        return;
                }

                StatusText.Text = $"Deleting {file.FileName}...";

                // capture current scroll offsets so we can restore them after the
                // view refresh.  note that scrolling normally happens as part of
                // selection restoration, but when deleting multiple items in a
                // batch the repeated calls to DeleteFileAsync could walk the
                // selection to the top; the offset ensures the user stays where
                // they were looking.
                double dataGridOffset = 0, listOffset = 0, gridOffset = 0;
                if (ResultsDataGrid != null)
                {
                    if (FindScrollViewer(ResultsDataGrid) is ScrollViewer dgv)
                        dataGridOffset = dgv.VerticalOffset;
                }
                if (ResultsListView != null)
                {
                    if (FindScrollViewer(ResultsListView) is ScrollViewer lv)
                        listOffset = lv.VerticalOffset;
                }
                if (ResultsScrollViewer != null)
                {
                    gridOffset = ResultsScrollViewer.VerticalOffset;
                }

                // Add to recycle bin before deleting
                AddToRecycleBin(file);

                // Release any media/GIF lock on this file before attempting OS deletion.
                await ReleaseMediaLockAsync([file.FilePath]);

                // Remember current selection positions BEFORE removing from models
                int oldGridIndex = -1;
                if (_currentViewMode != "list")
                {
                    oldGridIndex = _currentGridFiles.IndexOf(file);
                    if (oldGridIndex < 0)
                        oldGridIndex = _selectedGridIndex;
                }
                else
                {
                    oldGridIndex = _selectedGridIndex;
                }
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

                // Collect orphan files from groups that will be disbanded
                var singleDeletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.FilePath };
                foreach (var g in _groupViewModels)
                {
                    if (g.Files.Count <= 1)
                        foreach (var f in g.Files) singleDeletedPaths.Add(f.FilePath);
                }
                _currentGridFiles.RemoveAll(f => singleDeletedPaths.Contains(f.FilePath));
                _selectedGridItems.RemoveAll(f => singleDeletedPaths.Contains(f.FilePath));

                // Remove any empty groups
                _groupViewModels.RemoveAll(g => g.Files.Count <= 1);

                // Compute flattened list to adjust selection
                var flatAfter = _groupViewModels.SelectMany(g => g.Files).ToList();

                if (_currentViewMode != "list")
                {
                    if (flatAfter.Count == 0)
                    {
                        _selectedGridIndex = -1;
                        _selectedGridItems.Clear();
                        _lastSelectedGridItem = null;
                    }
                    else
                    {
                        _selectedGridIndex = Math.Min(oldGridIndex < 0 ? 0 : oldGridIndex, flatAfter.Count - 1);

                        // Keep a single item selected so the grid selection persists after deletion
                        var selectedFile = flatAfter[_selectedGridIndex];
                        _selectedGridItems.Clear();
                        _selectedGridItems.Add(selectedFile);
                        _lastSelectedGridItem = selectedFile;
                    }
                }

                ApplySorting();
                if (ResultsDataGrid != null) ResultsDataGrid.ItemsSource = null;
                if (ResultsListView != null) ResultsListView.ItemsSource = null;
                // Remove tiles from UI BEFORE file deletion — triggers grid.Unloaded,
                // which unsubscribes event handlers and breaks all reference chains to
                // MediaElement, making it eligible for GC so finalization releases the
                // WMF file handle.
                RemoveDeletedItemsFromGrid(singleDeletedPaths);
                UpdateFooterStats();

                // Nudge GC once; avoid blocking on finalizers.
                await Task.Run(() => GC.Collect(2, GCCollectionMode.Forced));

                await Task.Run(() =>
                {
                    if (TryDeleteFile(file.FilePath))
                        Log.Deletion($"[DELETED] \"{file.FileName}\" | {file.FilePath}");
                    else
                        Log.Info($"[FAILED] Could not delete \"{file.FilePath}\"");
                });

                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (ResultsListView != null)
                    {
                        if (FindScrollViewer(ResultsListView) is ScrollViewer lv)
                            lv.ScrollToVerticalOffset(listOffset);
                    }
                    if (ResultsDataGrid != null)
                    {
                        if (FindScrollViewer(ResultsDataGrid) is ScrollViewer dgv)
                            dgv.ScrollToVerticalOffset(dataGridOffset);
                    }
                    if (ResultsScrollViewer != null)
                    {
                        ResultsScrollViewer.ScrollToVerticalOffset(gridOffset);
                    }
                }, System.Windows.Threading.DispatcherPriority.Loaded);

                // For ListView, restore selection to next item (same index) or previous if at end
                if (_currentViewMode == "list" && ResultsListView != null && ResultsListView.Visibility == Visibility.Visible)
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
                            if (ResultsListView.ItemContainerGenerator.ContainerFromIndex(sel) is ListViewItem item)
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
                else if (ResultsDataGrid != null && ResultsDataGrid.Visibility == Visibility.Visible)
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
                            if (ResultsDataGrid.ItemContainerGenerator.ContainerFromIndex(sel) is DataGridRow row)
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
            finally
            {
                _isDeleting = false;
            }
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

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
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
                _currentViewMode = "grid";
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
                if (indicator.Parent is Grid grid && grid.Children.Count > 1 && grid.Children[1] is Grid labelGrid)
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
            if (ShowScanTimerCheckBox != null)
                ShowScanTimerCheckBox.IsChecked = SettingsService.GetShowScanTimer();

            if (EnableTelemetryCheckBox != null)
                EnableTelemetryCheckBox.IsChecked = SettingsService.GetEnableTelemetry();

            if (AutoPlayPreviewsCheckBox != null)
                AutoPlayPreviewsCheckBox.IsChecked = SettingsService.GetAutoPlayAnimatedPreviews();

            if (RecycleBinSizeTextBox != null)
                RecycleBinSizeTextBox.Text = SettingsService.MaxRecycleBinSize.ToString();
        }



        private void ConfirmDeleteCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ConfirmDeleteCheckBox == null)
                return;

            SettingsService.SetConfirmDelete(ConfirmDeleteCheckBox.IsChecked == true);
        }

        private void SaveRecycleBinSizeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                int maxSize = 30;

                if (!string.IsNullOrWhiteSpace(RecycleBinSizeTextBox.Text))
                {
                    if (!int.TryParse(RecycleBinSizeTextBox.Text, out maxSize) || maxSize < 0)
                    {
                        MessageBox.Show("Please enter a valid number (0 or greater). 0 means no limit.", "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                SettingsService.SetMaxRecycleBinSize(maxSize);

                // Trim existing recycle bin if needed
                if (maxSize > 0)
                {
                    while (_recycleBin.Count > maxSize)
                    {
                        _recycleBin.RemoveAt(_recycleBin.Count - 1);
                    }
                }

                MessageBox.Show(string.Format(Properties.Resources.RecycleBinSizeAppliedFormat, (maxSize == 0 ? "No limit" : maxSize.ToString())),
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Properties.Resources.ErrorSavingFormat, "recycle bin size", ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
            if (RecycleBinPanel != null && RecycleBinPanel.Visibility == Visibility.Visible && RecycleBinDataGrid != null)
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
            else if (ResultsDataGrid != null && ResultsDataGrid.Visibility == Visibility.Visible)
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
            else if (ResultsListView != null && ResultsListView.Visibility == Visibility.Visible)
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            // Check if we're in RecycleBinPanel
            if (RecycleBinPanel != null && RecycleBinPanel.Visibility == Visibility.Visible)
            {
                RecoverSelectedFiles();
                return;
            }

            if (_isDeleting) return;
            _isDeleting = true;
            try
            {

            var toDelete = new List<FileItemViewModel>();

            // First check grid selections (for grid view in scanned files)
            if (_currentViewMode != "list" && _selectedGridItems.Count > 0)
            {
                toDelete = [.. _selectedGridItems];
            }
            else if (ResultsDataGrid != null && ResultsDataGrid.Visibility == Visibility.Visible)
            {
                toDelete = [.. ResultsDataGrid.SelectedItems.Cast<FileItemViewModel>()];
            }
            else if (ResultsListView != null && ResultsListView.Visibility == Visibility.Visible)
            {
                toDelete = [.. ResultsListView.SelectedItems.Cast<FileItemViewModel>()];
            }

            if (toDelete.Count == 0)
            {
                MessageBox.Show(Properties.Resources.NoSelection, "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Services.SettingsService.ConfirmDelete)
            {
                var result = MessageBox.Show(string.Format(Properties.Resources.ConfirmDeleteMultipleFormat, toDelete.Count), "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // remember a position to reselect after deletion
            // Save the file path of the closest neighbor so we can find it after rebuild
            string? neighborPath = null;
            if (_currentViewMode != "list")
            {
                int idx = toDelete.Count > 0 ? _currentGridFiles.IndexOf(toDelete[0]) : _selectedGridIndex;
                if (idx >= 0)
                {
                    // Try the next item first, then the previous one
                    for (int probe = idx + 1; probe < _currentGridFiles.Count; probe++)
                    {
                        if (!toDelete.Contains(_currentGridFiles[probe]))
                        {
                            neighborPath = _currentGridFiles[probe].FilePath;
                            break;
                        }
                    }
                    if (neighborPath == null)
                    {
                        for (int probe = idx - 1; probe >= 0; probe--)
                        {
                            if (!toDelete.Contains(_currentGridFiles[probe]))
                            {
                                neighborPath = _currentGridFiles[probe].FilePath;
                                break;
                            }
                        }
                    }
                }
            }
            int keepIndex = -1;
            if (_currentViewMode == "list")
            {
                if (ResultsDataGrid != null && ResultsDataGrid.SelectedIndex >= 0)
                    keepIndex = ResultsDataGrid.SelectedIndex;
                else if (ResultsListView != null && ResultsListView.SelectedIndex >= 0)
                    keepIndex = ResultsListView.SelectedIndex;
            }

            // Capture scroll offsets before deletion so we can restore them.
            double dataGridOffset = 0, listOffset = 0, gridOffset = 0;
            if (ResultsDataGrid != null && FindScrollViewer(ResultsDataGrid) is ScrollViewer dgvSel)
                dataGridOffset = dgvSel.VerticalOffset;
            if (ResultsListView != null && FindScrollViewer(ResultsListView) is ScrollViewer lvSel)
                listOffset = lvSel.VerticalOffset;
            if (ResultsScrollViewer != null)
                gridOffset = ResultsScrollViewer.VerticalOffset;

            StatusText.Text = $"Deleting {toDelete.Count} file(s)...";

            // Add all files to recycle bin before deleting
            foreach (var file in toDelete)
            {
                AddToRecycleBin(file);
            }

            // Delete all files on a background thread (no per-file UI rebuild)
            var pathsToDelete = toDelete.Select(f => f.FilePath).ToList();
            int deleted = 0, failed = 0;
            // Release any media/GIF locks on these files before attempting OS deletion.
            await ReleaseMediaLockAsync(pathsToDelete);

            // Remove deleted files from view models BEFORE file deletion so 
            // grid.Unloaded fires and fully cleans up MediaElement references.
            var deletedPaths = new HashSet<string>(pathsToDelete, StringComparer.OrdinalIgnoreCase);
            foreach (var group in _groupViewModels)
            {
                var removedCount = group.Files.RemoveAll(f => deletedPaths.Contains(f.FilePath));
                if (removedCount > 0)
                    _totalDeletedSize += toDelete.Where(f => deletedPaths.Contains(f.FilePath))
                                                  .Sum(f => f.FileSize);
            }

            _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));

            // Collect orphan files from groups that will be disbanded
            foreach (var g in _groupViewModels)
            {
                if (g.Files.Count <= 1)
                    foreach (var f in g.Files) deletedPaths.Add(f.FilePath);
            }
            _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
            _groupViewModels.RemoveAll(g => g.Files.Count <= 1);

            // Single UI rebuild after all deletions
            ApplySorting();
            if (ResultsDataGrid != null) ResultsDataGrid.ItemsSource = null;
            if (ResultsListView != null) ResultsListView.ItemsSource = null;
            RemoveDeletedItemsFromGrid(deletedPaths);
            UpdateFooterStats();

            // Nudge GC once; avoid blocking on finalizers.
            await Task.Run(() => GC.Collect(2, GCCollectionMode.Forced));

            await Task.Run(() =>
            {
                foreach (var path in pathsToDelete)
                {
                    if (TryDeleteFile(path))
                    {
                        Console.WriteLine($"[DELETED] {path}");
                        Log.Deletion($"[DELETED] \"{Path.GetFileName(path)}\" | {path}");
                        Interlocked.Increment(ref deleted);
                    }
                    else
                    {
                        Console.WriteLine($"[FAILED] {path}");
                        Interlocked.Increment(ref failed);
                    }
                }
            });

            // Restore scroll offsets
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (ResultsListView != null && FindScrollViewer(ResultsListView) is ScrollViewer lv2)
                    lv2.ScrollToVerticalOffset(listOffset);
                if (ResultsDataGrid != null && FindScrollViewer(ResultsDataGrid) is ScrollViewer dgv2)
                    dgv2.ScrollToVerticalOffset(dataGridOffset);
                if (ResultsScrollViewer != null)
                    ResultsScrollViewer.ScrollToVerticalOffset(gridOffset);
            }, System.Windows.Threading.DispatcherPriority.Loaded);

            // Reselect the closest neighbor after deletion
            _selectedGridItems.Clear();
            _lastSelectedGridItem = null;
            _selectedGridIndex = -1;

            if (_currentViewMode != "list")
            {
                if (neighborPath != null && _currentGridFiles.Count > 0)
                {
                    var neighborFile = _currentGridFiles.FirstOrDefault(
                        f => string.Equals(f.FilePath, neighborPath, StringComparison.OrdinalIgnoreCase));
                    if (neighborFile != null)
                    {
                        _selectedGridIndex = _currentGridFiles.IndexOf(neighborFile);
                        _selectedGridItems.Add(neighborFile);
                        _lastSelectedGridItem = neighborFile;
                    }
                }

                // Fallback: if the preferred neighbor no longer exists (e.g. it became
                // an orphan and was removed), keep a stable selection on the first item.
                if (_selectedGridIndex < 0 && _currentGridFiles.Count > 0)
                {
                    _selectedGridIndex = 0;
                    var fallback = _currentGridFiles[0];
                    _selectedGridItems.Add(fallback);
                    _lastSelectedGridItem = fallback;
                }

                RefreshGridItemSelection();
            }
            else
            {
                if (ResultsDataGrid != null)
                {
                    ResultsDataGrid.SelectedItems.Clear();
                    if (keepIndex >= 0 && keepIndex < ResultsDataGrid.Items.Count)
                        ResultsDataGrid.SelectedIndex = keepIndex;
                    else if (ResultsDataGrid.Items.Count > 0)
                        ResultsDataGrid.SelectedIndex = Math.Max(0, Math.Min(keepIndex, ResultsDataGrid.Items.Count - 1));
                }
                if (ResultsListView != null)
                {
                    ResultsListView.SelectedItems.Clear();
                    if (keepIndex >= 0 && keepIndex < ResultsListView.Items.Count)
                        ResultsListView.SelectedIndex = keepIndex;
                    else if (ResultsListView.Items.Count > 0)
                        ResultsListView.SelectedIndex = Math.Max(0, Math.Min(keepIndex, ResultsListView.Items.Count - 1));
                }
            }

            UpdateDeleteCount();
            StatusText.Text = $"Deleted {deleted} file(s)" + (failed > 0 ? $" ({failed} failed)" : "");
            }
            finally
            {
                _isDeleting = false;
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
                if (_isDeleting) return;

                var toDelete = ResultsDataGrid.SelectedItems.Cast<FileItemViewModel>().ToList();
                if (toDelete.Count == 0)
                    return;

                if (toDelete.Count == 1)
                {
                    await DeleteFileAsync(toDelete[0]);
                }
                else
                {
                    // Batch delete: perform all deletions then refresh UI once
                    if (Services.SettingsService.ConfirmDelete)
                    {
                        var result = MessageBox.Show(string.Format(Properties.Resources.ConfirmDeleteMultipleFormat, toDelete.Count), "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (result != MessageBoxResult.Yes)
                            return;
                    }

                    int keepIndex = ResultsDataGrid.SelectedIndex;
                    foreach (var file in toDelete) AddToRecycleBin(file);

                    var paths = toDelete.Select(f => f.FilePath).ToList();
                    // Release any media/GIF locks on these files before attempting OS deletion.
                    await ReleaseMediaLockAsync(paths);

                    var deletedPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                    foreach (var group in _groupViewModels)
                        group.Files.RemoveAll(f => deletedPaths.Contains(f.FilePath));
                    _totalDeletedSize += toDelete.Where(f => deletedPaths.Contains(f.FilePath)).Sum(f => f.FileSize);
                    _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
                    _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));

                    // Collect orphan files from groups that will be disbanded
                    foreach (var g in _groupViewModels)
                    {
                        if (g.Files.Count <= 1)
                            foreach (var f in g.Files) deletedPaths.Add(f.FilePath);
                    }
                    _currentGridFiles.RemoveAll(f => deletedPaths.Contains(f.FilePath));
                    _selectedGridItems.RemoveAll(f => deletedPaths.Contains(f.FilePath));
                    _groupViewModels.RemoveAll(g => g.Files.Count <= 1);

                    ApplySorting();
                    ResultsDataGrid.ItemsSource = null;
                    // Remove tiles BEFORE file deletion
                    RemoveDeletedItemsFromGrid(deletedPaths);
                    UpdateFooterStats();
                    UpdateDeleteCount();

                    // Nudge GC once; avoid blocking on finalizers.
                    await Task.Run(() => GC.Collect(2, GCCollectionMode.Forced));

                    await Task.Run(() =>
                    {
                        foreach (var p in paths)
                            TryDeleteFile(p);
                    });

                    if (keepIndex >= 0 && keepIndex < ResultsDataGrid.Items.Count)
                        ResultsDataGrid.SelectedIndex = keepIndex;
                    else if (ResultsDataGrid.Items.Count > 0)
                        ResultsDataGrid.SelectedIndex = Math.Max(0, Math.Min(keepIndex, ResultsDataGrid.Items.Count - 1));
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
                    filesToDelete = [.. _selectedGridItems];
                }
                else if (_selectedGridIndex >= 0 && _selectedGridIndex < _currentGridFiles.Count)
                {
                    filesToDelete = [_currentGridFiles[_selectedGridIndex]];
                }
                else
                {
                    return;
                }

                // Batch delete: fire-and-forget with single UI refresh
                _ = BatchDeleteFilesAsync(filesToDelete);
                e.Handled = true;
            }
            else if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                HandleGridNavigation(e.Key);
                e.Handled = true;
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
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

            // Sync _selectedGridItems with the keyboard-navigated item
            _selectedGridItems.Clear();
            if (_selectedGridIndex >= 0 && _selectedGridIndex < _currentGridFiles.Count)
            {
                _selectedGridItems.Add(_currentGridFiles[_selectedGridIndex]);
                _lastSelectedGridItem = _currentGridFiles[_selectedGridIndex];
            }

            HighlightSelectedGridFile();
            UpdateDeleteCount();
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
                        MessageBox.Show(Properties.Resources.InvalidMinSize, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                if (!string.IsNullOrWhiteSpace(MaxFileSizeTextBox.Text))
                {
                    if (!long.TryParse(MaxFileSizeTextBox.Text, out maxSize) || maxSize < 0)
                    {
                        MessageBox.Show(Properties.Resources.InvalidMaxSize, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                if (maxSize > 0 && minSize > maxSize)
                {
                    MessageBox.Show(Properties.Resources.MinGreaterThanMax, "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SettingsService.SetMinFileSizeMB(minSize);
                SettingsService.SetMaxFileSizeMB(maxSize);

                MessageBox.Show(string.Format(Properties.Resources.FileSizeLimitsAppliedFormat,
                        (minSize == 0 ? "No limit" : minSize + " MB"),
                        (maxSize == 0 ? "No limit" : maxSize + " MB")),
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Properties.Resources.ErrorSavingFormat, "file size limits", ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                MessageBox.Show(string.Format(Properties.Resources.DuplicateLimitAppliedFormat,
                        (maxDuplicates == 0 ? "No limit" : maxDuplicates.ToString())),
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Properties.Resources.ErrorSavingFormat, "duplicate limit", ex.Message), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                // Update the virtual grid dimensions
                _virtualItemWidth = size + 56;  // size + panel padding + margins
                _virtualItemHeight = size + 104; // size + panel padding + text height + margins

                // If currently in grid view, refresh the display
                if (_currentViewMode != "list" && _groupViewModels != null && _groupViewModels.Count > 0)
                {
                    MessageBox.Show(string.Format(Properties.Resources.GridSizeAppliedRefreshFormat, size),
                        "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                    DisplayResults();
                }
                else
                {
                    MessageBox.Show(string.Format(Properties.Resources.GridSizeAppliedSwitchFormat, size),
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

        private void ShowScanTimerCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (ShowScanTimerCheckBox == null)
                return;

            SettingsService.SetShowScanTimer(ShowScanTimerCheckBox.IsChecked == true);
        }

        private void SaveScanTimerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool showTimer = ShowScanTimerCheckBox.IsChecked ?? false;
                SettingsService.SetShowScanTimer(showTimer);
                MessageBox.Show(showTimer ? "Scan timer enabled. It will display during similar-image scans." : "Scan timer disabled.",
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EnableTelemetryCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (EnableTelemetryCheckBox == null)
                return;

            SettingsService.SetEnableTelemetry(EnableTelemetryCheckBox.IsChecked == true);
        }

        private void SaveTelemetryButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool enabled = EnableTelemetryCheckBox.IsChecked ?? false;
                SettingsService.SetEnableTelemetry(enabled);
                MessageBox.Show(enabled ? "Telemetry enabled. Anonymous performance data will be logged." : "Telemetry disabled.",
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AutoPlayPreviewsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (AutoPlayPreviewsCheckBox == null)
                return;

            SettingsService.SetAutoPlayAnimatedPreviews(AutoPlayPreviewsCheckBox.IsChecked == true);

            // If the user toggles this while results are already displayed, refresh the grid so the change takes effect immediately.
            if (_currentViewMode == "grid")
                DisplayResults();
        }

        private void SaveAutoPlayPreviewsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool enabled = AutoPlayPreviewsCheckBox?.IsChecked == true;
                SettingsService.SetAutoPlayAnimatedPreviews(enabled);
                MessageBox.Show(enabled ? "Animated previews will auto-play." : "Animated previews will only play on hover.",
                    "Settings Saved", MessageBoxButton.OK, MessageBoxImage.Information);

                // Refresh to apply the new setting immediately in current view.
                if (_currentViewMode == "grid")
                    DisplayResults();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving setting: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to reset all settings to their default values?",
                "Reset Settings",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;

            SettingsService.ResetToDefaults();
            LoadSettingsValues();
            MessageBox.Show("Settings have been reset to defaults.", "Settings Reset", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Recycle Bin Methods
        private void AddToRecycleBin(FileItemViewModel file)
        {
            // Prevent duplicate entries (e.g. from double-click or re-entrant calls)
            if (_recycleBin.Any(r => string.Equals(r.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase)))
                return;

            // Ensure thumbnail is loaded before file gets deleted
            BitmapImage? thumb = file.Thumbnail;
            if (thumb == null && File.Exists(file.FilePath) && Services.ImagePreviewService.IsPreviewableImage(file.FilePath))
            {
                try
                {
                    int gridSize = SettingsService.GridPictureSize;
                    thumb = Services.ImagePreviewService.GetThumbnail(file.FilePath, gridSize, gridSize);
                }
                catch { }
            }

            var deletedItem = new DeletedFileItem
            {
                FileName = file.FileName,
                FilePath = file.FilePath,
                FileSize = file.FileSize,
                FileSizeFormatted = file.SizeFormatted,
                DeletedTime = DateTime.Now,
                OriginalViewModel = file,
                Thumbnail = thumb
            };

            // Add to beginning of list (most recent first)
            _recycleBin.Insert(0, deletedItem);

            // Maintain max size - remove oldest items
            int maxSize = SettingsService.MaxRecycleBinSize;
            if (maxSize > 0)
            {
                while (_recycleBin.Count > maxSize)
                {
                    _recycleBin.RemoveAt(_recycleBin.Count - 1);
                }
            }
        }

        private void UpdateRecycleBinDisplay()
        {
            RecycleBinDataGrid.ItemsSource = _recycleBin;
            if (RecycleBinCountText != null) RecycleBinCountText.Text = $"({_recycleBin.Count} file{(_recycleBin.Count != 1 ? "s" : "")})";
            UpdateRecycleBinCount();
        }

        private void DisplayRecycleBinResults()
        {
            // Clear grid selections when switching views
            _selectedRecycleBinItems.Clear();
            RecycleBinDataGrid?.UnselectAll();
            UpdateRecycleBinCount();

            // Update count text
            if (RecycleBinCountText != null) RecycleBinCountText.Text = $"({_recycleBin.Count} file{(_recycleBin.Count != 1 ? "s" : "")})";

            // Show/hide placeholder and action bar based on whether bin is empty
            if (_recycleBin.Count == 0)
            {
                NoRecycleBinPlaceholder.Visibility = Visibility.Visible;
                if (RecycleBinDataGrid != null) RecycleBinDataGrid.Visibility = Visibility.Collapsed;
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
                if (RecycleBinDataGrid != null) RecycleBinDataGrid.Visibility = Visibility.Visible;
                if (RecycleBinScrollViewer != null) RecycleBinScrollViewer.Visibility = Visibility.Collapsed;
                if (RecycleBinDataGrid != null) RecycleBinDataGrid.ItemsSource = _recycleBin;
            }
            else
            {
                // Show grid view
                if (RecycleBinDataGrid != null) RecycleBinDataGrid.Visibility = Visibility.Collapsed;
                if (RecycleBinScrollViewer != null) RecycleBinScrollViewer.Visibility = Visibility.Visible;
                if (RecycleBinResultsPanel != null) RecycleBinResultsPanel.Children.Clear();

                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // Try to constrain wrap width immediately (may be 0 on first layout).
                double wrapWidth = RecycleBinScrollViewer?.ViewportWidth ?? 0;
                if (double.IsNaN(wrapWidth) || wrapWidth <= 0)
                    wrapWidth = RecycleBinScrollViewer?.ActualWidth ?? 0;
                if (!double.IsNaN(wrapWidth) && wrapWidth > 0)
                    wrap.Width = wrapWidth;

                RecycleBinResultsPanel?.Children.Add(wrap);

                // Ensure we update the WrapPanel when the RecycleBin ScrollViewer reports its size
                if (RecycleBinScrollViewer != null)
                {
                    RecycleBinScrollViewer.SizeChanged -= RecycleBinScrollViewer_SizeChanged;
                    RecycleBinScrollViewer.SizeChanged += RecycleBinScrollViewer_SizeChanged;
                }

                // One-time LayoutUpdated handler for the recycle-bin WrapPanel so it reflows
                void recycleLayoutHandler(object? s, EventArgs ev)
                {
                    try
                    {
                        double w = RecycleBinScrollViewer?.ViewportWidth ?? 0;
                        if (double.IsNaN(w) || w <= 0)
                            w = RecycleBinScrollViewer?.ActualWidth ?? 0;
                        if (!double.IsNaN(w) && w > 0)
                        {
                            wrap.Width = w;
                            wrap.MinWidth = w;
                            wrap.MaxWidth = w;
                            wrap.InvalidateMeasure();
                            wrap.UpdateLayout();
                            if (RecycleBinScrollViewer != null) RecycleBinScrollViewer.LayoutUpdated -= recycleLayoutHandler;
                        }
                    }
                    catch
                    {
                        if (RecycleBinScrollViewer != null) RecycleBinScrollViewer.LayoutUpdated -= recycleLayoutHandler;
                    }
                }

                if (RecycleBinScrollViewer != null) RecycleBinScrollViewer.LayoutUpdated += recycleLayoutHandler;

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
                var file = clickedBorder?.Tag as DeletedFileItem;

                // Guard: tag might not be a DeletedFileItem
                if (file == null)
                    return;

                bool isCtrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

                if (isCtrlPressed)
                {
                    // Ctrl+Click for multi-select: toggle this item
                    if (_selectedRecycleBinItems.Contains(file))
                    {
                        _selectedRecycleBinItems.Remove(file);
                        clickedBorder!.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["BorderBrush"];
                        clickedBorder!.BorderThickness = new Thickness(1);
                    }
                    else
                    {
                        _selectedRecycleBinItems.Add(file);
                        clickedBorder!.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
                        clickedBorder!.BorderThickness = new Thickness(3);
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
                MaxWidth = gridSize,
                MaxHeight = gridSize,
                Stretch = Stretch.Uniform,
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
            if (RecycleBinPanel?.Visibility == Visibility.Visible)
            {
                // In RecycleBin - don't update scan file counts
                return;
            }

            int count;
            // Count from grid view if active
            if (_currentViewMode != "list")
            {
                count = _selectedGridItems?.Count ?? 0;
                Log.Info($"UpdateDeleteCount - Grid view, selected items: {count}");
            }
            else
            {
                // In list view, use ResultsDataGrid (not ResultsListView)
                count = ResultsDataGrid?.SelectedItems.Count ?? 0;
                Log.Info($"UpdateDeleteCount - List view (DataGrid), selected items: {count}");
            }

            Log.Info($"UpdateDeleteCount - Final count: {count}");
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void RecoverSelectedFiles()
        {
            // Get selected items based on current view mode
            List<DeletedFileItem> selectedItems;
            if (_currentViewMode != "list")
            {
                selectedItems = [.. _selectedRecycleBinItems];
            }
            else
            {
                selectedItems = [.. RecycleBinDataGrid.SelectedItems.Cast<DeletedFileItem>()];
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
                        if (item.FilePath != null && RestoreFromRecycleBin(item.FilePath))
                        {
                            _recycleBin.Remove(item);
                            successCount++;
                        }
                        else
                        {
                            failedFiles.Add(item.FileName ?? "<unknown>");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedFiles.Add($"{item.FileName}: {ex.Message}");
                    }
                }

                // Clear any cached selection state to avoid stale references
                _selectedRecycleBinItems.Clear();

                // Refresh UI after changes
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

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private bool RestoreFromRecycleBin(string filePath)
        {
            try
            {
                // Use Shell32 COM to restore from Recycle Bin (dynamic invocation)
                Type? shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;

                var shellObj = Activator.CreateInstance(shellType);
                if (shellObj == null) return false;

                dynamic shell = shellObj;
                dynamic recycleBin = shell.NameSpace(10); // 10 = Recycle Bin

                if (recycleBin == null)
                {
                    Log.Error("Failed to access Recycle Bin");
                    return false;
                }

                string fileName = Path.GetFileName(filePath);
                Log.Info($"Looking for file: {filePath}");

                foreach (dynamic item in recycleBin.Items())
                {
                    try
                    {
                        // Get item name and path
                        string itemName = item.Name;
                        string itemPath = item.Path;

                        Log.Info($"Checking item: {itemName} at {itemPath}");

                        // Try to match by name first (since path in recycle bin is different)
                        if (itemName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Info($"Found matching file by name: {itemName}");

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
                                    Log.Info($"Successfully restored: {filePath}");
                                    return true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error checking item: {ex.Message}");
                    }
                }

                Log.Info($"File not found in recycle bin: {fileName}");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"RestoreFromRecycleBin error: {ex.Message}");
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
                    Log.Info($"Available verb: {verbName}");

                    if (verbName.ToUpper().Contains(verb.ToUpper()))
                    {
                        Log.Info($"Executing verb: {verbName}");
                        itemVerb.DoIt();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"DoVerbs error: {ex.Message}");
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

    /// <summary>Represents a file tracked in the application's recycle bin UI.</summary>
    public class DeletedFileItem
    {
        public string? FileName { get; set; }
        public string? FilePath { get; set; }
        public long FileSize { get; set; }
        public string? FileSizeFormatted { get; set; }
        public DateTime DeletedTime { get; set; }
        public FileItemViewModel? OriginalViewModel { get; set; }
        public BitmapImage? Thumbnail { get; set; }
    }
}
