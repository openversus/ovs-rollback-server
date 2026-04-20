// SignalHandler.cs
using Microsoft.Extensions.Logging;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
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

    public static partial class SignalSender
    {
        private class SignalSenderLogger { }
        private static ILogger _logger = Utilities.NewLogger<SignalSenderLogger>();

        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static readonly bool IsUnix = (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                                                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
                                                    RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD));
        private static int ProcessID => Environment.ProcessId;

        // Unix

        private static partial class Unix
        {
            [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
            public static partial int Kill(int pid, int sig);
        }

        // Common Unix signals
        public static class UnixSignals
        {
            public const int SIGHUP = 1;
            public const int SIGINT = 2;
            public const int SIGQUIT = 3;
            public const int SIGKILL = 9;
            public const int SIGUSR1 = 10;
            public const int SIGUSR2 = 12;
            public const int SIGTERM = 15;
            public const int SIGCONT = 18;
            public const int SIGSTOP = 19;
            public const int SIGBREAK = 21; // Windows-specific, no Unix equivalent
        }

        // Windows

        private static partial class Windows
        {
            // For SIGINT-equivalent (Ctrl+C / Ctrl+Break) to a console process group
            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

            // For SIGKILL-equivalent
            [LibraryImport("kernel32.dll", SetLastError = true)]
            public static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

            [LibraryImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static partial bool CloseHandle(IntPtr hObject);

            public const uint CTRL_C_EVENT = 0;
            public const uint CTRL_BREAK_EVENT = 1;
            public const uint PROCESS_TERMINATE = 0x0001;
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a POSIX signal on Unix. On Windows, maps SIGINT→Ctrl+C,
        /// SIGBREAK→Ctrl+Break, and SIGKILL→TerminateProcess. Other signals
        /// throw PlatformNotSupportedException on Windows.
        /// </summary>
        public static void Send(int? pid, int unixSignalNumber = UnixSignals.SIGINT)
        {
            int targetPid = pid ?? ProcessID;

            if (IsUnix)
            {
                SendUnixSignal(targetPid, unixSignalNumber);
            }
            else if (IsWindows)
            {
                SendWindowsSignal(targetPid, unixSignalNumber);
            }
            else
            {
                throw new PlatformNotSupportedException("Unsupported OS platform.");
            }
        }

        private static void SendUnixSignal(int pid, int sig)
        {
            int result = Unix.Kill(pid, sig);
            if (result != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                throw new InvalidOperationException(
                    $"kill({pid}, {sig}) failed with errno {errno}.");
            }
        }

        private static void SendWindowsSignal(int pid, int unixSignalNumber)
        {
            switch (unixSignalNumber)
            {
                case UnixSignals.SIGINT:
                    // Sends Ctrl+C to the process group; pid must be a process group ID.
                    // Note: the calling process must also be attached to the same console,
                    // or you must use CREATE_NEW_PROCESS_GROUP when spawning the target.
                    if (!Windows.GenerateConsoleCtrlEvent(Windows.CTRL_C_EVENT, (uint)pid))
                        throw new InvalidOperationException(
                            $"GenerateConsoleCtrlEvent(CTRL_C, {pid}) failed: " +
                            $"error {Marshal.GetLastPInvokeError()}");
                    break;

                case 21: // SIGBREAK (Windows-specific, no Unix equivalent)
                    if (!Windows.GenerateConsoleCtrlEvent(Windows.CTRL_BREAK_EVENT, (uint)pid))
                        throw new InvalidOperationException(
                            $"GenerateConsoleCtrlEvent(CTRL_BREAK, {pid}) failed: " +
                            $"error {Marshal.GetLastPInvokeError()}");
                    break;

                case UnixSignals.SIGKILL:
                case UnixSignals.SIGTERM:
                    // Windows has no graceful SIGTERM; both map to TerminateProcess
                    IntPtr handle = Windows.OpenProcess(Windows.PROCESS_TERMINATE, false, pid);
                    if (handle == IntPtr.Zero)
                        throw new InvalidOperationException(
                            $"OpenProcess({pid}) failed: error {Marshal.GetLastPInvokeError()}");
                    try
                    {
                        if (!Windows.TerminateProcess(handle, 1))
                            throw new InvalidOperationException(
                                $"TerminateProcess({pid}) failed: error {Marshal.GetLastPInvokeError()}");
                    }
                    finally
                    {
                        Windows.CloseHandle(handle);
                    }
                    break;

                default:
                    throw new PlatformNotSupportedException(
                        $"Unix signal {unixSignalNumber} has no Windows equivalent.");
            }
        }

        internal static void MementoMori(object? sender, StatusEventArgs e)
        {
            MementoMori();
        }
        internal static void MementoMori()
        {
            _logger?.LogInformation("Memento Mori: Server has been alive for over 8.5 minutes. Shutting down...");
            Send(null, UnixSignals.SIGINT);
        }

        internal static void MementoMori(string? shutdownReason = "")
        {
            _logger?.LogInformation("Memento Mori: {shutdownReason} Shutting down...", shutdownReason);
            Send(null, UnixSignals.SIGINT);
        }
    }
}
