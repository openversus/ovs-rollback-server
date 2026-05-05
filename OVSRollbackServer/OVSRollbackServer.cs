// Program.cs
using Microsoft.Extensions.Logging;
using OVS;
using OVS.Rollback.Core;
using OVS.Rollback.Models;
using OVS.Rollback.Utils;
using OVS.Rollback.Configuration;
using System;
using System.Runtime.CompilerServices;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using System.Text;
using OVS.Rollback.Common;
using Serilog;

namespace OVS.Rollback
{
    public class Server
    {
        private static readonly ILogger<Server> logger = Utilities.NewLogger<Server>();
        //internal static readonly HTTPHelper HttpHelper = new HTTPHelper(logger);
        internal static readonly HTTPHelper HttpHelper = Singletons.SharedHTTPHelper;
        protected internal static CancellationTokenSource cts = Singletons.CTS;
        private static readonly System.Diagnostics.Stopwatch _runTimer = Singletons.SharedStopwatch;
        private static ServerConfiguration? Config;

        // If true, the server will self-destruct after the first match ends
        // If you plan to have long-running, non-ephemeral servers, set this to false and ensure you have a proper cleanup strategy in place to stop the server when it's no longer needed
        // Official OpenVersus servers are auto-provisioned/deprovisioned on-demand per game, so this is a (redundant) safety measure to prevent orphaned servers from running indefinitely
        // The primary method is a cleanup script scheduled via atd as part of the provisioning process, but this is an extra "just in case" measure
        protected internal static bool MementoMori = false;
        protected internal static System.Diagnostics.Stopwatch RunTimer { get => _runTimer; }
        public static readonly string LogPrefix = Utilities.LogPrefix;

        public static async Task<int> Main(string[] args)
        {
            RunTimer.Start();

            // ═══════════════════════════════════════════
            //  Initialize Configuration
            // ═══════════════════════════════════════════

            logger.LogInformation("{LogPrefix} Initializing configuration...", LogPrefix);

            if (Statics.PreLoggerMessages.Count > 0)
            {
                foreach (string message in Statics.PreLoggerMessages)
                {
                    logger.LogInformation($"{LogPrefix} PreLogger message: {message}");
                }
                Statics.PreLoggerMessages.Clear();
            }

            Config = Singletons.Config;
            MementoMori = Config.Server.MementoMori;

            RegisterMatchEvents();

            logger.LogInformation("{LogPrefix} Server is alive, starting heartbeat loop...", LogPrefix);
            HeartBeat pacemaker = new HeartBeat();
            _ = pacemaker.StartLoop();

            // Command-line args override config (for backwards compatibility)
            ushort port = Config.Server.Port;
            int maxPlayers = Config.Server.MaxPlayers;

            // cmdline options should take priority
            if (port != Singletons.Port)
            {
                port = Singletons.Port;
            }

            if (maxPlayers != Singletons.MaxPlayers)
            {
                maxPlayers = Singletons.MaxPlayers;
            }

            // ═══════════════════════════════════════════
            //  OpenTelemetry Metrics moved to DIContainer
            // ═══════════════════════════════════════════

            logger.LogInformation("{LogPrefix} {Metrics}", LogPrefix, Singletons.MetricsString);
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
                    pacemaker.StopLoop().Wait();
                    if (!cts.IsCancellationRequested)
                    {
                        cts.Cancel();
                    }
                });

            Console.CancelKeyPress += (_, token) => {
                token.Cancel = true;

                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) => {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            };

            // ═══════════════════════════════════════════
            //  Start Server
            // ═══════════════════════════════════════════
            logger.LogInformation("{LogPrefix} Starting server with configuration:", LogPrefix);
            logger.LogInformation("  Server: Port={Port}, MaxPlayers={MaxPlayers}", 
                port, maxPlayers);
            logger.LogInformation("  Performance: SpinThreshold={SpinUs}μs, Adaptive={Adaptive}, MetricsSampling={Sampling}",
                Config.Performance.SpinThresholdMicroseconds, 
                Config.Performance.UseAdaptiveSpinThreshold,
                Config.Performance.MetricsSamplingInterval);
            logger.LogInformation("  GameLogic: DisconnectTimeout={Timeout}s, MaxInputs={MaxInputs}, MinInputs={MinInputs}",
                Config.GameLogic.DisconnectTimeoutSeconds,
                Config.GameLogic.MaxInputsPerFrame,
                Config.GameLogic.MinimumInputFrames);

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
                CloseLogger();
                return 1;
            }
            finally
            {
                logger.LogInformation("{LogPrefix} Server stopped. Cleaning up resources...", LogPrefix);
                //meterProvider?.Dispose();
                Singletons.MetricsProvider?.Dispose();
                SignalHandler.Dispose();
                logger.LogInformation("{LogPrefix} Total runtime was: {runTime}s", LogPrefix, (RunTimer.ElapsedMilliseconds / 1000));
                RunTimer.Stop();
            }

            CloseLogger();
            return 0;
        }

        private static void RegisterMatchEvents()
        {
            Events.OnServerStart               +=    HttpHelper.SendMatchStatus;
            Events.OnServerStop                +=    HttpHelper.SendMatchStatus;
            Events.OnServerListening           +=    HttpHelper.SendMatchStatus;
            Events.OnHeartBeat                 +=    HttpHelper.SendMatchStatus;
            Events.OnConfigReceived            +=    HttpHelper.SendMatchStatus;
            Events.OnPlayerConnect             +=    HttpHelper.SendMatchStatus;
            Events.OnPlayerDisconnect          +=    HttpHelper.SendMatchStatus;
            Events.OnAllPlayesrDisconnected    +=    HttpHelper.SendMatchStatus;
            Events.OnPlayerReady               +=    HttpHelper.SendMatchStatus;
            Events.OnAllPlayersReady           +=    HttpHelper.SendMatchStatus;
            Events.OnRageQuit                  +=    HttpHelper.SendMatchStatus;
            Events.OnPingPhase                 +=    HttpHelper.SendMatchStatus;
            Events.OnTickPerformance           +=    HttpHelper.SendMatchStatus;
            Events.OnMatchStart                +=    HttpHelper.SendMatchStatus;
            Events.OnMatchEnd                  +=    HttpHelper.SendMatchStatus;
            Events.OnError                     +=    HttpHelper.SendMatchStatus;
            Events.OnTerminatingError          +=    HttpHelper.SendMatchStatus;
            Events.OnServerIdle                +=    HttpHelper.SendMatchStatus;
            Events.OnMementoMori               +=    HttpHelper.SendMatchStatus;
            //Events.OnMementoMori += (_) => {
            //    SignalSender.MementoMori();
            //    return Task.FromResult(default(MatchStatusResponse)!);
            //};
        }

        private static void CloseLogger()
        {
            try
            {
                Log.CloseAndFlush();
            }
            catch (Exception logEx)
            {
                Console.Error.WriteLine($"[{LogPrefix}] Failed to close and flush main logger instance: {logEx}");
            }
            if (Statics.ShouldMoveLogfile)
            {
                try
                {
                    File.Move(Utilities.LogPath, Statics.FinalLogFile);
                    Console.WriteLine($"[{LogPrefix}] Log file moved to final location: {Statics.FinalLogFile}");
                }
                catch (Exception archiveEx)
                {
                    Console.Error.WriteLine($"[{LogPrefix}] Failed to archive log file: {archiveEx}");
                }
            }
        }
    }
}
