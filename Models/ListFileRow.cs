using System;

namespace DupFree.Models
{
    public class ListFileRow
    {
        public int DupCount { get; set; }
        public string DupSpace { get; set; }
        public string FileName { get; set; }
        public string FilePath { get; set; }
        public FileItemViewModel Source { get; set; }
    }
}
