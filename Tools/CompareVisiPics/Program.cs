using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DupFree.Services;

class Program
{
    static int Main(string[] args)
    {
        string csv = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\..\\bench_edges.csv");
        string outGroups = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "..\\..\\..\\..\\bench_groups.csv");
        double threshold = args.Length > 2 && double.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0.92;

        csv = Path.GetFullPath(csv);
        outGroups = Path.GetFullPath(outGroups);

        if (!File.Exists(csv))
        {
            Log.Error($"Input CSV not found: {csv}");
            return 2;
        }

        Log.Info($"Parsing edges from: {csv}");
        Log.Info($"Composite threshold: {threshold:F4}");

        var pathToIdx = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        var parent = new List<int>();

        int find(int x) => parent[x] == x ? x : (parent[x] = find(parent[x]));
        Action<int,int> unite = (a,b) => {
            a = find(a); b = find(b);
            if (a==b) return;
            parent[b] = a;
        };

        int idxCounter = 0;
        int keptPairs = 0;

        using (var sr = new StreamReader(csv))
        {
            var header = sr.ReadLine();
            while (!sr.EndOfStream)
            {
                var line = sr.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                // naive CSV split: we expect last two fields are quoted paths
                // split by comma, but handle quoted path fields
                var parts = SplitCsvLine(line);
                if (parts.Count < 7) continue;
                if (!double.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var composite)) continue;
                if (composite < threshold) continue;
                string a = parts[5].Trim('"');
                string b = parts[6].Trim('"');

                if (!pathToIdx.TryGetValue(a, out var ia)) { ia = idxCounter++; pathToIdx[a] = ia; parent.Add(ia); }
                if (!pathToIdx.TryGetValue(b, out var ib)) { ib = idxCounter++; pathToIdx[b] = ib; parent.Add(ib); }
                unite(ia, ib);
                keptPairs++;
            }
        }

        // build groups
        var groups = new Dictionary<int, List<string>>();
        foreach (var kv in pathToIdx)
        {
            int i = kv.Value;
            int r = find(i);
            if (!groups.TryGetValue(r, out var list)) { list = new List<string>(); groups[r] = list; }
            list.Add(kv.Key);
        }

        var filtered = groups.Values.Where(g => g.Count >= 2).ToList();

        Log.Info($"Kept pairs: {keptPairs}");
        Log.Info($"Found groups (size>=2): {filtered.Count}");
        int totalImgs = filtered.Sum(g => g.Count);
        Log.Info($"Total images in groups: {totalImgs}");

        // write groups CSV
        using (var sw = new StreamWriter(outGroups))
        {
            sw.WriteLine("groupId,memberCount,members");
            int gi = 0;
            foreach (var g in filtered.OrderByDescending(g => g.Count))
            {
                var membersEsc = string.Join("|", g.Select(p => p.Replace("\"","\"\"")));
                sw.WriteLine($"group_{gi},{g.Count},\"{membersEsc}\"");
                gi++;
            }
        }

        Log.Info($"Wrote groups CSV: {outGroups}");
        return 0;
    }

    static List<string> SplitCsvLine(string line)
    {
        var res = new List<string>();
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();
        for (int i=0;i<line.Length;i++)
        {
            char c = line[i];
            if (c=='"') { inQuotes = !inQuotes; cur.Append(c); continue; }
            if (c==',' && !inQuotes) { res.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }
}
