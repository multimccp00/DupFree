using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using DupFree.Services;

class Program
{
        static async Task<int> Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        string csvPath = null;
        int hashOverride = -1;
        int ssimSize = 128;
        bool visipicsMode = false;
        bool forceBrute = false;
        bool safeOpt = false;
        bool simdSsim = false;
        bool gpuSsim = false;
        // simple args: <dir> [--csv <path>] [--hash <n>] [--ssim <size>]
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--csv" || args[i] == "-c")
            {
                if (i + 1 < args.Length) { csvPath = args[i + 1]; i++; }
                else csvPath = "dupfree_edges.csv";
            }
            else if (args[i] == "--hash")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var h)) { hashOverride = h; i++; }
            }
            else if (args[i] == "--ssim")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var s)) { ssimSize = s; i++; }
            }
            else if (args[i] == "--visipics")
            {
                visipicsMode = true;
            }
            else if (args[i] == "--force-brute")
            {
                forceBrute = true;
            }
            else if (args[i] == "--safe-opt")
            {
                safeOpt = true;
            }
            else if (args[i] == "--simd-ssim")
            {
                simdSsim = true;
            }
            else if (args[i] == "--gpu-ssim")
            {
                gpuSsim = true;
            }
        }
        if (!System.IO.Directory.Exists(dir))
        {
            Console.WriteLine($"Directory not found: {dir}");
            return 2;
        }

        var service = new SimilarImageService();
        service.OnStatusChanged += s => Console.WriteLine($"[status] {s}");

        Console.WriteLine($"Benchmark: scanning '{dir}'");
        var sw = Stopwatch.StartNew();
            try
            {
                var result = await service.FindSimilarImagesAsync(new List<string> { dir }, maxDistance: 92.0, showClosestPairsOnly: false, closestPairCount: 20, progress: null, cancellationToken: CancellationToken.None, exportEdgeCsv: csvPath, hashThresholdOverride: hashOverride, ssimThumbnailSize: ssimSize, visipicsMode: visipicsMode, forceBruteForce: forceBrute, useSimdSsim: simdSsim, useGpuSsim: gpuSsim);
            sw.Stop();
            Console.WriteLine($"Elapsed: {sw.Elapsed}");
            Console.WriteLine($"Groups found: {result.Count}");
            int imgs = 0;
            foreach (var g in result) imgs += g.Images?.Count ?? 0;
            Console.WriteLine($"Total images contained in groups: {imgs}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"Benchmark failed after {sw.Elapsed}: {ex}");
            return 1;
        }

        return 0;
    }
}
