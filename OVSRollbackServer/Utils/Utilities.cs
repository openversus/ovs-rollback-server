// Utilities.cs
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System;

namespace OVS.Rollback
{
    public static class Utilities
    {
        public static string TernaryNullCoalesce<T>(T? value, string nullMessage = "") where T : class
        {
            return value != null ? value.ToString() ?? nullMessage : nullMessage;
        }

        public static string TernaryIsNullOrWhitespace<T>(T? value, string nullOrWhitespaceMessage = "") where T : class
        {
            var strValue = value != null ? value.ToString() ?? "" : "";
            return !string.IsNullOrWhiteSpace(strValue) ? strValue : nullOrWhitespaceMessage;
        }

        public static string TernaryIsNullOrEmpty<T>(T? value, string nullOrEmptyMessage = "") where T : class
        {
            var strValue = value != null ? value.ToString() ?? "" : "";
            return !string.IsNullOrEmpty(strValue) ? strValue : nullOrEmptyMessage;
        }
        public static string Hostname { get; } =
            Environment.GetEnvironmentVariable(
                OperatingSystem.IsWindows() ? "COMPUTERNAME" : "HOSTNAME")
            ?? "unknown";

        public const string ServiceName = "RollbackServer";

        public static string LogPrefix { get; } = $"[{Hostname}.{ServiceName}]:";


        public static string GetLogPrefix([CallerMemberName] string callerName = "")
        {
            var shortenedCallerName = callerName.LastIndexOf('.') >= 0 ? callerName[(callerName.LastIndexOf('.') + 1)..] : callerName;
            return $"[{Hostname}.{shortenedCallerName}]:";
        }

        public static string GetLogPrefix<T>() where T : class
        {
            var typeName = GetShortTypeName<T>();
            return $"[{Hostname}.{typeName}]:";
        }

        public static string GetServiceName<T>() where T : class
        {
            return GetShortTypeName<T>();
        }

        public static string GetShortTypeName<T>() where T : class
        {
            var typeName = typeof(T).Name;
            var shortenedName = typeName.LastIndexOf('.') >= 0 ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
            return shortenedName;
        }

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

