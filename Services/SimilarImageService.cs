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
        private static readonly double CompositeWeightSsim = 0.65;
        private static readonly double CompositeWeightHash = 0.15;
        private static readonly double CompositeWeightHist = 0.10;
        private static readonly double CompositeWeightComp = 0.10;
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
            var entries = new System.Collections.Concurrent.ConcurrentBag<(string path, byte[] hash, float[] hist, float[] spatial, int originalIndex)>();
            
            Parallel.For(0, imageFiles.Count, new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct 
            }, i =>
            {
                string filePath = imageFiles[i];
                byte[] hash = GetImageHash(filePath);
                float[] hist = null;
                float[] spatial = null;
                try { hist = ComputeColorHistogram(filePath); } catch { hist = null; }
                try { spatial = ComputeSpatialHistogram(filePath); } catch { spatial = null; }
                if (hash != null)
                {
                    entries.Add((filePath, hash, hist, spatial, i));
                }

                if (i % 10 == 0)
                {
                    OnStatusChanged?.Invoke($"Hashing {i + 1}/{imageFiles.Count}...");
                    progress?.Report((i + 1, imageFiles.Count));
                }
            });

            var sortedEntries = entries.OrderBy(e => e.originalIndex).Select(e => (e.path, e.hash, e.hist, e.spatial)).ToList();

            if (sortedEntries.Count < 2) return results;

            // 3. PHASE 2: Build candidate pairs using hash pre-filter (fast, reduces SSIM comparisons) - PARALLELIZED
            OnStatusChanged?.Invoke("Finding hash-similar candidates...");
            // Allow user to lower similarity down to 75% (was previously clamped to 85%)
            double ssimThreshold = Math.Clamp(maxDistance, 75.0, 99.0) / 100.0;
            // Use LOOSER hash threshold (25 bits) since we skip exact duplicates anyway
            int hashThreshold = 25;
            // Stronger hash threshold previously used for rule checks; keep as a soft signal
            int strongHashThreshold = 22;
            
            var candidatePairsLock = new object();
            var candidatePairs = new System.Collections.Concurrent.ConcurrentBag<(int i, int j, int hashDist)>();
            // histogram-distance threshold (lower = more similar). tuneable.
            // Keep prefilter permissive so we don't drop candidates that only show
            // up at lower SSIM levels; use a tighter hist check later when accepting
            // low-SSIM pairs.
            double histThreshold = 0.95;
            double histTight = 0.25; // legacy - kept for compatibility
            double lowerSsimBound = 0.75; // legacy - kept for compatibility
            // Composite scoring weights
            double compositeThreshold = ssimThreshold; // require composite >= ssimThreshold
            // composition (spatial) histogram thresholds
            double compThreshold = 0.95; // permissive prefilter
            double compTight = 0.35; // stricter for low-SSIM acceptance
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
                    // quick color histogram prefilter to avoid obvious mismatches
                    var histA = sortedEntries[i].hist;
                    var histB = sortedEntries[j].hist;
                    var compA = sortedEntries[i].spatial;
                    var compB = sortedEntries[j].spatial;
                    bool histOk = true;
                    if (histA != null && histB != null)
                    {
                        var hd = HistogramDistance(histA, histB);
                        histOk = hd <= histThreshold;
                    }

                    bool compOk = true;
                    if (compA != null && compB != null)
                    {
                        var cd = SpatialHistogramDistance(compA, compB);
                        compOk = cd <= compThreshold;
                    }

                    if (dist <= hashThreshold && (histOk || compOk))
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

            // 4. PHASE 3: SSIM verification on candidates only (compute SSIMs in parallel and stream groups as found)
            OnStatusChanged?.Invoke($"Verifying {sortedCandidates.Count} candidates with SSIM (parallel streaming)...");
            var thumbnailCache = new System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage>();

            var allScoresBag = new System.Collections.Concurrent.ConcurrentBag<(double ssim, int a, int b)>();
            int verifiedCount = 0;

            // Prepare streaming/grouping structures up front so parallel threads can add groups immediately
            var pathToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int idx = 0; idx < sortedEntries.Count; idx++)
                pathToIndex[sortedEntries[idx].path] = idx;

            var groupAssignment = new int[sortedEntries.Count];
            Array.Fill(groupAssignment, -1);
            int groupIndex = 0;
            var groupAssignmentLock = new object();
            var resultsLock = new object();

            Parallel.ForEach(sortedCandidates, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, pair =>
            {
                var (i, j, hashDist) = pair;
                try
                {
                    // Skip files with same name and size
                    var fileInfoI = new FileInfo(sortedEntries[i].path);
                    var fileInfoJ = new FileInfo(sortedEntries[j].path);
                    if (fileInfoI.Name == fileInfoJ.Name && fileInfoI.Length == fileInfoJ.Length)
                        return;

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

                    if (thumbI == null || thumbJ == null) return;

                    double ssim = 0.0;
                    try
                    {
                        double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                        ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                    }
                    catch { }

                    allScoresBag.Add((ssim, i, j));
                    var current = System.Threading.Interlocked.Increment(ref verifiedCount);
                    if (current % 50 == 0)
                        OnStatusChanged?.Invoke($"SSIM computed {current}/{sortedCandidates.Count}...");

                    // Stream groups: compute composite score and accept if above threshold
                    int pairHashDist = HammingDistance(sortedEntries[i].hash, sortedEntries[j].hash);
                    double hashSim = 0.5;
                    if (sortedEntries[i].hash != null && sortedEntries[j].hash != null && sortedEntries[i].hash.Length > 0)
                        hashSim = 1.0 - (double)HammingDistance(sortedEntries[i].hash, sortedEntries[j].hash) / sortedEntries[i].hash.Length;

                    double pairHistDist = double.MaxValue; double histSim = 0.5;
                    if (sortedEntries[i].hist != null && sortedEntries[j].hist != null)
                    {
                        pairHistDist = HistogramDistance(sortedEntries[i].hist, sortedEntries[j].hist);
                        histSim = 1.0 - pairHistDist;
                    }

                    double pairCompDist = double.MaxValue; double compSim = 0.5;
                    if (sortedEntries[i].spatial != null && sortedEntries[j].spatial != null)
                    {
                        pairCompDist = SpatialHistogramDistance(sortedEntries[i].spatial, sortedEntries[j].spatial);
                        compSim = 1.0 - pairCompDist;
                    }

                    double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);

                    // Composite scoring is evaluated post-SSIM pass to build a full edge graph
                }
                catch { }
            });

            // Cleanup thumbnails
            foreach (var thumb in thumbnailCache.Values)
            {
                thumb.Dispose();
            }

            // Materialize scores list for later use (debugging / closest pairs)
            var allScores = allScoresBag.ToList();
            allScores = allScores.OrderByDescending(s => s.ssim).ToList();

            // Build graph edges using composite scoring and then form connected components (union-find)
            int n = sortedEntries.Count;
            var parent = new int[n];
            var compSize = new int[n];
            for (int i = 0; i < n; i++) { parent[i] = i; compSize[i] = 1; }
            Func<int,int> find = null;
            find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
            Action<int,int> unite = (a, b) => {
                a = find(a); b = find(b);
                if (a == b) return;
                if (compSize[a] < compSize[b]) { var t = a; a = b; b = t; }
                parent[b] = a; compSize[a] += compSize[b];
            };

            foreach (var s in allScores)
            {
                int a = s.a, b = s.b;
                double ssim = s.ssim;

                double hashSim = 0.5;
                if (sortedEntries[a].hash != null && sortedEntries[b].hash != null && sortedEntries[a].hash.Length > 0)
                    hashSim = 1.0 - (double)HammingDistance(sortedEntries[a].hash, sortedEntries[b].hash) / sortedEntries[a].hash.Length;

                double histSim = 0.5;
                if (sortedEntries[a].hist != null && sortedEntries[b].hist != null)
                    histSim = 1.0 - HistogramDistance(sortedEntries[a].hist, sortedEntries[b].hist);

                double compSim = 0.5;
                if (sortedEntries[a].spatial != null && sortedEntries[b].spatial != null)
                    compSim = 1.0 - SpatialHistogramDistance(sortedEntries[a].spatial, sortedEntries[b].spatial);

                double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);
                if (composite >= compositeThreshold)
                    unite(a, b);
            }

            // Collect components
            var comps = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                if (!comps.TryGetValue(r, out var list)) { list = new List<int>(); comps[r] = list; }
                list.Add(i);
            }

            // Create groups from components (only size >= 2)
            results.Clear();
            foreach (var kv in comps)
            {
                var members = kv.Value;
                if (members.Count < 2) continue;
                double bestSsim = 0.0;
                foreach (var p1 in members)
                    foreach (var p2 in members)
                        if (p2 > p1)
                        {
                            var found = allScores.FirstOrDefault(x => (x.a == p1 && x.b == p2) || (x.a == p2 && x.b == p1));
                            if (found.ssim > bestSsim) bestSsim = found.ssim;
                        }

                var group = new SimilarImageGroup
                {
                    GroupId = $"group_{results.Count}",
                    Images = members.Select(mi => CreateFileItem(sortedEntries[mi].path)).ToList(),
                    SimilarityScore = bestSsim
                };
                results.Add(group);
                OnGroupFound?.Invoke(group);
            }

            // Save scores for debugging
            try
            {
                var scorePath = Path.Combine(Path.GetTempPath(), "dupfree_scores.txt");
                var lines = allScores
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
            List<(string path, byte[] hash, float[] hist, float[] spatial)> entries,
            System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage> thumbnailCache,
            double ssimThreshold,
            Dictionary<string, int> pathToIndex,
            int strongHashThreshold,
            double lowerSsimBound,
            double histTight,
            double compTight,
            double compositeThreshold)
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
                        if (!pathToIndex.TryGetValue(groups[targetGroupIdx].Images[a].FilePath, out idxA)) continue;
                        if (!pathToIndex.TryGetValue(groups[otherIdx].Images[b].FilePath, out idxB)) continue;
                        if (idxA < 0 || idxB < 0 || idxA >= entries.Count || idxB >= entries.Count) continue;
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

                                    // Also require reasonable hash similarity and/or histogram agreement to avoid merging unrelated images
                                    int mergeHashDist = HammingDistance(entries[idxA].hash, entries[idxB].hash);
                                    double mergeHistDist = double.MaxValue;
                                    if (entries[idxA].hist != null && entries[idxB].hist != null)
                                        mergeHistDist = HistogramDistance(entries[idxA].hist, entries[idxB].hist);

                                    double mergeCompDist = double.MaxValue;
                                    if (entries[idxA].spatial != null && entries[idxB].spatial != null)
                                        mergeCompDist = SpatialHistogramDistance(entries[idxA].spatial, entries[idxB].spatial);

                                    double mergeHashSim = 0.5;
                                    if (entries[idxA].hash != null && entries[idxB].hash != null && entries[idxA].hash.Length > 0)
                                        mergeHashSim = 1.0 - (double)HammingDistance(entries[idxA].hash, entries[idxB].hash) / entries[idxA].hash.Length;

                                    double mergeHistSim = 0.5;
                                    if (entries[idxA].hist != null && entries[idxB].hist != null)
                                        mergeHistSim = 1.0 - HistogramDistance(entries[idxA].hist, entries[idxB].hist);

                                    double mergeCompSim = 0.5;
                                    if (entries[idxA].spatial != null && entries[idxB].spatial != null)
                                        mergeCompSim = 1.0 - SpatialHistogramDistance(entries[idxA].spatial, entries[idxB].spatial);

                                    double mergeComposite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * mergeHashSim) + (CompositeWeightHist * mergeHistSim) + (CompositeWeightComp * mergeCompSim);
                                    if (mergeComposite >= compositeThreshold)
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

        private float[] ComputeColorHistogram(string filePath)
        {
            const int binsPerChannel = 4; // 4x4x4 = 64-bin histogram
            int totalBins = binsPerChannel * binsPerChannel * binsPerChannel;
            var hist = new float[totalBins];

            using (var bmp = new Bitmap(filePath))
            using (var small = new Bitmap(bmp, new Size(64, 64)))
            {
                for (int y = 0; y < small.Height; y++)
                {
                    for (int x = 0; x < small.Width; x++)
                    {
                        var c = small.GetPixel(x, y);
                        int r = c.R * binsPerChannel / 256;
                        int g = c.G * binsPerChannel / 256;
                        int b = c.B * binsPerChannel / 256;
                        if (r >= binsPerChannel) r = binsPerChannel - 1;
                        if (g >= binsPerChannel) g = binsPerChannel - 1;
                        if (b >= binsPerChannel) b = binsPerChannel - 1;
                        int idx = (r * binsPerChannel + g) * binsPerChannel + b;
                        hist[idx] += 1.0f;
                    }
                }
            }

            // normalize
            float sum = hist.Sum();
            if (sum > 0)
            {
                for (int i = 0; i < hist.Length; i++)
                    hist[i] /= sum;
            }

            return hist;
        }

        private float[] ComputeSpatialHistogram(string filePath)
        {
            const int grid = 3;
            const int binsPerChannel = 4; // 4x4x4 per cell
            int binsPerCell = binsPerChannel * binsPerChannel * binsPerChannel; // 64
            int totalBins = binsPerCell * grid * grid; // 64 * 9 = 576
            var hist = new float[totalBins];

            using (var bmp = new Bitmap(filePath))
            using (var small = new Bitmap(bmp, new Size(96, 96)))
            {
                int cellW = small.Width / grid;
                int cellH = small.Height / grid;
                for (int y = 0; y < small.Height; y++)
                {
                    for (int x = 0; x < small.Width; x++)
                    {
                        var c = small.GetPixel(x, y);
                        int r = c.R * binsPerChannel / 256;
                        int g = c.G * binsPerChannel / 256;
                        int b = c.B * binsPerChannel / 256;
                        if (r >= binsPerChannel) r = binsPerChannel - 1;
                        if (g >= binsPerChannel) g = binsPerChannel - 1;
                        if (b >= binsPerChannel) b = binsPerChannel - 1;

                        int cellX = Math.Min(x / cellW, grid - 1);
                        int cellY = Math.Min(y / cellH, grid - 1);
                        int cellIdx = cellY * grid + cellX;
                        int idx = cellIdx * binsPerCell + (r * binsPerChannel + g) * binsPerChannel + b;
                        hist[idx] += 1.0f;
                    }
                }
            }

            // normalize
            float sum = hist.Sum();
            if (sum > 0)
            {
                for (int i = 0; i < hist.Length; i++)
                    hist[i] /= sum;
            }

            return hist;
        }

        private double SpatialHistogramDistance(float[] a, float[] b)
        {
            if (a == null || b == null) return double.MaxValue;
            if (a.Length != b.Length) return double.MaxValue;
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++)
                sum += Math.Abs(a[i] - b[i]);
            // normalize (max sum is 2)
            return sum / 2.0;
        }

        private double HistogramDistance(float[] a, float[] b)
        {
            if (a == null || b == null) return double.MaxValue;
            if (a.Length != b.Length) return double.MaxValue;
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                sum += Math.Abs(a[i] - b[i]);
            }
            // L1 distance normalized to [0,1] (max sum is 2)
            return sum / 2.0;
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

        private double? ComputeSsimBetweenIndices(int idxA, int idxB, List<(string path, byte[] hash, float[] hist, float[] spatial)> entries, System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage> thumbnailCache)
        {
            try
            {
                if (idxA < 0 || idxB < 0 || idxA >= entries.Count || idxB >= entries.Count) return null;

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

                if (thumbA == null || thumbB == null) return null;

                double distortion = thumbA.Compare(thumbB, ErrorMetric.StructuralSimilarity);
                double ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                return ssim;
            }
            catch { return null; }
        }
    }
}
