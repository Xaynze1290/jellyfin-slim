using System;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Abstractions.Services;
using Jellyfin.CoreDefaults.Services; // For ServiceOverrideHost
using Jellyfin.CoreDefaults.Transcoding; // For default TranscodeManager
using TestOverridePlugin; // For MockTranscoderService and Plugin
using Moq; // For mocking
using MediaBrowser.Controller; // For IServerApplicationHost
using Microsoft.Extensions.Logging;

namespace PluginOverrideTests
{
    public class ServiceOverrideTest
    {
        [Fact]
        public void TestPlugin_Overrides_ITranscoderService_Successfully()
        {
            var services = new ServiceCollection();

            // 1. Register IServiceOverrideHost
            services.AddSingleton<IServiceOverrideHost, ServiceOverrideHost>();

            // 2. Register default ITranscoderService using the factory pattern
            // This mimics Startup.cs but uses the concrete types for clarity in test
            services.AddSingleton<ITranscoderService>(serviceProvider =>
            {
                var overrideHost = serviceProvider.GetRequiredService<IServiceOverrideHost>();
                if (overrideHost.HasOverride<ITranscoderService>())
                {
                    return overrideHost.GetOverride<ITranscoderService>();
                }
                // For testing, we might not need full ActivatorUtilities if TranscodeManager has many deps.
                // Instead, we can ensure it's attempted to be created if not overridden.
                // Here, we'll assume we can create it or mock its creation if it's complex.
                // For this test, if it's not overridden, we could even return null or a specific marker.
                // However, to be closer to reality, let's try to use ActivatorUtilities.
                // This means TranscodeManager and its dependencies must be resolvable by this test ServiceProvider.
                // This can get complex. A simpler test might directly check ServiceOverrideHost.
                try
                {
                    return ActivatorUtilities.CreateInstance<TranscodeManager>(serviceProvider);
                }
                catch (Exception ex)
                {
                    // If TranscodeManager can't be created due to missing deps in this minimal setup,
                    // it's okay for this specific test, as long as the override happens.
                    Console.WriteLine($"Could not create default TranscodeManager in test: {ex.Message}. This is acceptable if override works.");
                    return null;
                }
            });

            // Add minimal dependencies for TranscodeManager if ActivatorUtilities is to succeed
            // This is where it gets tricky for a *minimal* DI container.
            // For now, we rely on the override happening, so default creation failure is logged but tolerated.
            var mockLoggerFactory = new Mock<ILoggerFactory>();
            mockLoggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            services.AddSingleton<ILoggerFactory>(mockLoggerFactory.Object);
            // Add other critical dependencies for TranscodeManager if known and simple, otherwise rely on override.


            // 3. Mock IServerApplicationHost and its ServiceProvider for the Plugin
            var mockAppHostServiceProvider = new Mock<IServiceProvider>();
            mockAppHostServiceProvider.Setup(sp => sp.GetService(typeof(IServiceOverrideHost)))
                .Returns(services.BuildServiceProvider().GetService<IServiceOverrideHost>()); // Use the host from our test container
            mockAppHostServiceProvider.Setup(sp => sp.GetService(typeof(ILoggerFactory)))
                .Returns(mockLoggerFactory.Object);


            var mockAppHost = new Mock<IServerApplicationHost>();
            mockAppHost.Setup(ah => ah.ServiceProvider).Returns(mockAppHostServiceProvider.Object);

            // 4. Instantiate the plugin (this should register the override)
            var pluginLogger = new Mock<ILogger<TestOverridePlugin.Plugin>>().Object;
            try
            {
                var testPlugin = new TestOverridePlugin.Plugin(mockAppHost.Object, pluginLogger);
            }
            catch (Exception ex)
            {
                // This can happen if the plugin's constructor tries to resolve something not mocked
                 Console.WriteLine($"Error instantiating plugin (might be ok if override host was still called): {ex.Message}");
            }


            // 5. Build the main service provider for the test
            var serviceProvider = services.BuildServiceProvider();

            // 6. Resolve ITranscoderService
            var resolvedTranscoderService = serviceProvider.GetService<ITranscoderService>();

            // 7. Assert that the resolved service is the mock implementation
            Assert.NotNull(resolvedTranscoderService);
            Assert.IsType<MockTranscoderService>(resolvedTranscoderService);

            // Optional: Call a method on the mock to see if it logs as expected
            var mockService = resolvedTranscoderService as MockTranscoderService;
            mockService?.PingTranscodingJobAsync("test-session", false).GetAwaiter().GetResult();
            // (Check logs or add a property to MockTranscoderService to verify call)
        }
    }
}
