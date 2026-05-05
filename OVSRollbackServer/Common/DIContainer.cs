using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OVS.Rollback.Common;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using OVS.Rollback.Utils;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;


namespace OVS.Rollback.Common
{
    /// <summary>
    /// Provides a container for configuring and managing dependency injection, application hosting, and logging
    /// services within the program. Serves as a central point for accessing service providers, host builders, and
    /// logging infrastructure.
    /// </summary>
    /// <remarks>
    /// The DIContainer class encapsulates the setup and lifetime management of core application
    /// services, including dependency injection, logging, and hosting. It is used to initialize and access
    /// shared services and infrastructure components throughout the program's lifecycle. Only one instance is
    /// intended to be active at a time, and an existing container will be reused if available.
    /// </remarks>
    public class DIContainer
    {
        private protected DateTime _creationTime;
        private protected IServiceCollection? _services;
        private protected IServiceProvider? _diServiceProvider;
        private protected IHostBuilder? _genericHostBuilder;
        private protected ILoggingBuilder? _loggingBuilder;
        private protected IHost? _genericHost;
        private protected ILogger<DIContainer>? _logger;
        private protected ILoggerFactory? _sharedLoggerFactory;
        private protected static CancellationTokenSource? _cts;
        private protected static HTTPHelper? _sharedHTTPHelper;
        private protected static Stopwatch? _sharedStopwatch;
        private static Serilog.ILogger? _rootLogger;
        private protected static LoggingColorRoot _loggingColorRoot = new LoggingColorRoot();
        private static readonly string LogPrefix = Utilities.GetLogPrefix<DIContainer>();

        /// <summary>
        /// Gets the root configured Serilog logger instance used for general program-wide logging.
        /// </summary>
        /// <remarks>Use this property to access the global logger for logging general messages throughout the
        /// program. Individual classes will typically grab an ILogger-abstracted class-specific instance manually via GetRequiredService
        /// or via constructor dependency injection.
        /// The returned instance may be null if the root logger has not been configured.</remarks>
        public static Serilog.ILogger? RootLoggerInstance
        {
            get
            {
                return _rootLogger;
            }
        }

        public static CancellationTokenSource CTS
        {
            get => _cts;
            private set => _cts = value;
        }

        internal static HTTPHelper SharedHTTPHelper
        {
            get => _sharedHTTPHelper;
            private set => _sharedHTTPHelper = value;
        }

        public static Stopwatch SharedStopwatch
        {
            get => _sharedStopwatch;
            private set => _sharedStopwatch = value;
        }

        /// <summary>
        /// Gets the date and time when the object was created.
        /// </summary>
        public DateTime CreationTime { get => _creationTime; }
        
        /// <summary>
        /// Gets the underlying generic host builder used to configure and build the program's DI host.
        /// </summary>
        public IHostBuilder? GenericHostBuilder
        {
            get => _genericHostBuilder;
            private set => _genericHostBuilder = value;
        }

        /// <summary>
        /// Gets the logging builder used to configure logging services for the program.
        /// </summary>
        public ILoggingBuilder? LoggingBuilder
        {
            get => _loggingBuilder;
            private set => _loggingBuilder = value;
        }

        /// <summary>
        /// Gets the underlying generic host instance used to manage the program DI container's lifetime and services.
        /// </summary>
        public IHost? GenericHost
        {
            get => _genericHost;
            private set => _genericHost = value;
        }

        /// <summary>
        /// Gets the collection of service descriptors for dependency injection configuration.
        /// </summary>
        public IServiceCollection? Services
        {
            get => _services;
            private set => _services = value;
        }

        /// <summary>
        /// Gets the dependency injection service provider used to resolve program DI services.
        /// </summary>
        public IServiceProvider? DIServiceProvider
        {
            get => _diServiceProvider;
            private set => _diServiceProvider = value;
        }

        /// <summary>
        /// Gets the shared logger factory instance used for creating loggers throughout the program.
        /// </summary>
        public ILoggerFactory? SharedLoggerFactory
        {
            get => _sharedLoggerFactory;
            private set => _sharedLoggerFactory = value;
        }

        /// <summary>
        /// Gets the logger instance used for recording diagnostic and operational messages for this DI container.
        /// </summary>
        public ILogger<DIContainer>? Logger
        {
            get => _logger;
            private set => _logger = value;
        }

