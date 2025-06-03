using System;
using System.Collections.Generic;

namespace Jellyfin.Abstractions.Services
{
    public class TranscodingTaskOptions // Basic options for starting a task
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string OutputFormat { get; set; } = string.Empty;
        public Dictionary<string, string> TranscodingParameters { get; set; } = new();
        public Guid UserId {get; set;}
        public string MediaSourceId {get; set;} = string.Empty;
        public string DeviceId {get; set;} = string.Empty;
        public string PlaySessionId {get; set;} = string.Empty;
        public string LiveStreamId {get; set;} = string.Empty;
    }
}
