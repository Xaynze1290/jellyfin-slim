using System.Collections.Generic;

namespace Jellyfin.Abstractions.Services
{
    public class CoreSessionInfo // Simplified/abstracted version if needed
    {
        public string Id { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public Dictionary<string, string> AdditionalProperties { get; set; } = new();
    }

    public class CreateCoreSessionRequest
    {
        public string UserId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
    }
}
