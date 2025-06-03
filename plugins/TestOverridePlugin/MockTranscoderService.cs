using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Abstractions.Services;
using Microsoft.Extensions.Logging; // Added for potential logging

namespace TestOverridePlugin
{
    public class MockTranscoderService : ITranscoderService
    {
        private readonly ILogger<MockTranscoderService> _logger;

        // Constructor that might take ILogger if available from plugin's DI scope (if any)
        public MockTranscoderService(ILogger<MockTranscoderService> logger)
        {
            _logger = logger;
            _logger.LogInformation("MockTranscoderService instantiated.");
        }

        public Task<TranscodingJobInfo> GetTranscodingJobInfoAsync(TranscodingJobInfoArgs args)
        {
            _logger.LogInformation("MockTranscoderService: GetTranscodingJobInfoAsync called for PlaySessionId: {PlaySessionId}", args.PlaySessionId);
            return Task.FromResult(new TranscodingJobInfo { Id = args.PlaySessionId ?? "mock_job_id", State = "Mocked", Progress = 50.0 });
        }

        public Task KillTranscodingJobsAsync(string deviceId, string playSessionId)
        {
            _logger.LogInformation("MockTranscoderService: KillTranscodingJobsAsync called for DeviceId: {DeviceId}, PlaySessionId: {PlaySessionId}", deviceId, playSessionId);
            return Task.CompletedTask;
        }

        public Task<TranscodingJobInfo> StartTranscodingAsync(TranscodingTaskOptions options)
        {
            _logger.LogInformation("MockTranscoderService: StartTranscodingAsync called for Input: {InputPath}, Output: {OutputPath}", options.InputPath, options.OutputPath);
            return Task.FromResult(new TranscodingJobInfo { Id = "mock_job_id_started", State = "MockedStart", Progress = 0.0 });
        }

        public Task ReportTranscodingProgressAsync(ReportTranscodingProgressArgs progressArgs)
        {
            _logger.LogInformation("MockTranscoderService: ReportTranscodingProgressAsync called for JobId: {JobId}, Percent: {PercentComplete}", progressArgs.JobId, progressArgs.PercentComplete);
            return Task.CompletedTask;
        }

        public Task PingTranscodingJobAsync(string playSessionId, bool? isUserPaused)
        {
            _logger.LogInformation("MockTranscoderService: PingTranscodingJobAsync called for PlaySessionId: {PlaySessionId}, Paused: {IsUserPaused}", playSessionId, isUserPaused);
            return Task.CompletedTask;
        }
    }
}
