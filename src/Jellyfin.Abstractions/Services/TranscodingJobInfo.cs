namespace Jellyfin.Abstractions.Services
{
    public class TranscodingJobInfo // Basic info about a job
    {
        public string Id { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty; // e.g. "Running", "Paused", "Finished"
        public double Progress { get; set; }
    }
}
