using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using DupFree.Models;
using DupFree.Services;
using System.Windows.Data;
using System.Collections.Generic;
using Microsoft.VisualBasic.FileIO;
using Ookii.Dialogs.Wpf;

namespace DupFree.Views
{
    public partial class MainWindow : Window
    {
        private DuplicateSearchService _searchService;
        private List<string> _selectedDirectories;
        private List<DuplicateGroupViewModel> _groupViewModels;
        private string _currentViewMode = "large_icons";
        private string _currentSortBy = "Name";
        private List<FileItemViewModel> _currentGridFiles = new();
        private readonly Dictionary<int, FrameworkElement> _realizedGridItems = new();
        private bool _isVirtualGridActive = false;
        private double _virtualItemWidth = 156;
        private double _virtualItemHeight = 196;
        private int _virtualColumns = 1;
        private int _selectedGridIndex = -1;
        private int _gridColumns = 0;
        private System.Threading.CancellationTokenSource _scanCancellation;
        private int _filesRendered = 0;
        private const int FILES_PER_BATCH = 500;  // Render 500 files at a time

        public MainWindow()
        {
            InitializeComponent();
            _searchService = new DuplicateSearchService();
            // Ensure service events update UI on dispatcher thread
            _searchService.OnStatusChanged += (status) => Dispatcher.Invoke(() => StatusText.Text = status);
            _searchService.OnProgressChanged += (progress) => Dispatcher.Invoke(() => ProgressBar.Value = progress);
            _selectedDirectories = new List<string>();
            _groupViewModels = new List<DuplicateGroupViewModel>();
            // Show large-icon grid by default (after collections are initialized)
            DisplayResults();

            // Initialize theme and unit comboboxes
            ThemeComboBox.SelectedIndex = Services.SettingsService.CurrentTheme == "dark" ? 1 : 0;
            UnitComboBox.SelectedIndex = (int)Services.SettingsService.CurrentSizeUnit;

            // Apply initial theme
            ApplyTheme(Services.SettingsService.CurrentTheme);

            Services.SettingsService.OnSettingsChanged += () =>
            {
                // Refresh sizes and theme when settings change
                RefreshSizes();
                ApplyTheme(Services.SettingsService.CurrentTheme);
            };
        }

