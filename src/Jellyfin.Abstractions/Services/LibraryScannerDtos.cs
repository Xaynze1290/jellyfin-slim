using System.Collections.Generic;

namespace Jellyfin.Abstractions.Services
{
    public class LibraryScanRequest
    {
        public string Path { get; set; } = string.Empty;
        public Dictionary<string, string> Options { get; set; } = new(); // e.g., "fullScan": "true"
    }

    public class ScanProgressReport
    {
        public double Progress { get; set; }
        public string CurrentItem { get; set; } = string.Empty;
    }
}
