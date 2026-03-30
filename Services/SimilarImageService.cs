using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Windows.Media.Imaging;
using System.Drawing;
using DupFree.Models;
using ImageMagick;

namespace DupFree.Services
{
    /// <summary>
    /// Options used by the automatic selection algorithm when choosing the preferred file
    /// from a group of similar images.
    /// </summary>
    public class AutoSelectOptions
    {
        public bool PreferUncompressed { get; set; } = false;
        public bool PreferHigherResolution { get; set; } = true;
        public bool PreferLargerFilesize { get; set; } = false;
        public List<string> PreferredDirectories { get; set; } = [];
    }

    /// <summary>Represents a set of visually similar images discovered by the finder.</summary>
    public class SimilarImageGroup
    {
        public List<FileItemViewModel> Images { get; set; } = [];
        /// <summary>Similarity score in range [0,1] where higher means more similar.</summary>
        public double SimilarityScore { get; set; }
        public string GroupId { get; set; } = string.Empty;
    }

    /// <summary>Service that finds visually similar images using a fast hash prefilter and SSIM verification.</summary>
    /// <remarks>Reports progress and discovered groups via events.</remarks>
    public class SimilarImageService
    {
        private static readonly double CompositeWeightSsim = 0.65;
        private static readonly double CompositeWeightHash = 0.15;
        private static readonly double CompositeWeightHist = 0.10;
        private static readonly double CompositeWeightComp = 0.10;

        private readonly SynchronizationContext? _syncContext;

        public SimilarImageService()
        {
            _syncContext = SynchronizationContext.Current;
        }

        /// <summary>Safely load a MagickImage with a timeout to prevent hanging on corrupted files.</summary>
        private MagickImage? TryLoadImageWithTimeout(string path, int timeoutMs = 5000)
        {
            try
            {
                MagickImage? result = null;
                var task = Task.Run(() =>
                {
                    try
                    {
                        return new MagickImage(path);
                    }
                    catch { return null; }
                });

                if (task.Wait(TimeSpan.FromMilliseconds(timeoutMs)))
                {
                    result = task.Result;
                }
                else
                {
                    Log.Info($"SimilarImageService: MagickImage load timeout for '{Path.GetFileName(path)}'");
                }
                return result;
            }
            catch { return null; }
        }

