using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

class Program
{
    static int Main(string[] args)
    {
        string groupsCsv = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "..\\..\\..\\..\\bench_groups_0_80.csv");
        string outDir = args.Length > 1 ? args[1] : Path.Combine(Directory.GetCurrentDirectory(), "..\\..\\..\\..\\group_previews");
        int thumbW = 256, thumbH = 256;
        int maxCols = 5;

        groupsCsv = Path.GetFullPath(groupsCsv);
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        if (!File.Exists(groupsCsv))
        {
            Console.WriteLine($"Groups CSV not found: {groupsCsv}");
            return 2;
        }

        Console.WriteLine($"Reading groups from: {groupsCsv}");
        var lines = File.ReadAllLines(groupsCsv).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        int count = 0;
        foreach (var line in lines)
        {
            // format: groupId,memberCount,"path1|path2|..."
            var parts = SplitCsvLine(line);
            if (parts.Count < 3) continue;
            string gid = parts[0];
            string membersField = parts[2].Trim();
            if (membersField.StartsWith("\"") && membersField.EndsWith("\""))
                membersField = membersField.Substring(1, membersField.Length - 2);
            var members = membersField.Split('|').Select(p => p.Replace("\"\"","\"")).ToList();
            if (members.Count == 0) continue;

            var thumbs = new List<Image>();
            foreach (var p in members)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    using (var img = Image.FromFile(p))
                    {
                        var tb = new Bitmap(thumbW, thumbH);
                        using (var g = Graphics.FromImage(tb))
                        {
                            g.Clear(Color.Black);
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            var srcRect = GetFitRect(img.Width, img.Height, thumbW, thumbH);
                            g.DrawImage(img, srcRect.Destination, srcRect.Source, GraphicsUnit.Pixel);
                        }
                        thumbs.Add(tb);
                    }
                }
                catch { }
            }

            if (thumbs.Count == 0) continue;

            int cols = Math.Min(maxCols, thumbs.Count);
            int rows = (int)Math.Ceiling(thumbs.Count / (double)cols);
            int canvasW = cols * thumbW;
            int canvasH = rows * thumbH;

            using (var canvas = new Bitmap(canvasW, canvasH))
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.DimGray);
                for (int i = 0; i < thumbs.Count; i++)
                {
                    int r = i / cols;
                    int c = i % cols;
                    int x = c * thumbW;
                    int y = r * thumbH;
                    g.DrawImage(thumbs[i], x, y, thumbW, thumbH);
                }

                string outPath = Path.Combine(outDir, gid + ".jpg");
                try { canvas.Save(outPath, ImageFormat.Jpeg); Console.WriteLine($"Wrote {outPath}"); }
                catch (Exception ex) { Console.WriteLine($"Failed to save {outPath}: {ex.Message}"); }
            }

            // dispose thumbs
            foreach (var t in thumbs) t.Dispose();
            count++;
        }

        Console.WriteLine($"Generated {count} group previews in {outDir}");
        return 0;
    }

    struct FitRect { public Rectangle Source; public Rectangle Destination; }
    static FitRect GetFitRect(int srcW, int srcH, int dstW, int dstH)
    {
        double rw = (double)dstW / srcW;
        double rh = (double)dstH / srcH;
        double r = Math.Min(rw, rh);
        int w = (int)(srcW * r);
        int h = (int)(srcH * r);
        int x = (dstW - w) / 2;
        int y = (dstH - h) / 2;
        return new FitRect { Source = new Rectangle(0, 0, srcW, srcH), Destination = new Rectangle(x, y, w, h) };
    }

    static List<string> SplitCsvLine(string line)
    {
        var res = new List<string>();
        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();
        for (int i=0;i<line.Length;i++)
        {
            char c = line[i];
            if (c=='"') { cur.Append(c); inQuotes = !inQuotes; continue; }
            if (c==',' && !inQuotes) { res.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }
}