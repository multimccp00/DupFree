using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DupFree.Services
{
    /// <summary>Represents a group of files that are duplicates of each other.</summary>
    public class DuplicateFileGroup
    {
        /// <summary>Optional computed hash for the group (may be empty if not computed).</summary>
        public string FileHash { get; set; } = string.Empty;
        /// <summary>Files that belong to this duplicate group.</summary>
        public List<FileInfo> Files { get; set; } = [];
    }

    /// <summary>Service that locates duplicate files by grouping on file name and size (fast and robust).</summary>
    // Reverted: reliable, minimal duplicate search  group by (Name, Size) only.
    public class DuplicateSearchService
    {
        private List<DuplicateFileGroup> _duplicates = [];

        /// <summary>True if any folder/file could not be accessed due to permissions during the last scan.</summary>
        public bool HadAccessErrors { get; private set; }
        /// <summary>Total number of files scanned during the last search.</summary>
        public int TotalFilesScanned { get; private set; }
        /// <summary>Raised with human-readable status messages during scanning.</summary>
        public event Action<string>? OnStatusChanged;

        /// <summary>Asynchronously finds duplicate files under the supplied directories.</summary>
        /// <param name="directories">Directories to scan (recursively).</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="maxFilesToProcess">Optional limit to the number of files to return.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of duplicate file groups.</returns>
        public async Task<List<DuplicateFileGroup>> FindDuplicatesAsync(List<string> directories, IProgress<(int current, int total)>? progress = null, int? maxFilesToProcess = null, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => FindDuplicatesInternal(directories, progress, maxFilesToProcess, cancellationToken), cancellationToken);
        }

        private List<DuplicateFileGroup> FindDuplicatesInternal(List<string> directories, IProgress<(int current, int total)>? progress = null, int? maxFilesToProcess = null, CancellationToken cancellationToken = default)
        {
            _duplicates.Clear();
            TotalFilesScanned = 0;
            HadAccessErrors = false;

            OnStatusChanged?.Invoke("Collecting files...");
            progress?.Report((0, 100));

            var allFiles = new ConcurrentBag<FileInfo>();
            foreach (var dir in directories)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try { CollectFilesSequential(dir, allFiles, cancellationToken, progress); } catch { }
            }

            if (cancellationToken.IsCancellationRequested) { _duplicates.Clear(); return _duplicates; }

            var fileList = allFiles.ToList();
            TotalFilesScanned = fileList.Count;
            OnStatusChanged?.Invoke($"Found {TotalFilesScanned} total files. Filtering...");

            var filtered = new List<FileInfo>();
            foreach (var file in fileList)
            {
                try
                {
                    if ((file.Attributes & FileAttributes.Hidden) != 0 || (file.Attributes & FileAttributes.System) != 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    filtered.Add(file);
                }
                catch { }
            }

            progress?.Report((50, 100));
            OnStatusChanged?.Invoke($"Found {filtered.Count} files after filtering. Grouping by name and size...");

            if (filtered.Count == 0)
            {
                progress?.Report((100, 100));
                OnStatusChanged?.Invoke("No files found (all files filtered)");
                if (HadAccessErrors)
                    OnStatusChanged?.Invoke("Some directories could not be accessed due to permissions and were skipped.");
                return _duplicates;
            }

            var groups = filtered.GroupBy(f => (f.Length, f.Name)).Where(g => g.Count() > 1).ToList();
            _duplicates.Clear();
            foreach (var g in groups) _duplicates.Add(new DuplicateFileGroup { FileHash = string.Empty, Files = [.. g] });

            OnStatusChanged?.Invoke($"Found {_duplicates.Count} duplicate groups ({_duplicates.Sum(d => d.Files.Count)} files)");
            if (HadAccessErrors)
                OnStatusChanged?.Invoke("Note: some files or folders could not be accessed and were skipped (permission denied).");
            progress?.Report((100, 100));

            if (maxFilesToProcess.HasValue)
            {
                var limited = new List<DuplicateFileGroup>();
                int taken = 0;
                foreach (var grp in _duplicates)
                {
                    if (taken >= maxFilesToProcess.Value) break;
                    var take = Math.Min(grp.Files.Count, maxFilesToProcess.Value - taken);
                    limited.Add(new DuplicateFileGroup { FileHash = grp.FileHash, Files = [.. grp.Files.Take(take)] });
                    taken += take;
                }
                _duplicates = limited;
            }

            return _duplicates;
        }

        // File collection (unchanged, robust)
        private void CollectFilesSequential(string rootPath, ConcurrentBag<FileInfo> allFiles, CancellationToken cancellationToken, IProgress<(int current, int total)>? progress = null)
        {
            try
            {
                var dirs = new Queue<(string path, int depth)>();
                dirs.Enqueue((rootPath, 0));
                int maxDepth = 100;
                int dirCount = 0; int fileCount = 0; int errorCount = 0;
                var statusTimer = Stopwatch.StartNew();

                while (dirs.Count > 0)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    (string currentDir, int currentDepth) = dirs.Dequeue();
                    if (string.IsNullOrEmpty(currentDir) || currentDepth > maxDepth) continue;

                    DirectoryInfo dirInfo;
                    try { dirInfo = new DirectoryInfo(currentDir); dirCount++; } catch { errorCount++; continue; }

                    try { var attrs = dirInfo.Attributes; if ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Hidden) != 0 || (attrs & FileAttributes.System) != 0) continue; } catch { errorCount++; continue; }

                    FileInfo[] files = Array.Empty<FileInfo>();
                    try { files = dirInfo.GetFiles(); } 
                    catch (UnauthorizedAccessException)
                    { errorCount++; HadAccessErrors = true; files = Array.Empty<FileInfo>(); }
                    catch { errorCount++; files = Array.Empty<FileInfo>(); }
                    foreach (var file in files) try { allFiles.Add(file); fileCount++; } catch { }

                    if (statusTimer.ElapsedMilliseconds > 500)
                    {
                        OnStatusChanged?.Invoke($"Collecting files... {fileCount:N0} files, {dirCount:N0} dirs");
                        int estimatedProgress = Math.Min(60, (int)(60.0 * (1.0 - 1.0 / (1.0 + fileCount / 1000.0))));
                        progress?.Report((estimatedProgress, 100));
                        statusTimer.Restart();
                    }

                    DirectoryInfo[] subdirs = Array.Empty<DirectoryInfo>();
                    try { subdirs = dirInfo.GetDirectories(); } 
                    catch (UnauthorizedAccessException)
                    { errorCount++; HadAccessErrors = true; subdirs = Array.Empty<DirectoryInfo>(); }
                    catch { errorCount++; subdirs = Array.Empty<DirectoryInfo>(); }
                    foreach (var subDir in subdirs) try { dirs.Enqueue((subDir.FullName, currentDepth + 1)); } catch { }
                }

                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "collection_diag.log"), $"Directories scanned: {dirCount}\nFiles found: {fileCount}\nErrors: {errorCount}\n"); } catch { }
            }
            catch { }
        }
    }
}