        private IEnumerable<ulong> ComputeTilePackedHashes(string path, int grid = 3)
        {
            var res = new List<ulong>();
            try
            {
                using var img = Image.FromFile(path);
                int w = img.Width;
                int h = img.Height;
                int gw = grid, gh = grid;
                for (int gy = 0; gy < gh; gy++)
                {
                    for (int gx = 0; gx < gw; gx++)
                    {
                        int x0 = (int)Math.Floor((double)gx * w / gw);
                        int y0 = (int)Math.Floor((double)gy * h / gh);
                        int x1 = (int)Math.Ceiling((double)(gx + 1) * w / gw);
                        int y1 = (int)Math.Ceiling((double)(gy + 1) * h / gh);
                        int tw = Math.Max(8, x1 - x0);
                        int th = Math.Max(8, y1 - y0);
                        try
                        {
                            var bmp = new Bitmap(tw, th);
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.DrawImage(img, new Rectangle(0, 0, tw, th), new Rectangle(x0, y0, tw, th), GraphicsUnit.Pixel);
                            }
                            var (hbytes, packed) = ComputePerceptualHash(bmp);
                            res.Add(packed);
                            bmp.Dispose();
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return [.. res.Distinct()];
        }

        /// <summary>Raised to communicate textual status updates (e.g. "Hashing 123/456").</summary>
        public event Action<string>? OnStatusChanged;

        /// <summary>Raised to report scan progress as an integer percent (0–100).</summary>
        public event Action<int>? OnProgressChanged;

        /// <summary>
        /// Fired on the background thread whenever a new group is found or an existing group gains a member.
        /// The UI should marshal this to the dispatcher.
        /// </summary>
        public event Action<SimilarImageGroup>? OnGroupFound;

        /// <summary>
        /// Fired when a new image is added to an already-reported group.
        /// Parameters: (groupId, newImage)
        /// </summary>
        public event Action<string, FileItemViewModel>? OnImageAddedToGroup;

        /// <summary>
        /// Streams similar image groups progressively as they are discovered.
        /// Uses 2-phase approach: fast hash pre-filtering + SSIM verification on candidates only.
        /// </summary>
        public async Task<List<SimilarImageGroup>> FindSimilarImagesAsync(
            List<string> directories,
            double maxDistance = 92.0,
            bool showClosestPairsOnly = false,
            int closestPairCount = 20,
            IProgress<(int current, int total)>? progress = null,
            CancellationToken cancellationToken = default,
            string? exportEdgeCsv = null,
            int hashThresholdOverride = -1,
            int ssimThumbnailSize = 128,
            bool visipicsMode = false,
            bool forceBruteForce = false,
            bool useSimdSsim = false,
            bool useGpuSsim = false)
        {
            TelemetryService.TrackEvent("SimilarImageScanStart");
            Log.Info($"SimilarImageService: FindSimilarImagesAsync entered (GPU={useGpuSsim}, SIMD={useSimdSsim}, hashThresholdOverride={hashThresholdOverride}, thumb={ssimThumbnailSize})");
            using (TelemetryService.Measure("SimilarImageScan"))
            {
                return await Task.Run(() =>
                    FindSimilarInternal(directories, maxDistance, showClosestPairsOnly, closestPairCount, progress, cancellationToken, exportEdgeCsv, hashThresholdOverride, ssimThumbnailSize, visipicsMode: visipicsMode, forceBruteForce: forceBruteForce, safeOptimizations: false, useSimdSsim: useSimdSsim, useGpuSsim: useGpuSsim),
                    cancellationToken);
            }
        }

        private List<SimilarImageGroup> FindSimilarInternal(
            List<string> directories,
            double maxDistance,
            bool showClosestPairsOnly,
            int closestPairCount,
            IProgress<(int current, int total)>? progress,
            CancellationToken ct,
            string? exportEdgeCsv,
            int hashThresholdOverride,
            int ssimThumbnailSize,
            bool visipicsMode,
            bool forceBruteForce,
            bool safeOptimizations,
            bool useSimdSsim,
            bool useGpuSsim)
        {
            var results = new List<SimilarImageGroup>();

            // Staged progress model so the bar moves across all major phases, not just SSIM.
            // 0-8: file collection, 8-35: hashing, 35-55: candidate building,
            // 55-70: thumbnail preload, 70-99: SSIM verification, 100: finalize.
            void RaiseProgressForStage(double stageStart, double stageSpan, int completed, int total)
            {
                double fraction = total <= 0 ? 1.0 : Math.Clamp((double)completed / total, 0.0, 1.0);
                int percent = (int)Math.Round(stageStart + (stageSpan * fraction));
                RaiseProgress(percent);
            }

            RaiseProgress(0);

            // 1. Collect image files (skip GIFs and MP4s - only actual static images for duplicate detection)
            RaiseStatus("Collecting image files...");
            var imageFiles = new List<string>();
            int processedDirs = 0;
            int totalDirs = Math.Max(1, directories.Count);
            foreach (var dir in directories)
            {
                if (ct.IsCancellationRequested) return results;
                try
                {
                    imageFiles.AddRange(
                        Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
                            .Where(f => ImagePreviewService.IsScannableImage(f)));
                }
                catch { }

                processedDirs++;
                RaiseProgressForStage(0, 8, processedDirs, totalDirs);
            }

            if (imageFiles.Count < 2)
            {
                RaiseProgress(100);
                return results;
            }
            RaiseStatus($"Found {imageFiles.Count} images");

            // 2. PHASE 1: Fast hash computation (O(N)) - PARALLELIZED
            RaiseStatus("Computing perceptual hashes...");
            var entriesLock = new object();
            var entries = new System.Collections.Concurrent.ConcurrentBag<(string path, byte[]? hash, ulong packedHash, float[]? hist, float[]? spatial, int originalIndex)>();
            int hashedCount = 0;

            Parallel.For(0, imageFiles.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            }, i =>
            {
                string filePath = imageFiles[i];
                var (hash, packedHash) = GetImageHash(filePath);
                // defer histogram/spatial computation to when needed (lazy cache)
                float[]? hist = null;
                float[]? spatial = null;
                if (hash != null)
                {
                    entries.Add((filePath, hash, packedHash, hist, spatial, i));
                }

                var current = System.Threading.Interlocked.Increment(ref hashedCount);
                if (current % 10 == 0 || current == imageFiles.Count)
                {
                    RaiseStatus($"Hashing {current}/{imageFiles.Count}...");
                    progress?.Report((current, imageFiles.Count));
                    RaiseProgressForStage(8, 27, current, imageFiles.Count);
                }
            });
            RaiseProgressForStage(8, 27, imageFiles.Count, imageFiles.Count);

            // (was disposing a shared GPU helper here prematurely) - will dispose at method end

            var sortedEntries = entries.OrderBy(e => e.originalIndex).Select(e => (e.path, e.hash, e.packedHash, e.hist, e.spatial)).ToList();

            // Precompute lightweight file metadata once to avoid repeated FileInfo I/O in hot loops.
            var fileMeta = sortedEntries.Select(s =>
            {
                try
                {
                    var fi = new FileInfo(s.path);
                    return (hasMeta: true, name: fi.Name, length: fi.Length);
                }
                catch
                {
                    return (hasMeta: false, name: string.Empty, length: -1L);
                }
            }).ToArray();

            if (sortedEntries.Count < 2) return results;

            // 3. PHASE 2: Build candidate pairs using hash pre-filter (fast, reduces SSIM comparisons)
            RaiseStatus("Finding hash-similar candidates (using BK-tree index)...");

            // Allow user to lower similarity down to 75% (was previously clamped to 85%)
            double ssimThreshold = Math.Clamp(maxDistance, 75.0, 99.0) / 100.0;
            // Use LOOSER hash threshold (25 bits) since we skip exact duplicates anyway
            int hashThreshold = hashThresholdOverride > 0 ? hashThresholdOverride : 25;
            if (hashThresholdOverride <= 0)
            {
                // Adaptive tightening for very large sets to keep candidate growth in check.
                if (sortedEntries.Count >= 15000)
                    hashThreshold = 18;
                else if (sortedEntries.Count >= 9000)
                    hashThreshold = 20;
                else if (sortedEntries.Count >= 5000)
                    hashThreshold = 22;
            }
            RaiseStatus($"Hash threshold: {hashThreshold} (images={sortedEntries.Count})");

            var candidatePairs = new System.Collections.Concurrent.ConcurrentBag<(int i, int j, int hashDist)>();
            var featureCache = new System.Collections.Concurrent.ConcurrentDictionary<int, (float[]? hist, float[]? spatial)>();
            (float[]? hist, float[]? spatial) GetFeatures(int idx)
            {
                return featureCache.GetOrAdd(idx, k =>
                {
                    try { return ComputeColorAndSpatialHistograms(sortedEntries[k].path); }
                    catch { return (null, null); }
                });
            }
            double histThreshold = 0.95;
            double compositeThreshold = ssimThreshold;
            double compThreshold = 0.95;
            int totalPairs = sortedEntries.Count * (sortedEntries.Count - 1) / 2;

            // If VisiPics-like fast greedy mode requested, do a single-pass removal-based grouping
            if (visipicsMode)
            {
                RaiseStatus("Running VisiPics-fast greedy pass...");
                var visThumbCache = new System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage?>();
                var removed = new bool[sortedEntries.Count];
                var resultsGreedy = new List<SimilarImageGroup>();

                for (int i = 0; i < sortedEntries.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    if (removed[i]) continue;

                    if ((i + 1) % 8 == 0 || i == sortedEntries.Count - 1)
                    {
                        RaiseProgressForStage(35, 20, i + 1, sortedEntries.Count);
                    }

                    var members = new List<int> { i };

                    for (int j = i + 1; j < sortedEntries.Count; j++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (removed[j]) continue;



                        // quick file-name/size skip
                        var mi = fileMeta[i];
                        var mj = fileMeta[j];
                        if (mi.hasMeta && mj.hasMeta && mi.name == mj.name && mi.length == mj.length)
                        {
                            removed[j] = true;
                            members.Add(j);
                            continue;
                        }

                        int dist = int.MaxValue;
                        if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                            dist = HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash, hashThreshold);
                        if (dist > hashThreshold) continue;

                        // compute hist/spatial on demand (single decode per image via shared feature cache)
                        var (histA, compA) = GetFeatures(i);
                        var (histB, compB) = GetFeatures(j);

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
                        if (!(histOk || compOk)) continue;

                        // compute SSIM on-demand
                        var thumbI = visThumbCache.GetOrAdd(i, idx =>
                        {
                            try
                            {
                                var img = TryLoadImageWithTimeout(sortedEntries[idx].path, timeoutMs: 3000);
                                if (img == null) return null;
                                var geo = new MagickGeometry((uint)ssimThumbnailSize, (uint)ssimThumbnailSize)
                                {
                                    IgnoreAspectRatio = false
                                };
                                img.Resize(geo);
                                img.Extent((uint)ssimThumbnailSize, (uint)ssimThumbnailSize, Gravity.Center, MagickColors.Black);
                                return img;
                            }
                            catch { return null; }
                        });

                        var thumbJ = visThumbCache.GetOrAdd(j, idx =>
                        {
                            try
                            {
                                var img = TryLoadImageWithTimeout(sortedEntries[idx].path, timeoutMs: 3000);
                                if (img == null) return null;
                                var geo = new MagickGeometry((uint)ssimThumbnailSize, (uint)ssimThumbnailSize)
                                {
                                    IgnoreAspectRatio = false
                                };
                                img.Resize(geo);
                                img.Extent((uint)ssimThumbnailSize, (uint)ssimThumbnailSize, Gravity.Center, MagickColors.Black);
                                return img;
                            }
                            catch { return null; }
                        });

                        if (thumbI == null || thumbJ == null) continue;

                        double ssim = 0.0;
                        try
                        {
                            double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                            ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                        }
                        catch { }

                        double hashSim = 0.5;
                        if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                            hashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash) / 64.0;

                        double histSim = 0.5;
                        if (histA != null && histB != null)
                        {
                            var pairHistDist = HistogramDistance(histA, histB);
                            histSim = 1.0 - pairHistDist;
                        }

                        double compSim = 0.5;
                        if (compA != null && compB != null)
                        {
                            var pairCompDist = SpatialHistogramDistance(compA, compB);
                            compSim = 1.0 - pairCompDist;
                        }

                        double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);
                        if (composite >= compositeThreshold)
                        {
                            members.Add(j);
                            removed[j] = true;
                        }
                    }

                    // create a group if we found matches
                    if (members.Count >= 2)
                    {
                        var group = new SimilarImageGroup
                        {
                            GroupId = $"visipics_group_{results.Count}",
                            Images = [.. members.Select(mi => CreateFileItem(sortedEntries[mi].path))],
                            SimilarityScore = 0.0
                        };
                        results.Add(group);
                        RaiseGroupFound(group);
                        // mark i removed
                        removed[i] = true;
                    }
                }

                // cleanup thumbnail cache
                foreach (var kv in visThumbCache)
                {
                    try { kv.Value?.Dispose(); } catch { }
                }

                RaiseStatus($"VisiPics-fast done. Found {results.Count} groups");
                RaiseProgress(100);
                return results;
            }

