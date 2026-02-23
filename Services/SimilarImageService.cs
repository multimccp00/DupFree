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

            // 1. Collect image files
            RaiseStatus("Collecting image files...");
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
            RaiseStatus($"Found {imageFiles.Count} images");

            // 2. PHASE 1: Fast hash computation (O(N)) - PARALLELIZED
            RaiseStatus("Computing perceptual hashes...");
            var entriesLock = new object();
            var entries = new System.Collections.Concurrent.ConcurrentBag<(string path, byte[]? hash, ulong packedHash, float[]? hist, float[]? spatial, int originalIndex)>();

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

                if (i % 10 == 0)
                {
                    RaiseStatus($"Hashing {i + 1}/{imageFiles.Count}...");
                    progress?.Report((i + 1, imageFiles.Count));
                }
            });

            // (was disposing a shared GPU helper here prematurely) - will dispose at method end

            var sortedEntries = entries.OrderBy(e => e.originalIndex).Select(e => (e.path, e.hash, e.packedHash, e.hist, e.spatial)).ToList();

            if (sortedEntries.Count < 2) return results;

            // 3. PHASE 2: Build candidate pairs using hash pre-filter (fast, reduces SSIM comparisons)
            RaiseStatus("Finding hash-similar candidates (using BK-tree index)...");

            // Allow user to lower similarity down to 75% (was previously clamped to 85%)
            double ssimThreshold = Math.Clamp(maxDistance, 75.0, 99.0) / 100.0;
            // Use LOOSER hash threshold (25 bits) since we skip exact duplicates anyway
            int hashThreshold = hashThresholdOverride > 0 ? hashThresholdOverride : 25;

            var candidatePairs = new System.Collections.Concurrent.ConcurrentBag<(int i, int j, int hashDist)>();
            var histCache = new System.Collections.Concurrent.ConcurrentDictionary<int, float[]?>();
            var spatialCache = new System.Collections.Concurrent.ConcurrentDictionary<int, float[]?>();
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

                    var members = new List<int> { i };

                    for (int j = i + 1; j < sortedEntries.Count; j++)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (removed[j]) continue;



                        // quick file-name/size skip
                        try
                        {
                            var fi = new FileInfo(sortedEntries[i].path);
                            var fj = new FileInfo(sortedEntries[j].path);
                            if (fi.Name == fj.Name && fi.Length == fj.Length) { removed[j] = true; members.Add(j); continue; }
                        }
                        catch { }

                        int dist = int.MaxValue;
                        if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                            dist = HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash, hashThreshold);
                        if (dist > hashThreshold) continue;

                        // compute hist/spatial on demand
                        var histA = histCache.GetOrAdd(i, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                        var histB = histCache.GetOrAdd(j, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                        var compA = spatialCache.GetOrAdd(i, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
                        var compB = spatialCache.GetOrAdd(j, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });

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
                                var img = new MagickImage(sortedEntries[idx].path);
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
                                var img = new MagickImage(sortedEntries[idx].path);
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
                            var histA = histCache.GetOrAdd(i, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                            var histB = histCache.GetOrAdd(ni, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                            var compA = spatialCache.GetOrAdd(i, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
                            var compB = spatialCache.GetOrAdd(ni, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
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
                    });

                    RaiseStatus($"Found {candidatePairs.Count} hash-similar candidates (BK-tree + tiles)");
                }
                catch
                {
                    // fallback to original O(N^2) scanning (rare)
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
                            var histA = histCache.GetOrAdd(i, idx =>
                            {
                                try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; }
                            });
                            var histB = histCache.GetOrAdd(j, idx =>
                            {
                                try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; }
                            });
                            var compA = spatialCache.GetOrAdd(i, idx =>
                            {
                                try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; }
                            });
                            var compB = spatialCache.GetOrAdd(j, idx =>
                            {
                                try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; }
                            });
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
                    });
                }
            }
            else
            {
                // force brute-force path requested: original O(N^2) scanning
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
                        var histA = histCache.GetOrAdd(i, idx =>
                        {
                            try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; }
                        });
                        var histB = histCache.GetOrAdd(j, idx =>
                        {
                            try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; }
                        });
                        var compA = spatialCache.GetOrAdd(i, idx =>
                        {
                            try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; }
                        });
                        var compB = spatialCache.GetOrAdd(j, idx =>
                        {
                            try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; }
                        });
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
                });
            }

            var sortedCandidates = candidatePairs.OrderBy(p => p.hashDist).ToList();
            RaiseStatus($"Found {sortedCandidates.Count} hash-similar candidates (from {totalPairs} pairs)");

            if (sortedCandidates.Count == 0)
            {
                RaiseStatus("No similar images found");
                return results;
            }

            // 4. PHASE 3: SSIM verification on candidates only (compute SSIMs in parallel and stream groups as found)
            RaiseStatus($"Verifying {sortedCandidates.Count} candidates with SSIM (parallel streaming)...");

            // Calculate total operations for overall progress tracking
            int totalOps = imageFiles.Count + sortedCandidates.Count;
            RaiseProgress(0);  // Reset progress to 0 as SSIM phase begins
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
                                var gf = CreateGrayscaleThumbnailFloats(path, ssimThumbnailSize);
                                if (gf != null) grayscaleThumbCache.TryAdd(idx, gf);
                            }
                            catch { }
                        }
                        return;
                    }

                    // otherwise create and save (Magick thumbnail)
                    var swStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var img = new MagickImage(path);
                    var swEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                    System.Threading.Interlocked.Add(ref imageLoadTicks, swEnd - swStart);
                    System.Threading.Interlocked.Increment(ref imageLoadCount);

                    var geo = new MagickGeometry((uint)ssimThumbnailSize, (uint)ssimThumbnailSize)
                    {
                        IgnoreAspectRatio = false
                    };
                    img.Resize(geo);
                    img.Extent((uint)ssimThumbnailSize, (uint)ssimThumbnailSize, Gravity.Center, MagickColors.Black);
                    thumbnailCache.TryAdd(idx, img);
                    try { SaveThumbnailToDiskCache(path, img, thumbCacheDir); } catch { }

                    // Additionally, build a grayscale float[] thumbnail for SIMD SSIM path if requested
                    if (useSimdSsim || useGpuSsim)
                    {
                        try
                        {
                            var gf = CreateGrayscaleThumbnailFloats(path, ssimThumbnailSize);
                            if (gf != null) grayscaleThumbCache.TryAdd(idx, gf);
                        }
                        catch { }
                    }
                }
                catch { }
            });

            var allScoresBag = new System.Collections.Concurrent.ConcurrentBag<(double ssim, int a, int b)>();
            int verifiedCount = 0;

            // Prepare streaming/grouping structures up front so parallel threads can add groups immediately
            var pathToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int idx = 0; idx < sortedEntries.Count; idx++)
                pathToIndex[sortedEntries[idx].path] = idx;

            var groupAssignment = new int[sortedEntries.Count];
            Array.Fill(groupAssignment, -1);
            var groupAssignmentLock = new object();
            var resultsLock = new object();

            // If GPU SSIM is requested, initialize one shared GpuSsim instance to avoid per-pair device creation.
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

                    // Quick upper-bound composite check (no SSIM) to skip impossible pairs
                    double quickHashSim = 0.5;
                    if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                        quickHashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash) / 64.0;

                    // Optimistic upper bound: assume hist/spatial could be perfect (1.0).
                    // This is a safe (non-underestimating) upper bound that avoids computing
                    // hist/spatial here while still allowing aggressive skipping.
                    double compositeUpper = (CompositeWeightSsim * 1.0) + (CompositeWeightHash * quickHashSim) + (CompositeWeightHist * 1.0) + (CompositeWeightComp * 1.0);
                    if (compositeUpper < compositeThreshold)
                        return; // skip this pair; cannot reach threshold even with perfect hist/comp/ssim

                    // Load thumbnails on-demand - SMALLER SIZE (128x128) for speed
                    var thumbI = thumbnailCache.GetOrAdd(i, idx =>
                    {
                        try
                        {
                            var swStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            var img = new MagickImage(sortedEntries[idx].path);
                            var swEnd = System.Diagnostics.Stopwatch.GetTimestamp();
                            System.Threading.Interlocked.Add(ref imageLoadTicks, swEnd - swStart);
                            System.Threading.Interlocked.Increment(ref imageLoadCount);
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

                    var thumbJ = thumbnailCache.GetOrAdd(j, idx =>
                    {
                        try
                        {
                            var img = new MagickImage(sortedEntries[idx].path);
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

                    if (thumbI == null || thumbJ == null) return;

                    double ssim = 0.0;
                    try
                    {
                        if (useGpuSsim)
                        {
                            try
                            {
                                var gs = sharedGs;
                                if (gs != null && sharedGsReady)
                                {
                                    if (grayscaleThumbCache.TryGetValue(i, out var gfA) && grayscaleThumbCache.TryGetValue(j, out var gfB))
                                    {
                                        var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                        ssim = gs.ComputeSsimGpu(gfA, gfB, ssimThumbnailSize, ssimThumbnailSize);
                                        var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                        System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                        System.Threading.Interlocked.Increment(ref ssimCompareCount);
                                    }
                                    else
                                    {
                                        var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                        double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                                        var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                        System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                        System.Threading.Interlocked.Increment(ref ssimCompareCount);
                                        ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                                    }
                                }
                                else
                                {
                                    // fallback to Magick if GPU init failed
                                    var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                    double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                                    var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                    System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                    System.Threading.Interlocked.Increment(ref ssimCompareCount);
                                    ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                                }
                            }
                            catch
                            {
                                var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                                var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                System.Threading.Interlocked.Increment(ref ssimCompareCount);
                                ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                            }
                        }
                        else if (useSimdSsim)
                        {
                            if (grayscaleThumbCache.TryGetValue(i, out var gfA) && grayscaleThumbCache.TryGetValue(j, out var gfB))
                            {
                                var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                ssim = ComputeSsimSimd(gfA, gfB, ssimThumbnailSize, ssimThumbnailSize);
                                var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                System.Threading.Interlocked.Increment(ref ssimCompareCount);
                            }
                            else
                            {
                                var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                                double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                                var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                                System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                                System.Threading.Interlocked.Increment(ref ssimCompareCount);
                                ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                            }
                        }
                        else
                        {
                            var sws = System.Diagnostics.Stopwatch.GetTimestamp();
                            double distortion = thumbI.Compare(thumbJ, ErrorMetric.StructuralSimilarity);
                            var swe = System.Diagnostics.Stopwatch.GetTimestamp();
                            System.Threading.Interlocked.Add(ref ssimCompareTicks, swe - sws);
                            System.Threading.Interlocked.Increment(ref ssimCompareCount);
                            ssim = Math.Clamp(1.0 - distortion, 0.0, 1.0);
                        }
                    }
                    catch { }

                    allScoresBag.Add((ssim, i, j));
                    var current = System.Threading.Interlocked.Increment(ref verifiedCount);
                    if (current % 50 == 0)
                    {
                        RaiseStatus($"SSIM computed {current}/{sortedCandidates.Count}...");
                        int overallPercent = (int)Math.Round((imageFiles.Count + current) * 100.0 / Math.Max(1, totalOps));
                        RaiseProgress(overallPercent);
                    }

                    // Stream groups: compute composite score and accept if above threshold
                    int pairHashDist = int.MaxValue;
                    if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                        pairHashDist = HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash);
                    double hashSim = 0.5;
                    if (sortedEntries[i].hash != null && sortedEntries[j].hash != null)
                        hashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[i].packedHash, sortedEntries[j].packedHash) / 64.0;

                    double pairHistDist = double.MaxValue; double histSim = 0.5;
                    try
                    {
                        var histAi = histCache.GetOrAdd(i, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                        var histBj = histCache.GetOrAdd(j, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
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
                        var compAi = spatialCache.GetOrAdd(i, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
                        var compBj = spatialCache.GetOrAdd(j, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
                        if (compAi != null && compBj != null)
                        {
                            pairCompDist = SpatialHistogramDistance(compAi, compBj);
                            compSim = 1.0 - pairCompDist;
                        }
                    }
                    catch { }

                    double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);

                    // Composite scoring is evaluated post-SSIM pass to build a full edge graph
                }
                catch { }
            });

            // Cleanup thumbnails
            foreach (var thumb in thumbnailCache.Values)
            {
                try { thumb?.Dispose(); } catch { }
            }

            // Materialize scores list for later use (debugging / closest pairs)
            var allScores = allScoresBag.ToList();
            allScores = [.. allScores.OrderByDescending(s => s.ssim)];

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

                        double hashSim = 0.5;
                        if (sortedEntries[a].hash != null && sortedEntries[b].hash != null)
                            hashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[a].packedHash, sortedEntries[b].packedHash) / 64.0;

                        double histSim = 0.5;
                        try
                        {
                            var histAi = histCache.GetOrAdd(a, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                            var histBj = histCache.GetOrAdd(b, idx => { try { return ComputeColorHistogram(sortedEntries[idx].path); } catch { return null; } });
                            if (histAi != null && histBj != null)
                            {
                                var pairHistDist = HistogramDistance(histAi, histBj);
                                histSim = 1.0 - pairHistDist;
                            }
                        }
                        catch { }

                        double compSim = 0.5;
                        try
                        {
                            var compAi = spatialCache.GetOrAdd(a, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
                            var compBj = spatialCache.GetOrAdd(b, idx => { try { return ComputeSpatialHistogram(sortedEntries[idx].path); } catch { return null; } });
                            if (compAi != null && compBj != null)
                            {
                                var pairCompDist = SpatialHistogramDistance(compAi, compBj);
                                compSim = 1.0 - pairCompDist;
                            }
                        }
                        catch { }

                        double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);
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

            // Stream groups as edges are accepted: union-find with live group tracking
            var liveGroupByRoot = new Dictionary<int, SimilarImageGroup>();
            var reportedRoot = new HashSet<int>();

            foreach (var s in allScores)
            {
                int a = s.a, b = s.b;
                double ssim = s.ssim;

                double hashSim = 0.5;
                if (sortedEntries[a].hash != null && sortedEntries[b].hash != null)
                    hashSim = 1.0 - (double)HammingDistancePacked(sortedEntries[a].packedHash, sortedEntries[b].packedHash) / 64.0;

                double histSim = 0.5;
                if (sortedEntries[a].hist != null && sortedEntries[b].hist != null)
                    histSim = 1.0 - HistogramDistance(sortedEntries[a].hist, sortedEntries[b].hist);

                double compSim = 0.5;
                if (sortedEntries[a].spatial != null && sortedEntries[b].spatial != null)
                    compSim = 1.0 - SpatialHistogramDistance(sortedEntries[a].spatial, sortedEntries[b].spatial);

                double composite = (CompositeWeightSsim * ssim) + (CompositeWeightHash * hashSim) + (CompositeWeightHist * histSim) + (CompositeWeightComp * compSim);
                if (composite < compositeThreshold) continue;

                int ra = find(a), rb = find(b);
                if (ra == rb) continue;

                // Before union, capture members for reporting
                var membersA = new List<int>();
                var membersB = new List<int>();
                for (int i = 0; i < n; i++) if (find(i) == ra) membersA.Add(i);
                for (int i = 0; i < n; i++) if (find(i) == rb) membersB.Add(i);

                unite(a, b);
                int rnew = find(a);

                // Merge live groups if present
                liveGroupByRoot.TryGetValue(ra, out var gA);
                liveGroupByRoot.TryGetValue(rb, out var gB);

                if (gA == null && gB == null)
                {
                    // create new group if resulting component has >=2
                    var newMembers = new List<int>();
                    newMembers.AddRange(membersA);
                    newMembers.AddRange(membersB);
                    if (newMembers.Count >= 2)
                    {
                        var bestSsim = ssim; // use this edge's ssim as representative score
                        var group = new SimilarImageGroup
                        {
                            GroupId = $"group_{results.Count}",
                            Images = [.. newMembers.Select(mi => CreateFileItem(sortedEntries[mi].path))],
                            SimilarityScore = bestSsim
                        };
                        liveGroupByRoot[rnew] = group;
                        results.Add(group);
                        RaiseGroupFound(group);
                    }
                }
                else if (gA != null && gB == null)
                {
                    // add B members to A's group
                    var gA_nonnull = gA;
                    var added = membersB.Where(x => !gA_nonnull.Images.Any(img => img.FilePath == sortedEntries[x].path)).ToList();
                    foreach (var idx in added)
                    {
                        var item = CreateFileItem(sortedEntries[idx].path);
                        gA_nonnull.Images.Add(item);
                        RaiseImageAddedToGroup(gA_nonnull.GroupId, item);
                    }
                    liveGroupByRoot.Remove(ra);
                    liveGroupByRoot[rnew] = gA_nonnull;
                }
                else if (gA == null && gB != null)
                {
                    var gB_nonnull = gB;
                    var added = membersA.Where(x => !gB_nonnull.Images.Any(img => img.FilePath == sortedEntries[x].path)).ToList();
                    foreach (var idx in added)
                    {
                        var item = CreateFileItem(sortedEntries[idx].path);
                        gB_nonnull.Images.Add(item);
                        RaiseImageAddedToGroup(gB_nonnull.GroupId, item);
                    }
                    liveGroupByRoot.Remove(rb);
                    liveGroupByRoot[rnew] = gB_nonnull;
                }
                else
                {
                    // both groups exist: merge gB into gA
                    var gA_nonnull = gA!;
                    var gB_nonnull = gB!;
                    foreach (var img in gB_nonnull.Images)
                    {
                        if (!gA_nonnull.Images.Any(x => x.FilePath == img.FilePath))
                        {
                            gA_nonnull.Images.Add(img);
                            RaiseImageAddedToGroup(gA_nonnull.GroupId, img);
                        }
                    }
                    liveGroupByRoot.Remove(rb);
                    liveGroupByRoot.Remove(ra);
                    liveGroupByRoot[rnew] = gA_nonnull;
                }
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
                foreach (var (ssim, a, b) in closest)
                {
                    results.Add(new SimilarImageGroup
                    {
                        GroupId = $"pair_{gi++}",
                        Images =
                        [
                            CreateFileItem(sortedEntries[a].path),
                            CreateFileItem(sortedEntries[b].path)
                        ],
                        SimilarityScore = ssim
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

        private float[]? ComputeColorHistogram(string filePath)
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

        private float[]? ComputeSpatialHistogram(string filePath)
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
                using var img = Image.FromFile(path);
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Black);
                    // preserve aspect ratio
                    double srcW = img.Width;
                    double srcH = img.Height;
                    double scale = Math.Min((double)size / srcW, (double)size / srcH);
                    int tw = Math.Max(1, (int)Math.Round(srcW * scale));
                    int th = Math.Max(1, (int)Math.Round(srcH * scale));
                    int x = (size - tw) / 2;
                    int y = (size - th) / 2;
                    g.DrawImage(img, new Rectangle(x, y, tw, th), new Rectangle(0, 0, (int)srcW, (int)srcH), GraphicsUnit.Pixel);
                }

                var lockBits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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
                    bmp.Dispose();
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
                image = new MagickImage(path);
                return true;
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
