using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DupFree.Services
{
    public class DuplicateFileGroup
    {
        public string FileHash { get; set; }
        public List<FileInfo> Files { get; set; } = new();
    }

    public class DuplicateSearchService
    {
        private List<DuplicateFileGroup> _duplicates = new();
        public int TotalFilesScanned { get; private set; }
        public event Action<string> OnStatusChanged;

        public async Task<List<DuplicateFileGroup>> FindDuplicatesAsync(List<string> directories, IProgress<(int current, int total)> progress = null, int? maxFilesToProcess = null, System.Threading.CancellationToken cancellationToken = default)
        {
            // Run heavy lifting on background thread to keep UI responsive
            return await Task.Run(() => FindDuplicatesInternal(directories, progress, maxFilesToProcess, cancellationToken), cancellationToken);
        }

        private List<DuplicateFileGroup> FindDuplicatesInternal(List<string> directories, IProgress<(int current, int total)> progress = null, int? maxFilesToProcess = null, System.Threading.CancellationToken cancellationToken = default)
        {
            _duplicates.Clear();
            TotalFilesScanned = 0;
            
            OnStatusChanged?.Invoke("Collecting files...");
            progress?.Report((0, 100));

            // Collect files with error handling
            var allFiles = new ConcurrentBag<FileInfo>();
            
            foreach (var dir in directories)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    CollectFilesSequential(dir, allFiles, cancellationToken, progress);
                    OnStatusChanged?.Invoke($"Scanning... {allFiles.Count} files found");
                }
                catch (OperationCanceledException)
                {
                    // Cancelled
                    break;
                }
                catch
                {
                    // Skip this directory
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _duplicates.Clear();
                return _duplicates;
            }

            var fileList = allFiles.ToList();
            int totalFiles = fileList.Count;
            TotalFilesScanned = totalFiles;
            OnStatusChanged?.Invoke($"Found {totalFiles} total files. Filtering...");

            // Diagnostic: write collection stats
            try
            {
                var diagPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dup_diag.log");
                File.WriteAllText(diagPath, $"Total files collected: {totalFiles}\n");
            }
            catch { }

            // Filter: skip hidden/system/reparse files, keep ALL file sizes
            var filtered = new List<FileInfo>();
            foreach (var file in fileList)
            {
                try
                {
                    // Skip hidden/system files
                    if ((file.Attributes & FileAttributes.Hidden) != 0 ||
                        (file.Attributes & FileAttributes.System) != 0 ||
                        (file.Attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    // Keep ALL file sizes 
                    filtered.Add(file);
                }
                catch
                {
                    // Skip files we can't access
                }
            }

            OnStatusChanged?.Invoke($"Found {filtered.Count} files after filtering. Grouping by name and size...");
            progress?.Report((70, 100));

            if (filtered.Count == 0)
            {
                OnStatusChanged?.Invoke("No files found (all files filtered)");
                progress?.Report((100, 100));
                return _duplicates;
            }

            // GROUP BY (SIZE, NAME) - WizTree approach: duplicates have same name and size
            var nameSizeGroups = new Dictionary<(long, string), List<FileInfo>>();
            int groupingProcessed = 0;
            foreach (var file in filtered)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _duplicates.Clear();
                    return _duplicates;
                }
                var key = (file.Length, file.Name);
                if (!nameSizeGroups.TryGetValue(key, out var list))
                {
                    list = new List<FileInfo>();
                    nameSizeGroups[key] = list;
                }
                list.Add(file);
                groupingProcessed++;
                // Report progress: 70-90% for grouping phase
                if (groupingProcessed % 5000 == 0)
                    progress?.Report((70 + (int)(20.0 * groupingProcessed / filtered.Count), 100));
            }

            OnStatusChanged?.Invoke($"Found {nameSizeGroups.Count} unique (name, size) groups. Finding duplicates...");
            progress?.Report((90, 100));

            // Convert to results - files with same name and size are considered duplicates
            _duplicates.Clear();
            int duplicateGroupCount = 0;
            foreach (var group in nameSizeGroups.Values)
            {
                if (group.Count > 1)
                {
                    duplicateGroupCount++;
                    _duplicates.Add(new DuplicateFileGroup
                    {
                        FileHash = $"{group[0].Name}_{group[0].Length}",
                        Files = group
                    });
                }
            }

            int totalDuplicatesFound = _duplicates.Sum(g => g.Files.Count);
            if (totalDuplicatesFound == 0)
            {
                OnStatusChanged?.Invoke($"No duplicates found ({nameSizeGroups.Count} unique (name, size) groups, no matches)");
                return _duplicates;
            }

            OnStatusChanged?.Invoke($"Found {duplicateGroupCount} duplicate groups with {totalDuplicatesFound} total files");

            // Apply max limit if needed
            if (maxFilesToProcess.HasValue && totalDuplicatesFound > maxFilesToProcess.Value)
            {
                var limited = new List<DuplicateFileGroup>();
                int count = 0;
                foreach (var group in _duplicates)
                {
                    var toTake = Math.Min(group.Files.Count, maxFilesToProcess.Value - count);
                    if (toTake > 0)
                    {
                        limited.Add(new DuplicateFileGroup { FileHash = group.FileHash, Files = group.Files.Take(toTake).ToList() });
                        count += toTake;
                        if (count >= maxFilesToProcess.Value) break;
                    }
                }
                _duplicates = limited;
            }

            OnStatusChanged?.Invoke($"Found {_duplicates.Sum(g => g.Files.Count)} duplicate files in {_duplicates.Count} groups");
            progress?.Report((100, 100));

            // Diagnostic: log file counts and sample group keys
            try
            {
                var diagPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dup_diag.log");
                using (var sw = new StreamWriter(diagPath, false))
                {
                    sw.WriteLine($"Total files collected: {fileList.Count}");
                    sw.WriteLine($"Files >= 1KB after filtering: {filtered.Count}");
                    sw.WriteLine($"Unique (name, size) groups: {nameSizeGroups.Count}");
                    sw.WriteLine($"Duplicate groups (>1 file): {duplicateGroupCount}");
                    sw.WriteLine($"Total duplicate files: {totalDuplicatesFound}");
                    sw.WriteLine();
                    int sample = 0;
                    foreach (var kv in nameSizeGroups)
                    {
                        if (kv.Value.Count > 1 && sample++ < 50)
                            sw.WriteLine($"Group: Name={kv.Key.Item2}, Size={kv.Key.Item1}, Count={kv.Value.Count}");
                    }
                }
            }
            catch { }

            return _duplicates;
        }

        // Ultra-simple file collection - no recursion, minimal I/O
        private void CollectFilesSequential(string rootPath, ConcurrentBag<FileInfo> allFiles, System.Threading.CancellationToken cancellationToken, IProgress<(int current, int total)> progress = null)
        {
            try
            {
                var dirs = new Queue<(string path, int depth)>();
                dirs.Enqueue((rootPath, 0));
                int maxDepth = 100;
                int dirCount = 0;
                int fileCount = 0;
                int errorCount = 0;
            var statusTimer = Stopwatch.StartNew();

                while (dirs.Count > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    (string currentDir, int currentDepth) = (null, 0);
                    try
                    {
                        (currentDir, currentDepth) = dirs.Dequeue();

                        if (string.IsNullOrEmpty(currentDir) || currentDepth > maxDepth)
                            continue;

                        DirectoryInfo dirInfo = null;
                        try
                        {
                            dirInfo = new DirectoryInfo(currentDir);
                            dirCount++;
                        }
                        catch
                        {
                            errorCount++;
                            continue;
                        }

                        if (dirInfo == null)
                            continue;

                        // Skip reparse points and hidden/system directories to avoid cycles and access issues
                        try
                        {
                            var attrs = dirInfo.Attributes;
                            if ((attrs & FileAttributes.ReparsePoint) != 0 ||
                                (attrs & FileAttributes.Hidden) != 0 ||
                                (attrs & FileAttributes.System) != 0)
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            errorCount++;
                            continue;
                        }

                        // Get files
                        FileInfo[] files = null;
                        try
                        {
                            files = dirInfo.GetFiles();
                        }
                        catch
                        {
                            errorCount++;
                            files = new FileInfo[0];
                        }

                        if (files != null)
                        {
                            foreach (var file in files)
                            {
                                try
                                {
                                    allFiles.Add(file);
                                    fileCount++;
                                }
                                catch { }
                            }
                        }

                        if (statusTimer.ElapsedMilliseconds > 500)
                        {
                            OnStatusChanged?.Invoke($"Collecting files... {fileCount:N0} files, {dirCount:N0} dirs");
                            // Report collection progress: 0-60% range, estimate based on files found
                            // Use a logarithmic scale so progress moves even for large directories
                            int estimatedProgress = Math.Min(60, (int)(60.0 * (1.0 - 1.0 / (1.0 + fileCount / 1000.0))));
                            progress?.Report((estimatedProgress, 100));
                            statusTimer.Restart();
                        }

                        // Get subdirectories
                        DirectoryInfo[] subdirs = null;
                        try
                        {
                            subdirs = dirInfo.GetDirectories();
                        }
                        catch
                        {
                            errorCount++;
                            subdirs = new DirectoryInfo[0];
                        }

                        if (subdirs != null)
                        {
                            foreach (var subDir in subdirs)
                            {
                                try
                                {
                                    dirs.Enqueue((subDir.FullName, currentDepth + 1));
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                // Log collection stats
                try
                {
                    var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "collection_diag.log");
                    File.WriteAllText(logPath, $"Directories scanned: {dirCount}\nFiles found: {fileCount}\nErrors: {errorCount}\n");
                }
                catch { }
            }
            catch { }
        }
    }
}