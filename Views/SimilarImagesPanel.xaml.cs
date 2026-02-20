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
        private readonly ObservableCollection<SimilarImageGroupViewModel> _similarGroups = [];
        private readonly SimilarImageService _similarImageService;
        private List<string> _currentDirectories = [];
        private CancellationTokenSource? _scanCancellation;
        private bool _isScanning = false;
        private DateTime _scanStartTime;
        private readonly System.Windows.Threading.DispatcherTimer _timerDisplay;

        public SimilarImagesPanel()
        {
            InitializeComponent();
            _similarImageService = new SimilarImageService();
            DataContext = _similarGroups;

            InitializeAutoSelectSettingsUI();

            // Setup event handlers
            _similarImageService.OnStatusChanged += (status) => Dispatcher.Invoke(() => UpdateStatus(status));
            _similarImageService.OnProgressChanged += (progress) => Dispatcher.Invoke(() => UpdateProgress(progress));

            // Timer for elapsed time display (disabled by default)
            _timerDisplay = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timerDisplay.Tick += (s, e) => UpdateTimerDisplay();

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

        private void InitializeAutoSelectSettingsUI()
        {
            AutoSelectKeepUncompressedCheckBox.IsChecked = SettingsService.AutoSelectKeepUncompressed;
            AutoSelectKeepHigherResolutionCheckBox.IsChecked = SettingsService.AutoSelectKeepHigherResolution;
            AutoSelectKeepLargerFilesizeCheckBox.IsChecked = SettingsService.AutoSelectKeepLargerFilesize;
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
                if (Window.GetWindow(this) is MainWindow mainWindow)
                {
                    if (!mainWindow.TrySelectDirectories(autoScan: false))
                        return;
                }
                else
                {
                    MessageBox.Show("Please select directories first.", "No Directories", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            _scanCancellation = new CancellationTokenSource();
            _isScanning = true;
            ScanSimilarButton.Content = "Stop";
            StatusText.Text = "";
            SimilarScanProgressIndicator.Width = 0;
            SimilarScanProgressText.Text = "0%";
            SimilarScanProgressBarContainer.Visibility = Visibility.Visible;
            SimilarScanProgressText.Visibility = Visibility.Visible;
            ProgressLabel.Visibility = Visibility.Visible;
            // Show/hide timer based on SettingsService (or default to hidden)
            bool showTimer = SettingsService.GetShowScanTimer();
            SimilarScanTimerText.Visibility = showTimer ? Visibility.Visible : Visibility.Collapsed;
            if (showTimer)
            {
                _scanStartTime = DateTime.Now;
                _timerDisplay.Start();
            }
            _similarGroups.Clear();
            NoSimilarPlaceholder.Visibility = Visibility.Collapsed;
            SimilarGroupsItemsControl.Visibility = Visibility.Visible;

            // Track groups by ID for streaming updates
            var groupViewModels = new Dictionary<string, SimilarImageGroupViewModel>();

            // Subscribe to streaming events
            void onGroupFound(SimilarImageGroup group)
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
            }

            void onImageAdded(string groupId, FileItemViewModel image)
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
            }

            _similarImageService.OnGroupFound += onGroupFound;
            _similarImageService.OnImageAddedToGroup += onImageAdded;

            try
            {
                double maxDistance = MaxDistanceSlider.Value;
                StatusText.Text = "";

                // This now streams groups via events while running
                var groups = await _similarImageService.FindSimilarImagesAsync(
                    _currentDirectories,
                    maxDistance,
                    showClosestPairsOnly: false,
                    closestPairCount: 20,
                    cancellationToken: _scanCancellation.Token,
                    useGpuSsim: true
                );

                StatusText.Text = $"Done! Found {_similarGroups.Count} groups";

                NoSimilarPlaceholder.Visibility = _similarGroups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                SimilarGroupsItemsControl.Visibility = _similarGroups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "";
            }
            catch (Exception ex)
            {
                StatusText.Text = "";
                MessageBox.Show($"Error scanning: {ex.Message}\n\n{ex.StackTrace}", "Scan Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Unsubscribe streaming events
                _similarImageService.OnGroupFound -= onGroupFound;
                _similarImageService.OnImageAddedToGroup -= onImageAdded;

                _isScanning = false;
                _timerDisplay.Stop();
                ScanSimilarButton.Content = "Scan for Similar";
                ScanSimilarButton.IsEnabled = true;
                UpdateProgress(100);
                StatusText.Text = "";
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
            var imagesToKeep = new HashSet<FileItemViewModel>
            {
                group.Images[0] // Always keep first as default
            };

            foreach (var preference in preferences)
            {
                switch (preference)
                {
                    case "keep_uncompressed":
                        KeepUncompressedFormats([.. group.Images], imagesToKeep);
                        break;
                    case "keep_higher_res":
                        KeepHigherResolution([.. group.Images], imagesToKeep);
                        break;
                    case "keep_larger":
                        KeepLargerFilesize([.. group.Images], imagesToKeep);
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

        private void AutoSelectSettingsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            SettingsService.SetAutoSelectKeepUncompressed(AutoSelectKeepUncompressedCheckBox.IsChecked == true);
            SettingsService.SetAutoSelectKeepHigherResolution(AutoSelectKeepHigherResolutionCheckBox.IsChecked == true);
            SettingsService.SetAutoSelectKeepLargerFilesize(AutoSelectKeepLargerFilesizeCheckBox.IsChecked == true);
            SettingsService.SaveToFile();
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
            if (_selectedForPreview != null)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _selectedForPreview.FilePath,
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
            StatusText.Text = "";
        }

        private void UpdateProgress(int progress)
        {
            if (progress < 0) progress = 0;
            if (progress > 100) progress = 100;
            if (SimilarScanProgressBarContainer != null && SimilarScanProgressIndicator != null)
            {
                var totalWidth = SimilarScanProgressBarContainer.ActualWidth;
                if (totalWidth <= 0)
                    totalWidth = SimilarScanProgressBarContainer.Width;
                if (double.IsNaN(totalWidth) || totalWidth <= 0)
                    totalWidth = 150;
                var availableWidth = Math.Max(0, totalWidth - 2);
                SimilarScanProgressIndicator.Width = availableWidth * (progress / 100.0);
            }
            SimilarScanProgressText.Text = $"{progress}%";
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
            SimilarScanProgressBarContainer.Visibility = Visibility.Collapsed;
            SimilarScanProgressText.Visibility = Visibility.Collapsed;
            ProgressLabel.Visibility = Visibility.Collapsed;
            SimilarScanTimerText.Visibility = Visibility.Collapsed;
            SimilarScanProgressIndicator.Width = 0;
        }

        private void MaxDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaxDistanceText != null)
            {
                MaxDistanceText.Text = $"{(int)e.NewValue}%";
            }
        }

        private FileItemViewModel? _selectedForPreview = null;

        private void Thumbnail_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is FileItemViewModel fileItem)
            {
                // Left click toggles selection; always update preview
                if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                {
                    fileItem.IsSelected = !fileItem.IsSelected;
                    UpdateDeleteButtonCount();
                }

                _selectedForPreview = fileItem;
                UpdatePreviewPane(fileItem);
            }
        }

        private void Thumbnail_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is Border border && border.DataContext is FileItemViewModel fileItem)
            {
                // Hover only updates preview, does not change selection
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
                SimilarScanProgressBarContainer.Visibility = Visibility.Collapsed;
                // Always load full resolution image for preview and real dimensions
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                SimilarScanProgressIndicator.Width = 0;
                bitmapImage.UriSource = new Uri(fileItem.FilePath);
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                PreviewDimensions.Text = $"Dimensions: {bitmapImage.PixelWidth}×{bitmapImage.PixelHeight}";
                PreviewImage.Source = bitmapImage;
            }
            catch (Exception ex)
            {
                PreviewFileSize.Text = $"Error: {ex.Message}";
                PreviewImage.Source = fileItem.Thumbnail;
            }
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = ["B", "KB", "MB", "GB"];
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private void UpdateTimerDisplay()
        {
            if (_isScanning)
            {
                TimeSpan elapsed = DateTime.Now - _scanStartTime;
                SimilarScanTimerText.Text = elapsed.ToString(@"mm\:ss");
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
