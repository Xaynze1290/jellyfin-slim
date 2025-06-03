using System;
using System.Threading.Tasks;
// Note: DTOs are now in separate files in the same namespace.

namespace Jellyfin.Abstractions.Services
{
    public interface ITranscoderService
    {
        Task<TranscodingJobInfo> GetTranscodingJobInfoAsync(TranscodingJobInfoArgs args);
        Task KillTranscodingJobsAsync(string deviceId, string playSessionId);
        Task<TranscodingJobInfo> StartTranscodingAsync(TranscodingTaskOptions options);
        Task ReportTranscodingProgressAsync(ReportTranscodingProgressArgs progressArgs);
        Task PingTranscodingJobAsync(string playSessionId, bool? isUserPaused);
        // Add other necessary methods, potentially with simplified signatures for now
    }
}
