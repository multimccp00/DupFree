using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.Threading;
using DupFree.Models;
using DupFree.Services;
using Microsoft.VisualBasic.FileIO;

namespace DupFree.Views
{
    public partial class SimilarImagesPanel : UserControl
    {
        private ObservableCollection<SimilarImageGroupViewModel> _similarGroups = new();
        private SimilarImageService _similarImageService;
        private List<string> _currentDirectories = new();
        private CancellationTokenSource _scanCancellation;
        private bool _isScanning = false;
        private string _lastDistanceReport = "";

        public SimilarImagesPanel()
        {
            InitializeComponent();
            _similarImageService = new SimilarImageService();
            DataContext = _similarGroups;

            // Setup event handlers
            _similarImageService.OnStatusChanged += (status) => Dispatcher.Invoke(() => UpdateStatus(status));
            _similarImageService.OnProgressChanged += (progress) => Dispatcher.Invoke(() => UpdateProgress(progress));
            
            // Monitor for selection changes
            _similarGroups.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                {
                    foreach (SimilarImageGroupViewModel group in e.NewItems)
                    {
                        group.PropertyChanged += (sender, args) =>
                        {
                            if (args.PropertyName == nameof(SimilarImageGroupViewModel.IsSelected))
                                UpdateDeleteButtonCount();
                        };

                        foreach (var image in group.Images)
                        {
                            image.PropertyChanged += (sender, args) =>
                            {
                                if (args.PropertyName == nameof(FileItemViewModel.IsSelected))
                                    UpdateDeleteButtonCount();
                            };
                        }

                        group.Images.CollectionChanged += (sender, args) =>
                        {
                            if (args.NewItems != null)
                            {
                                foreach (FileItemViewModel image in args.NewItems)
                                {
                                    image.PropertyChanged += (imgSender, imgArgs) =>
                                    {
                                        if (imgArgs.PropertyName == nameof(FileItemViewModel.IsSelected))
                                            UpdateDeleteButtonCount();
                                    };
                                }
                            }
                        };
                    }
                }
            };
        }

        public void SetDirectories(List<string> directories)
        {
            _currentDirectories = directories;
        }

        private async void ScanSimilarButton_Click(object sender, RoutedEventArgs e)
        {
            // If already scanning, stop it
            if (_isScanning)
            {
                _scanCancellation?.Cancel();
                ScanSimilarButton.Content = "Stopping...";
                ScanSimilarButton.IsEnabled = false;
                return;
            }

            if (_currentDirectories.Count == 0)
            {
                MessageBox.Show("Please select directories first.", "No Directories", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _scanCancellation = new CancellationTokenSource();
            _isScanning = true;
            ScanSimilarButton.Content = "Stop";
            StatusText.Text = "Scanning...";
            _similarGroups.Clear();
            NoSimilarPlaceholder.Visibility = Visibility.Collapsed;
            SimilarGroupsItemsControl.Visibility = Visibility.Visible;

            // Track groups by ID for streaming updates
            var groupViewModels = new Dictionary<string, SimilarImageGroupViewModel>();

            // Subscribe to streaming events
            Action<SimilarImageGroup> onGroupFound = (group) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var vmGroup = new SimilarImageGroupViewModel
                    {
                        GroupId = group.GroupId,
                        SimilarityScore = group.SimilarityScore
                    };

                    foreach (var image in group.Images)
                    {
                        vmGroup.Images.Add(image);
                        image.PropertyChanged += (s, e2) =>
                        {
                            if (e2.PropertyName == nameof(FileItemViewModel.IsSelected))
                                UpdateDeleteButtonCount();
                        };
                    }

                    groupViewModels[group.GroupId] = vmGroup;
                    _similarGroups.Add(vmGroup);
                    StatusText.Text = $"Found {_similarGroups.Count} groups (scanning...)";
                });
            };

