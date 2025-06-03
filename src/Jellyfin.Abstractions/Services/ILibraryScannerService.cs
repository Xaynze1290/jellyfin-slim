using System;
using System.Threading.Tasks;

namespace Jellyfin.Abstractions.Services
{
    public interface ILibraryScannerService
    {
        // Example methods, adapt from ILibraryManager
        Task ScanLibraryAsync(LibraryScanRequest request);
        Task<ScanProgressReport> GetScanProgressAsync(string scanId); // Assuming a scan can be identified
        // event EventHandler<ScanProgressReport> ScanProgressChanged; // Events are harder to abstract simply for now
    }
}
