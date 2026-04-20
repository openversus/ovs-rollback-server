using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using OVS.Rollback.Utils;
using System.Diagnostics;
using System.Runtime.CompilerServices;

// The only scenario in which these fields are null
// is before the module initializer runs. If they're null
// after that, there are bigger problems, like the fact
// that this program can't run at all
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning disable CS8602

namespace OVS.Rollback.Common
{
    /// <summary>
    /// Provides shared singleton instances of core services and helpers used throughout the program.
    /// </summary>
    /// <remarks>This class centralizes access to commonly used singleton objects, such as dependency
    /// injection containers, logging infrastructure, and helper utilities. All members are static and intended for
    /// internal use only. The class is initialized automatically at module load time.</remarks>
    internal static class Singletons
    {
        /// <summary>
        /// Provides a globally accessible dependency injection container for use by the program.
        /// </summary>
        /// <remarks>This static field is intended to be used by classes/functions that require dependency
        /// resolution. It should be initialized before any entities attempt to resolve dependencies.
        /// </remarks>
        public static DIContainer RollbackDIContainer;

        /// <summary>
        /// Provides a shared instance of an <see cref="ILoggerFactory"/> for use across the program.
        /// </summary>
        /// <remarks>Use this factory to create logger instances that share configuration and resources.
        /// Sharing a single <see cref="ILoggerFactory"/> instance is recommended to ensure consistent logging behavior
        /// and to avoid unnecessary resource usage.</remarks>
        public static ILoggerFactory SharedLoggerFactory;

        /// <summary>
        /// Represents the root Serilog logger instance from which all oher loggers should derive.
        /// </summary>
        /// <remarks>This logger is configured within the DI container at program startup and is used
        /// as the base logger for all logging operations. Modifying this instance affects logging behavior throughout the
        /// program.</remarks>
        public static Serilog.ILogger _rootLogger;

        /// <summary>
        /// Gets the root Serilog logger instance from which all oher loggers should derive.
        /// </summary>
        /// <remarks>Use this property to access the global logger for logging messages throughout the
        /// program. This logger is configured within the DI container at program startup and should be used for
        /// general, contextless logging needs. This property is thread-safe.</remarks>
        public static Serilog.ILogger RootLoggerInstance
        {
            get => _rootLogger!;
        }

        public static ServerConfiguration Config;

        public static HTTPHelper SharedHTTPHelper;
        public static HttpClient SharedHTTPClient;

        public static Stopwatch SharedStopwatch;

        public static CancellationTokenSource CTS;

        public static ushort Port { get; set; } = 41234;
        public static int MaxPlayers { get; set; } = 8;
        public static string ConfigPath { get; set; } = String.Empty;
        public static MeterProvider? MetricsProvider { get; set; } = null;
        public static string MetricsString { get; set; } = String.Empty;

        /// <summary>
        /// Initializes shared service instances and dependencies required by the module at program startup.
        /// </summary>
        /// <remarks>This method is automatically invoked during module initialization due to the <see
        /// cref="ModuleInitializerAttribute"/>. It sets up core services such as logging, dependency injection, and
        /// helper utilities, making them available for use throughout the module and during bootstrapping. This method
        /// should not be called directly.</remarks>
        [ModuleInitializer]
        public static void Initialize()
        {
            RollbackDIContainer ??= new DIContainer();
            Config = RollbackDIContainer.GenericHost.Services.GetRequiredService<ServerConfiguration>();
            SharedLoggerFactory = RollbackDIContainer.GenericHost.Services.GetRequiredService<ILoggerFactory>();
            SharedHTTPClient = RollbackDIContainer.GenericHost.Services.GetRequiredService<HttpClient>();
            SharedHTTPHelper = RollbackDIContainer.GenericHost.Services.GetRequiredService<HTTPHelper>();
            SharedStopwatch = RollbackDIContainer.GenericHost.Services.GetRequiredService<Stopwatch>();
            CTS = RollbackDIContainer.GenericHost.Services.GetRequiredService<CancellationTokenSource>();
        }

    }
}

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
#pragma warning restore CS8602
