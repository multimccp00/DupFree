using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;

namespace DupFree.Models
{
    /// <summary>ViewModel representing a tracked file and its preview metadata used by the UI.</summary>
    public class FileItemViewModel : INotifyPropertyChanged
    {
        private BitmapImage? _thumbnail;
        private string _sizeFormatted = string.Empty;
        private bool _isSelected;

        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime ModifiedDate { get; set; }
        public string FileHash { get; set; } = string.Empty;

        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail != value)
                {
                    _thumbnail = value;
                    OnPropertyChanged(nameof(Thumbnail));
                }
            }
        }

        private BitmapImage? _animatedThumbnail;
        public BitmapImage? AnimatedThumbnail
        {
            get => _animatedThumbnail;
            set
            {
                if (_animatedThumbnail != value)
                {
                    _animatedThumbnail = value;
                    OnPropertyChanged(nameof(AnimatedThumbnail));
                }
            }
        }

        // Manual-frame animator cache (used for robust GIF hover animation)
        private BitmapSource[] _animatedFrames = [];
        public BitmapSource[] AnimatedFrames
        {
            get => _animatedFrames;
            set
            {
                if (_animatedFrames != value)
                {
                    _animatedFrames = value;
                    OnPropertyChanged(nameof(AnimatedFrames));
                }
            }
        }

        private int[] _animatedFrameDelays = [];
        public int[] AnimatedFrameDelays
        {
            get => _animatedFrameDelays;
            set
            {
                if (_animatedFrameDelays != value)
                {
                    _animatedFrameDelays = value;
                    OnPropertyChanged(nameof(AnimatedFrameDelays));
                }
            }
        }

        public string SizeFormatted
        {
            get => _sizeFormatted;
            set
            {
                if (_sizeFormatted != value)
                {
                    _sizeFormatted = value;
                    OnPropertyChanged(nameof(SizeFormatted));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public bool IsPreviewable => Services.ImagePreviewService.IsPreviewableImage(FilePath);
        public int DupCount { get; set; }
        public string DupSpace { get; set; } = string.Empty;

        /// <summary>Create a <see cref="FileItemViewModel"/> from a <see cref="FileInfo"/> instance.</summary>
        /// <param name="fileInfo">FileInfo for the file.</param>
        /// <param name="hash">Optional precomputed file hash.</param>
        /// <param name="loadThumbnail">Whether to attempt loading an image thumbnail.</param>
        /// <returns>Initialized <see cref="FileItemViewModel"/>.</returns>
        public static FileItemViewModel FromFileInfo(FileInfo fileInfo, string? hash = null, bool loadThumbnail = true)
        {
            var item = new FileItemViewModel
            {
                FilePath = fileInfo.FullName,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                ModifiedDate = fileInfo.LastWriteTime,
                FileHash = hash ?? string.Empty,
                SizeFormatted = Services.ImagePreviewService.FormatFileSize(fileInfo.Length, Services.SettingsService.CurrentSizeUnit)
            };

            if (loadThumbnail && item.IsPreviewable)
            {
                item.Thumbnail = Services.ImagePreviewService.GetThumbnail(fileInfo.FullName);
            }

            return item;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString()
        {
            return $"{FileName} - {SizeFormatted} - {FilePath}";
        }
    }

    /// <summary>ViewModel for UI representation of a duplicate file group.</summary>
    public class DuplicateGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        public string GroupHash { get; set; } = string.Empty;
        public List<FileItemViewModel> Files { get; set; } = [];

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                }
            }
        }

        public long TotalWastedSpace => (Files.Count - 1) * Files[0].FileSize;
        public string TotalWastedSpaceFormatted => Services.ImagePreviewService.FormatFileSize(TotalWastedSpace);

        public int DupCount => Files?.Count ?? 0;
        public string DupSpace => TotalWastedSpaceFormatted;
        public string RepresentativeName => Files != null && Files.Count > 0 ? Files[0].FileName : string.Empty;
        public string RepresentativePath => Files != null && Files.Count > 0 ? Files[0].FilePath : string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>ViewModel for a group of visually similar images used by the Similar Images UI panel.</summary>
    public class SimilarImageGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        private bool _isSelected = false;

        public string GroupId { get; set; } = string.Empty;
        public ObservableCollection<FileItemViewModel> Images { get; set; } = [];
        public double SimilarityScore { get; set; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public int ImageCount => Images?.Count ?? 0;
        public string SimilarityPercentage => $"{SimilarityScore:P0}";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
