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
using System.Text;

namespace OVS.Rollback
{
    public class Server
    {
        private static readonly ILogger<Server> logger = Utilities.NewLogger<Server>();
        protected internal static CancellationTokenSource cts = new CancellationTokenSource();

        // If true, the server will self-destruct after the first match ends
        // If you plan to have long-running, non-ephemeral servers, set this to false and ensure you have a proper cleanup strategy in place to stop the server when it's no longer needed
        // Official OpenVersus servers are auto-provisioned/deprovisioned on-demand per game, so this is a (redundant) safety measure to prevent orphaned servers from running indefinitely
        // The primary method is a cleanup script scheduled via atd as part of the provisioning process, but this is an extra "just in case" measure
        protected internal static bool MementoMori = false;
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
            MementoMori = config.Server.MementoMori;

            logger.LogInformation("{LogPrefix} Server is alive, starting heartbeat loop...", LogPrefix);
            HeartBeat pacemaker = new HeartBeat();
            _ = pacemaker.StartLoop();

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
                if (int.TryParse(args[1], out var mp) && mp is > 0 and <= 8)
                {
                    maxPlayers = mp;
                    logger.LogInformation("{LogPrefix} MaxPlayers overridden by command line: {MaxPlayers}", LogPrefix, maxPlayers);
                }
                else
                {
                    logger.LogWarning("{LogPrefix} Max players must be between 1 and 8. Using config: {MaxPlayers}", LogPrefix, maxPlayers);
                }
            }

            // ═══════════════════════════════════════════
            //  Setup OpenTelemetry Metrics
            // ═══════════════════════════════════════════
            MeterProvider? meterProvider = null;
            if (config.Logging.EnableMetrics)   
            {
                string defaultMeterName = "OVS.Rollback.Server";
                StringBuilder logEntry = new($"{LogPrefix} Metrics enabled");

                if (config.Logging.EnableConsoleMetrics)
                {
                    meterProvider = Sdk.CreateMeterProviderBuilder()
                        .AddMeter(defaultMeterName)
                        .AddConsoleExporter()
                        .Build();
                    logEntry.AppendLine(" - exporting to console");
                }
                else
                {
                    meterProvider = Sdk.CreateMeterProviderBuilder()
                        .AddMeter(defaultMeterName)
                        .Build();

                    logEntry.AppendLine($" - to view, use: dotnet-counters monitor -p {Environment.ProcessId} --counters \"{defaultMeterName}\"");
                }

                logger.LogInformation(logEntry.ToString());
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
                Events.OnServerStartHandler(server, new EventArgs());

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