            // Build persistent pHash index + BK-tree to get candidate neighbors faster than O(N^2)
            if (!forceBruteForce)
            {
                try
                {
                    var indexCacheDir = GetThumbCacheDir();
                    var paths = sortedEntries.Select(s => s.path).ToList();
                    var (phashEntries, bk, tileIndex) = PhashIndex.LoadOrBuild(indexCacheDir, paths, GetImageHash, p => ComputeTilePackedHashes(p));
                    int candidateSeedProcessed = 0;

                    // For each entry query BK-tree within radius = hashThreshold and also tile matches
                    Parallel.For(0, phashEntries.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, i =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var candidatesSet = new HashSet<int>();

                        // BK-tree neighbors
                        var neis = bk.QueryRadius(phashEntries[i].PackedHash, hashThreshold);
                        foreach (var ni in neis) if (ni > i) candidatesSet.Add(ni);

                        // tile-based neighbors (allow images sharing at least 1 tile)
                        if (phashEntries[i].TileHashes != null)
                        {
                            foreach (var th in phashEntries[i].TileHashes)
                            {
                                if (tileIndex.TryGetValue(th, out var list))
                                {
                                    foreach (var ni in list) if (ni > i) candidatesSet.Add(ni);
                                }
                            }
                        }

                        foreach (var ni in candidatesSet)
                        {
                            // hist/comp prefilter on demand
                            var (histA, compA) = GetFeatures(i);
                            var (histB, compB) = GetFeatures(ni);
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
                            if (histOk || compOk)
                            {
                                int dist = HammingDistancePacked(phashEntries[i].PackedHash, phashEntries[ni].PackedHash);
                                candidatePairs.Add((i, ni, dist));
                            }
                        }

                        var current = System.Threading.Interlocked.Increment(ref candidateSeedProcessed);
                        if (current % 16 == 0 || current == phashEntries.Count)
                            RaiseProgressForStage(35, 20, current, phashEntries.Count);
                    });

                    RaiseStatus($"Found {candidatePairs.Count} hash-similar candidates (BK-tree + tiles)");
                }
                catch
                {
                    // fallback to original O(N^2) scanning (rare)
                    int candidateSeedProcessed = 0;
                    Parallel.For(0, sortedEntries.Count, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                        CancellationToken = ct
                    }, i =>
                    {
                        for (int j = i + 1; j < sortedEntries.Count; j++)
                        {
                            if (ct.IsCancellationRequested) return;



                            int dist = int.MaxValue;
                            if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                                dist = HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash, hashThreshold);
                            // quick color histogram prefilter to avoid obvious mismatches (compute on demand)
                            var (histA, compA) = GetFeatures(i);
                            var (histB, compB) = GetFeatures(j);
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
                        }

                        var current = System.Threading.Interlocked.Increment(ref candidateSeedProcessed);
                        if (current % 8 == 0 || current == sortedEntries.Count)
                            RaiseProgressForStage(35, 20, current, sortedEntries.Count);
                    });
                }
            }
            else
            {
                // force brute-force path requested: original O(N^2) scanning
                int candidateSeedProcessed = 0;
                Parallel.For(0, sortedEntries.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = ct
                }, i =>
                {
                    for (int j = i + 1; j < sortedEntries.Count; j++)
                    {
                        if (ct.IsCancellationRequested) return;

                        int dist = int.MaxValue;
                        if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                            dist = HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash, hashThreshold);
                        // quick color histogram prefilter to avoid obvious mismatches (compute on demand)
                        var (histA, compA) = GetFeatures(i);
                        var (histB, compB) = GetFeatures(j);
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
                    }

                    var current = System.Threading.Interlocked.Increment(ref candidateSeedProcessed);
                    if (current % 8 == 0 || current == sortedEntries.Count)
                        RaiseProgressForStage(35, 20, current, sortedEntries.Count);
                });
            }

            RaiseProgressForStage(35, 20, 1, 1);

            var sortedCandidates = candidatePairs.OrderBy(p => p.hashDist).ToList();
            RaiseStatus($"Found {sortedCandidates.Count} hash-similar candidates (from {totalPairs} pairs)");

            // Guardrail for very large datasets: cap candidate verifications to keep runtime practical.
            // Candidates are sorted by hash distance, so we retain the most promising pairs first.
            int maxCandidatesForSsim = int.MaxValue;
            if (sortedEntries.Count >= 15000) maxCandidatesForSsim = 45000;
            else if (sortedEntries.Count >= 9000) maxCandidatesForSsim = 70000;
            else if (sortedEntries.Count >= 5000) maxCandidatesForSsim = 90000;

            if (sortedCandidates.Count > maxCandidatesForSsim)
            {
                int originalCount = sortedCandidates.Count;
                sortedCandidates = sortedCandidates.Take(maxCandidatesForSsim).ToList();
                RaiseStatus($"Candidate cap applied: using top {sortedCandidates.Count}/{originalCount} pairs for SSIM");
            }

            if (sortedCandidates.Count == 0)
            {
                RaiseStatus("No similar images found");
                RaiseProgress(100);
                return results;
            }

            // Adaptive thumbnail size: lower resolution for huge workloads to reduce
            // per-pair SSIM and thumbnail IO costs while keeping relative ranking useful.
            int effectiveSsimThumbnailSize = ssimThumbnailSize;
            if (sortedCandidates.Count >= 60000)
                effectiveSsimThumbnailSize = Math.Min(effectiveSsimThumbnailSize, 64);
            else if (sortedCandidates.Count >= 20000)
                effectiveSsimThumbnailSize = Math.Min(effectiveSsimThumbnailSize, 96);
            // 4. PHASE 3: SSIM verification on candidates only (compute SSIMs in parallel and stream groups as found)
            // Init GPU SSIM before the preload so status text is correct from the start
            GpuSsim? sharedGs = null;
            bool sharedGsReady = false;
            if (useGpuSsim)
            {
                try
                {
                    sharedGs = new GpuSsim();
                    sharedGsReady = sharedGs.Init();
                    Log.Info($"SimilarImageService: shared GPU init = {sharedGsReady}");
                }
                catch (Exception ex) { Log.Error(ex); sharedGsReady = false; }
            }

            int gpuBatchSize = Math.Max(8, GpuSsim.MaxBatchPairs);
            int gpuPairsProcessed = 0;
            int gpuFallbackPairs = 0;

            if (useGpuSsim)
                RaiseStatus(sharedGsReady
                    ? $"Verifying {sortedCandidates.Count} candidates with GPU SSIM ({gpuBatchSize}-pair batches)..."
                    : $"GPU SSIM unavailable, falling back to CPU for {sortedCandidates.Count} candidates...");
            else
                RaiseStatus($"Verifying {sortedCandidates.Count} candidates with SSIM ({effectiveSsimThumbnailSize}px thumbs, parallel streaming)...");

            var thumbnailCache = new System.Collections.Concurrent.ConcurrentDictionary<int, MagickImage?>();
            var grayscaleThumbCache = new System.Collections.Concurrent.ConcurrentDictionary<int, float[]>();

            // Profiling counters (ticks measured via Stopwatch.GetTimestamp)
            long ssimCompareTicks = 0;
            long ssimCompareCount = 0;
            long imageLoadTicks = 0;
            long imageLoadCount = 0;

            // Preload thumbnails for all indices referenced by candidate pairs to avoid
            // repeated image loads. Use a persistent cache in %LOCALAPPDATA%/DupFree/thumbcache
            // keyed by file path + last write/length so thumbnails survive across runs.
            var indicesToPreload = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
            foreach (var (i, j, hashDist) in sortedCandidates)
            {
                indicesToPreload.TryAdd(i, 0);
                indicesToPreload.TryAdd(j, 0);
            }

            var thumbCacheDir = GetThumbCacheDir();
            int totalPreloadItems = Math.Max(1, indicesToPreload.Count);
            int preloadedItems = 0;
            Parallel.ForEach(indicesToPreload.Keys, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) }, idx =>
            {
                try
                {
                    var path = sortedEntries[idx].path;
                    // try persistent cache first
                    if (TryLoadThumbnailFromDiskCache(path, thumbCacheDir, out var cached))
                    {
                        thumbnailCache.TryAdd(idx, cached!);
                        // Also build grayscale float[] for GPU/SIMD SSIM even when loaded from disk cache
                        if (useSimdSsim || useGpuSsim)
                        {
                            try
                            {
                                var gf = CreateGrayscaleThumbnailFloats(cached!, effectiveSsimThumbnailSize);
                                if (gf != null) grayscaleThumbCache.TryAdd(idx, gf);
                            }
                            catch { }
                        }
                        return;
                    }

                    // otherwise create and save (Magick thumbnail)
                    var swStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var img = TryLoadImageWithTimeout(path, timeoutMs: 3000);
                    var swEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (img == null) return; // Skip if load failed/timed out
                    System.Threading.Interlocked.Add(ref imageLoadTicks, swEnd - swStart);
                    System.Threading.Interlocked.Increment(ref imageLoadCount);

                    var geo = new MagickGeometry((uint)effectiveSsimThumbnailSize, (uint)effectiveSsimThumbnailSize)
                    {
                        IgnoreAspectRatio = false
                    };
                    img.Resize(geo);
                    img.Extent((uint)effectiveSsimThumbnailSize, (uint)effectiveSsimThumbnailSize, Gravity.Center, MagickColors.Black);
                    thumbnailCache.TryAdd(idx, img);
                    try { SaveThumbnailToDiskCache(path, img, thumbCacheDir); } catch { }

                    // Additionally, build a grayscale float[] thumbnail for SIMD SSIM path if requested
                    if (useSimdSsim || useGpuSsim)
                    {
                        try
                        {
                            var gf = CreateGrayscaleThumbnailFloats(img, effectiveSsimThumbnailSize);
                            if (gf != null) grayscaleThumbCache.TryAdd(idx, gf);
                        }
                        catch { }
                    }
                }
                catch { }
                finally
                {
                    var current = System.Threading.Interlocked.Increment(ref preloadedItems);
                    if (current % 8 == 0 || current == totalPreloadItems)
                        RaiseProgressForStage(55, 15, current, totalPreloadItems);
                }
            });
            RaiseProgressForStage(55, 15, 1, 1);

            var allScoresBag = new System.Collections.Concurrent.ConcurrentBag<(double ssim, double hashSim, double histSim, double compSim, double composite, int a, int b)>();
            int verifiedCount = 0;

            int gpuWorkerCount = Math.Max(2, Math.Min(Environment.ProcessorCount, (Environment.ProcessorCount / 2) + 2));
            int ssimWorkerCount = (useGpuSsim && sharedGsReady)
                ? gpuWorkerCount
                : Environment.ProcessorCount;

            MagickImage? GetThumbnail(int index)
            {
                return thumbnailCache.GetOrAdd(index, idx =>
                {
                    try
                    {
                        var swStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        var img = TryLoadImageWithTimeout(sortedEntries[idx].path, timeoutMs: 3000);
                        var swEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (img == null) return null;
                        System.Threading.Interlocked.Add(ref imageLoadTicks, swEnd - swStart);
                        System.Threading.Interlocked.Increment(ref imageLoadCount);
                        var geo = new MagickGeometry((uint)effectiveSsimThumbnailSize, (uint)effectiveSsimThumbnailSize)
                        {
                            IgnoreAspectRatio = false
                        };
                        img.Resize(geo);
                        img.Extent((uint)effectiveSsimThumbnailSize, (uint)effectiveSsimThumbnailSize, Gravity.Center, MagickColors.Black);
                        return img;
                    }
                    catch { return null; }
                });
            }

            double ComputeMagickSsim(MagickImage thumbA, MagickImage thumbB)
            {
                var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                double distortion = thumbA.Compare(thumbB, ErrorMetric.StructuralSimilarity);
                var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                System.Threading.Interlocked.Increment(ref ssimCompareCount);
                return Math.Clamp(1.0 - distortion, 0.0, 1.0);
            }

            void FinalizePairScore(int i, int j, double ssim)
            {
                double hashSim = 0.5;
                if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                    hashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash) / 64.0;

                double pairHistDist = double.MaxValue; double histSim = 0.5;
                try
                {
                    var (histAi, _) = GetFeatures(i);
                    var (histBj, _) = GetFeatures(j);
                    if (histAi != null && histBj != null)
                    {
                        pairHistDist = HistogramDistance(histAi, histBj);
                        histSim = 1.0 - pairHistDist;
                    }
                }
                catch { }

                double pairCompDist = double.MaxValue; double compSim = 0.5;
                try
                {
                    var (_, compAi) = GetFeatures(i);
                    var (_, compBj) = GetFeatures(j);
                    if (compAi != null && compBj != null)
                    {
                        pairCompDist = SpatialHistogramDistance(compAi, compBj);
                        compSim = 1.0 - pairCompDist;
                    }
                }
                catch { }

                double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);
                allScoresBag.Add((ssim, hashSim, histSim, compSim, composite, i, j));

                var current = System.Threading.Interlocked.Increment(ref verifiedCount);
                if (current % 10 == 0 || current == sortedCandidates.Count)
                {
                    RaiseStatus($"SSIM computed {current}/{sortedCandidates.Count}...");
                    RaiseProgressForStage(70, 29, current, sortedCandidates.Count);
                }
            }

            bool TryPreparePair((int i, int j, int hashDist) pair, out int i, out int j, out MagickImage? thumbI, out MagickImage? thumbJ)
            {
                i = pair.i;
                j = pair.j;
                thumbI = null;
                thumbJ = null;

                var mi = fileMeta[i];
                var mj = fileMeta[j];
                if (mi.hasMeta && mj.hasMeta && mi.name == mj.name && mi.length == mj.length)
                    return false;

                double quickHashSim = 0.5;
                if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                    quickHashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash) / 64.0;

                double compositeUpper = (CompositeWeightSsim * 1.0) + (CompositeWeightHash * quickHashSim) + (CompositeWeightHist * 1.0) + (CompositeWeightComp * 1.0);
                if (compositeUpper < compositeThreshold)
                    return false;

                thumbI = GetThumbnail(i);
                thumbJ = GetThumbnail(j);
                return thumbI != null && thumbJ != null;
            }

            if (useGpuSsim && sharedGsReady && sharedGs != null)
            {
                int totalCount = sortedCandidates.Count;
                // Pre-allocate for all pairs; only [0..gpuCount) slots will be used.
                var gpuItems = new (float[] a, float[] b, int i, int j)[totalCount];
                int gpuCount = 0;
                var cpuFallbackBag = new System.Collections.Concurrent.ConcurrentBag<(int i, int j)>();

                // Stage 1: Parallel CPU prep — collect grayscale float pairs without loading MagickImages.
                Parallel.For(0, totalCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, ci =>
                {
                    try
                    {
                        var (pi, pj, _) = sortedCandidates[ci];
                        int i = pi, j = pj;

                        // Quick skip: identical file metadata
                        var mi = fileMeta[i];
                        var mj = fileMeta[j];
                        if (mi.hasMeta && mj.hasMeta && mi.name == mj.name && mi.length == mj.length)
                            return;

                        // Quick skip: composite upper bound below threshold
                        double quickHashSim = 0.5;
                        if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                            quickHashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash) / 64.0;
                        double compositeUpper = (CompositeWeightSsim * 1.0) + (CompositeWeightHash * quickHashSim) + (CompositeWeightHist * 1.0) + (CompositeWeightComp * 1.0);
                        if (compositeUpper < compositeThreshold)
                            return;

                        // Grab grayscale float arrays — already populated during preload for most images.
                        if (!grayscaleThumbCache.TryGetValue(i, out var gfA))
                        {
                            var thumbA = GetThumbnail(i);
                            if (thumbA != null)
                                gfA = CreateGrayscaleThumbnailFloats(thumbA, effectiveSsimThumbnailSize);
                            if (gfA != null) grayscaleThumbCache.TryAdd(i, gfA);
                        }
                        if (!grayscaleThumbCache.TryGetValue(j, out var gfB))
                        {
                            var thumbB = GetThumbnail(j);
                            if (thumbB != null)
                                gfB = CreateGrayscaleThumbnailFloats(thumbB, effectiveSsimThumbnailSize);
                            if (gfB != null) grayscaleThumbCache.TryAdd(j, gfB);
                        }

                        if (gfA != null && gfB != null)
                        {
                            // Lock-free slot assignment: each thread writes to its own unique slot.
                            int slot = System.Threading.Interlocked.Increment(ref gpuCount) - 1;
                            gpuItems[slot] = (gfA, gfB, i, j);
                        }
                        else
                        {
                            cpuFallbackBag.Add((i, j));
                        }
                    }
                    catch { }
                });

                // Stage 2: Sequential GPU dispatch — one large dispatch at a time, no lock contention.
                int batchSz = GpuSsim.MaxBatchPairs;
                var batchInput = new List<(float[], float[])>(batchSz);
                for (int start = 0; start < gpuCount; start += batchSz)
                {
                    int end = Math.Min(start + batchSz, gpuCount);
                    batchInput.Clear();
                    for (int bi = start; bi < end; bi++)
                        batchInput.Add((gpuItems[bi].a, gpuItems[bi].b));

                    var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                    var batchScores = sharedGs.ComputeSsimGpuBatched(batchInput, effectiveSsimThumbnailSize, effectiveSsimThumbnailSize);
                    var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                    System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                    System.Threading.Interlocked.Add(ref ssimCompareCount, batchScores.Length);

                    for (int bi = 0; bi < batchScores.Length; bi++)
                        FinalizePairScore(gpuItems[start + bi].i, gpuItems[start + bi].j, batchScores[bi]);

                    gpuPairsProcessed += batchScores.Length;
                    if (gpuPairsProcessed % 20000 < batchSz)
                    {
                        RaiseStatus($"GPU SSIM: {gpuPairsProcessed:N0} / {gpuCount:N0} pairs...");
                        RaiseProgressForStage(70, 25, gpuPairsProcessed, gpuCount);
                    }
                }

                // Stage 3: CPU fallback in parallel for pairs missing grayscale float data.
                Parallel.ForEach(cpuFallbackBag, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, item =>
                {
                    try
                    {
                        var thumbA = GetThumbnail(item.i);
                        var thumbB = GetThumbnail(item.j);
                        if (thumbA == null || thumbB == null) return;
                        double ssim = ComputeMagickSsim(thumbA, thumbB);
                        FinalizePairScore(item.i, item.j, ssim);
                        System.Threading.Interlocked.Increment(ref gpuFallbackPairs);
                    }
                    catch { }
                });
            }
            else
            {
                Parallel.ForEach(sortedCandidates, new ParallelOptions { MaxDegreeOfParallelism = ssimWorkerCount, CancellationToken = ct }, pair =>
                {
                    try
                    {
                        if (!TryPreparePair(pair, out var i, out var j, out var thumbI, out var thumbJ))
                            return;

                        double ssim = 0.0;
                        try
                        {
                            if (useSimdSsim && grayscaleThumbCache.TryGetValue(i, out var gfA) && grayscaleThumbCache.TryGetValue(j, out var gfB))
                            {
                                var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                ssim = ComputeSsimSimd(gfA, gfB, effectiveSsimThumbnailSize, effectiveSsimThumbnailSize);
                                var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                System.Threading.Interlocked.Increment(ref ssimCompareCount);
                            }
                            else
                            {
                                ssim = ComputeMagickSsim(thumbI!, thumbJ!);
                            }
                        }
                        catch { }

                        FinalizePairScore(i, j, ssim);
                    }
                    catch { }
                });
            }

            if (useGpuSsim)
            {
                Log.Info($"SimilarImageService: GPU SSIM pairs={gpuPairsProcessed}, GPU fallback pairs={gpuFallbackPairs}, candidates={sortedCandidates.Count}, gpuReady={sharedGsReady}");
                RaiseStatus($"GPU SSIM done: {gpuPairsProcessed:N0} GPU pairs, {gpuFallbackPairs} CPU fallback");
            }

            // Cleanup thumbnails
            foreach (var thumb in thumbnailCache.Values)
            {
                try { thumb?.Dispose(); } catch { }
            }

            // Materialize scores list for later use (debugging / closest pairs)
            var allScores = allScoresBag.ToList();

            // If requested, export per-candidate detailed CSV with SSIM and component scores
            if (!string.IsNullOrEmpty(exportEdgeCsv))
            {
                try
                {
                    var csvLines = new System.Collections.Generic.List<string>
                    {
                        "ssim,hashSim,histSim,compSim,composite,pathA,pathB"
                    };
                    foreach (var s in allScores)
                    {
                        int a = s.a, b = s.b;
                        double ssim = s.ssim;
                        double hashSim = s.hashSim;
                        double histSim = s.histSim;
                        double compSim = s.compSim;
                        double composite = s.composite;
                        // Escape paths that may contain commas
                        string pa = sortedEntries[a].path.Replace("\"", "\"\"");
                        string pb = sortedEntries[b].path.Replace("\"", "\"\"");
                        csvLines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4},{3:F4},{4:F4},\"{5}\",\"{6}\"", ssim, hashSim, histSim, compSim, composite, pa, pb));
                    }
                    System.IO.File.WriteAllLines(exportEdgeCsv, csvLines);
                }
                catch { }
            }

            // Build graph edges using composite scoring and then form connected components (union-find)
            int n = sortedEntries.Count;
            var parent = new int[n];
            var compSize = new int[n];
            for (int i = 0; i < n; i++) { parent[i] = i; compSize[i] = 1; }
            int find(int x) => parent[x] == x ? x : (parent[x] = find(parent[x]));
            void unite(int a, int b)
            {
                a = find(a); b = find(b);
                if (a == b) return;
                if (compSize[a] < compSize[b])
                {
                    (b, a) = (a, b);
                }
                parent[b] = a; compSize[a] += compSize[b];
            }

            // Union all accepted edges first. Final component materialization guarantees
            // a strict one-group-per-image invariant.
            foreach (var s in allScores)
            {
                if (s.composite < compositeThreshold) continue;
                unite(s.a, s.b);
            }

            var components = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int root = find(i);
                if (!components.TryGetValue(root, out var members))
                {
                    members = [];
                    components[root] = members;
                }
                members.Add(i);
            }

            var acceptedEdgeWeights = new Dictionary<(int, int), double>();
            foreach (var s in allScores)
            {
                if (s.composite < compositeThreshold) continue;
                var key = (Math.Min(s.a, s.b), Math.Max(s.a, s.b));
                if (!acceptedEdgeWeights.TryGetValue(key, out var existingWeight) || s.composite > existingWeight)
                    acceptedEdgeWeights[key] = s.composite;
            }

            // Post-process groups with stricter direct-edge clustering so transitive
            // chains do not survive as one large group. A candidate can join a cluster
            // only if it has a direct strong edge to every existing member.
            double minClusterSsim = Math.Max(0.80, Math.Min(ssimThreshold, 0.92));
            double minClusterComposite = Math.Max(0.85, compositeThreshold);
            var edgeMetrics = new Dictionary<(int, int), (double ssim, double composite)>();
            foreach (var s in allScores)
            {
                if (s.composite < minClusterComposite || s.ssim < minClusterSsim) continue;
                var key = (Math.Min(s.a, s.b), Math.Max(s.a, s.b));
                if (!edgeMetrics.TryGetValue(key, out var existing) || s.composite > existing.composite)
                    edgeMetrics[key] = (s.ssim, s.composite);
            }

            static (int, int) GetPairKey(int a, int b) => a < b ? (a, b) : (b, a);

            double GetAcceptedComposite(int a, int b)
            {
                return acceptedEdgeWeights.TryGetValue(GetPairKey(a, b), out var composite) ? composite : 0.0;
            }

            bool IsStrictCluster(List<int> clusterMembers)
            {
                for (int i = 0; i < clusterMembers.Count; i++)
                {
                    for (int j = i + 1; j < clusterMembers.Count; j++)
                    {
                        if (!edgeMetrics.ContainsKey(GetPairKey(clusterMembers[i], clusterMembers[j])))
                            return false;
                    }
                }

                return true;
            }

            List<List<int>> PartitionIntoStrictClusters(List<int> clusterMembers)
            {
                if (clusterMembers.Count < 2)
                    return [];

                if (clusterMembers.Count == 2)
                {
                    return edgeMetrics.ContainsKey(GetPairKey(clusterMembers[0], clusterMembers[1]))
                        ? [clusterMembers]
                        : [];
                }

                if (IsStrictCluster(clusterMembers))
                    return [clusterMembers];

                int seedA = clusterMembers[0];
                int seedB = clusterMembers[1];
                double weakestComposite = double.MaxValue;
                for (int i = 0; i < clusterMembers.Count; i++)
                {
                    for (int j = i + 1; j < clusterMembers.Count; j++)
                    {
                        double pairComposite = GetAcceptedComposite(clusterMembers[i], clusterMembers[j]);
                        if (pairComposite < weakestComposite)
                        {
                            weakestComposite = pairComposite;
                            seedA = clusterMembers[i];
                            seedB = clusterMembers[j];
                        }
                    }
                }

                var left = new List<int> { seedA };
                var right = new List<int> { seedB };

                var remaining = clusterMembers
                    .Where(idx => idx != seedA && idx != seedB)
                    .OrderByDescending(idx => Math.Max(GetAcceptedComposite(idx, seedA), GetAcceptedComposite(idx, seedB)))
                    .ToList();

                foreach (var idx in remaining)
                {
                    double leftScore = left.Count == 0 ? 0.0 : left.Average(member => GetAcceptedComposite(idx, member));
                    double rightScore = right.Count == 0 ? 0.0 : right.Average(member => GetAcceptedComposite(idx, member));

                    if (leftScore > rightScore)
                    {
                        left.Add(idx);
                    }
                    else if (rightScore > leftScore)
                    {
                        right.Add(idx);
                    }
                    else if (left.Count <= right.Count)
                    {
                        left.Add(idx);
                    }
                    else
                    {
                        right.Add(idx);
                    }
                }

                var result = new List<List<int>>();
                result.AddRange(PartitionIntoStrictClusters(left));
                result.AddRange(PartitionIntoStrictClusters(right));
                return result;
            }

            List<List<int>> RefineClusterByCohesion(List<int> clusterMembers)
            {
                var refined = new List<List<int>>();
                var pending = new Queue<List<int>>();
                pending.Enqueue(clusterMembers);

                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    if (current.Count < 2)
                        continue;

                    // Keep small groups intact; over-splitting hurts useful output.
                    if (current.Count < 5)
                    {
                        refined.Add(current);
                        continue;
                    }

                    var memberAverageSsim = new Dictionary<int, double>();
                    foreach (var member in current)
                    {
                        double sum = 0.0;
                        int count = 0;
                        foreach (var other in current)
                        {
                            if (member == other) continue;
                            if (edgeMetrics.TryGetValue(GetPairKey(member, other), out var metric))
                            {
                                sum += metric.ssim;
                                count += 1;
                            }
                        }

                        memberAverageSsim[member] = count > 0 ? sum / count : 0.0;
                    }

                    double groupAverage = memberAverageSsim.Values.Average();
                    double minAllowed = Math.Max(0.84, Math.Min(0.96, groupAverage - 0.035));

                    var weakest = memberAverageSsim.OrderBy(kvp => kvp.Value).First();
                    if (weakest.Value >= minAllowed)
                    {
                        refined.Add(current);
                        continue;
                    }

                    int weakMember = weakest.Key;
                    var side = new List<int> { weakMember };
                    var rest = current.Where(idx => idx != weakMember).ToList();

                    double sideMinSimilarity = Math.Max(minClusterSsim, minAllowed - 0.01);
                    foreach (var idx in rest)
                    {
                        if (edgeMetrics.TryGetValue(GetPairKey(weakMember, idx), out var metric) && metric.ssim >= sideMinSimilarity)
                            side.Add(idx);
                    }

                    var sideSet = new HashSet<int>(side);
                    rest = current.Where(idx => !sideSet.Contains(idx)).ToList();

                    // If we failed to split meaningfully, keep current cluster as-is.
                    if (side.Count == current.Count || rest.Count == current.Count || (side.Count < 2 && rest.Count < 2))
                    {
                        refined.Add(current);
                        continue;
                    }

                    if (side.Count >= 2)
                        pending.Enqueue(side);
                    if (rest.Count >= 2)
                        pending.Enqueue(rest);
                }

                return refined;
            }

            var finalClusters = new List<List<int>>();
            foreach (var members in components.Values)
            {
                if (members.Count < 2) continue;

                var strictClusters = PartitionIntoStrictClusters([.. members]);
                foreach (var strictCluster in strictClusters)
                {
                    finalClusters.AddRange(RefineClusterByCohesion(strictCluster));
                }
            }

            foreach (var members in finalClusters)
            {
                if (members.Count < 2) continue;

                double totalSsim = 0.0;
                int totalEdges = 0;
                for (int i = 0; i < members.Count; i++)
                {
                    for (int j = i + 1; j < members.Count; j++)
                    {
                        if (edgeMetrics.TryGetValue(GetPairKey(members[i], members[j]), out var metrics))
                        {
                            totalSsim += metrics.ssim;
                            totalEdges += 1;
                        }
                    }
                }

                double avgSsim = totalEdges > 0 ? totalSsim / totalEdges : 0.0;

                var group = new SimilarImageGroup
                {
                    GroupId = $"group_{results.Count}",
                    Images = [.. members.Select(mi => CreateFileItem(sortedEntries[mi].path))],
                    SimilarityScore = avgSsim
                };

                results.Add(group);
                RaiseGroupFound(group);
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

            // Report profiling summary (convert ticks to ms)
            try
            {
                double tickFreq = (double)System.Diagnostics.Stopwatch.Frequency;
                double ssimMs = (double)System.Threading.Interlocked.Read(ref ssimCompareTicks) * 1000.0 / tickFreq;
                long ssimCount = System.Threading.Interlocked.Read(ref ssimCompareCount);
                double imgLoadMs = (double)System.Threading.Interlocked.Read(ref imageLoadTicks) * 1000.0 / tickFreq;
                long imgLoadCount = System.Threading.Interlocked.Read(ref imageLoadCount);
                RaiseStatus($"Profile: SSIM compares={ssimCount}, SSIM ms={ssimMs:F1}, image loads={imgLoadCount}, image load ms={imgLoadMs:F1}");
                if (TelemetryService.Enabled)
                {
                    TelemetryService.TrackMetric("SSIMms", ssimMs);
                    TelemetryService.TrackMetric("SSIMCount", ssimCount);
                    TelemetryService.TrackMetric("ImageLoadMs", imgLoadMs);
                    TelemetryService.TrackMetric("ImageLoadCount", imgLoadCount);
                }
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
                        Images =
                        [
                            CreateFileItem(sortedEntries[s.a].path),
                            CreateFileItem(sortedEntries[s.b].path)
                        ],
                        SimilarityScore = s.ssim
                    });
                }
                return results;
            }

            RaiseStatus($"Done! Found {results.Count} groups");
            RaiseProgress(100);

            return results;
        }

        private (byte[]? hash, ulong packed) GetImageHash(string filePath)
        {
            try
            {
                using var image = Image.FromFile(filePath);
                return ComputePerceptualHash(image);
            }
            catch
            {
                return (null, 0UL);
            }
        }

        private (byte[] hash, ulong packed) ComputePerceptualHash(Image image)
        {
            using var resized = new Bitmap(image, new Size(64, 64));
            var grayscale = ToGrayscale(resized);
            var hash = new byte[64];
            ulong packed = 0UL;
            int hashIndex = 0;

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    if (hashIndex < 64 && x < 63)
                    {
                        int current = grayscale[y * 64 + x];
                        int next = grayscale[y * 64 + (x + 1)];
                        byte bit = (byte)(current < next ? 1 : 0);
                        hash[hashIndex] = bit;
                        if (bit != 0)
                            packed |= (1UL << hashIndex);
                        hashIndex++;
                    }
                }
            }

            return (hash, packed);
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

        private int HammingDistancePacked(ulong h1, ulong h2, int earlyExitThreshold = int.MaxValue)
        {
            // XOR and population count
            int diff = BitOperations.PopCount(h1 ^ h2);
            if (diff > earlyExitThreshold)
                return diff;
            return diff;
        }

        private (float[]? hist, float[]? spatial) ComputeColorAndSpatialHistograms(string filePath)
        {
            const int colorBinsPerChannel = 4; // 4x4x4 = 64 bins
            const int spatialGrid = 3;
            const int spatialBinsPerCell = 64;
            const int spatialTotalBins = spatialBinsPerCell * spatialGrid * spatialGrid; // 576

            var colorHist = new float[64];
            var spatialHist = new float[spatialTotalBins];

            using (var bmp = new Bitmap(filePath))
            using (var small = new Bitmap(bmp, new Size(96, 96)))
            {
                int cellW = Math.Max(1, small.Width / spatialGrid);
                int cellH = Math.Max(1, small.Height / spatialGrid);

                var rect = new System.Drawing.Rectangle(0, 0, small.Width, small.Height);
                var data = small.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    int stride = data.Stride;
                    int h = small.Height;
                    int w = small.Width;
                    byte[] pixels = new byte[stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

                    for (int y = 0; y < h; y++)
                    {
                        int row = y * stride;
                        int cellY = Math.Min(y / cellH, spatialGrid - 1);
                        for (int x = 0; x < w; x++)
                        {
                            int p = row + (x * 3);
                            int b = pixels[p] >> 6;
                            int g = pixels[p + 1] >> 6;
                            int r = pixels[p + 2] >> 6;

                            int colorBin = (r << 4) | (g << 2) | b;
                            colorHist[colorBin] += 1.0f;

                            int cellX = Math.Min(x / cellW, spatialGrid - 1);
                            int cellIdx = cellY * spatialGrid + cellX;
                            int spatialIdx = (cellIdx * spatialBinsPerCell) + colorBin;
                            spatialHist[spatialIdx] += 1.0f;
                        }
                    }
                }
                finally
                {
                    small.UnlockBits(data);
                }
            }

            float colorSum = colorHist.Sum();
            if (colorSum > 0)
            {
                for (int i = 0; i < colorHist.Length; i++)
                    colorHist[i] /= colorSum;
            }

            float spatialSum = spatialHist.Sum();
            if (spatialSum > 0)
            {
                for (int i = 0; i < spatialHist.Length; i++)
                    spatialHist[i] /= spatialSum;
            }

            return (colorHist, spatialHist);
        }

        private float[]? ComputeColorHistogram(string filePath)
        {
            try
            {
                var (hist, _) = ComputeColorAndSpatialHistograms(filePath);
                return hist;
            }
            catch
            {
                return null;
            }
        }

        private float[]? ComputeSpatialHistogram(string filePath)
        {
            try
            {
                var (_, spatial) = ComputeColorAndSpatialHistograms(filePath);
                return spatial;
            }
            catch
            {
                return null;
            }
        }

        private double SpatialHistogramDistance(float[]? a, float[]? b)
        {
            if (a == null || b == null) return double.MaxValue;
            if (a.Length != b.Length) return double.MaxValue;
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++)
                sum += Math.Abs(a[i] - b[i]);
            // normalize (max sum is 2)
            return sum / 2.0;
        }

        private double HistogramDistance(float[]? a, float[]? b)
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

        private float[]? CreateGrayscaleThumbnailFloats(string path, int size)
        {
            try
            {
                using var img = TryLoadImageWithTimeout(path, timeoutMs: 3000);
                if (img == null) return null;
                return CreateGrayscaleThumbnailFloats(img, size);
            }
            catch { return null; }
        }

        private float[]? CreateGrayscaleThumbnailFloats(MagickImage image, int size)
        {
            try
            {
                using var working = image.Clone();
                if (working.Width != size || working.Height != size)
                {
                    var geo = new MagickGeometry((uint)size, (uint)size)
                    {
                        IgnoreAspectRatio = false
                    };
                    working.Resize(geo);
                    working.Extent((uint)size, (uint)size, Gravity.Center, MagickColors.Black);
                }

                using var bitmapStream = new MemoryStream();
                working.Format = MagickFormat.Bmp;
                working.Write(bitmapStream);
                bitmapStream.Position = 0;

                using var sourceBitmap = new Bitmap(bitmapStream);
                using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.DrawImage(sourceBitmap, new Rectangle(0, 0, size, size));
                }

                var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                var lockBits = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                try
                {
                    int stride = lockBits.Stride;
                    int h = bmp.Height;
                    int w = bmp.Width;
                    byte[] pixels = new byte[stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(lockBits.Scan0, pixels, 0, pixels.Length);
                    var floats = new float[w * h];
                    for (int yy = 0; yy < h; yy++)
                    {
                        for (int xx = 0; xx < w; xx++)
                        {
                            int idx = yy * stride + xx * 3;
                            byte b = pixels[idx];
                            byte g = pixels[idx + 1];
                            byte r = pixels[idx + 2];
                            floats[yy * w + xx] = (r + g + b) / 3.0f;
                        }
                    }
                    return floats;
                }
                finally
                {
                    bmp.UnlockBits(lockBits);
                }
            }
            catch { return null; }
        }

        private double ComputeSsimSimd(float[] a, float[] b, int w, int h)
        {
            if (a == null || b == null) return 0.0;
            int n = w * h;
            if (a.Length != n || b.Length != n) return 0.0;

            // compute means
            double sumA = 0.0, sumB = 0.0;
            int vecSize = Vector<float>.Count;
            var vSumA = Vector<float>.Zero;
            var vSumB = Vector<float>.Zero;
            int i;
            for (i = 0; i + vecSize <= n; i += vecSize)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                vSumA += va;
                vSumB += vb;
            }
            for (int k = 0; k < vecSize; k++)
            {
                sumA += vSumA[k];
                sumB += vSumB[k];
            }
            for (; i < n; i++) { sumA += a[i]; sumB += b[i]; }
            double muA = sumA / n;
            double muB = sumB / n;

            // compute variances and covariance
            double varA = 0.0, varB = 0.0, cov = 0.0;
            var vVarA = Vector<float>.Zero;
            var vVarB = Vector<float>.Zero;
            var vCov = Vector<float>.Zero;
            var vMuA = new Vector<float>((float)muA);
            var vMuB = new Vector<float>((float)muB);
            for (i = 0; i + vecSize <= n; i += vecSize)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                var da = va - vMuA;
                var db = vb - vMuB;
                vVarA += da * da;
                vVarB += db * db;
                vCov += da * db;
            }
            for (int k = 0; k < vecSize; k++)
            {
                varA += vVarA[k];
                varB += vVarB[k];
                cov += vCov[k];
            }
            for (; i < n; i++)
            {
                double da = a[i] - muA;
                double db = b[i] - muB;
                varA += da * da;
                varB += db * db;
                cov += da * db;
            }
            varA /= n;
            varB /= n;
            cov /= n;

            // SSIM constants
            const double K1 = 0.01, K2 = 0.03;
            const double L = 255.0;
            double C1 = (K1 * L) * (K1 * L);
            double C2 = (K2 * L) * (K2 * L);

            double numerator = (2.0 * muA * muB + C1) * (2.0 * cov + C2);
            double denominator = (muA * muA + muB * muB + C1) * (varA + varB + C2);
            if (denominator <= 0.0) return 0.0;
            double ssim = numerator / denominator;
            if (double.IsNaN(ssim) || double.IsInfinity(ssim)) return 0.0;
            return Math.Clamp(ssim, 0.0, 1.0);
        }

        // Persistent thumbnail cache helpers
        private string GetThumbCacheDir()
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DupFree", "thumbcache");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }

        private bool TryLoadThumbnailFromDiskCache(string sourcePath, string cacheDir, out MagickImage? image)
        {
            image = null;
            try
            {
                var fi = new FileInfo(sourcePath);
                string key = $"{sourcePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                var name = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".png";
                var path = Path.Combine(cacheDir, name);
                if (!File.Exists(path)) return false;
                image = TryLoadImageWithTimeout(path, timeoutMs: 3000);
                return image != null;
            }
            catch
            {
                image = null;
                return false;
            }
        }

        private void SaveThumbnailToDiskCache(string sourcePath, MagickImage image, string cacheDir)
        {
            try
            {
                var fi = new FileInfo(sourcePath);
                string key = $"{sourcePath}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                using var md5 = MD5.Create();
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
                var name = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant() + ".png";
                var path = Path.Combine(cacheDir, name);
                // clone before writing in case caller continues to use the image
                using var clone = image.Clone();
                clone.Format = MagickFormat.Png;
                clone.Write(path);
            }
            catch { }
        }

        // Helper: post status message to UI thread if possible
        private void RaiseStatus(string msg)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => OnStatusChanged?.Invoke(msg), null);
            else
                OnStatusChanged?.Invoke(msg);
        }

        private void RaiseProgress(int percent)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => OnProgressChanged?.Invoke(percent), null);
            else
                OnProgressChanged?.Invoke(percent);
        }

        private void RaiseGroupFound(SimilarImageGroup group)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => OnGroupFound?.Invoke(group), null);
            else
                OnGroupFound?.Invoke(group);
        }

        private void RaiseImageAddedToGroup(string groupId, FileItemViewModel item)
        {
            if (_syncContext != null)
                _syncContext.Post(_ => OnImageAddedToGroup?.Invoke(groupId, item), null);
            else
                OnImageAddedToGroup?.Invoke(groupId, item);
        }
    }
}
