// Program.cs
using Microsoft.Extensions.Logging;
using OVS;
using OVS.Rollback.Core;
using OVS.Rollback.Utils;
using OVS.Rollback.Configuration;
using System;
using System.Runtime.CompilerServices;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace OVS.Rollback
{
    public class Server
    {
        private static readonly ILogger<Server> logger = Utilities.NewLogger<Server>();
        protected internal static CancellationTokenSource cts = new CancellationTokenSource();
        public static readonly string LogPrefix = Utilities.LogPrefix;

        public static async Task<int> Main(string[] args)
        {
            // ═══════════════════════════════════════════
            //  Initialize Configuration
            // ═══════════════════════════════════════════
            logger.LogInformation("{LogPrefix} Initializing configuration...", LogPrefix);
            
            // Allow config file path override from command line
            string? configPath = args.Length > 0 && args[0].EndsWith(".json") ? args[0] : null;
            ServerConfiguration.Initialize(logger, configPath);
            var config = ServerConfiguration.Instance;

            // Command-line args override config (for backwards compatibility)
            ushort port = config.Server.Port;
            int maxPlayers = config.Server.MaxPlayers;

            if (args.Length > 0 && !args[0].EndsWith(".json"))
            {
                if (ushort.TryParse(args[0], out var p))
                {
                    port = p;
                    logger.LogInformation("{LogPrefix} Port overridden by command line: {Port}", LogPrefix, port);
                }
                else
                {
                    logger.LogWarning("{LogPrefix} Invalid port number. Using config: {Port}", LogPrefix, port);
                }
            }

            if (args.Length > 1)
            {
                if (int.TryParse(args[1], out var mp) && mp is > 0 and <= 4)
                {
                    maxPlayers = mp;
                    logger.LogInformation("{LogPrefix} MaxPlayers overridden by command line: {MaxPlayers}", LogPrefix, maxPlayers);
                }
                else
                {
                    logger.LogWarning("{LogPrefix} Max players must be between 1 and 4. Using config: {MaxPlayers}", LogPrefix, maxPlayers);
                }
            }

            // ═══════════════════════════════════════════
            //  Setup OpenTelemetry Metrics
            // ═══════════════════════════════════════════
            MeterProvider? meterProvider = null;
            if (config.Logging.EnableMetrics)
            {
                if (config.Logging.EnableConsoleMetrics)
                {
                    meterProvider = Sdk.CreateMeterProviderBuilder()
                        .AddMeter("OVS.Rollback.Server")
                        .AddConsoleExporter()
                        .Build();
                }
                else
                {
                    meterProvider = Sdk.CreateMeterProviderBuilder()
                        .AddMeter("OVS.Rollback.Server")
                        .Build();
                }

                    logger.LogInformation("{LogPrefix} Metrics enabled - exporting to console", LogPrefix);
            }
            else
            {
                logger.LogInformation("{LogPrefix} Metrics disabled by configuration", LogPrefix);
            }

            ILogger<RollbackServer> rollbackLogger = Utilities.NewLogger<RollbackServer>();

            // ═══════════════════════════════════════════
            //  Setup Signal Handlers (SIGHUP for reload)
            // ═══════════════════════════════════════════
            SignalHandler.Initialize(
                logger,
                onReload: () =>
                {
                    // Configuration reloaded - log new values
                    var newConfig = ServerConfiguration.Instance;
                    logger.LogInformation("{LogPrefix} Active configuration: Port={Port}, MaxPlayers={MaxPlayers}, " +
                        "SpinThreshold={SpinUs}μs, TargetFPS={Fps}",
                        LogPrefix, newConfig.Server.Port, newConfig.Server.MaxPlayers,
                        newConfig.Performance.SpinThresholdMicroseconds, newConfig.Performance.TargetFrameRate);
                },
                onShutdown: () =>
                {
                    cts.Cancel();
                });

            Console.CancelKeyPress += (_, token) => {
                token.Cancel = true;
                cts.Cancel();
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            // ═══════════════════════════════════════════
            //  Start Server
            // ═══════════════════════════════════════════
            logger.LogInformation("{LogPrefix} Starting server with configuration:", LogPrefix);
            logger.LogInformation("  Server: Port={Port}, MaxPlayers={MaxPlayers}", 
                port, maxPlayers);
            logger.LogInformation("  Performance: SpinThreshold={SpinUs}μs, Adaptive={Adaptive}, MetricsSampling={Sampling}",
                config.Performance.SpinThresholdMicroseconds, 
                config.Performance.UseAdaptiveSpinThreshold,
                config.Performance.MetricsSamplingInterval);
            logger.LogInformation("  GameLogic: DisconnectTimeout={Timeout}s, MaxInputs={MaxInputs}, MinInputs={MinInputs}",
                config.GameLogic.DisconnectTimeoutSeconds,
                config.GameLogic.MaxInputsPerFrame,
                config.GameLogic.MinimumInputFrames);

            try
            {
                await using var server = new RollbackServer(logger: rollbackLogger, port: port, maxPlayers: maxPlayers);
                server.Start();

                logger.LogInformation("{LogPrefix} Server running. Press Ctrl+C to stop.", LogPrefix);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    logger.LogInformation("{LogPrefix} Send 'kill -HUP {Pid}' to reload configuration", 
                        LogPrefix, Environment.ProcessId);
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException)
                {

                }

                logger.LogInformation("{LogPrefix} Shutting down server...", LogPrefix);
                await server.StopAsync();
            }
            catch (Exception ex)
            {
                logger.LogError("{LogPrefix} Error: {Message}", LogPrefix, ex.Message);
                return 1;
            }
            finally
            {
                meterProvider?.Dispose();
                SignalHandler.Dispose();
            }

            return 0;
        }
    }
}
