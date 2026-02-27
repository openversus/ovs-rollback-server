// SignalHandler.cs
using Microsoft.Extensions.Logging;
using OVS.Rollback.Configuration;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OVS.Rollback.Utils
{
    /// <summary>
    /// Handles OS signals for configuration reload (SIGHUP on Linux, Ctrl+Break on Windows)
    /// </summary>
    public static class SignalHandler
    {
        private static ILogger? _logger;
        private static bool _initialized = false;

        // PosixSignalRegistration is available in .NET 6+ for cross-platform signal handling
        private static PosixSignalRegistration? _sighupRegistration;
        private static PosixSignalRegistration? _sigtermRegistration;
        private static PosixSignalRegistration? _sigintRegistration;

        /// <summary>
        /// Initialize signal handlers for configuration reload and graceful shutdown
        /// </summary>
        public static void Initialize(ILogger logger, Action? onReload = null, Action? onShutdown = null)
        {
            if (_initialized) return;
            
            _logger = logger;

            try
            {
                // SIGHUP - Reload configuration (Linux/Mac)
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    _sighupRegistration = PosixSignalRegistration.Create(
                        PosixSignal.SIGHUP,
                        context =>
                        {
                            context.Cancel = true; // Don't terminate
                            _logger?.LogInformation("Received SIGHUP signal, reloading configuration...");
                            
                            try
                            {
                                ServerConfiguration.Reload();
                                onReload?.Invoke();
                                _logger?.LogInformation("Configuration reloaded successfully");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Failed to reload configuration");
                            }
                        });

                    _logger?.LogInformation("SIGHUP handler registered (send 'kill -HUP {Pid}' to reload config)", 
                        Environment.ProcessId);
                }

                // SIGTERM - Graceful shutdown
                _sigtermRegistration = PosixSignalRegistration.Create(
                    PosixSignal.SIGTERM,
                    context =>
                    {
                        context.Cancel = true;
                        _logger?.LogInformation("Received SIGTERM signal, shutting down gracefully...");
                        onShutdown?.Invoke();
                    });

                // SIGINT (Ctrl+C) - Graceful shutdown
                _sigintRegistration = PosixSignalRegistration.Create(
                    PosixSignal.SIGINT,
                    context =>
                    {
                        context.Cancel = true;
                        _logger?.LogInformation("Received SIGINT signal, shutting down gracefully...");
                        onShutdown?.Invoke();
                    });

                _logger?.LogInformation("Signal handlers initialized (SIGTERM, SIGINT{HupNote})",
                    OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? ", SIGHUP" : "");

                _initialized = true;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to initialize signal handlers (may not be supported on this platform)");
            }
        }

        /// <summary>
        /// Cleanup signal handlers on shutdown
        /// </summary>
        public static void Dispose()
        {
            _sighupRegistration?.Dispose();
            _sigtermRegistration?.Dispose();
            _sigintRegistration?.Dispose();
            
            _logger?.LogDebug("Signal handlers disposed");
            _initialized = false;
        }
    }
}
