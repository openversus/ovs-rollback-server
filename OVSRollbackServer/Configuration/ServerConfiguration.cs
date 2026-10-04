// ServerConfiguration.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        /// <summary>P2P node settings (OVS.Rollback.Node); the server itself never reads them.</summary>
        public NodeSettings Node { get; set; } = new();

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

                    config = JsonSerializer.Deserialize(json, OVSJsonContext.Default.ServerConfiguration) ?? BootStrapEnvVariables();

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
            serverSettings.BaseUrl = GetFirstEnvString(BaseUrlEnvKeys) ?? serverSettings.BaseUrl ?? "";
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
            // Megabytes, like the appsettings value and the env var. The fallback is the value
            // already loaded from configuration, so an unset env var leaves the file's value alone.
            performanceSettings.GarbageCollectionFreeRAMThreshold = GetEnvInt(
                "Performance__GarbageCollectionFreeRAMThreshold", performanceSettings.GarbageCollectionFreeRAMThreshold);

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
            gameLogicSettings.MissToleranceFrames = GetEnvUInt("GameLogic__MissToleranceFrames", gameLogicSettings.MissToleranceFrames);

            // Rift calculation settings
            riftCalculationSettings.PingAlpha = GetEnvFloat("RiftCalculation__PingAlpha", riftCalculationSettings.PingAlpha);
            riftCalculationSettings.RiftAlpha = GetEnvFloat("RiftCalculation__RiftAlpha", riftCalculationSettings.RiftAlpha);
            riftCalculationSettings.MaxRiftDeviation = GetEnvFloat("RiftCalculation__MaxRiftDeviation", riftCalculationSettings.MaxRiftDeviation);
            riftCalculationSettings.TargetRift = GetEnvFloat("RiftCalculation__TargetRift", riftCalculationSettings.TargetRift);
            riftCalculationSettings.RiftUpdateInterval = GetEnvUInt("RiftCalculation__RiftUpdateInterval", riftCalculationSettings.RiftUpdateInterval);
            riftCalculationSettings.RiftUpdateThreshold = GetEnvUInt("RiftCalculation__RiftUpdateThreshold", riftCalculationSettings.RiftUpdateThreshold);
            riftCalculationSettings.UseAggressiveCorrection = GetEnvBool("RiftCalculation__UseAggressiveCorrection", riftCalculationSettings.UseAggressiveCorrection);
            riftCalculationSettings.Algorithm = GetEnvString("RiftCalculation__Algorithm", riftCalculationSettings.Algorithm) ?? "ClientMatched";
            riftCalculationSettings.ReportedPing = GetEnvString("RiftCalculation__ReportedPing", riftCalculationSettings.ReportedPing) ?? "Raw";
            riftCalculationSettings.PeakPingWindow = GetEnvUInt("RiftCalculation__PeakPingWindow", riftCalculationSettings.PeakPingWindow);
            riftCalculationSettings.MaxRiftDeviationBoost = GetEnvFloat("RiftCalculation__MaxRiftDeviationBoost", riftCalculationSettings.MaxRiftDeviationBoost);
            riftCalculationSettings.MaxRiftDeviationBoostAbove = GetEnvFloat("RiftCalculation__MaxRiftDeviationBoostAbove", riftCalculationSettings.MaxRiftDeviationBoostAbove);
            riftCalculationSettings.HysteresisEnter = GetEnvFloat("RiftCalculation__HysteresisEnter", riftCalculationSettings.HysteresisEnter);
            riftCalculationSettings.HysteresisExit = GetEnvFloat("RiftCalculation__HysteresisExit", riftCalculationSettings.HysteresisExit);
            riftCalculationSettings.SmallErrorAlpha = GetEnvFloat("RiftCalculation__SmallErrorAlpha", riftCalculationSettings.SmallErrorAlpha);
            riftCalculationSettings.SmallErrorBelow = GetEnvFloat("RiftCalculation__SmallErrorBelow", riftCalculationSettings.SmallErrorBelow);

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
            Server.BaseUrl = GetFirstEnvString(BaseUrlEnvKeys) ?? Server.BaseUrl ?? "";
            Server.HostName = GetEnvString("Server__HostName", Server.HostName) ?? "";
            Server.FireMatchEvents = GetEnvBool("Server__FireMatchEvents", Server.FireMatchEvents);
            Server.MatchUpdateKey = GetEnvString("Server__MatchUpdateKey", Server.MatchUpdateKey) ?? "MisconfiguredMatchUpdateKey";

            // Node settings (P2P)
            Node.Rendezvous = GetEnvString("Node__Rendezvous", Node.Rendezvous) ?? "";
            Node.RelayFallback = GetEnvString("Node__RelayFallback", Node.RelayFallback) ?? "";
            Node.ForceRole = GetEnvString("Node__ForceRole", Node.ForceRole) ?? "";
            Node.PunchTimeoutSeconds = GetEnvInt("Node__PunchTimeoutSeconds", Node.PunchTimeoutSeconds);
            Node.PunchDeadlineSeconds = GetEnvInt("Node__PunchDeadlineSeconds", Node.PunchDeadlineSeconds);
            Node.ForwarderGraceSeconds = GetEnvInt("Node__ForwarderGraceSeconds", Node.ForwarderGraceSeconds);
            Node.ProbeIntervalMilliseconds = GetEnvInt("Node__ProbeIntervalMilliseconds", Node.ProbeIntervalMilliseconds);
            Node.RegisterIntervalMilliseconds = GetEnvInt("Node__RegisterIntervalMilliseconds", Node.RegisterIntervalMilliseconds);
            Node.KeepAliveIntervalMilliseconds = GetEnvInt("Node__KeepAliveIntervalMilliseconds", Node.KeepAliveIntervalMilliseconds);
            Node.PeerTimeoutSeconds = GetEnvInt("Node__PeerTimeoutSeconds", Node.PeerTimeoutSeconds);
            Node.GameTimeoutSeconds = GetEnvInt("Node__GameTimeoutSeconds", Node.GameTimeoutSeconds);
            Node.PortFile = GetEnvString("Node__PortFile", Node.PortFile) ?? "";
            Node.ParentToken = GetEnvString("Node__ParentToken", Node.ParentToken) ?? "";
            Node.ParentTimeoutSeconds = GetEnvInt("Node__ParentTimeoutSeconds", Node.ParentTimeoutSeconds);
            Server.VerboseLogging = GetEnvBool("Server__VerboseLogging", Server.VerboseLogging);
            Server.MementoMori = GetEnvBool("Server__MementoMori", Server.MementoMori);

            // Performance settings
            Performance.SpinThresholdMicroseconds = GetEnvInt("Performance__SpinThresholdMicroseconds", Performance.SpinThresholdMicroseconds);
            Performance.UseAdaptiveSpinThreshold = GetEnvBool("Performance__UseAdaptiveSpinThreshold", Performance.UseAdaptiveSpinThreshold);
            Performance.MetricsSamplingInterval = GetEnvInt("Performance__MetricsSamplingInterval", Performance.MetricsSamplingInterval);
            Performance.TargetFrameRate = GetEnvInt("Performance__TargetFrameRate", Performance.TargetFrameRate);
            // Env var is in MB; default 1024 MB (see note above).
            Performance.GarbageCollectionFreeRAMThreshold = GetEnvInt(
                "Performance__GarbageCollectionFreeRAMThreshold", Performance.GarbageCollectionFreeRAMThreshold);

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
            GameLogic.MissToleranceFrames = GetEnvUInt("GameLogic__MissToleranceFrames", GameLogic.MissToleranceFrames);

            // Rift calculation settings
            RiftCalculation.PingAlpha = GetEnvFloat("RiftCalculation__PingAlpha", RiftCalculation.PingAlpha);
            RiftCalculation.RiftAlpha = GetEnvFloat("RiftCalculation__RiftAlpha", RiftCalculation.RiftAlpha);
            RiftCalculation.MaxRiftDeviation = GetEnvFloat("RiftCalculation__MaxRiftDeviation", RiftCalculation.MaxRiftDeviation);
            RiftCalculation.TargetRift = GetEnvFloat("RiftCalculation__TargetRift", RiftCalculation.TargetRift);
            RiftCalculation.RiftUpdateInterval = GetEnvUInt("RiftCalculation__RiftUpdateInterval", RiftCalculation.RiftUpdateInterval);
            RiftCalculation.RiftUpdateThreshold = GetEnvUInt("RiftCalculation__RiftUpdateThreshold", RiftCalculation.RiftUpdateThreshold);
            RiftCalculation.UseAggressiveCorrection = GetEnvBool("RiftCalculation__UseAggressiveCorrection", RiftCalculation.UseAggressiveCorrection);
            RiftCalculation.Algorithm = GetEnvString("RiftCalculation__Algorithm", RiftCalculation.Algorithm) ?? "ClientMatched";
            RiftCalculation.ReportedPing = GetEnvString("RiftCalculation__ReportedPing", RiftCalculation.ReportedPing) ?? "Raw";
            RiftCalculation.PeakPingWindow = GetEnvUInt("RiftCalculation__PeakPingWindow", RiftCalculation.PeakPingWindow);
            RiftCalculation.MaxRiftDeviationBoost = GetEnvFloat("RiftCalculation__MaxRiftDeviationBoost", RiftCalculation.MaxRiftDeviationBoost);
            RiftCalculation.MaxRiftDeviationBoostAbove = GetEnvFloat("RiftCalculation__MaxRiftDeviationBoostAbove", RiftCalculation.MaxRiftDeviationBoostAbove);
            RiftCalculation.HysteresisEnter = GetEnvFloat("RiftCalculation__HysteresisEnter", RiftCalculation.HysteresisEnter);
            RiftCalculation.HysteresisExit = GetEnvFloat("RiftCalculation__HysteresisExit", RiftCalculation.HysteresisExit);
            RiftCalculation.SmallErrorAlpha = GetEnvFloat("RiftCalculation__SmallErrorAlpha", RiftCalculation.SmallErrorAlpha);
            RiftCalculation.SmallErrorBelow = GetEnvFloat("RiftCalculation__SmallErrorBelow", RiftCalculation.SmallErrorBelow);
            RiftCalculation.HostPingParity = GetEnvBool("RiftCalculation__HostPingParity", RiftCalculation.HostPingParity);

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
            Logging.RiftReportEveryFrames = GetEnvUInt("Logging__RiftReportEveryFrames", Logging.RiftReportEveryFrames);

            _logger?.LogDebug("{LogPrefix} Environment variables applied to configuration", LogPrefix);
        }

        // Env vars that set the matchmaker base URL, highest priority first. OVS_SERVER and
        // mvsi_server are the legacy names; mvsi_server also selects the MVSI endpoints.
        internal static readonly string[] BaseUrlEnvKeys = ["Server__BaseUrl", "OVS_SERVER", "mvsi_server"];

        // Helper methods for environment variable parsing
        protected internal static string? GetEnvString(string key, string? defaultValue)
            => Environment.GetEnvironmentVariable(key) ?? defaultValue;

        /// <summary>
        /// Returns the first of <paramref name="keys"/> whose environment variable is set to a
        /// non-blank value, or null. Unlike chaining GetEnvString with ??, a set-but-empty
        /// variable or a config default of "" does not stop the search.
        /// </summary>
        protected internal static string? GetFirstEnvString(params string[] keys)
        {
            foreach (var key in keys)
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }

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

    /// <summary>
    /// Settings for the P2P node (OVS.Rollback.Node), the process on a player's machine that the game
    /// connects to as its rollback server. The node reads them; the server ignores the section.
    /// </summary>
    public class NodeSettings
    {
        /// <summary>The rendezvous service, as host:port (UDP). Empty disables P2P: every match is forwarded to RelayFallback.</summary>
        public string Rendezvous { get; set; } = String.Empty;
        /// <summary>
        /// Bench only: a fixed relay (host:port) used when no peer path opens, instead of asking the server for
        /// one (/ovs_p2p_failed, which deploys the match's relay and answers with its address). Empty on players.
        /// </summary>
        public string RelayFallback { get; set; } = String.Empty;
        /// <summary>Testing only: "host" or "forwarder" takes the role regardless of the match config; empty follows is_host.</summary>
        public string ForceRole { get; set; } = String.Empty;
        /// <summary>Seconds of probing, counted from when a peer's address is known, before it is given up on.</summary>
        public int PunchTimeoutSeconds { get; set; } = 8;
        /// <summary>
        /// Seconds after the local game first connected by which every peer must have been reached, else
        /// RelayFallback; a peer whose game is still on the perk screen has not registered yet. The game itself
        /// waits 45 s for its server, so this leaves it time to reach the relay.
        /// </summary>
        public int PunchDeadlineSeconds { get; set; } = 30;
        /// <summary>
        /// Seconds past PunchDeadlineSeconds after which a forwarder whose path to the host opened but which has had
        /// nothing for its game from the host's node falls back too (the host falls back at its deadline and tells its
        /// peers; this covers a lost notice or a host that died). Must leave the game time to reach the relay within
        /// its own 45 s.
        /// </summary>
        public int ForwarderGraceSeconds { get; set; } = 5;
        /// <summary>Milliseconds between probes to each candidate address while punching.</summary>
        public int ProbeIntervalMilliseconds { get; set; } = 100;
        /// <summary>Milliseconds between registrations with the rendezvous until every peer is known.</summary>
        public int RegisterIntervalMilliseconds { get; set; } = 500;
        /// <summary>Milliseconds between keepalives on an open peer path (keeps the NAT mapping during pre-match waits).</summary>
        public int KeepAliveIntervalMilliseconds { get; set; } = 1000;
        /// <summary>Seconds without anything from a peer before its path is considered lost.</summary>
        public int PeerTimeoutSeconds { get; set; } = 30;
        /// <summary>Seconds without anything from the local game before the node forgets the match and waits for the next.</summary>
        public int GameTimeoutSeconds { get; set; } = 60;
        /// <summary>
        /// A file to write the UDP port the node actually listens on (one line, the number), once bound. The mod
        /// that starts the node reads it and reports the port to the server. Empty writes nothing.
        /// </summary>
        public string PortFile { get; set; } = String.Empty;
        /// <summary>
        /// The watchdog token, a decimal unsigned 64-bit number. When set, the process that started the node must
        /// send a Parent keepalive carrying it (P2P protocol, over loopback) at least every ParentTimeoutSeconds, or
        /// the node exits: a game that crashed takes its node with it. Empty means no watchdog.
        /// </summary>
        public string ParentToken { get; set; } = String.Empty;
        /// <summary>Seconds without a Parent keepalive before the node exits (when ParentToken is set).</summary>
        public int ParentTimeoutSeconds { get; set; } = 10;
    }

    public class PerformanceSettings
    {
        public int SpinThresholdMicroseconds { get; set; } = 500;
        public bool UseAdaptiveSpinThreshold { get; set; } = true;
        public int MetricsSamplingInterval { get; set; } = 10;
        public int TargetFrameRate { get; set; } = 60;
        /// <summary>
        /// Free RAM below which a garbage collection is worth forcing, **in megabytes** — the same
        /// unit as `appsettings.json` and the `Performance__GarbageCollectionFreeRAMThreshold`
        /// environment variable. A consumer needs bytes: multiply by `1024L * 1024L`, in long,
        /// because megabytes above 2047 overflow a 32-bit byte count.
        /// </summary>
        public int GarbageCollectionFreeRAMThreshold { get; set; } = 1024;
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
        /// <summary>Ticks to resend a peer's last acked input before predicting its next frames.</summary>
        public uint MissToleranceFrames { get; set; } = 10;
    }

    public class RiftCalculationSettings
    {
        public float PingAlpha { get; set; } = 0.15f;
        /// <summary>
        /// Ping sent to clients, which they use to raise (never lower) their input delay:
        /// "Raw" (the latest round trip; the original behaviour), "Smoothed" (SmoothedPing, which
        /// removes spikes and so tends to leave input delay lower), or "Peak" (the highest round trip
        /// over the last PeakPingWindow acks). Unrecognised values mean Raw.
        /// </summary>
        public string ReportedPing { get; set; } = "Raw";
        public uint PeakPingWindow { get; set; } = 60;
        public float RiftAlpha { get; set; } = 0.08f;
        /// <summary>
        /// Normal limit on the reported rift, in frames. 10 is where the game client's correction gain
        /// triples; past it corrections overshoot (see Core/Rift/RiftClamp).
        /// </summary>
        public float MaxRiftDeviation { get; set; } = 10.0f;
        /// <summary>Limit allowed while the error exceeds MaxRiftDeviationBoostAbove. Must stay below 50 (client disconnect).</summary>
        public float MaxRiftDeviationBoost { get; set; } = 20.0f;
        public float MaxRiftDeviationBoostAbove { get; set; } = 60.0f;
        public float TargetRift { get; set; } = 0.5f;
        public uint RiftUpdateInterval { get; set; } = 10;
        public uint RiftUpdateThreshold { get; set; } = 500;
        public bool UseAggressiveCorrection { get; set; } = true;
        /// <summary>
        /// "ClientMatched" (the default; Core/Rift/ClientMatchedRiftAlgorithm) or "Legacy" (the original
        /// OVS smoothing). An unrecognised name falls back to the default, with a warning at startup.
        /// </summary>
        public string Algorithm { get; set; } = "ClientMatched";
        // ClientMatched only
        public float HysteresisEnter { get; set; } = 1.25f;
        public float HysteresisExit { get; set; } = 0.75f;
        public float SmallErrorAlpha { get; set; } = 0.2f;
        public float SmallErrorBelow { get; set; } = 3.0f;
        /// <summary>
        /// P2P host fairness. A game on the same machine as the engine (loopback) is told the slowest remote
        /// player's round trip as its ping, so it raises its input delay as far as they do. The games' clocks are
        /// already aligned by the rift (the half-ping term in the rift only undoes the staleness of a remote
        /// client's reported frame); measured 2026-10-03 at 80 and 160 ms round trips: with equal input delay both
        /// sides hold the other's inputs the same number of frames ahead, and shifting the host's clock as well
        /// made it worse by half the round trip. Off for a relay.
        /// </summary>
        public bool HostPingParity { get; set; } = false;
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
        /// <summary>
        /// When true, a player exceeding MaxDesyncCount is sent a Kick message and
        /// marked disconnected. When false (default), the kick is only logged —
        /// log-only mode for validating detection before enforcement.
        /// </summary>
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
        /// <summary>
        /// Every N server frames, log each player's clock against the engine's: raw rift, smoothed rift, the
        /// reported rift and ping, and the frames. 0 (the default) logs nothing; a bench node sets 300.
        /// </summary>
        public uint RiftReportEveryFrames { get; set; } = 0;
    }
}
