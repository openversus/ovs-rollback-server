// ServerConfiguration.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

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
        public static readonly string LogPrefix = Utilities.LogPrefix;

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

        public ServerConfiguration(ILogger<ServerConfiguration> logger) {
            _logger = logger;
            Init();
        }

        [JsonConstructor]
        public ServerConfiguration()
        {
        }

        public ServerConfiguration(ILogger<ServerConfiguration> logger, string? configPath = "")
        {
            _logger = logger;
            _configPath = configPath.NotNullOrEmpty ? configPath! : _configPath;
            Init();
        }

        public ServerConfiguration(ILogger logger, string? configPath = "")
        {
            _logger = logger;
            _configPath = configPath.NotNullOrEmpty ? configPath! : _configPath;
            Init();
        }

        private void Init()
        {
            lock (_lock)
            {
                _instance = Load();
            }
        }

        /// <summary>
        /// Initialize configuration with logger
        /// </summary>
        public static void Initialize(ILogger logger, string? configPath = "")
        {
            _logger = logger;
            if (configPath.NotNullOrEmpty)
                // Null-forgiving operator is required here because apparently Roslyn is drunk
                _configPath = configPath!;
            
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
                _logger?.LogInformation("{LogPrefix} Reloading configuration from {ConfigPath}", LogPrefix, _configPath);
                var newConfig = Load();
                _instance = newConfig;
                _logger?.LogInformation("{LogPrefix} Configuration reloaded successfully", LogPrefix);

                if (_logger?.IsEnabled(LogLevel.Information) == true)
                {
                    _logger.LogInformation("{LogPrefix} New configuration: {@Config}", LogPrefix, newConfig);
                }
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

                    config = JsonConvert.DeserializeObject<ServerConfiguration>(json) ?? BootStrapEnvVariables();

                    _logger?.LogInformation("{LogPrefix} Loaded configuration from {ConfigPath}", LogPrefix, _configPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "{LogPrefix} Failed to load configuration from {ConfigPath}, using defaults", LogPrefix, _configPath);
                    config = BootStrapEnvVariables();
                }
            }
            else
            {
                _logger?.LogWarning("{LogPrefix} Configuration file {ConfigPath} not found, using defaults", LogPrefix, _configPath);
                config = BootStrapEnvVariables();
            }

            // Apply environment variable overrides
            config.ApplyEnvironmentVariables();

            return config;
        }

        public ServerConfiguration(
            ILogger<ServerConfiguration> logger,
            ServerSettings _server,
            PerformanceSettings _performanceSettings,
            NetworkingSettings _networkingSettings,
            GameLogicSettings _gameLogicSettings,
            RiftCalculationSettings _riftCalculationSettings,
            PingPhaseSettings _pingPhaseSettings,
            InputValidationSettings _inputValidationSettings,
            DesyncDetectionSettings _desyncDetectionSettings,
            LoggingSettings _loggingSettings
            )
        {
            _logger = logger;
            Server = _server;
            Performance = _performanceSettings;
            Networking = _networkingSettings;
            GameLogic = _gameLogicSettings;
            RiftCalculation = _riftCalculationSettings;
            PingPhase = _pingPhaseSettings;
            InputValidation = _inputValidationSettings;
            DesyncDetection = _desyncDetectionSettings;
            Logging = _loggingSettings;
        }
        private static ServerConfiguration BootStrapEnvVariables()
        {
            ILogger<ServerConfiguration> logger = Utilities.NewLogger<ServerConfiguration>();
            ServerSettings serverSettings = new ServerSettings();
            PerformanceSettings performanceSettings = new PerformanceSettings();
            NetworkingSettings networkingSettings = new NetworkingSettings();
            GameLogicSettings gameLogicSettings = new GameLogicSettings();
            RiftCalculationSettings riftCalculationSettings = new RiftCalculationSettings();
            PingPhaseSettings pingPhaseSettings = new PingPhaseSettings();
            InputValidationSettings inputValidationSettings = new InputValidationSettings();
            DesyncDetectionSettings desyncDetectionSettings = new DesyncDetectionSettings();
            LoggingSettings loggingSettings = new LoggingSettings();


            // Server settings
            serverSettings.Port = GetEnvUShort("Server__Port", serverSettings.Port);
            serverSettings.MaxPlayers = GetEnvInt("Server__MaxPlayers", serverSettings.MaxPlayers);
            serverSettings.BaseUrl = GetEnvString("Server__BaseUrl", serverSettings.BaseUrl) ??
                            GetEnvString("OVS_SERVER", serverSettings.BaseUrl) ??
                            GetEnvString("mvsi_server", serverSettings.BaseUrl) ?? "";
            serverSettings.HostName = GetEnvString("Server__HostName", serverSettings.HostName) ?? "";
            serverSettings.FireMatchEvents = GetEnvBool("Server__FireMatchEvents", serverSettings.FireMatchEvents);
            serverSettings.MatchUpdateKey = GetEnvString("Server__MatchUpdateKey", serverSettings.MatchUpdateKey) ?? "MisconfiguredMatchUpdateKey";
            serverSettings.VerboseLogging = GetEnvBool("Server__VerboseLogging", serverSettings.VerboseLogging);
            serverSettings.MementoMori = GetEnvBool("Server__MementoMori", serverSettings.MementoMori);

            // Performance settings
            performanceSettings.SpinThresholdMicroseconds = GetEnvInt("Performance__SpinThresholdMicroseconds", performanceSettings.SpinThresholdMicroseconds);
            performanceSettings.UseAdaptiveSpinThreshold = GetEnvBool("Performance__UseAdaptiveSpinThreshold", performanceSettings.UseAdaptiveSpinThreshold);
            performanceSettings.MetricsSamplingInterval = GetEnvInt("Performance__MetricsSamplingInterval", performanceSettings.MetricsSamplingInterval);
            performanceSettings.TargetFrameRate = GetEnvInt("Performance__TargetFrameRate", performanceSettings.TargetFrameRate);
            performanceSettings.GarbageCollectionFreeRAMThreshold = GetEnvInt("Performance__GarbageCollectionFreeRAMThreshold", performanceSettings.GarbageCollectionFreeRAMThreshold) * 1024 * 1024;

            // Networking settings
            networkingSettings.ReceiveBufferSize = GetEnvInt("Networking__ReceiveBufferSize", networkingSettings.ReceiveBufferSize);
            networkingSettings.SendBufferSize = GetEnvInt("Networking__SendBufferSize", networkingSettings.SendBufferSize);
            networkingSettings.DscpValue = GetEnvInt("Networking__DscpValue", networkingSettings.DscpValue);
            networkingSettings.DontFragment = GetEnvBool("Networking__DontFragment", networkingSettings.DontFragment);
            networkingSettings.HttpTimeoutSeconds = GetEnvInt("Networking__HttpTimeoutSeconds", networkingSettings.HttpTimeoutSeconds);

            // Game logic settings
            gameLogicSettings.DisconnectTimeoutSeconds = GetEnvInt("GameLogic__DisconnectTimeoutSeconds", gameLogicSettings.DisconnectTimeoutSeconds);
            gameLogicSettings.MaxInputsPerFrame = GetEnvByte("GameLogic__MaxInputsPerFrame", gameLogicSettings.MaxInputsPerFrame);
            gameLogicSettings.InputHistoryFrames = GetEnvUInt("GameLogic__InputHistoryFrames", gameLogicSettings.InputHistoryFrames);
            gameLogicSettings.InputCleanupInterval = GetEnvUInt("GameLogic__InputCleanupInterval", gameLogicSettings.InputCleanupInterval);
            gameLogicSettings.MinimumInputFrames = GetEnvInt("GameLogic__MinimumInputFrames", gameLogicSettings.MinimumInputFrames);

            // Rift calculation settings
            riftCalculationSettings.PingAlpha = GetEnvFloat("RiftCalculation__PingAlpha", riftCalculationSettings.PingAlpha);
            riftCalculationSettings.RiftAlpha = GetEnvFloat("RiftCalculation__RiftAlpha", riftCalculationSettings.RiftAlpha);
            riftCalculationSettings.MaxRiftDeviation = GetEnvFloat("RiftCalculation__MaxRiftDeviation", riftCalculationSettings.MaxRiftDeviation);
            riftCalculationSettings.TargetRift = GetEnvFloat("RiftCalculation__TargetRift", riftCalculationSettings.TargetRift);
            riftCalculationSettings.RiftUpdateInterval = GetEnvUInt("RiftCalculation__RiftUpdateInterval", riftCalculationSettings.RiftUpdateInterval);
            riftCalculationSettings.RiftUpdateThreshold = GetEnvUInt("RiftCalculation__RiftUpdateThreshold", riftCalculationSettings.RiftUpdateThreshold);
            riftCalculationSettings.UseAggressiveCorrection = GetEnvBool("RiftCalculation__UseAggressiveCorrection", riftCalculationSettings.UseAggressiveCorrection);
            riftCalculationSettings.FastConvergenceThreshold = GetEnvFloat("RiftCalculation__FastConvergenceThreshold", riftCalculationSettings.FastConvergenceThreshold);

            // Ping phase settings
            pingPhaseSettings.TotalPings = GetEnvUInt("PingPhase__TotalPings", pingPhaseSettings.TotalPings);
            pingPhaseSettings.PingIntervalMilliseconds = GetEnvInt("PingPhase__PingIntervalMilliseconds", pingPhaseSettings.PingIntervalMilliseconds);

            // Input validation settings
            inputValidationSettings.EnableRateLimiting = GetEnvBool("InputValidation__EnableRateLimiting", inputValidationSettings.EnableRateLimiting);
            inputValidationSettings.MaxInputsPerSecond = GetEnvInt("InputValidation__MaxInputsPerSecond", inputValidationSettings.MaxInputsPerSecond);
            inputValidationSettings.InputLookaheadFrames = GetEnvUInt("InputValidation__InputLookaheadFrames", inputValidationSettings.InputLookaheadFrames);
            inputValidationSettings.InputLookbackFrames = GetEnvUInt("InputValidation__InputLookbackFrames", inputValidationSettings.InputLookbackFrames);

            // Desync detection settings
            desyncDetectionSettings.EnableDesyncDetection = GetEnvBool("DesyncDetection__EnableDesyncDetection", desyncDetectionSettings.EnableDesyncDetection);
            desyncDetectionSettings.KickDesyncingPlayer = GetEnvBool("DesyncDetection__KickDesyncingPlayer", desyncDetectionSettings.KickDesyncingPlayer);
            desyncDetectionSettings.ChecksumRetentionFrames = GetEnvUInt("DesyncDetection__ChecksumRetentionFrames", desyncDetectionSettings.ChecksumRetentionFrames);
            desyncDetectionSettings.ChecksumCleanupInterval = GetEnvUInt("DesyncDetection__ChecksumCleanupInterval", desyncDetectionSettings.ChecksumCleanupInterval);
            desyncDetectionSettings.MaxDesyncCount = GetEnvInt("DesyncDetection__MaxDesyncCount", desyncDetectionSettings.MaxDesyncCount);

            // Logging settings
            loggingSettings.MinimumLevel = GetEnvString("Logging__MinimumLevel", loggingSettings.MinimumLevel) ?? "Information";
            loggingSettings.LogFilePath = GetEnvString("Logging__LogFilePath", loggingSettings.LogFilePath) ?? String.Empty;
            loggingSettings.LogArchivePath = GetEnvString("Logging__LogArchivePath", loggingSettings.LogArchivePath) ?? String.Empty;
            loggingSettings.EnableMetrics = GetEnvBool("Logging__EnableMetrics", loggingSettings.EnableMetrics);
            loggingSettings.EnableConsoleMetrics = GetEnvBool("Logging__EnableConsoleMetrics", loggingSettings.EnableConsoleMetrics);
            loggingSettings.EnableDebugLogs = GetEnvBool("Logging__EnableDebugLogs", loggingSettings.EnableDebugLogs);
            loggingSettings.LogTickPerformance = GetEnvBool("Logging__LogTickPerformance", loggingSettings.LogTickPerformance);
            loggingSettings.TickPerformanceInterval = GetEnvInt("Logging__TickPerformanceInterval", loggingSettings.TickPerformanceInterval);

            return new ServerConfiguration(
                logger,
                serverSettings,
                performanceSettings,
                networkingSettings,
                gameLogicSettings,
                riftCalculationSettings,
                pingPhaseSettings,
                inputValidationSettings,
                desyncDetectionSettings,
                loggingSettings
                );
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
            Server.FireMatchEvents = GetEnvBool("Server__FireMatchEvents", Server.FireMatchEvents);
            Server.MatchUpdateKey = GetEnvString("Server__MatchUpdateKey", Server.MatchUpdateKey) ?? "MisconfiguredMatchUpdateKey";
            Server.VerboseLogging = GetEnvBool("Server__VerboseLogging", Server.VerboseLogging);
            Server.MementoMori = GetEnvBool("Server__MementoMori", Server.MementoMori);

            // Performance settings
            Performance.SpinThresholdMicroseconds = GetEnvInt("Performance__SpinThresholdMicroseconds", Performance.SpinThresholdMicroseconds);
            Performance.UseAdaptiveSpinThreshold = GetEnvBool("Performance__UseAdaptiveSpinThreshold", Performance.UseAdaptiveSpinThreshold);
            Performance.MetricsSamplingInterval = GetEnvInt("Performance__MetricsSamplingInterval", Performance.MetricsSamplingInterval);
            Performance.TargetFrameRate = GetEnvInt("Performance__TargetFrameRate", Performance.TargetFrameRate);
            // Env var is in MB; default 1024 MB. Avoid re-multiplying the already-byte-valued field.
            Performance.GarbageCollectionFreeRAMThreshold = GetEnvInt("Performance__GarbageCollectionFreeRAMThreshold", 1024) * 1024 * 1024;

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
            GameLogic.MissToleranceFrames = GetEnvInt("GameLogic__MissToleranceFrames", GameLogic.MissToleranceFrames);

            // Rift calculation settings
            RiftCalculation.PingAlpha = GetEnvFloat("RiftCalculation__PingAlpha", RiftCalculation.PingAlpha);
            RiftCalculation.RiftAlpha = GetEnvFloat("RiftCalculation__RiftAlpha", RiftCalculation.RiftAlpha);
            RiftCalculation.MaxRiftDeviation = GetEnvFloat("RiftCalculation__MaxRiftDeviation", RiftCalculation.MaxRiftDeviation);
            RiftCalculation.TargetRift = GetEnvFloat("RiftCalculation__TargetRift", RiftCalculation.TargetRift);
            RiftCalculation.RiftUpdateInterval = GetEnvUInt("RiftCalculation__RiftUpdateInterval", RiftCalculation.RiftUpdateInterval);
            RiftCalculation.RiftUpdateThreshold = GetEnvUInt("RiftCalculation__RiftUpdateThreshold", RiftCalculation.RiftUpdateThreshold);
            RiftCalculation.UseAggressiveCorrection = GetEnvBool("RiftCalculation__UseAggressiveCorrection", RiftCalculation.UseAggressiveCorrection);
            RiftCalculation.FastConvergenceThreshold = GetEnvFloat("RiftCalculation__FastConvergenceThreshold", RiftCalculation.FastConvergenceThreshold);

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
            DesyncDetection.KickDesyncingPlayer = GetEnvBool("DesyncDetection__KickDesyncingPlayer", DesyncDetection.KickDesyncingPlayer);
            DesyncDetection.ChecksumRetentionFrames = GetEnvUInt("DesyncDetection__ChecksumRetentionFrames", DesyncDetection.ChecksumRetentionFrames);
            DesyncDetection.ChecksumCleanupInterval = GetEnvUInt("DesyncDetection__ChecksumCleanupInterval", DesyncDetection.ChecksumCleanupInterval);
            DesyncDetection.MaxDesyncCount = GetEnvInt("DesyncDetection__MaxDesyncCount", DesyncDetection.MaxDesyncCount);

            // Logging settings
            Logging.MinimumLevel = GetEnvString("Logging__MinimumLevel", Logging.MinimumLevel) ?? "Information";
            Logging.LogFilePath = GetEnvString("Logging__LogFilePath", Logging.LogFilePath) ?? String.Empty;
            Logging.LogArchivePath = GetEnvString("Logging__LogArchivePath", Logging.LogArchivePath) ?? String.Empty;
            Logging.EnableMetrics = GetEnvBool("Logging__EnableMetrics", Logging.EnableMetrics);
            Logging.EnableConsoleMetrics = GetEnvBool("Logging__EnableConsoleMetrics", Logging.EnableConsoleMetrics);
            Logging.EnableDebugLogs = GetEnvBool("Logging__EnableDebugLogs", Logging.EnableDebugLogs);
            Logging.LogTickPerformance = GetEnvBool("Logging__LogTickPerformance", Logging.LogTickPerformance);
            Logging.TickPerformanceInterval = GetEnvInt("Logging__TickPerformanceInterval", Logging.TickPerformanceInterval);

            _logger?.LogDebug("{LogPrefix} Environment variables applied to configuration", LogPrefix);
        }

        // Helper methods for environment variable parsing
        protected internal static string? GetEnvString(string key, string? defaultValue)
            => Environment.GetEnvironmentVariable(key) ?? defaultValue;

        protected internal static int GetEnvInt(string key, int defaultValue)
            => int.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        protected internal static uint GetEnvUInt(string key, uint defaultValue)
            => uint.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        protected internal static ushort GetEnvUShort(string key, ushort defaultValue)
            => ushort.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        protected internal static byte GetEnvByte(string key, byte defaultValue)
            => byte.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        protected internal static float GetEnvFloat(string key, float defaultValue)
            => float.TryParse(Environment.GetEnvironmentVariable(key), out var val) ? val : defaultValue;

        protected internal static bool GetEnvBool(string key, bool defaultValue)
        {
            string value = Environment.GetEnvironmentVariable(key) ?? string.Empty;
            if (value.StringIsNullOrEmpty)
            {
                return defaultValue;
            }
            return value.ToLowerInvariant() is "true" or "1" or "yes" or "on";
        }
    }

    // Configuration section classes
    public class ServerSettings
    {
        public ushort Port { get; set; } = 8080;
        public int MaxPlayers { get; set; } = 6;
        public string BaseUrl { get; set; } = String.Empty;
        public string HostName { get; set; } = String.Empty;
        public bool FireMatchEvents { get; set; } = true;
        public string MatchUpdateKey { get; set; } = String.Empty;
        public bool VerboseLogging { get; set; } = false;
        public bool MementoMori { get; set; } = true;
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
        public byte MaxInputsPerFrame { get; set; } = 120;
        public uint InputHistoryFrames { get; set; } = 150;
        public uint InputCleanupInterval { get; set; } = 200;
        public int MinimumInputFrames { get; set; } = 5;
        /// <summary>
        /// How many consecutive ticks the server will re-send a player's last known
        /// input before switching to <see cref="InputPredictor"/>. Lower values make
        /// opponents feel more responsive during packet loss at the cost of more
        /// rollbacks. 3 frames (~50ms at 60fps) is appropriate for platform fighters.
        /// </summary>
        public int MissToleranceFrames { get; set; } = 3;
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
        /// <summary>
        /// When |riftError| exceeds this threshold (in frames), the RiftUpdateInterval
        /// gate is bypassed so corrections are applied every tick until the player
        /// converges. This prevents sluggish correction for high-latency cross-region
        /// players (hi Jenettee, Pumba, Milkman, Dills, and Rainbows) where rift diverges
        /// faster than the update interval can track. Measured in frames.
        /// Default: 2.1 frames (~25ms at 60fps).
        /// </summary>
        public float FastConvergenceThreshold { get; set; } = 2.1f;
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
        public bool KickDesyncingPlayer { get; set; } = false;
        public uint ChecksumRetentionFrames { get; set; } = 300;
        public uint ChecksumCleanupInterval { get; set; } = 200;
        public int MaxDesyncCount { get; set; } = 10;
    }

    public class LoggingSettings
    {
        public string MinimumLevel { get; set; } = "Information";
        public string LogFilePath { get; set; } = String.Empty;
        public string LogArchivePath { get; set; } = String.Empty;
        public bool EnableMetrics { get; set; } = true;
        public bool EnableConsoleMetrics { get; set; } = false;
        public bool EnableDebugLogs { get; set; } = false;
        public bool LogTickPerformance { get; set; } = true;
        public int TickPerformanceInterval { get; set; } = 500;
    }
}
