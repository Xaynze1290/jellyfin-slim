using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Jellyfin.Abstractions.Services; // Correct using for the new interface

namespace Jellyfin.CoreDefaults.Transcoding; // Correct namespace

/// <inheritdoc cref="ITranscodeManager"/>
// ITranscoderService is now the one from Jellyfin.Abstractions.Services
public sealed class TranscodeManager : ITranscodeManager, ITranscoderService, IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TranscodeManager> _logger;
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IAttachmentExtractor _attachmentExtractor;

    private readonly List<TranscodingJob> _activeTranscodingJobs = new();
    private readonly AsyncKeyedLocker<string> _transcodingLocks = new(o =>
    {
        o.PoolSize = 20;
        o.PoolInitialFill = 1;
    });

    private readonly Version _maxFFmpegCkeyPauseSupported = new Version(6, 1);

    public TranscodeManager(
        ILoggerFactory loggerFactory,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IServerConfigurationManager serverConfigurationManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        EncodingHelper encodingHelper,
        IMediaEncoder mediaEncoder,
        IMediaSourceManager mediaSourceManager,
        IAttachmentExtractor attachmentExtractor)
    {
        _loggerFactory = loggerFactory;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        _serverConfigurationManager = serverConfigurationManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _encodingHelper = encodingHelper;
        _mediaEncoder = mediaEncoder;
        _mediaSourceManager = mediaSourceManager;
        _attachmentExtractor = attachmentExtractor;

        _logger = loggerFactory.CreateLogger<TranscodeManager>();
        DeleteEncodedMediaCache();
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStart += OnPlaybackProgress;
    }

    public TranscodingJob? GetTranscodingJob(string playSessionId)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => string.Equals(j.PlaySessionId, playSessionId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            return _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused)
    {
        ArgumentException.ThrowIfNullOrEmpty(playSessionId);
        _logger.LogDebug("PingTranscodingJob PlaySessionId={0} isUsedPaused: {1}", playSessionId, isUserPaused);
        List<TranscodingJob> jobs;
        lock (_activeTranscodingJobs)
        {
            jobs = _activeTranscodingJobs.Where(j => string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        foreach (var job in jobs)
        {
            if (isUserPaused.HasValue)
            {
                _logger.LogDebug("Setting job.IsUserPaused to {0}. jobId: {1}", isUserPaused, job.Id);
                job.IsUserPaused = isUserPaused.Value;
            }
            PingTimer(job, true);
        }
    }

    private void PingTimer(TranscodingJob job, bool isProgressCheckIn)
    {
        if (job.HasExited) { job.StopKillTimer(); return; }
        var timerDuration = 10000;
        if (job.Type != TranscodingJobType.Progressive) { timerDuration = 60000; }
        job.PingTimeout = timerDuration;
        job.LastPingDate = DateTime.UtcNow;
        if (job.Type != TranscodingJobType.Progressive || !isProgressCheckIn) { job.StartKillTimer(OnTranscodeKillTimerStopped); }
        else { job.ChangeKillTimerIfStarted(); }
    }

    private async void OnTranscodeKillTimerStopped(object? state)
    {
        var job = state as TranscodingJob ?? throw new ArgumentException($"{nameof(state)} is not of type {nameof(TranscodingJob)}", nameof(state));
        if (!job.HasExited && job.Type != TranscodingJobType.Progressive)
        {
            var timeSinceLastPing = (DateTime.UtcNow - job.LastPingDate).TotalMilliseconds;
            if (timeSinceLastPing < job.PingTimeout) { job.StartKillTimer(OnTranscodeKillTimerStopped, job.PingTimeout); return; }
        }
        _logger.LogInformation("Transcoding kill timer stopped for JobId {0} PlaySessionId {1}. Killing transcoding", job.Id, job.PlaySessionId);
        await KillTranscodingJob(job, true, path => true).ConfigureAwait(false);
    }

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles)
    {
        var jobs = new List<TranscodingJob>();
        lock (_activeTranscodingJobs)
        {
            jobs.AddRange(_activeTranscodingJobs.Where(j => string.IsNullOrWhiteSpace(playSessionId)
                ? string.Equals(deviceId, j.DeviceId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(playSessionId, j.PlaySessionId, StringComparison.OrdinalIgnoreCase)));
        }
        return Task.WhenAll(GetKillJobs());
        IEnumerable<Task> GetKillJobs() { foreach (var job in jobs) { yield return KillTranscodingJob(job, false, deleteFiles); } }
    }

    private async Task KillTranscodingJob(TranscodingJob job, bool closeLiveStream, Func<string, bool> delete)
    {
        job.DisposeKillTimer();
        _logger.LogDebug("KillTranscodingJob - JobId {0} PlaySessionId {1}. Killing transcoding", job.Id, job.PlaySessionId);
        lock (_activeTranscodingJobs)
        {
            _activeTranscodingJobs.Remove(job);
            if (job.CancellationTokenSource?.IsCancellationRequested == false)
            {
                job.CancellationTokenSource.Cancel();
            }
        }
        job.Stop();
        if (delete(job.Path!))
        {
            await DeletePartialStreamFiles(job.Path!, job.Type, 0, 1500).ConfigureAwait(false);
        }
        if (closeLiveStream && !string.IsNullOrWhiteSpace(job.LiveStreamId))
        {
            await _sessionManager.CloseLiveStreamIfNeededAsync(job.LiveStreamId, job.PlaySessionId).ConfigureAwait(false);
        }
    }

    private async Task DeletePartialStreamFiles(string path, TranscodingJobType jobType, int retryCount, int delayMs)
    {
        if (retryCount >= 10) { return; }
        _logger.LogInformation("Deleting partial stream file(s) {Path}", path);
        await Task.Delay(delayMs).ConfigureAwait(false);
        try
        {
            if (jobType == TranscodingJobType.Progressive) { DeleteProgressivePartialStreamFiles(path); }
            else { DeleteHlsPartialStreamFiles(path); }
        }
        catch (IOException ex) { _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path); await DeletePartialStreamFiles(path, jobType, retryCount + 1, 500).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogError(ex, "Error deleting partial stream file(s) {Path}", path); }
    }

    private void DeleteProgressivePartialStreamFiles(string outputFilePath)
    {
        if (File.Exists(outputFilePath)) { _fileSystem.DeleteFile(outputFilePath); }
    }

    private void DeleteHlsPartialStreamFiles(string outputFilePath)
    {
        var directory = Path.GetDirectoryName(outputFilePath) ?? throw new ArgumentException("Path can't be a root directory.", nameof(outputFilePath));
        var name = Path.GetFileNameWithoutExtension(outputFilePath);
        var filesToDelete = _fileSystem.GetFilePaths(directory).Where(f => f.Contains(name, StringComparison.OrdinalIgnoreCase));
        List<Exception>? exs = null;
        foreach (var file in filesToDelete)
        {
            try { _logger.LogDebug("Deleting HLS file {0}", file); _fileSystem.DeleteFile(file); }
            catch (IOException ex) { (exs ??= new List<Exception>()).Add(ex); _logger.LogError(ex, "Error deleting HLS file {Path}", file); }
        }
        if (exs is not null) { throw new AggregateException("Error deleting HLS files", exs); }
    }

    public void ReportTranscodingProgress( TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
    {
        var ticks = transcodingPosition?.Ticks;
        if (job is not null) { job.Framerate = framerate; job.CompletionPercentage = percentComplete; job.TranscodingPositionTicks = ticks; job.BytesTranscoded = bytesTranscoded; job.BitRate = bitRate; }
        var deviceId = state.Request.DeviceId;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var audioCodec = state.ActualOutputAudioCodec;
            var videoCodec = state.ActualOutputVideoCodec;
            var hardwareAccelerationType = _serverConfigurationManager.GetEncodingOptions().HardwareAccelerationType;
            _sessionManager.ReportTranscodingInfo(deviceId, new TranscodingInfo { Bitrate = bitRate ?? state.TotalOutputBitrate, AudioCodec = audioCodec, VideoCodec = videoCodec, Container = state.OutputContainer, Framerate = framerate, CompletionPercentage = percentComplete, Width = state.OutputWidth, Height = state.OutputHeight, AudioChannels = state.OutputAudioChannels, IsAudioDirect = EncodingHelper.IsCopyCodec(state.OutputAudioCodec), IsVideoDirect = EncodingHelper.IsCopyCodec(state.OutputVideoCodec), HardwareAccelerationType = hardwareAccelerationType, TranscodeReasons = state.TranscodeReasons });
        }
    }

    public async Task<TranscodingJob> StartFfMpeg( StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException($"Provided path ({outputPath}) is not valid.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        await AcquireResources(state, cancellationTokenSource).ConfigureAwait(false);
        if (state.VideoRequest is not null && !EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
        {
            var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
            if (user is not null && !user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding)) { OnTranscodeFailedToStart(outputPath, transcodingJobType, state); throw new ArgumentException("User does not have access to video transcoding."); }
        }
        ArgumentException.ThrowIfNullOrEmpty(_mediaEncoder.EncoderPath);
        if (state.SubtitleStream is not null && state.SubtitleDeliveryMethod == SubtitleDeliveryMethod.Encode)
        {
            if (state.MediaSource.VideoType == VideoType.Dvd || state.MediaSource.VideoType == VideoType.BluRay)
            {
                var concatPath = Path.Join(_appPaths.CachePath, "concat", state.MediaSource.Id + ".concat");
                await _attachmentExtractor.ExtractAllAttachments(concatPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
            else { await _attachmentExtractor.ExtractAllAttachments(state.MediaPath, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false); }
            if (state.SubtitleStream.IsExternal && Path.GetExtension(state.SubtitleStream.Path.AsSpan()).Equals(".mks", StringComparison.OrdinalIgnoreCase))
            {
                await _attachmentExtractor.ExtractAllAttachments(state.SubtitleStream.Path, state.MediaSource, cancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        var process = new Process { StartInfo = new ProcessStartInfo { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = true, FileName = _mediaEncoder.EncoderPath, Arguments = commandLineArguments, WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : workingDirectory, ErrorDialog = false }, EnableRaisingEvents = true };
        var transcodingJob = OnTranscodeBeginning( outputPath, state.Request.PlaySessionId, state.MediaSource.LiveStreamId, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), transcodingJobType, process, state.Request.DeviceId, state, cancellationTokenSource);
        _logger.LogInformation("{Filename} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);
        var logFilePrefix = "FFmpeg.Transcode-";
        if (state.VideoRequest is not null && EncodingHelper.IsCopyCodec(state.OutputVideoCodec)) { logFilePrefix = EncodingHelper.IsCopyCodec(state.OutputAudioCodec) ? "FFmpeg.Remux-" : "FFmpeg.DirectStream-"; }
        if (state.VideoRequest is null && EncodingHelper.IsCopyCodec(state.OutputAudioCodec)) { logFilePrefix = "FFmpeg.Remux-"; }
        var logFilePath = Path.Combine( _serverConfigurationManager.ApplicationPaths.LogDirectoryPath, $"{logFilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{state.Request.MediaSourceId}_{Guid.NewGuid().ToString()[..8]}.log");
        Stream logStream = new FileStream( logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(logStream, state.MediaSource, cancellationToken: cancellationTokenSource.Token).ConfigureAwait(false);
        var commandLineLogMessageBytes = Encoding.UTF8.GetBytes( Environment.NewLine + Environment.NewLine + process.StartInfo.FileName + " " + process.StartInfo.Arguments + Environment.NewLine + Environment.NewLine);
        await logStream.WriteAsync(commandLineLogMessageBytes, cancellationTokenSource.Token).ConfigureAwait(false);
        process.Exited += (_, _) => OnFfMpegProcessExited(process, transcodingJob, state);
        try { process.Start(); }
        catch (Exception ex) { _logger.LogError(ex, "Error starting FFmpeg"); OnTranscodeFailedToStart(outputPath, transcodingJobType, state); throw; }
        _logger.LogDebug("Launched FFmpeg process");
        state.TranscodingJob = transcodingJob;
        _ = new JobLogger(_logger).StartStreamingLog(state, process.StandardError, logStream);
        var ffmpegTargetFile = state.WaitForPath ?? outputPath;
        _logger.LogDebug("Waiting for the creation of {0}", ffmpegTargetFile);
        while (!File.Exists(ffmpegTargetFile) && !transcodingJob.HasExited) { await Task.Delay(100, cancellationTokenSource.Token).ConfigureAwait(false); }
        _logger.LogDebug("File {0} created or transcoding has finished", ffmpegTargetFile);
        if (state.IsInputVideo && transcodingJob.Type == TranscodingJobType.Progressive && !transcodingJob.HasExited)
        {
            await Task.Delay(1000, cancellationTokenSource.Token).ConfigureAwait(false);
            if (state.ReadInputAtNativeFramerate && !transcodingJob.HasExited) { await Task.Delay(1500, cancellationTokenSource.Token).ConfigureAwait(false); }
        }
        if (!transcodingJob.HasExited) { StartThrottler(state, transcodingJob); StartSegmentCleaner(state, transcodingJob); }
        else if (transcodingJob.ExitCode != 0) { throw new FfmpegException(string.Format(CultureInfo.InvariantCulture, "FFmpeg exited with code {0}", transcodingJob.ExitCode)); }
        _logger.LogDebug("StartFfMpeg() finished successfully");
        return transcodingJob;
    }

    private void StartThrottler(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableThrottling(state) && (_mediaEncoder.IsPkeyPauseSupported || _mediaEncoder.EncoderVersion <= _maxFFmpegCkeyPauseSupported))
        {
            transcodingJob.TranscodingThrottler = new TranscodingThrottler(transcodingJob, _loggerFactory.CreateLogger<TranscodingThrottler>(), _serverConfigurationManager, _fileSystem, _mediaEncoder);
            transcodingJob.TranscodingThrottler.Start();
        }
    }

    private static bool EnableThrottling(StreamState state) => state.InputProtocol == MediaProtocol.File && state.RunTimeTicks.HasValue && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks && state.IsInputVideo && state.VideoType == VideoType.VideoFile;

    private void StartSegmentCleaner(StreamState state, TranscodingJob transcodingJob)
    {
        if (EnableSegmentCleaning(state))
        {
            transcodingJob.TranscodingSegmentCleaner = new TranscodingSegmentCleaner(transcodingJob, _loggerFactory.CreateLogger<TranscodingSegmentCleaner>(), _serverConfigurationManager, _fileSystem, _mediaEncoder, state.SegmentLength);
            transcodingJob.TranscodingSegmentCleaner.Start();
        }
    }

    private static bool EnableSegmentCleaning(StreamState state) => state.InputProtocol is MediaProtocol.File or MediaProtocol.Http && state.IsInputVideo && state.TranscodingType == TranscodingJobType.Hls && state.RunTimeTicks.HasValue && state.RunTimeTicks.Value >= TimeSpan.FromMinutes(5).Ticks;

    private TranscodingJob OnTranscodeBeginning( string path, string? playSessionId, string? liveStreamId, string transcodingJobId, TranscodingJobType type, Process process, string? deviceId, StreamState state, CancellationTokenSource cancellationTokenSource)
    {
        lock (_activeTranscodingJobs)
        {
            var job = new TranscodingJob(_loggerFactory.CreateLogger<TranscodingJob>()) { Type = type, Path = path, Process = process, ActiveRequestCount = 1, DeviceId = deviceId, CancellationTokenSource = cancellationTokenSource, Id = transcodingJobId, PlaySessionId = playSessionId, LiveStreamId = liveStreamId, MediaSource = state.MediaSource };
            _activeTranscodingJobs.Add(job);
            ReportTranscodingProgress(job, state, null, null, null, null, null);
            return job;
        }
    }

    public void OnTranscodeEndRequest(TranscodingJob job)
    {
        job.ActiveRequestCount--;
        _logger.LogDebug("OnTranscodeEndRequest job.ActiveRequestCount={ActiveRequestCount}", job.ActiveRequestCount);
        if (job.ActiveRequestCount <= 0) { PingTimer(job, false); }
    }

    private void OnTranscodeFailedToStart(string path, TranscodingJobType type, StreamState state)
    {
        lock (_activeTranscodingJobs) { var job = _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase)); if (job is not null) { _activeTranscodingJobs.Remove(job); } }
        if (!string.IsNullOrWhiteSpace(state.Request.DeviceId)) { _sessionManager.ClearTranscodingInfo(state.Request.DeviceId); }
    }

    private void OnFfMpegProcessExited(Process process, TranscodingJob job, StreamState state)
    {
        job.HasExited = true; job.ExitCode = process.ExitCode;
        ReportTranscodingProgress(job, state, null, null, null, null, null);
        _logger.LogDebug("Disposing stream resources"); state.Dispose();
        if (process.ExitCode == 0) { _logger.LogInformation("FFmpeg exited with code 0"); }
        else { _logger.LogError("FFmpeg exited with code {0}", process.ExitCode); }
        job.Dispose();
    }

    private async Task AcquireResources(StreamState state, CancellationTokenSource cancellationTokenSource)
    {
        if (state.MediaSource.RequiresOpening && string.IsNullOrWhiteSpace(state.Request.LiveStreamId))
        {
            var liveStreamResponse = await _mediaSourceManager.OpenLiveStream( new LiveStreamRequest { OpenToken = state.MediaSource.OpenToken }, cancellationTokenSource.Token).ConfigureAwait(false);
            var encodingOptions = _serverConfigurationManager.GetEncodingOptions();
            _encodingHelper.AttachMediaSourceInfo(state, encodingOptions, liveStreamResponse.MediaSource, state.RequestedUrl);
            if (state.VideoRequest is not null) { _encodingHelper.TryStreamCopy(state); }
        }
        if (state.MediaSource.BufferMs.HasValue) { await Task.Delay(state.MediaSource.BufferMs.Value, cancellationTokenSource.Token).ConfigureAwait(false); }
    }

    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
    {
        lock (_activeTranscodingJobs)
        {
            var job = _activeTranscodingJobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            if (job is null) { return null; }
            job.ActiveRequestCount++;
            if (string.IsNullOrWhiteSpace(job.PlaySessionId) || job.Type == TranscodingJobType.Progressive) { job.StopKillTimer(); }
            return job;
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PlaySessionId)) { PingTranscodingJob(e.PlaySessionId, e.IsPaused); }
    }

    private void DeleteEncodedMediaCache()
    {
        var path = _serverConfigurationManager.GetTranscodePath();
        if (!Directory.Exists(path)) { return; }
        foreach (var file in _fileSystem.GetFilePaths(path, true))
        {
            try { _fileSystem.DeleteFile(file); }
            catch (Exception ex) { _logger.LogError(ex, "Error deleting encoded media cache file {Path}", path); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken)
    {
        return _transcodingLocks.LockAsync(outputPath, cancellationToken);
    }

    public void Dispose()
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStart -= OnPlaybackProgress;
        _transcodingLocks.Dispose();
    }

    // The methods defined in MediaBrowser.Common.Abstractions.Services.ITranscoderService are:
    // - TranscodingJob? GetTranscodingJob(string playSessionId); -> Existing method
    // - Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles); -> Existing method
    // - Task<TranscodingJob> StartFfMpeg(...); -> Existing method
    // - void ReportTranscodingProgress(...); -> Existing method
    // - void PingTranscodingJob(string playSessionId, bool? isUserPaused); -> Existing method
    // These existing methods will need to be adapted or new ones created to match the DTO-based ITranscoderService signatures.

    #region ITranscoderService Implementation (DTO-based)

    public async Task<TranscodingJobInfo> GetTranscodingJobInfoAsync(TranscodingJobInfoArgs args)
    {
        // This needs to adapt. The original GetTranscodingJob takes playSessionId or path+type.
        // The new ITranscoderService uses a DTO `TranscodingJobInfoArgs`.
        TranscodingJob? concreteJob = null;
        if (!string.IsNullOrEmpty(args.PlaySessionId))
        {
            concreteJob = GetTranscodingJob(args.PlaySessionId);
        }
        else if (!string.IsNullOrEmpty(args.TranscodingPath) && !string.IsNullOrEmpty(args.TranscodingJobType))
        {
            // Assuming TranscodingJobType enum can be parsed or mapped from string
            if (Enum.TryParse<TranscodingJobType>(args.TranscodingJobType, true, out var jobType))
            {
                concreteJob = GetTranscodingJob(args.TranscodingPath, jobType);
            }
        }

        if (concreteJob is null)
        {
            // Consider throwing an exception or returning a specific status if not found,
            // but the interface implies returning a DTO. A null DTO might be okay if the job doesn't exist.
            // For now, let's return a new object indicating not found or default state.
             return new TranscodingJobInfo { Id = string.Empty, State = "NotFound", Progress = 0 };
        }

        // Map concreteJob to TranscodingJobInfo DTO
        return new TranscodingJobInfo
        {
            Id = concreteJob.Id,
            State = concreteJob.HasExited ? "Finished" : (concreteJob.IsPaused ? "Paused" : "Running"), // Simplified state
            Progress = concreteJob.CompletionPercentage ?? 0
        };
    }

    public Task KillTranscodingJobsAsync(string deviceId, string playSessionId)
    {
        // The new interface has a slightly different signature (no Func<string, bool> deleteFiles).
        // We'll call the existing method with a default deleteFiles behavior.
        return KillTranscodingJobs(deviceId, playSessionId, path => true); // Default to deleting files
    }

    public async Task<TranscodingJobInfo> StartTranscodingAsync(TranscodingTaskOptions options)
    {
        // This is the most complex adaptation.
        // Need to create StreamState, commandLineArguments etc. from TranscodingTaskOptions.
        // This is a placeholder and would require significant logic from other parts of the application (e.g., StreamingService).
        _logger.LogWarning("ITranscoderService.StartTranscodingAsync is a placeholder and not fully implemented.");
        // For now, throwing NotImplementedException as a full implementation is too complex for this step.
        // A real implementation would construct StreamState, then call the existing StartFfMpeg.
        // var streamState = await _someMediaInfoService.BuildStreamState(options...);
        // var concreteJob = await StartFfMpeg(streamState, options.OutputPath, ...);
        // return new TranscodingJobInfo { Id = concreteJob.Id, ... };
        throw new NotImplementedException("StartTranscodingAsync requires a full adaptation layer.");
    }

    public Task ReportTranscodingProgressAsync(ReportTranscodingProgressArgs progressArgs)
    {
        // This needs to find the TranscodingJob and StreamState.
        // The concrete ReportTranscodingProgress takes TranscodingJob and StreamState.
        // This is a simplified version.
        TranscodingJob? job = null;
        lock(_activeTranscodingJobs)
        {
            job = _activeTranscodingJobs.FirstOrDefault(j => j.Id == progressArgs.JobId);
        }

        if (job != null /* && job.StreamState != null, but StreamState is not directly on job in this context */)
        {
            // We don't have the full StreamState here. We'd need to reconstruct parts of it or fetch it.
            // This is a significant adaptation. For now, just update what we can on the job.
            job.Framerate = progressArgs.Framerate;
            job.CompletionPercentage = progressArgs.PercentComplete;
            job.TranscodingPositionTicks = progressArgs.TranscodingPosition?.Ticks;
            job.BytesTranscoded = progressArgs.BytesTranscoded;
            job.BitRate = progressArgs.BitRate;

            // The original ReportTranscodingProgress also updates SessionManager.
            // This part needs to be replicated carefully.
             if (!string.IsNullOrWhiteSpace(progressArgs.DeviceId))
             {
                 var hardwareAccelerationType = _serverConfigurationManager.GetEncodingOptions().HardwareAccelerationType;
                 _sessionManager.ReportTranscodingInfo(progressArgs.DeviceId, new TranscodingInfo
                 {
                     Bitrate = progressArgs.BitRate ?? progressArgs.TotalOutputBitrate,
                     AudioCodec = progressArgs.OutputAudioCodec,
                     VideoCodec = progressArgs.OutputVideoCodec,
                     Container = progressArgs.OutputContainer,
                     Framerate = progressArgs.Framerate,
                     CompletionPercentage = progressArgs.PercentComplete,
                     Width = progressArgs.OutputWidth,
                     Height = progressArgs.OutputHeight,
                     AudioChannels = progressArgs.OutputAudioChannels,
                     // IsAudioDirect/IsVideoDirect would need more info from the original StreamState
                     HardwareAccelerationType = hardwareAccelerationType,
                     TranscodeReasons = progressArgs.TranscodeReasons ?? new List<string>()
                 });
             }
        }
        else
        {
            _logger.LogWarning($"ReportTranscodingProgressAsync: Job with ID {progressArgs.JobId} not found or StreamState unavailable.");
        }
        return Task.CompletedTask;
    }

    public Task PingTranscodingJobAsync(string playSessionId, bool? isUserPaused)
    {
        PingTranscodingJob(playSessionId, isUserPaused); // Direct call to existing method
        return Task.CompletedTask;
    }

    #endregion
}
