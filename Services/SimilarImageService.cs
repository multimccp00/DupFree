using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Drawing;
using DupFree.Models;
using ImageMagick;

namespace DupFree.Services
{
    public class AutoSelectOptions
    {
        public bool PreferUncompressed { get; set; } = false;
        public bool PreferHigherResolution { get; set; } = true;
        public bool PreferLargerFilesize { get; set; } = false;
        public List<string> PreferredDirectories { get; set; } = new();
    }

    public class SimilarImageGroup
    {
        public List<FileItemViewModel> Images { get; set; } = new();
        public double SimilarityScore { get; set; }
        public string GroupId { get; set; }
    }

    public class SimilarImageService
    {
        public event Action<string> OnStatusChanged;
        public event Action<int> OnProgressChanged;

        /// <summary>
        /// Fired on the background thread whenever a new group is found or an existing group gains a member.
        /// The UI should marshal this to the dispatcher.
        /// </summary>
        public event Action<SimilarImageGroup> OnGroupFound;

        /// <summary>
        /// Fired when a new image is added to an already-reported group.
        /// Parameters: (groupId, newImage)
        /// </summary>
        public event Action<string, FileItemViewModel> OnImageAddedToGroup;

        /// <summary>
        /// Streams similar image groups progressively as they are discovered.
        /// Uses 2-phase approach: fast hash pre-filtering + SSIM verification on candidates only.
        /// </summary>
        public async Task<List<SimilarImageGroup>> FindSimilarImagesAsync(
            List<string> directories,
            double maxDistance = 92.0,
            bool showClosestPairsOnly = false,
            int closestPairCount = 20,
            IProgress<(int current, int total)> progress = null,
            CancellationToken cancellationToken = default)
        {
            return await Task.Run(() =>
                FindSimilarInternal(directories, maxDistance, showClosestPairsOnly, closestPairCount, progress, cancellationToken),
                cancellationToken);
        }

        private List<SimilarImageGroup> FindSimilarInternal(
            List<string> directories,
            double maxDistance,
            bool showClosestPairsOnly,
            int closestPairCount,
            IProgress<(int current, int total)> progress,
            CancellationToken ct)
        {
            var results = new List<SimilarImageGroup>();

            // 1. Collect image files
            OnStatusChanged?.Invoke("Collecting image files...");
            var imageFiles = new List<string>();
            foreach (var dir in directories)
            {
                if (ct.IsCancellationRequested) return results;
                try
                {
                    imageFiles.AddRange(
                        Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                            .Where(f => ImagePreviewService.IsPreviewableImage(f)));
                }
                catch { }
            }

            if (imageFiles.Count < 2) return results;
            OnStatusChanged?.Invoke($"Found {imageFiles.Count} images");

            // 2. PHASE 1: Fast hash computation (O(N)) - PARALLELIZED
            OnStatusChanged?.Invoke("Computing perceptual hashes...");
            var entriesLock = new object();
            var entries = new System.Collections.Concurrent.ConcurrentBag<(string path, byte[] hash, int originalIndex)>();
            
            Parallel.For(0, imageFiles.Count, new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct 
            }, i =>
            {
                string filePath = imageFiles[i];
                byte[] hash = GetImageHash(filePath);
                if (hash != null)
                {
                    entries.Add((filePath, hash, i));
                }

                if (i % 10 == 0)
                {
                    OnStatusChanged?.Invoke($"Hashing {i + 1}/{imageFiles.Count}...");
                    progress?.Report((i + 1, imageFiles.Count));
                }
            });

            var sortedEntries = entries.OrderBy(e => e.originalIndex).Select(e => (e.path, e.hash)).ToList();

            if (sortedEntries.Count < 2) return results;

            // 3. PHASE 2: Build candidate pairs using hash pre-filter (fast, reduces SSIM comparisons) - PARALLELIZED
            OnStatusChanged?.Invoke("Finding hash-similar candidates...");
            double ssimThreshold = Math.Clamp(maxDistance, 85.0, 99.0) / 100.0;
            // Use LOOSER hash threshold (25 bits) since we skip exact duplicates anyway
            int hashThreshold = 25;
            
            var candidatePairsLock = new object();
            var candidatePairs = new System.Collections.Concurrent.ConcurrentBag<(int i, int j, int hashDist)>();
            int totalPairs = sortedEntries.Count * (sortedEntries.Count - 1) / 2;
            int pairsChecked = 0;
            var pairsCheckedLock = new object();

