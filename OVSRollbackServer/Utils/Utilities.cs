// Utilities.cs
using Microsoft.Extensions.Logging;
using System;

namespace OVS.Rollback
{
    public static class Utilities
    {
        public static string Hostname { get; } =
            Environment.GetEnvironmentVariable(
                OperatingSystem.IsWindows() ? "COMPUTERNAME" : "HOSTNAME")
            ?? "unknown";

        public const string ServiceName = "RollbackServer";

        public static string LogPrefix { get; } = $"[{Hostname}.{ServiceName}]:";


        public static ILogger<T> NewLogger<T>() where T : class
        {
            var loggerFactory = LoggerFactory.Create(builder => {
                builder
                    .SetMinimumLevel(LogLevel.Debug)
                    .AddSimpleConsole(opts => {
                        opts.TimestampFormat = "HH:mm:ss.fff ";
                        opts.SingleLine = true;
                        opts.IncludeScopes = false;
                    });
            });
            return loggerFactory.CreateLogger<T>();
        }
    }
}

