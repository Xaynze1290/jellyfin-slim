namespace Jellyfin.Abstractions.Services
{
    public class TranscodingJobInfoArgs
    {
        public string PlaySessionId { get; set; } = string.Empty;
        public string TranscodingPath { get; set; } = string.Empty;
        public string TranscodingJobType { get; set; } = string.Empty;
    }
}
