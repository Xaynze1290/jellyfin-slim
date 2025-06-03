using System;
using System.Collections.Generic;

namespace Jellyfin.Abstractions.Services
{
    public class ReportTranscodingProgressArgs
    {
        public string JobId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string OutputContainer {get; set;} = string.Empty;
        public string OutputVideoCodec {get; set;} = string.Empty;
        public string OutputAudioCodec {get; set;} = string.Empty;
        public int? OutputWidth {get; set;}
        public int? OutputHeight {get; set;}
        public int? OutputAudioChannels {get; set;}
        public int TotalOutputBitrate {get; set;}
        public List<string> TranscodeReasons {get; set;} = new();
        public TimeSpan? TranscodingPosition { get; set; }
        public float? Framerate { get; set; }
        public double? PercentComplete { get; set; }
        public long? BytesTranscoded { get; set; }
        public int? BitRate { get; set; }
    }
}