        private void RefreshSizes()
        {
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

        private void ApplyTheme(string theme)
        {
            var appResources = Application.Current.Resources;
            if (theme == "dark")
            {
                appResources["AppBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30));
                appResources["TopBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40));
                appResources["PanelBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 24));
                appResources["WindowForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                appResources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
                appResources["ControlBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48));
                appResources["ControlForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 204, 204));
                appResources["BorderBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70));
                appResources["HeaderBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 45, 48));
                appResources["ScrollBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66));
                appResources["SeparatorBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 63, 70));
                appResources["AlternatingRowBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 37, 38));
            }
            else
            {
                appResources["AppBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));
                appResources["TopBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                appResources["PanelBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                appResources["WindowForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 17, 17));
                appResources["AccentBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 167, 69));
                appResources["ControlBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                appResources["ControlForeground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 34, 34));
                appResources["BorderBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 204, 204));
                appResources["HeaderBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 230, 230));
                appResources["ScrollBarBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224));
                appResources["SeparatorBrush"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 204, 204));
                appResources["AlternatingRowBackground"] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));
            }
            
            // Force refresh ComboBox styles
            LimitComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            SortComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            ThemeComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            UnitComboBox.Foreground = (System.Windows.Media.Brush)appResources["ControlForeground"];
            
            // Update Scan button style separately
            ScanButton.Background = appResources["AccentBrush"] as System.Windows.Media.Brush;
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
            ScanButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            ProgressBar.Value = 0;
            ResultsPanel.Children.Clear();

            // Create cancellation token source for this scan
            _scanCancellation = new System.Threading.CancellationTokenSource();

            // Progress callback updates UI with current/total hashed files
            var progress = new Progress<(int current, int total)>((p) =>
            {
                if (p.total > 0)
                {
                    ProgressBar.Value = (p.current * 100) / p.total;
                    StatusText.Text = $"Hashing {p.current}/{p.total}";
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

            var duplicates = await _searchService.FindDuplicatesAsync(_selectedDirectories, progress, limit, _scanCancellation.Token);

            _groupViewModels.Clear();
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

            ApplySorting();
            
            // Count total files
            int totalFiles = 0;
            foreach (var group in _groupViewModels)
                totalFiles += group.Files.Count;
            
            // Default to list view after scan, but keep grid available
            _currentViewMode = "list";
            EnableGridViewButtons();
            
            DisplayResults();
            
            ScanButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressBar.Value = 100;
        }

        private void DisableGridViewButtons()
        {
            // No-op: keep grid view available
        }

        private void EnableGridViewButtons()
        {
            IconViewButton.IsEnabled = true;
            LargeIconViewButton.IsEnabled = true;
            XLargeIconViewButton.IsEnabled = true;
            IconViewButton.ToolTip = "Icon View";
            LargeIconViewButton.ToolTip = "Large Icon View";
            XLargeIconViewButton.ToolTip = "Extra Large Icon View";
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _scanCancellation?.Cancel();
            StatusText.Text = "Scan cancelled";
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

        private async void DisplayResults()
        {
            _filesRendered = 0;  // Reset batch counter
            
            // Count total files first
            int totalFiles = 0;
            foreach (var group in _groupViewModels)
            {
                totalFiles += group.Files.Count;
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
                foreach (var group in _groupViewModels)
                {
                    var dupCount = group.Files?.Count ?? 0;
                    var dupSpace = group.TotalWastedSpaceFormatted;
                    foreach (var f in group.Files)
                    {
                        f.DupCount = dupCount;
                        f.DupSpace = dupSpace;
                        flat.Add(f);
                    }
                }

                // Use DataGrid for proper column binding
                ResultsDataGrid.ItemsSource = flat;
                
                StatusText.Text = $"Displaying {flat.Count} files in list view";
            }
            else if (_currentViewMode == "icons" || _currentViewMode == "large_icons" || _currentViewMode == "xlarge_icons")
            {
                ResultsListView.Visibility = Visibility.Collapsed;
                ResultsDataGrid.Visibility = Visibility.Collapsed;
                ResultsScrollViewer.Visibility = Visibility.Visible;
                ResultsPanel.Children.Clear();

                // Flatten all files
                _currentGridFiles.Clear();
                foreach (var group in _groupViewModels)
                {
                    _currentGridFiles.AddRange(group.Files);
                }

                // Create canvas for virtualized rendering
                var gridCanvas = new Canvas();
                ResultsPanel.Children.Add(gridCanvas);

                _isVirtualGridActive = true;
                SetupVirtualGrid(gridCanvas);

                StatusText.Text = $"Displaying {_currentGridFiles.Count} files (virtualized grid)";
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
                _virtualItemWidth = 156;
                _virtualItemHeight = 196;
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
            if (!_isVirtualGridActive || ResultsPanel.Children.Count == 0)
                return;

            if (ResultsPanel.Children[0] is Canvas canvas)
            {
                RecalculateVirtualGrid(canvas);
                UpdateVirtualGrid(canvas);
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
            var panel = new StackPanel
            {
                Width = 140,
                Height = 180,
                Margin = new Thickness(8),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top
            };

            // Always show full path on tooltip for quick location visibility
            panel.ToolTip = file.FilePath;

            // Thumbnail or icon (lazy-loaded)
            panel.Children.Add(CreatePreviewElement(file, 110));

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

            // Path below name (truncated) - skip for large counts
            if (_currentGridFiles.Count <= 2000)
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

            // Wrap in border for keyboard selection highlighting
            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0)
            };

            border.MouseLeftButtonDown += (s, e) =>
            {
                var idx = _currentGridFiles.IndexOf(file);
                if (idx >= 0)
                {
                    _selectedGridIndex = idx;
                    HighlightSelectedGridFile();
                }
                ResultsPanel.Focus();
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
                Margin = new Thickness(10),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top
            };

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

            // Wrap in border for keyboard selection highlighting (large icons too)
            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0)
            };

            border.MouseLeftButtonDown += (s, e) =>
            {
                var idx = _currentGridFiles.IndexOf(file);
                if (idx >= 0)
                {
                    _selectedGridIndex = idx;
                    HighlightSelectedGridFile();
                }
                ResultsPanel.Focus();
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
                Margin = new Thickness(12),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top
            };

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

            var border = new Border
            {
                Child = panel,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Transparent),
                Padding = new Thickness(0)
            };

            border.MouseLeftButtonDown += (s, e) =>
            {
                var idx = _currentGridFiles.IndexOf(file);
                if (idx >= 0)
                {
                    _selectedGridIndex = idx;
                    HighlightSelectedGridFile();
                }
                ResultsPanel.Focus();
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

            grid.Children.Add(image);

            if (file.IsPreviewable)
            {
                if (file.Thumbnail != null)
                {
                    image.Source = file.Thumbnail;
                    placeholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    EnsureThumbnailAsync(file, image, placeholder, (int)size);
                }
            }

            return grid;
        }

        private void EnsureThumbnailAsync(FileItemViewModel file, Image image, TextBlock placeholder, int size)
        {
            Task.Run(() =>
            {
                try
                {
                    var thumb = Services.ImagePreviewService.GetThumbnail(file.FilePath, size, size);
                    if (thumb == null)
                        return;

                    Dispatcher.Invoke(() =>
                    {
                        file.Thumbnail = thumb;
                        image.Source = thumb;
                        placeholder.Visibility = Visibility.Collapsed;
                    });
                }
                catch { }
            });
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

        private async Task DeleteFileAsync(FileItemViewModel file)
        {
            try
            {
                StatusText.Text = $"Deleting {file.FileName}...";
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

                // Remove from view models
                foreach (var group in _groupViewModels)
                {
                    var toRemove = group.Files.FirstOrDefault(f => f.FilePath == file.FilePath);
                    if (toRemove != null)
                    {
                        group.Files.Remove(toRemove);
                        break;
                    }
                }

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
                DisplayResults();

                // For list view, restore a sensible selection (next row or previous)
                if (_currentViewMode == "list")
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var count = ResultsListView.Items.Count;
                        if (count == 0) return;
                        int sel = oldListIndex >= 0 ? Math.Min(oldListIndex, count - 1) : 0;
                        ResultsListView.SelectedIndex = sel;
                        ResultsListView.ScrollIntoView(ResultsListView.SelectedItem);
                    }, System.Windows.Threading.DispatcherPriority.Input);
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
            if (IconViewButton.IsEnabled)
            {
                _currentViewMode = "icons";
                DisplayResults();
            }
        }

        private void LargeIconViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (LargeIconViewButton.IsEnabled)
            {
                _currentViewMode = "large_icons";
                DisplayResults();
            }
        }

        private void XLargeIconViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (XLargeIconViewButton.IsEnabled)
            {
                _currentViewMode = "xlarge_icons";
                DisplayResults();
            }
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

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeComboBox.SelectedItem is ComboBoxItem item)
            {
                var v = item.Content.ToString().ToLower();
                Services.SettingsService.SetTheme(v);
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
                if (ResultsDataGrid.SelectedItem is FileItemViewModel file)
                {
                    await DeleteFileAsync(file);
                }
            }
        }

        private void ResultsPanel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Handle keyboard navigation on grid view - use PreviewKeyDown to capture before bubbling
            if (_currentGridFiles.Count == 0) return;

            if (e.Key == Key.Delete && _selectedGridIndex >= 0 && _selectedGridIndex < _currentGridFiles.Count)
            {
                var file = _currentGridFiles[_selectedGridIndex];
#pragma warning disable CS4014
                DeleteFileAsync(file);
#pragma warning restore CS4014
                e.Handled = true;
            }
            else if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                HandleGridNavigation(e.Key);
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
    }
}