            Parallel.For(0, sortedEntries.Count, new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct 
            }, i =>
            {
                for (int j = i + 1; j < sortedEntries.Count; j++)
                {
                    if (ct.IsCancellationRequested) return;

                    int dist = HammingDistance(sortedEntries[i].hash, sortedEntries[j].hash);
                    if (dist <= hashThreshold)
                    {
                        candidatePairs.Add((i, j, dist));
                    }

                    int currentChecked;
                    lock (pairsCheckedLock)
                    {
                        pairsChecked++;
                        currentChecked = pairsChecked;
                    }

                    if (currentChecked % 5000 == 0)
                        OnStatusChanged?.Invoke($"Checked {currentChecked}/{totalPairs} hash pairs... ({candidatePairs.Count} candidates)");
                }
            });

            var sortedCandidates = candidatePairs.OrderBy(p => p.hashDist).ToList();
            OnStatusChanged?.Invoke($"Found {sortedCandidates.Count} hash-similar candidates (from {totalPairs} pairs)");

            if (sortedCandidates.Count == 0)
            {
                OnStatusChanged?.Invoke("No similar images found");
                return results;
            }

            // 4. PHASE 3: SSIM verification on candidates only (accurate but only for small subset) - PARALLELIZED
            OnStatusChanged?.Invoke($"Verifying {sortedCandidates.Count} candidates with SSIM...");
            var groupAssignment = new int[sortedEntries.Count];
            Array.Fill(groupAssignment, -1);
            int groupIndex = 0;
            var allScoresLock = new object();
            var allScores = new List<(double ssim, int a, int b)>();

            // Load thumbnails on-demand with caching and thread-safety (smaller 128x128 for speed)
            var thumbnailCache = new System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage>();
            var groupAssignmentLock = new object();
            var resultsLock = new object();

            int verified = 0;
            var verifiedLock = new object();

            // Process candidates in order (by hash distance) to get best matches first
            foreach (var (i, j, hashDist) in sortedCandidates)
            {
                if (ct.IsCancellationRequested) break;

                // Skip files with same name AND size (already handled by duplicate detection)
                var fileInfoI = new FileInfo(sortedEntries[i].path);
                var fileInfoJ = new FileInfo(sortedEntries[j].path);
                if (fileInfoI.Name == fileInfoJ.Name && fileInfoI.Length == fileInfoJ.Length)
                {
                    continue;
                }

                // Load thumbnails on-demand - SMALLER SIZE (128x128) for speed
                var thumbI = thumbnailCache.GetOrAdd(i, idx =>
                {
                    try
                    {
                        var img = new MagickImage(sortedEntries[idx].path);
                        var geo = new MagickGeometry(128, 128);
                        geo.IgnoreAspectRatio = false;
                        img.Resize(geo);
                        img.Extent(128, 128, Gravity.Center, MagickColors.Black);
                        return img;
                    }
                    catch { return null; }
                });

                var thumbJ = thumbnailCache.GetOrAdd(j, idx =>
                {
                    try
                    {
                        var img = new MagickImage(sortedEntries[idx].path);
                        var geo = new MagickGeometry(128, 128);
                        geo.IgnoreAspectRatio = false;
                        img.Resize(geo);
                        img.Extent(128, 128, Gravity.Center, MagickColors.Black);
                        return img;
                    }
                    catch { return null; }
                });

                if (thumbI == null || thumbJ == null) continue;

                // SSIM verification
                double ssim = 0.0;
                try
                {
                    double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                    ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                }
                catch { }

                lock (allScoresLock)
                {
                    allScores.Add((ssim, i, j));
                }

                lock (verifiedLock)
                {
                    verified++;
                    if (verified % 10 == 0)
                        OnStatusChanged?.Invoke($"SSIM verified {verified}/{sortedCandidates.Count}... ({results.Count} groups)");
                }

                // Stream: Check if this pair should form/extend a group
                if (!showClosestPairsOnly && ssim >= ssimThreshold)
                {
                    lock (groupAssignmentLock)
                    {
                        int gi = groupAssignment[i];
                        int gj = groupAssignment[j];

                        if (gi == -1 && gj == -1)
                        {
                            // Create new group
                            var group = new SimilarImageGroup
                            {
                                GroupId = $"group_{groupIndex}",
                                Images = new List<FileItemViewModel>
                                {
                                    CreateFileItem(sortedEntries[i].path),
                                    CreateFileItem(sortedEntries[j].path)
                                },
                                SimilarityScore = ssim
                            };
                            lock (resultsLock)
                            {
                                results.Add(group);
                                groupAssignment[i] = groupIndex;
                                groupAssignment[j] = groupIndex;
                                groupIndex++;
                                OnGroupFound?.Invoke(group);
                            }
                        }
                        else if (gi >= 0 && gj == -1)
                        {
                            var item = CreateFileItem(sortedEntries[j].path);
                            results[gi].Images.Add(item);
                            groupAssignment[j] = gi;
                            OnImageAddedToGroup?.Invoke(results[gi].GroupId, item);
                            
                            // Try to merge groups if they connect
                            TryMergeGroupsStreaming(results, gi, groupAssignment, sortedEntries, thumbnailCache, ssimThreshold);
                        }
                        else if (gi == -1 && gj >= 0)
                        {
                            var item = CreateFileItem(sortedEntries[i].path);
                            results[gj].Images.Add(item);
                            groupAssignment[i] = gj;
                            OnImageAddedToGroup?.Invoke(results[gj].GroupId, item);
                            
                            // Try to merge groups if they connect
                            TryMergeGroupsStreaming(results, gj, groupAssignment, sortedEntries, thumbnailCache, ssimThreshold);
                        }
                    }
                }
            }

