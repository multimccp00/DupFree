using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Media.Imaging;
using System.Linq;

namespace DupFree.Models
{
    public class FileItemViewModel : INotifyPropertyChanged
    {
        private BitmapImage _thumbnail;
        private string _sizeFormatted;
        private bool _isSelected;

        public string FilePath { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public DateTime ModifiedDate { get; set; }
        public string FileHash { get; set; }

        public BitmapImage Thumbnail
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
        public string DupSpace { get; set; }
        public int DisplayIndex { get; set; }

        public static FileItemViewModel FromFileInfo(FileInfo fileInfo, string hash = null, bool loadThumbnail = true)
        {
            var item = new FileItemViewModel
            {
                FilePath = fileInfo.FullName,
                FileName = fileInfo.Name,
                FileSize = fileInfo.Length,
                ModifiedDate = fileInfo.LastWriteTime,
                FileHash = hash,
                SizeFormatted = Services.ImagePreviewService.FormatFileSize(fileInfo.Length, Services.SettingsService.CurrentSizeUnit)
            };

            if (loadThumbnail && item.IsPreviewable)
            {
                item.Thumbnail = Services.ImagePreviewService.GetThumbnail(fileInfo.FullName);
            }

            return item;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString()
        {
            return $"{FileName} - {SizeFormatted} - {FilePath}";
        }
    }

    public class DuplicateGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded;
        public string GroupHash { get; set; }
        public List<FileItemViewModel> Files { get; set; } = new();

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

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class SimilarImageGroupViewModel : INotifyPropertyChanged
    {
        private bool _isExpanded = true;
        private bool _isSelected = false;

        public string GroupId { get; set; }
        public ObservableCollection<FileItemViewModel> Images { get; set; } = new();
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

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
