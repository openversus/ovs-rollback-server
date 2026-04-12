// Utilities.cs
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using static OVS.Rollback.Core.LoggerTemplates;

namespace OVS.Rollback
{
    public static class Utilities
    {
        private class UtilitiesLog { }
        private static readonly ILogger<UtilitiesLog> logger = NewLogger<UtilitiesLog>();
        private static bool _isOVS = default;
        private static bool _isMVSI = default;
        public static string BaseUrl { get; private set; } = "";
        public static bool IsOVS {
            
            get {
                if (_isOVS == default || _isMVSI == default)
                {
                    GetBaseUrlFromEnv();
                }
                return _isOVS;
            }
            private set { _isOVS = value; }
        }
        public static bool IsMVSI
        {
            get => !IsOVS;
            private set { _isMVSI = value; }
        }
        public static string GetBaseUrlFromEnv(ILogger _logger)
        {
            var url = Environment.GetEnvironmentVariable("OVS_SERVER") ?? "";
            _isOVS = !string.IsNullOrWhiteSpace(url);

            if (string.IsNullOrWhiteSpace(url))
            {
                _isOVS = false;
                Log.OVSNotSet(_logger);
                url = Environment.GetEnvironmentVariable("mvsi_server") ?? "";
                _isMVSI = !string.IsNullOrWhiteSpace(url);
            }

            if (!string.IsNullOrWhiteSpace(url) && url.EndsWith('/'))
                return url[..^1];

            if (!_isOVS && !_isMVSI)
                Log.NoServerConfigured(_logger);

            return url;
        }

        public static string GetBaseUrlFromEnv<T>(ILogger<T> _logger) where T : class
        {
            return GetBaseUrlFromEnv((ILogger)_logger);
        }

        public static string GetBaseUrlFromEnv()
        {
            return GetBaseUrlFromEnv((ILogger) logger);
        }

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