            // Cleanup thumbnails
            foreach (var thumb in thumbnailCache.Values)
            {
                thumb.Dispose();
            }

            // Save scores for debugging
            try
            {
                var scorePath = Path.Combine(Path.GetTempPath(), "dupfree_scores.txt");
                var lines = allScores
                    .OrderByDescending(s => s.ssim)
                    .Take(50)
                    .Select(s => $"{s.ssim:F4}\t{Path.GetFileName(sortedEntries[s.a].path)}\t{Path.GetFileName(sortedEntries[s.b].path)}");
                File.WriteAllLines(scorePath, lines);
            }
            catch { }

            // Handle closest pairs mode
            if (showClosestPairsOnly)
            {
                results.Clear();
                var closest = allScores
                    .OrderByDescending(s => s.ssim)
                    .Take(closestPairCount)
                    .ToList();

                int gi = 0;
                foreach (var s in closest)
                {
                    results.Add(new SimilarImageGroup
                    {
                        GroupId = $"pair_{gi++}",
                        Images = new List<FileItemViewModel>
                        {
                            CreateFileItem(sortedEntries[s.a].path),
                            CreateFileItem(sortedEntries[s.b].path)
                        },
                        SimilarityScore = s.ssim
                    });
                }
                return results;
            }

            OnStatusChanged?.Invoke($"Done! Found {results.Count} groups");
            
