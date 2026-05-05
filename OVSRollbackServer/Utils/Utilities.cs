// Utilities.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using OVS.Rollback.Configuration;
using OVS.Rollback.Utils;
using Serilog.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
        public static string BaseUrl { get; private set; } = String.Empty;
        public static string LogDir { get; internal set; } = String.Empty;
        public static string LogFilename { get; internal set; } = $"{Guid.NewGuid()}.log";
        public static string LogPath { get; internal set; } = CreateAndSetLogPath();

        internal static string CreateAndSetLogPath()
        {
            bool useTempFile = false;
            string LogFile = String.Empty;

            string AppData = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string envLogPathOverride = ServerConfiguration.GetEnvString("Logging__LogFilePath", String.Empty) ?? String.Empty;

            if (envLogPathOverride.NotNullOrWhiteSpace)
            {
                LogDir = Path.GetDirectoryName(envLogPathOverride) ?? String.Empty;
                LogFilename = Path.GetFileName(envLogPathOverride) ?? LogFilename;
            }
            else
            {
                LogDir = Path.Combine(AppData, "openversus", "rollback-server");
            }

            if (!Path.Exists(LogDir))
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                }
                catch
                {
                    useTempFile = true;
                    LogFile = Path.GetTempFileName();
                }
            }

            if (!useTempFile)
            {
                LogFile = Path.Combine(LogDir, LogFilename);
            }

            //Console.WriteLine($"Log file path: {LogFile}");
            return LogFile;
        }

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
            var url = Environment.GetEnvironmentVariable("OVS_SERVER") ?? string.Empty;
            _isOVS = url.NotNullOrWhiteSpace;

            if (url.StringIsNullOrWhiteSpace)
            {
                _isOVS = false;
                Log.OVSNotSet(_logger);
                url = Environment.GetEnvironmentVariable("mvsi_server") ?? string.Empty;
                _isMVSI = url.NotNullOrWhiteSpace;
            }

            if (url.NotNullOrWhiteSpace && url.EndsWith('/'))
            {
                return url[..^1];
            }

            if (!_isOVS && !_isMVSI)
            {
                // Normally we would fire a TerminatingError event here, but since there's no server configured to
                // receive it, that doesn't exactly make a whole hell of a lot of sense, so we'll just log it
                // and walk into traffic

                Log.NoServerConfigured(_logger);
                SignalSender.MementoMori("Neither OVS_SERVER nor mvsi_server set");
            }

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
            return new SerilogLoggerFactory().CreateLogger<T>();

            //var sharedLoggerFactory = Singletons.SharedLoggerFactory;
            //return sharedLoggerFactory.CreateLogger<T>();


            //var loggerFactory = LoggerFactory.Create(builder => {
            //    builder
            //        .SetMinimumLevel(LogLevel.Debug)
            //        .AddSimpleConsole(opts => {
            //            opts.TimestampFormat = "HH:mm:ss.fff ";
            //            opts.SingleLine = true;
            //            opts.IncludeScopes = false;
            //        });
            //});
            //return loggerFactory.CreateLogger<T>();
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

        /// <summary>
        /// Retrieves a required service of the specified type from the application's DI Container.
        /// </summary>
        /// <remarks>This method throws an exception if the requested service type is not
        /// registered in the dependency injection container. Use this method when the service is expected to be
        /// available and its absence should be treated as an error.</remarks>
        /// <typeparam name="TType">The type of the service to retrieve. Must be a reference type.</typeparam>
        /// <returns>An instance of the specified service type if it is registered; otherwise, throws an exception.</returns>
        public static dynamic? GetRequiredService<TType>() where TType : class
        {
            return Singletons.RollbackDIContainer?.GenericHost?.Services.GetRequiredService<TType>();
        }

        /// <summary>
        /// Retrieves a required service of the specified type from the application's DI Container.
        /// </summary>
        /// <remarks>This method relies on the application's dependency injection container being
        /// initialized. If the service of type <typeparamref name="TType"/> is not registered, an exception will be
        /// thrown. Use this method when the service is required and its absence should be treated as an
        /// error.</remarks>
        /// <typeparam name="TType">The type of the service to retrieve. Must be a reference type.</typeparam>
        /// <param name="type">An instance of the type used to specify the service to retrieve. This parameter is not used to resolve
        /// the service and may be null.</param>
        /// <returns>An instance of the requested service type if it is registered; otherwise, throws an exception.</returns>
        public static dynamic? GetRequiredService<TType>(TType type) where TType : class
        {
            return Singletons.RollbackDIContainer?.GenericHost?.Services.GetRequiredService<TType>();
        }
    }
}

