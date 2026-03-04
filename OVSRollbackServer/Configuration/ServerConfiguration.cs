// ServerConfiguration.cs
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OVS.Rollback.Configuration
{
    /// <summary>
    /// Root configuration class for OVS Rollback Server.
    /// Supports JSON file + environment variable overrides + hot reload via SIGHUP.
    /// </summary>
    public class ServerConfiguration
    {
        private static ServerConfiguration? _instance;
        private static readonly object _lock = new();
        private static ILogger? _logger;
        private static string _configPath = "appsettings.json";

        // Configuration sections
        public ServerSettings Server { get; set; } = new();
        public PerformanceSettings Performance { get; set; } = new();
        public NetworkingSettings Networking { get; set; } = new();
        public GameLogicSettings GameLogic { get; set; } = new();
        public RiftCalculationSettings RiftCalculation { get; set; } = new();
        public PingPhaseSettings PingPhase { get; set; } = new();
        public InputValidationSettings InputValidation { get; set; } = new();
        public DesyncDetectionSettings DesyncDetection { get; set; } = new();
        public LoggingSettings Logging { get; set; } = new();

        /// <summary>
        /// Singleton instance with thread-safe access
        /// </summary>
        public static ServerConfiguration Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= Load();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Initialize configuration with logger
        /// </summary>
        public static void Initialize(ILogger logger, string? configPath = null)
        {
            _logger = logger;
            if (!string.IsNullOrEmpty(configPath))
                _configPath = configPath;
            
            lock (_lock)
            {
                _instance = Load();
            }
        }

        /// <summary>
        /// Reload configuration from disk (called on SIGHUP)
        /// </summary>
        public static void Reload()
        {
            lock (_lock)
            {
                _logger?.LogInformation("Reloading configuration from {ConfigPath}", _configPath);
                var newConfig = Load();
                _instance = newConfig;
                _logger?.LogInformation("Configuration reloaded successfully");

                _logger?.LogInformation("New configuration: {@Config}", newConfig);
            }
        }

        /// <summary>
        /// Load configuration from JSON file with environment variable overrides
        /// </summary>
        private static ServerConfiguration Load()
        {
            ServerConfiguration config;

            // Try to load from JSON file
            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    config = JsonSerializer.Deserialize<ServerConfiguration>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    }) ?? new ServerConfiguration();
                    
                    _logger?.LogInformation("Loaded configuration from {ConfigPath}", _configPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to load configuration from {ConfigPath}, using defaults", _configPath);
                    config = new ServerConfiguration();
                }
            }
            else
            {
                _logger?.LogWarning("Configuration file {ConfigPath} not found, using defaults", _configPath);
                config = new ServerConfiguration();
            }

            // Apply environment variable overrides
            config.ApplyEnvironmentVariables();

            return config;
        }

        /// <summary>
        /// Apply environment variable overrides (format: SECTION__PROPERTY)
        /// </summary>
        private void ApplyEnvironmentVariables()
        {
            // Server settings
            Server.Port = GetEnvUShort("Server__Port", Server.Port);
            Server.MaxPlayers = GetEnvInt("Server__MaxPlayers", Server.MaxPlayers);
            Server.BaseUrl = GetEnvString("Server__BaseUrl", Server.BaseUrl) ?? 
                            GetEnvString("OVS_SERVER", Server.BaseUrl) ?? 
                            GetEnvString("mvsi_server", Server.BaseUrl) ?? "";
            Server.HostName = GetEnvString("Server__HostName", Server.HostName) ?? "";

            // Performance settings
            Performance.SpinThresholdMicroseconds = GetEnvInt("Performance__SpinThresholdMicroseconds", Performance.SpinThresholdMicroseconds);
            Performance.UseAdaptiveSpinThreshold = GetEnvBool("Performance__UseAdaptiveSpinThreshold", Performance.UseAdaptiveSpinThreshold);
            Performance.MetricsSamplingInterval = GetEnvInt("Performance__MetricsSamplingInterval", Performance.MetricsSamplingInterval);
            Performance.TargetFrameRate = GetEnvInt("Performance__TargetFrameRate", Performance.TargetFrameRate);
            Performance.GarbageCollectionFreeRAMThreshold = GetEnvInt("Performance__GarbageCollectionFreeRAMThreshold", Performance.GarbageCollectionFreeRAMThreshold) * 1024 * 1024;

            // Networking settings
            Networking.ReceiveBufferSize = GetEnvInt("Networking__ReceiveBufferSize", Networking.ReceiveBufferSize);
            Networking.SendBufferSize = GetEnvInt("Networking__SendBufferSize", Networking.SendBufferSize);
            Networking.DscpValue = GetEnvInt("Networking__DscpValue", Networking.DscpValue);
            Networking.DontFragment = GetEnvBool("Networking__DontFragment", Networking.DontFragment);
            Networking.HttpTimeoutSeconds = GetEnvInt("Networking__HttpTimeoutSeconds", Networking.HttpTimeoutSeconds);

            // Game logic settings
            GameLogic.DisconnectTimeoutSeconds = GetEnvInt("GameLogic__DisconnectTimeoutSeconds", GameLogic.DisconnectTimeoutSeconds);
            GameLogic.MaxInputsPerFrame = GetEnvByte("GameLogic__MaxInputsPerFrame", GameLogic.MaxInputsPerFrame);
            GameLogic.InputHistoryFrames = GetEnvUInt("GameLogic__InputHistoryFrames", GameLogic.InputHistoryFrames);
            GameLogic.InputCleanupInterval = GetEnvUInt("GameLogic__InputCleanupInterval", GameLogic.InputCleanupInterval);
            GameLogic.MinimumInputFrames = GetEnvInt("GameLogic__MinimumInputFrames", GameLogic.MinimumInputFrames);

            // Rift calculation settings
            RiftCalculation.PingAlpha = GetEnvFloat("RiftCalculation__PingAlpha", RiftCalculation.PingAlpha);
            RiftCalculation.RiftAlpha = GetEnvFloat("RiftCalculation__RiftAlpha", RiftCalculation.RiftAlpha);
            RiftCalculation.MaxRiftDeviation = GetEnvFloat("RiftCalculation__MaxRiftDeviation", RiftCalculation.MaxRiftDeviation);
            RiftCalculation.TargetRift = GetEnvFloat("RiftCalculation__TargetRift", RiftCalculation.TargetRift);
            RiftCalculation.RiftUpdateInterval = GetEnvUInt("RiftCalculation__RiftUpdateInterval", RiftCalculation.RiftUpdateInterval);
            RiftCalculation.RiftUpdateThreshold = GetEnvUInt("RiftCalculation__RiftUpdateThreshold", RiftCalculation.RiftUpdateThreshold);
            RiftCalculation.UseAggressiveCorrection = GetEnvBool("RiftCalculation__UseAggressiveCorrection", RiftCalculation.UseAggressiveCorrection);

            // Ping phase settings
            PingPhase.TotalPings = GetEnvUInt("PingPhase__TotalPings", PingPhase.TotalPings);
            PingPhase.PingIntervalMilliseconds = GetEnvInt("PingPhase__PingIntervalMilliseconds", PingPhase.PingIntervalMilliseconds);

            // Input validation settings
            InputValidation.EnableRateLimiting = GetEnvBool("InputValidation__EnableRateLimiting", InputValidation.EnableRateLimiting);
            InputValidation.MaxInputsPerSecond = GetEnvInt("InputValidation__MaxInputsPerSecond", InputValidation.MaxInputsPerSecond);
            InputValidation.InputLookaheadFrames = GetEnvUInt("InputValidation__InputLookaheadFrames", InputValidation.InputLookaheadFrames);
            InputValidation.InputLookbackFrames = GetEnvUInt("InputValidation__InputLookbackFrames", InputValidation.InputLookbackFrames);

            // Desync detection settings
            DesyncDetection.EnableDesyncDetection = GetEnvBool("DesyncDetection__EnableDesyncDetection", DesyncDetection.EnableDesyncDetection);
            DesyncDetection.ChecksumRetentionFrames = GetEnvUInt("DesyncDetection__ChecksumRetentionFrames", DesyncDetection.ChecksumRetentionFrames);
            DesyncDetection.ChecksumCleanupInterval = GetEnvUInt("DesyncDetection__ChecksumCleanupInterval", DesyncDetection.ChecksumCleanupInterval);
            DesyncDetection.MaxDesyncCount = GetEnvInt("DesyncDetection__MaxDesyncCount", DesyncDetection.MaxDesyncCount);

            // Logging settings
            Logging.MinimumLevel = GetEnvString("Logging__MinimumLevel", Logging.MinimumLevel) ?? "Information";
            Logging.EnableMetrics = GetEnvBool("Logging__EnableMetrics", Logging.EnableMetrics);
            Logging.EnableConsoleMetrics = GetEnvBool("Logging__EnableMetrics", Logging.EnableConsoleMetrics);
            Logging.EnableDebugLogs = GetEnvBool("Logging__EnableDebugLogs", Logging.EnableDebugLogs);
            Logging.LogTickPerformance = GetEnvBool("Logging__LogTickPerformance", Logging.LogTickPerformance);
            Logging.TickPerformanceInterval = GetEnvInt("Logging__TickPerformanceInterval", Logging.TickPerformanceInterval);

            _logger?.LogDebug("Environment variables applied to configuration");
        }

        // Helper methods for environment variable parsing
        private static string? GetEnvString(string key, string? defaultValue)
            => Environment.GetEnvironmentVariable(key) ?? defaultValue;

        private static int GetEnvInt(string key, int defaultValue)
            => int.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        private static uint GetEnvUInt(string key, uint defaultValue)
            => uint.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        private static ushort GetEnvUShort(string key, ushort defaultValue)
            => ushort.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        private static byte GetEnvByte(string key, byte defaultValue)
            => byte.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        private static float GetEnvFloat(string key, float defaultValue)
            => float.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        private static bool GetEnvBool(string key, bool defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return value.ToLowerInvariant() is "true" or "1" or "yes" or "on";
        }
    }

    // Configuration section classes
    public class ServerSettings
    {
        public ushort Port { get; set; } = 8080;
        public int MaxPlayers { get; set; } = 6;
        public string BaseUrl { get; set; } = "";
        public string HostName { get; set; } = "";
    }

    public class PerformanceSettings
    {
        public int SpinThresholdMicroseconds { get; set; } = 500;
        public bool UseAdaptiveSpinThreshold { get; set; } = true;
        public int MetricsSamplingInterval { get; set; } = 10;
        public int TargetFrameRate { get; set; } = 60;
        public int GarbageCollectionFreeRAMThreshold { get; set; } = 1024 * 1024 * 1024;
    }

    public class NetworkingSettings
    {
        public int ReceiveBufferSize { get; set; } = 65536;
        public int SendBufferSize { get; set; } = 65536;
        public int DscpValue { get; set; } = 46;
        public bool DontFragment { get; set; } = true;
        public int HttpTimeoutSeconds { get; set; } = 5;
    }

    public class GameLogicSettings
    {
        public int DisconnectTimeoutSeconds { get; set; } = 45;
        public byte MaxInputsPerFrame { get; set; } = 30;
        public uint InputHistoryFrames { get; set; } = 150;
        public uint InputCleanupInterval { get; set; } = 200;
        public int MinimumInputFrames { get; set; } = 5;
    }

    public class RiftCalculationSettings
    {
        public float PingAlpha { get; set; } = 0.15f;
        public float RiftAlpha { get; set; } = 0.08f;
        public float MaxRiftDeviation { get; set; } = 20.0f;
        public float TargetRift { get; set; } = 0.5f;
        public uint RiftUpdateInterval { get; set; } = 10;
        public uint RiftUpdateThreshold { get; set; } = 500;
        public bool UseAggressiveCorrection { get; set; } = true;
    }

    public class PingPhaseSettings
    {
        public uint TotalPings { get; set; } = 20;
        public int PingIntervalMilliseconds { get; set; } = 50;
    }

    public class InputValidationSettings
    {
        public bool EnableRateLimiting { get; set; } = true;
        public int MaxInputsPerSecond { get; set; } = 120;
        public uint InputLookaheadFrames { get; set; } = 100;
        public uint InputLookbackFrames { get; set; } = 200;
    }

    public class DesyncDetectionSettings
    {
        public bool EnableDesyncDetection { get; set; } = true;
        public uint ChecksumRetentionFrames { get; set; } = 300;
        public uint ChecksumCleanupInterval { get; set; } = 200;
        public int MaxDesyncCount { get; set; } = 10;
    }

    public class LoggingSettings
    {
        public string MinimumLevel { get; set; } = "Information";
        public bool EnableMetrics { get; set; } = true;
        public bool EnableConsoleMetrics { get; set; } = false;
        public bool EnableDebugLogs { get; set; } = false;
        public bool LogTickPerformance { get; set; } = true;
        public int TickPerformanceInterval { get; set; } = 500;
    }
}