            return results;
        }

        /// <summary>
        /// Try to merge the target group with others during streaming.
        /// Called whenever a group gains a new member.
        /// </summary>
        private void TryMergeGroupsStreaming(
            List<SimilarImageGroup> groups,
            int targetGroupIdx,
            int[] groupAssignment,
            List<(string path, byte[] hash)> entries,
            System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage> thumbnailCache,
            double ssimThreshold)
        {
            if (targetGroupIdx < 0 || targetGroupIdx >= groups.Count) return;

            // Check if this group can merge with another
            for (int otherIdx = 0; otherIdx < groups.Count; otherIdx++)
            {
                if (otherIdx == targetGroupIdx) continue;

                // Check just 1-2 image pairs (quick check)
                bool shouldMerge = false;
                int checksPerformed = 0;
                const int maxChecks = 2;

                for (int a = 0; a < groups[targetGroupIdx].Images.Count && checksPerformed < maxChecks; a++)
                {
                    for (int b = 0; b < groups[otherIdx].Images.Count && checksPerformed < maxChecks; b++)
                    {
                        int idxA = -1, idxB = -1;
                        for (int k = 0; k < entries.Count; k++)
                        {
                            if (entries[k].path == groups[targetGroupIdx].Images[a].FilePath) idxA = k;
                            if (entries[k].path == groups[otherIdx].Images[b].FilePath) idxB = k;
                        }

                        if (idxA < 0 || idxB < 0) continue;
                        checksPerformed++;

                        var thumbA = thumbnailCache.GetOrAdd(idxA, idx =>
                        {
                            try
                            {
                                var img = new MagickImage(entries[idx].path);
                                var geo = new MagickGeometry(128, 128);
                                geo.IgnoreAspectRatio = false;
                                img.Resize(geo);
                                img.Extent(128, 128, Gravity.Center, MagickColors.Black);
                                return img;
                            }
                            catch { return null; }
                        });

                        var thumbB = thumbnailCache.GetOrAdd(idxB, idx =>
                        {
                            try
                            {
                                var img = new MagickImage(entries[idx].path);
                                var geo = new MagickGeometry(128, 128);
                                geo.IgnoreAspectRatio = false;
                                img.Resize(geo);
                                img.Extent(128, 128, Gravity.Center, MagickColors.Black);
                                return img;
                            }
                            catch { return null; }
                        });

                        if (thumbA == null || thumbB == null) continue;

                        try
                        {
                            double distortion = thumbA.Compare(thumbB, ErrorMetric.StructuralSimilarity);
                            double ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);

                            // Check if same name and size (skip those, already in duplicate detection)
                            var infoA = new FileInfo(entries[idxA].path);
                            var infoB = new FileInfo(entries[idxB].path);
                            if (infoA.Name == infoB.Name && infoA.Length == infoB.Length)
                                continue;

                            if (ssim >= ssimThreshold) // Keep all similar (including exact)
                            {
                                shouldMerge = true;
                                break;
                            }
                        }
                        catch { }
                    }
                    if (shouldMerge) break;
                }

                if (shouldMerge)
                {
                    // Merge otherIdx into targetGroupIdx
                    foreach (var img in groups[otherIdx].Images)
                    {
                        groups[targetGroupIdx].Images.Add(img);
                    }
                    groups.RemoveAt(otherIdx);
                    
                    // Update group assignments
                    for (int k = 0; k < groupAssignment.Length; k++)
                    {
                        if (groupAssignment[k] == otherIdx)
                            groupAssignment[k] = targetGroupIdx;
                        else if (groupAssignment[k] > otherIdx)
                            groupAssignment[k]--;
                    }
                    break; // Only merge one at a time to keep streaming smooth
                }
            }
        }

        private byte[] GetImageHash(string filePath)
        {
            try
            {
                using (var image = Image.FromFile(filePath))
                {
                    return ComputePerceptualHash(image);
                }
            }
            catch
            {
                return null;
            }
        }

        private byte[] ComputePerceptualHash(Image image)
        {
            using (var resized = new Bitmap(image, new Size(64, 64)))
            {
                var grayscale = ToGrayscale(resized);
                var hash = new byte[64];
                int hashIndex = 0;

                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        if (hashIndex < 64 && x < 63)
                        {
                            int current = grayscale[y * 64 + x];
                            int next = grayscale[y * 64 + (x + 1)];
                            hash[hashIndex++] = (byte)(current < next ? 1 : 0);
                        }
                    }
                }

                return hash;
            }
        }

        private int[] ToGrayscale(Bitmap bitmap)
        {
            int[] grayscale = new int[64 * 64];
            var lockBits = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, 64, 64),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            try
            {
                IntPtr ptr = lockBits.Scan0;
                byte[] pixels = new byte[lockBits.Stride * 64];
                System.Runtime.InteropServices.Marshal.Copy(ptr, pixels, 0, pixels.Length);

                for (int y = 0; y < 64; y++)
                {
                    for (int x = 0; x < 64; x++)
                    {
                        int index = y * lockBits.Stride + x * 3;
                        byte b = pixels[index];
                        byte g = pixels[index + 1];
                        byte r = pixels[index + 2];

                        grayscale[y * 64 + x] = (r + g + b) / 3;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(lockBits);
            }

            return grayscale;
        }

        private int HammingDistance(byte[] hash1, byte[] hash2)
        {
            if (hash1 == null || hash2 == null) return int.MaxValue;
            if (hash1.Length != hash2.Length) return int.MaxValue;

            int distance = 0;
            for (int i = 0; i < hash1.Length; i++)
            {
                if (hash1[i] != hash2[i])
                    distance++;
            }
            return distance;
        }

        private FileItemViewModel CreateFileItem(string filePath)
        {
            try
            {
                return FileItemViewModel.FromFileInfo(new FileInfo(filePath), loadThumbnail: true);
            }
            catch
            {
                return new FileItemViewModel { FilePath = filePath, FileName = Path.GetFileName(filePath) };
            }
        }
    }
}