        /// <summary>
        /// Initializes a new instance of the DIContainer class and sets up the program's dependency injection
        /// infrastructure.
        /// </summary>
        /// <remarks>This constructor configures the application's dependency injection container,
        /// logging, and console encoding. If a DIContainer instance already exists in
        /// Singletons.RollbackDIContainer, the constructor just returns without reinitializing the container
        /// or creating a duplicate. The constructor also starts the generic host asynchronously and makes the
        /// service provider and logger factory available for dependency resolution throughout the program.
        /// </remarks>
        public DIContainer()
        {
            if (Singletons.RollbackDIContainer != null)
            {
                return;
            }

            Utilities.CreateAndSetLogPath();
            Statics.PreLoggerMessages.Add($"{LogPrefix} Log file path: ${Utilities.LogPath}");
            Statics.PrematchMatchUpdateKey = ServerConfiguration.GetEnvString("Server__MatchUpdateKey", "DIMisconfiguredMatchUpdateKey") ?? "DIMisconfiguredMatchUpdateKey";

            _rootLogger ??= DICreateRootLogger();
            ParseCmdLine();

            _genericHostBuilder = BuildAppHost();
            _genericHost = GenericHost = _genericHostBuilder.Build();
            _genericHost.RunAsync();
            _diServiceProvider = DIServiceProvider = _genericHost.Services;
            _creationTime = DateTime.Now;
            _sharedLoggerFactory = _genericHost.Services.GetRequiredService<ILoggerFactory>();
            _cts = _genericHost.Services.GetRequiredService<CancellationTokenSource>();
            _sharedHTTPHelper = _genericHost.Services.GetRequiredService<HTTPHelper>();
            _sharedStopwatch = _genericHost.Services.GetRequiredService<Stopwatch>();
            _logger = _genericHost.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DIContainer>>();

            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            Singletons.RollbackDIContainer = this;
        }