            Action<string, FileItemViewModel> onImageAdded = (groupId, image) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (groupViewModels.TryGetValue(groupId, out var vmGroup))
                    {
                        vmGroup.Images.Add(image);
                        image.PropertyChanged += (s, e2) =>
                        {
                            if (e2.PropertyName == nameof(FileItemViewModel.IsSelected))
                                UpdateDeleteButtonCount();
                        };
                    }
                });
            };

            _similarImageService.OnGroupFound += onGroupFound;
            _similarImageService.OnImageAddedToGroup += onImageAdded;

            try
            {
                double maxDistance = MaxDistanceSlider.Value;
                StatusText.Text = $"Scanning (SSIM ≥ {maxDistance:F0}%)...";

                // This now streams groups via events while running
                var groups = await _similarImageService.FindSimilarImagesAsync(
                    _currentDirectories,
                    maxDistance,
                    showClosestPairsOnly: false,
                    closestPairCount: 20,
                    cancellationToken: _scanCancellation.Token
                );

                StatusText.Text = $"Done! Found {_similarGroups.Count} groups";
                _lastDistanceReport = "";

                NoSimilarPlaceholder.Visibility = _similarGroups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                SimilarGroupsItemsControl.Visibility = _similarGroups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = $"Stopped. Found {_similarGroups.Count} groups";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Error scanning: {ex.Message}\n\n{ex.StackTrace}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Unsubscribe streaming events
                _similarImageService.OnGroupFound -= onGroupFound;
                _similarImageService.OnImageAddedToGroup -= onImageAdded;

                _isScanning = false;
                ScanSimilarButton.Content = "Scan for Similar";
                ScanSimilarButton.IsEnabled = true;
                if (string.IsNullOrEmpty(StatusText.Text) || StatusText.Text == "Scanning...")
                    StatusText.Text = $"Done! Found {_similarGroups.Count} groups";
            }
        }

        private async void ClosestPairsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentDirectories.Count == 0)
            {
                MessageBox.Show("Please select directories first.", "No Directories", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _scanCancellation = new CancellationTokenSource();
            ClosestPairsButton.IsEnabled = false;
            StatusText.Text = "Finding closest pairs...";

            try
            {
                double maxDistance = MaxDistanceSlider.Value;
                var groups = await _similarImageService.FindSimilarImagesAsync(
                    _currentDirectories,
                    maxDistance,
                    showClosestPairsOnly: true,
                    closestPairCount: 30,
                    cancellationToken: _scanCancellation.Token
                );

                StatusText.Text = $"Found {groups.Count} closest pairs";

                _similarGroups.Clear();
                foreach (var group in groups)
                {
                    var vmGroup = new SimilarImageGroupViewModel
                    {
                        GroupId = group.GroupId,
                        SimilarityScore = group.SimilarityScore
                    };

                    foreach (var image in group.Images)
                    {
                        vmGroup.Images.Add(image);
                        image.PropertyChanged += (s, e2) =>
                        {
                            if (e2.PropertyName == nameof(FileItemViewModel.IsSelected))
                            {
                                UpdateDeleteButtonCount();
                            }
                        };
                    }

                    _similarGroups.Add(vmGroup);
                }

                NoSimilarPlaceholder.Visibility = _similarGroups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                SimilarGroupsItemsControl.Visibility = _similarGroups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Error scanning: {ex.Message}\n\n{ex.StackTrace}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ClosestPairsButton.IsEnabled = true;
            }
        }

        private void AutoSelectButton_Click(object sender, RoutedEventArgs e)
        {
            PerformAutoSelect();
        }

        private void PerformAutoSelect()
        {
            // Auto-select based on settings
            foreach (var group in _similarGroups)
            {
                if (group.Images.Count > 1)
                {
                    SelectImagesBySettings(group);
                }
            }

            UpdateDeleteButtonCount();
        }

        private void SelectImagesBySettings(SimilarImageGroupViewModel group)
        {
            // Build preferences list
            var preferences = new List<string>();
            
            if (SettingsService.AutoSelectKeepUncompressed)
                preferences.Add("keep_uncompressed");
            if (SettingsService.AutoSelectKeepHigherResolution)
                preferences.Add("keep_higher_res");
            if (SettingsService.AutoSelectKeepLargerFilesize)
                preferences.Add("keep_larger");

            // Mark images for deletion based on preferences
            var imagesToKeep = new HashSet<FileItemViewModel>();
            imagesToKeep.Add(group.Images[0]); // Always keep first as default

            foreach (var preference in preferences)
            {
                switch (preference)
                {
                    case "keep_uncompressed":
                        KeepUncompressedFormats(group.Images.ToList(), imagesToKeep);
                        break;
                    case "keep_higher_res":
                        KeepHigherResolution(group.Images.ToList(), imagesToKeep);
                        break;
                    case "keep_larger":
                        KeepLargerFilesize(group.Images.ToList(), imagesToKeep);
                        break;
                }
            }

            // Select all images NOT in the keep list
            foreach (var image in group.Images)
            {
                if (!imagesToKeep.Contains(image))
                {
                    image.IsSelected = true;
                }
            }
        }

        private void KeepUncompressedFormats(List<FileItemViewModel> images, HashSet<FileItemViewModel> imagesToKeep)
        {
            var uncompressed = new[] { ".bmp", ".tiff", ".tif", ".png" };
            var toRemove = images.Where(img => !uncompressed.Contains(Path.GetExtension(img.FilePath).ToLower())).ToList();
            foreach (var img in toRemove)
            {
                imagesToKeep.Remove(img);
            }
        }

        private void KeepHigherResolution(List<FileItemViewModel> images, HashSet<FileItemViewModel> imagesToKeep)
        {
            try
            {
                var imageData = images.Select(img => (
                    Image: img,
                    Bitmap: new BitmapImage() { UriSource = new Uri(img.FilePath), CacheOption = BitmapCacheOption.OnLoad }
                )).ToList();

                if (imageData.Count == 0) return;

                var maxResolution = imageData.Max(x => x.Bitmap.Width * x.Bitmap.Height);
                var imagesToRemove = imageData.Where(x => (x.Bitmap.Width * x.Bitmap.Height) < maxResolution).ToList();

                foreach (var imgData in imagesToRemove)
                {
                    imagesToKeep.Remove(imgData.Image);
                }
            }
            catch { }
        }

        private void KeepLargerFilesize(List<FileItemViewModel> images, HashSet<FileItemViewModel> imagesToKeep)
        {
            var maxSize = images.Max(img => img.FileSize);
            var imagesToRemove = images.Where(img => img.FileSize < maxSize).ToList();
            foreach (var img in imagesToRemove)
            {
                imagesToKeep.Remove(img);
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowAutoSelectSettings();
        }

        private void ShowAutoSelectSettings()
        {
            var window = new Window
            {
                Title = "Auto-Select Settings",
                Width = 500,
                Height = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = Application.Current.Resources["AppBackground"] as System.Windows.Media.Brush,
                Foreground = Application.Current.Resources["WindowForeground"] as System.Windows.Media.Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Top };

            var title = new TextBlock
            {
                Text = "What pictures will be selected first?",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = Application.Current.Resources["WindowForeground"] as System.Windows.Media.Brush
            };
            panel.Children.Add(title);

            var cb1 = new CheckBox
            {
                Content = "Uncompressed filetype (keep JPG/PNG, delete BMP/TIFF)",
                IsChecked = SettingsService.AutoSelectKeepUncompressed,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Application.Current.Resources["WindowForeground"] as System.Windows.Media.Brush
            };
            cb1.Checked += (s, e) => SettingsService.SetAutoSelectKeepUncompressed(true);
            cb1.Unchecked += (s, e) => SettingsService.SetAutoSelectKeepUncompressed(false);
            panel.Children.Add(cb1);

            var cb2 = new CheckBox
            {
                Content = "Lower resolution (keep higher, delete lower)",
                IsChecked = SettingsService.AutoSelectKeepHigherResolution,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Application.Current.Resources["WindowForeground"] as System.Windows.Media.Brush
            };
            cb2.Checked += (s, e) => SettingsService.SetAutoSelectKeepHigherResolution(true);
            cb2.Unchecked += (s, e) => SettingsService.SetAutoSelectKeepHigherResolution(false);
            panel.Children.Add(cb2);

            var cb3 = new CheckBox
            {
                Content = "Smaller filesize (keep larger, delete smaller)",
                IsChecked = SettingsService.AutoSelectKeepLargerFilesize,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = Application.Current.Resources["WindowForeground"] as System.Windows.Media.Brush
            };
            cb3.Checked += (s, e) => SettingsService.SetAutoSelectKeepLargerFilesize(true);
            cb3.Unchecked += (s, e) => SettingsService.SetAutoSelectKeepLargerFilesize(false);
            panel.Children.Add(cb3);

            var dirLabel = new TextBlock
            {
                Text = "Preferred directories (one per line):",
                Foreground = Application.Current.Resources["MutedForeground"] as System.Windows.Media.Brush,
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 12
            };
            panel.Children.Add(dirLabel);

            var dirBox = new TextBox
            {
                Text = SettingsService.AutoSelectPreferredDirectories,
                Height = 80,
                AcceptsReturn = true,
                Background = Application.Current.Resources["ControlBackground"] as System.Windows.Media.Brush,
                Foreground = Application.Current.Resources["WindowForeground"] as System.Windows.Media.Brush,
                Margin = new Thickness(0, 0, 0, 16)
            };
            panel.Children.Add(dirBox);

            var closeBtn = new Button
            {
                Content = "Close",
                Padding = new Thickness(20, 8, 20, 8),
                HorizontalAlignment = HorizontalAlignment.Right,
                Style = Application.Current.Resources["PrimaryButton"] as Style
            };
            closeBtn.Click += (s, e) =>
            {
                SettingsService.SetAutoSelectPreferredDirectories(dirBox.Text);
                SettingsService.SaveToFile();
                window.Close();
            };
            panel.Children.Add(closeBtn);

            window.Content = panel;
            window.ShowDialog();
        }

        private void DeleteSimilarSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedImages = _similarGroups
                .SelectMany(g => g.Images)
                .Where(img => img.IsSelected)
                .ToList();

            if (selectedImages.Count == 0)
            {
                MessageBox.Show("No images selected for deletion.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (SettingsService.ConfirmDelete)
            {
                var result = MessageBox.Show(
                    $"Delete {selectedImages.Count} selected image(s)?\n\nThis cannot be undone.",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Delete files and update UI
            int deletedCount = 0;
            foreach (var image in selectedImages)
            {
                try
                {
                    if (File.Exists(image.FilePath))
                    {
                        FileSystem.DeleteFile(image.FilePath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error deleting {image.FileName}: {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            // Remove deleted images from groups
            foreach (var group in _similarGroups.ToList())
            {
                foreach (var image in selectedImages)
                {
                    group.Images.Remove(image);
                }

                // Remove empty groups
                if (group.Images.Count < 2)
                {
                    _similarGroups.Remove(group);
                }
            }

            NoSimilarPlaceholder.Visibility = _similarGroups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdateDeleteButtonCount();

            MessageBox.Show($"Deleted {deletedCount} image(s).", "Delete Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ViewImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is FileItemViewModel image)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = image.FilePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void UpdateStatus(string status)
        {
            StatusText.Text = status;
            // Capture distance reports
            if (status.Contains("TOP 20"))
            {
                _lastDistanceReport = status;
            }
        }

        private void UpdateProgress(int progress)
        {
            // Progress updates can be displayed in the parent window
        }

        private void UpdateDeleteButtonCount()
        {
            var selectedCount = _similarGroups.SelectMany(g => g.Images).Count(img => img.IsSelected);
            DeleteSimilarSelectedButton.Content = $"Delete Selected ({selectedCount})";
        }

        public void Clear()
        {
            _similarGroups.Clear();
            NoSimilarPlaceholder.Visibility = Visibility.Visible;
            SimilarGroupsItemsControl.Visibility = Visibility.Collapsed;
        }

        private void MaxDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaxDistanceText != null)
            {
                MaxDistanceText.Text = $"{(int)e.NewValue}%";
            }
        }

        private FileItemViewModel _selectedForPreview = null;

        private void Thumbnail_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is FileItemViewModel fileItem)
            {
                _selectedForPreview = fileItem;
                UpdatePreviewPane(fileItem);
            }
        }

        private void UpdatePreviewPane(FileItemViewModel fileItem)
        {
            if (fileItem == null)
            {
                PreviewFileName.Text = "No image selected";
                PreviewFilePath.Text = "";
                PreviewFileSize.Text = "";
                PreviewDimensions.Text = "";
                PreviewImage.Source = null;
                return;
            }

            PreviewFileName.Text = fileItem.FileName;
            PreviewFilePath.Text = fileItem.FilePath;
            
            try
            {
                var fileInfo = new FileInfo(fileItem.FilePath);
                PreviewFileSize.Text = $"Size: {FormatBytes(fileInfo.Length)}";

                if (fileItem.Thumbnail is BitmapImage bitmap && bitmap.PixelWidth > 0)
                {
                    PreviewDimensions.Text = $"Dimensions: {bitmap.PixelWidth}×{bitmap.PixelHeight}";
                    PreviewImage.Source = bitmap;
                }
                else
                {
                    // Try to load full resolution image for preview
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.UriSource = new Uri(fileItem.FilePath);
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    
                    PreviewDimensions.Text = $"Dimensions: {bitmapImage.PixelWidth}×{bitmapImage.PixelHeight}";
                    PreviewImage.Source = bitmapImage;
                }
            }
            catch (Exception ex)
            {
                PreviewFileSize.Text = $"Error: {ex.Message}";
                PreviewImage.Source = fileItem.Thumbnail;
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void ToggleSelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedForPreview != null)
            {
                _selectedForPreview.IsSelected = !_selectedForPreview.IsSelected;
                ToggleSelectButton.Content = _selectedForPreview.IsSelected ? "Unmark for Deletion" : "Mark for Deletion";
                UpdateDeleteButtonCount();
            }
        }

        private void SelectGroupButton_Click(object sender, RoutedEventArgs e)
        {
            // Find which group this button belongs to
            var button = sender as Button;
            if (button?.DataContext is SimilarImageGroupViewModel group)
            {
                foreach (var image in group.Images)
                {
                    image.IsSelected = true;
                }
                UpdateDeleteButtonCount();
            }
        }
    }
}
