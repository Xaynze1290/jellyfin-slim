using System.Collections.Generic;

namespace Jellyfin.Abstractions.Services
{
    public class CoreAuthRequest
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty; // Or other credential types
        public Dictionary<string, string> Properties { get; set; } = new();
    }

    public class CoreAuthResult
    {
        public bool IsSuccess { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public Dictionary<string, string> UserProperties { get; set; } = new();
    }
}