        /// <summary>
        /// Configures and creates a new host builder with default settings, custom configuration, logging, and service
        /// registrations for the program.
        /// </summary>
        /// <remarks>The returned host builder includes Serilog integration for logging and registers
        /// several singleton services required by the program.
        /// </remarks>
        /// <returns>An <see cref="IHostBuilder"/> instance preconfigured with program-specific services, logging, and
        /// configuration providers.</returns>
        private IHostBuilder BuildAppHost()
        {
            string assmLocation = Assembly.GetExecutingAssembly().Location;
            string manifestName = Assembly.GetExecutingAssembly().ManifestModule.Name;
            string basePath = assmLocation.Replace(manifestName, "");

            IHostBuilder hostBuilder = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(c =>
                {
                    c.SetBasePath(basePath);
                })
                .ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory());
                })
                .ConfigureLogging((context, logging) =>
                {

                    logging.AddSerilog(Singletons.RootLoggerInstance, dispose: true)
                    .SetMinimumLevel(LogLevel.Information);
                })
                .ConfigureServices((context, services) =>
                {
                    services.AddSingleton<LoggingColorRoot>(_loggingColorRoot);
                    services.AddSingleton<CancellationTokenSource>();
                    services.AddSingleton<Stopwatch>();
                });
            
            Microsoft.Extensions.Logging.ILogger<ServerConfiguration> configLogger = new SerilogLoggerFactory().CreateLogger<ServerConfiguration>();
            ServerConfiguration config = new ServerConfiguration(configLogger);
            if (config.Server.MatchUpdateKey.StringIsNullOrWhiteSpace || config.Server.MatchUpdateKey == "DIMisconfiguredMatchUpdateKey" || config.Server.MatchUpdateKey == "MisconfiguredMatchUpdateKey")
            {
                Statics.PreLoggerMessages.Add($"{LogPrefix} Server__MatchUpdateKey is not set or is using the default placeholder value. Using default: {Statics.PrematchMatchUpdateKey}");
                config.Server.MatchUpdateKey = Statics.PrematchMatchUpdateKey;
            }
            //Server__MatchUpdateKey
            HttpClient httpClient = new HttpClient {
                Timeout = TimeSpan.FromSeconds(config.Networking.HttpTimeoutSeconds)
            };
            SetupMeterProvider(config);

            hostBuilder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<ServerConfiguration>(config);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<HTTPHelper>();
                services.AddSingleton<RollbackServer>();
                services.AddSingleton<MeterProvider>();
            });

            return hostBuilder;
        }

        /// <summary>
        /// Initializes the program's root Serilog logger instance and configures global logging context properties.
        /// </summary>
        /// <remarks>This method is intended to be called automatically during module initialization and
        /// should not be invoked directly. It sets up the root logger using configuration from a JSON file
        /// and establishes global context properties for logging. This ensures that logging is available and properly
        /// configured before any other code executes.
        /// 
        /// This is the configured logger instance from which all other logger instances should derive, either directly
        /// or via the Microsoft.Extensions.Logging abstractions.
        /// </remarks>
        [ModuleInitializer]
        internal static void CreateRootLogger()
        {
            // Force Utilities ModuleInitializer to run to ensure LogPath is set before logger configuration
            _ = Utilities.LogPath;

            string assmLocation = Assembly.GetExecutingAssembly().Location;
            string manifestName = Assembly.GetExecutingAssembly().ManifestModule.Name;
            string basePath = assmLocation.Replace(manifestName, "");
            ;
            var rootLoggerConfig = new ConfigurationBuilder()
                    .SetBasePath(basePath)
                    .AddJsonFile(basePath / "Configuration" / "Logging" / "Runtime" / "serilog-config.json")
                    .Build();

            var logPathKey = rootLoggerConfig
                    .AsEnumerable()
                    .FirstOrDefault(kvp => kvp.Value == "__DO_NOT_EDIT_PLACEHOLDER_PATH__")
                    .Key;

            if (!logPathKey.StringIsNullOrWhiteSpace)
            {
                rootLoggerConfig = new ConfigurationBuilder()
                    .SetBasePath(basePath)
                    .AddJsonFile(basePath / "Configuration" / "Logging" / "Runtime" / "serilog-config.json")
                    .AddInMemoryCollection(new Dictionary<string, string?> { [logPathKey] = Utilities.LogPath })
                    .Build();
            }

            Log.Logger = _rootLogger = Singletons._rootLogger = new LoggerConfiguration()
                .ReadFrom.Configuration(rootLoggerConfig)
                .CreateLogger();

            Dictionary<string, bool> globalContextItems = new Dictionary<string, bool>()
            {
                { "RootLoggerCreated", true }
            };

            //GlobalLogContext.PushProperty("Bools", globalContextItems);
        }

        /// <summary>
        /// Gets the root Serilog logger instance used for program-wide general logging.
        /// </summary>
        /// <returns>The root <see cref="Serilog.ILogger"/> instance for logging across the program.</returns>
        internal static Serilog.ILogger GetRootLogger()
        {
            return Singletons.RootLoggerInstance;
        }


        /// <summary>
        /// Initializes and returns the root Serilog logger instance. Private, class-specific method used as
        /// a wrapper around the static CreateRootLogger and GetRootLogger methods.
        /// </summary>
        /// <remarks>This method ensures that the root logger is created before returning it. The class
        /// calls this method to create & obtain the program's primary logger for logging operations.</remarks>
        /// <returns>A <see cref="Serilog.ILogger"/> representing the root logger. The same instance is returned on subsequent
        /// calls.
        /// </returns>
        private Serilog.ILogger DICreateRootLogger()
        {
            CreateRootLogger();
            return GetRootLogger();
        }

        private void ParseCmdLine()
        {
            string[] cmdLineArgsList = Environment.GetCommandLineArgs();
            RootLoggerInstance?.Information("{LogPrefix} Rollback server started with {Num} command line arguments: {Args}", LogPrefix, cmdLineArgsList.Length, cmdLineArgsList);

            if (cmdLineArgsList.Length == 1)
            {
                return;
            }

            Singletons.ConfigPath = cmdLineArgsList.Length > 0 && cmdLineArgsList[1].EndsWith(".json") ? cmdLineArgsList[1] : String.Empty;

            if (cmdLineArgsList.Length > 0 && !cmdLineArgsList[1].EndsWith(".json"))
            {
                if (ushort.TryParse(cmdLineArgsList[1], out var p))
                {
                    Singletons.Port = p;
                    RootLoggerInstance?.Information("{LogPrefix} Port overridden by command line: {Port}", LogPrefix, Singletons.Port);
                }
                else
                {
                    RootLoggerInstance?.Warning("{LogPrefix} Invalid port number. Using config: {Port}", LogPrefix, Singletons.Port);
                }
            }

            if (cmdLineArgsList.Length > 2)
            {
                if (int.TryParse(cmdLineArgsList[2], out var mp) && mp is > 0 and <= 8)
                {
                    Singletons.MaxPlayers = mp;
                    RootLoggerInstance?.Information("{LogPrefix} MaxPlayers overridden by command line: {MaxPlayers}", LogPrefix, Singletons.MaxPlayers);
                }
                else
                {
                    RootLoggerInstance?.Warning("{LogPrefix} Max players must be between 1 and 8. Using config: {MaxPlayers}", LogPrefix, Singletons.MaxPlayers);
                }
            }
        }

        private void SetupMeterProvider(ServerConfiguration config)
        {
            MeterProvider? meterProvider = null;
            if (config.Logging.EnableMetrics)
            {
                string defaultMeterName = "OVS.Rollback.Server";
                StringBuilder logEntry = new($"Metrics enabled");

                if (config.Logging.EnableConsoleMetrics)
                {
                    meterProvider = Sdk.CreateMeterProviderBuilder()
                        .AddMeter(defaultMeterName)
                        .AddConsoleExporter()
                        .Build();
                    logEntry.AppendLine(" - exporting to console");
                }
                else
                {
                    meterProvider = Sdk.CreateMeterProviderBuilder()
                        .AddMeter(defaultMeterName)
                        .Build();

                    logEntry.AppendLine($" - to view, use: dotnet-counters monitor -p {Environment.ProcessId} --counters \"{defaultMeterName}\"");
                }
                Singletons.MetricsProvider = meterProvider;
                Singletons.MetricsString = logEntry.ToString();
            }
            else
            {
                Singletons.MetricsString = $"Metrics disabled by configuration";
            }
        }
    }
}
