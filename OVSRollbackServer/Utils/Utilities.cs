// Utilities.cs
using Microsoft.Extensions.Logging;
using System;
using System.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using static OVS.Rollback.Core.LoggerTemplates;

namespace OVS.Rollback
{
    public enum HMACType
    {
        Hexlower = 0,
        Hexupper = 1,
        Hex = 1,
        Base64 = 2,
        Base64Lower = 2,
        Base64Upper = 3,
        ByteArray = 4
    }

    public enum HMACHashAlgorithm
    {
        MD5 = 0,
        SHA1 = 1,
        SHA256 = 2,
        SHA384 = 3,
        SHA512 = 4,
        SHA3_256 = 5,
        SHA3_384 = 6,
        SHA3_512 = 7
    }
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

        public static T CreateHMAC<T>(byte[] key, string message, HMACType type, HMACHashAlgorithm algo = HMACHashAlgorithm.SHA1) where T : class
        {
            T? returnObject = default;
            using HMAC hmac = algo switch {
                HMACHashAlgorithm.MD5 => new HMACMD5(key),
                HMACHashAlgorithm.SHA1 => new HMACSHA1(key),
                HMACHashAlgorithm.SHA256 => new HMACSHA256(key),
                HMACHashAlgorithm.SHA384 => new HMACSHA384(key),
                HMACHashAlgorithm.SHA512 => new HMACSHA512(key),
                HMACHashAlgorithm.SHA3_256 => new HMACSHA3_256(key),
                HMACHashAlgorithm.SHA3_384 => new HMACSHA3_384(key),
                HMACHashAlgorithm.SHA3_512 => new HMACSHA3_512(key),
                _ => new HMACSHA1(key),
            };

            Func<string, byte[], string> HexHash = (message, key) => {
                var messageByteArray = Encoding.UTF8.GetBytes(message);
                using var memoryStream = new MemoryStream(messageByteArray);
                return hmac.ComputeHash(memoryStream).Aggregate("", (s, e) => s + String.Format("{0:x2}", e), s => s);
            };

            switch (type)
            {
                case HMACType.Hex:
                    returnObject = HexHash(message, key).ToUpper() as T;
                    break;
                case HMACType.Hexlower:
                    returnObject = HexHash(message, key).ToLower() as T;
                    break;
                case HMACType.ByteArray:
                    returnObject = hmac.ComputeHash(Encoding.UTF8.GetBytes(message)) as T;
                    break;
                case HMACType.Base64:
                    returnObject = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))) as T;
                    break;
                case HMACType.Base64Upper:
                    returnObject = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToUpper() as T;
                    break;
                default:
                    returnObject = HexHash(message, key).ToLower() as T;
                    break;
            }

            return returnObject ?? throw new InvalidOperationException($"Failed to create HMACSHA1 hash of type {type} for the given key and message.");
        }

        public static T CreateHMAC<T>(string key, string message, HMACType type) where T : class
        {
            return CreateHMAC<T>(Encoding.UTF8.GetBytes(key), message, type);
        }
    }
}

