using System;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller; // For IServerApplicationHost
using MediaBrowser.Model.Plugins;
using Jellyfin.Abstractions.Services; // For IServiceOverrideHost and ITranscoderService
using Microsoft.Extensions.DependencyInjection; // For GetService
using Microsoft.Extensions.Logging; // For ILogger

namespace TestOverridePlugin
{
    public class Plugin : IPlugin
    {
        public string Name => "Test Override Plugin";
        public string Description => "A plugin to test overriding core services.";
        public Guid Id => Guid.Parse("12345678-1234-1234-1234-1234567890AB"); // Unique GUID for this plugin
        public Version Version => new Version("1.0.0.0");
        public string AssemblyFilePath { get; private set; }
        public bool CanUninstall => true;
        public string DataFolderPath { get; private set; }

        private readonly IServerApplicationHost _appHost;
        private readonly ILogger<Plugin> _logger;

        // Constructor will be called by the PluginManager.
        // We need IServerApplicationHost to get IServiceOverrideHost.
        // ILogger for logging.
        public Plugin(IServerApplicationHost appHost, ILogger<Plugin> logger)
        {
            _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogInformation("TestOverridePlugin: Constructor called.");

            RegisterOverride();
        }

        private void RegisterOverride()
        {
            try
            {
                var serviceOverrideHost = _appHost.ServiceProvider.GetService<IServiceOverrideHost>();
                if (serviceOverrideHost != null)
                {
                    // Need an ILoggerFactory to create ILogger for MockTranscoderService
                    var loggerFactory = _appHost.ServiceProvider.GetService<ILoggerFactory>();
                    if (loggerFactory == null) {
                        _logger.LogError("TestOverridePlugin: Could not get ILoggerFactory to create logger for MockTranscoderService.");
                        // Optionally, create MockTranscoderService without logger or handle error
                        return;
                    }
                    var mockLogger = loggerFactory.CreateLogger<MockTranscoderService>();
                    var mockTranscoder = new MockTranscoderService(mockLogger);

                    serviceOverrideHost.RegisterOverride<ITranscoderService>(mockTranscoder);
                    _logger.LogInformation("TestOverridePlugin: Successfully registered MockTranscoderService override.");
                }
                else
                {
                    _logger.LogError("TestOverridePlugin: IServiceOverrideHost not found. Cannot register override.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TestOverridePlugin: Error during service override registration.");
            }
        }

        public PluginInfo GetPluginInfo()
        {
            return new PluginInfo
            {
                Name = this.Name,
                Description = this.Description,
                Id = this.Id,
                Version = this.Version.ToString(),
                CanUninstall = this.CanUninstall,
                DataFolderPath = this.DataFolderPath
            };
        }

        public void OnUninstalling() { }

        // Required by IPluginAssembly (which BasePlugin implements, but IPlugin might be used directly by PluginManager)
        public void SetAttributes(string assemblyFilePath, string dataFolderPath, Version assemblyVersion)
        {
            AssemblyFilePath = assemblyFilePath;
            DataFolderPath = dataFolderPath;
            // Version is already set
        }

        public void SetId(Guid id)
        {
            // Id is already set
        }
    }
}
