using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace DupFree.Services
{
    internal class PhashEntry
    {
        public string Path { get; set; }
        public ulong PackedHash { get; set; }
        public long Length { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public List<ulong> TileHashes { get; set; } = new();
    }

    internal class BKTree
    {
        private class Node
        {
            public ulong Hash;
            public List<int> Indices = new();
            public Dictionary<int, Node> Children = new();
            public Node(ulong h, int idx) { Hash = h; Indices.Add(idx); }
        }

        private Node _root;

        public void Add(ulong hash, int idx)
        {
            if (_root == null) { _root = new Node(hash, idx); return; }
            var cur = _root;
            while (true)
            {
                int d = BitOperations.PopCount(cur.Hash ^ hash);
                if (d == 0) { cur.Indices.Add(idx); return; }
                if (!cur.Children.TryGetValue(d, out var child))
                {
                    cur.Children[d] = new Node(hash, idx);
                    return;
                }
                cur = child;
            }
        }

        public List<int> QueryRadius(ulong hash, int radius)
        {
            var res = new List<int>();
            if (_root == null) return res;
            var stack = new Stack<Node>();
            stack.Push(_root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                int d = BitOperations.PopCount(node.Hash ^ hash);
                if (d <= radius) res.AddRange(node.Indices);
                // children distances that could be within radius are in [d-radius, d+radius]
                int lo = Math.Max(0, d - radius);
                int hi = d + radius;
                foreach (var kv in node.Children)
                {
                    if (kv.Key >= lo && kv.Key <= hi) stack.Push(kv.Value);
                }
            }
            return res;
        }
    }

    internal static class PhashIndex
    {
        private const string IndexFileName = "phash_index.json";

        public static (List<PhashEntry> entries, BKTree tree, Dictionary<ulong, List<int>> tileIndex) LoadOrBuild(
            string cacheDir,
            List<string> paths,
            Func<string, (byte[] hash, ulong packed)> computeHash,
            Func<string, IEnumerable<ulong>> computeTileHashes = null)
        {
            var idxPath = Path.Combine(cacheDir, IndexFileName);
            var existing = new Dictionary<string, PhashEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(idxPath))
                {
                    var txt = File.ReadAllText(idxPath);
                    var arr = JsonSerializer.Deserialize<List<PhashEntry>>(txt);
                    if (arr != null)
                    {
                        foreach (var e in arr) existing[e.Path] = e;
                    }
                }
            }
            catch { existing.Clear(); }

            var entries = new List<PhashEntry>();
            bool changed = false;
            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i];
                try
                {
                    var fi = new FileInfo(p);
                    long len = fi.Length;
                    long ticks = fi.LastWriteTimeUtc.Ticks;
                    if (existing.TryGetValue(p, out var e) && e.Length == len && e.LastWriteUtcTicks == ticks)
                    {
                        entries.Add(e);
                        continue;
                    }
                    // compute
                    var (h, packed) = computeHash(p);
                    if (h == null) continue;
                    var ne = new PhashEntry { Path = p, PackedHash = packed, Length = len, LastWriteUtcTicks = ticks };
                    if (computeTileHashes != null)
                    {
                        try
                        {
                            var tiles = computeTileHashes(p)?.ToList();
                            if (tiles != null && tiles.Count > 0) ne.TileHashes = tiles;
                        }
                        catch { }
                    }
                    entries.Add(ne);
                    existing[p] = ne;
                    changed = true;
                }
                catch { }
            }

            if (changed)
            {
                try
                {
                    var list = existing.Values.ToList();
                    var txt = JsonSerializer.Serialize(list);
                    File.WriteAllText(idxPath, txt);
                }
                catch { }
            }

            // build BK-tree
            var tree = new BKTree();
            for (int i = 0; i < entries.Count; i++) tree.Add(entries[i].PackedHash, i);

            // build tile inverted index
            var tileIndex = new Dictionary<ulong, List<int>>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.TileHashes == null) continue;
                foreach (var th in e.TileHashes)
                {
                    if (!tileIndex.TryGetValue(th, out var list)) { list = new List<int>(); tileIndex[th] = list; }
                    list.Add(i);
                }
            }

            return (entries, tree, tileIndex);
        }
    }
}
