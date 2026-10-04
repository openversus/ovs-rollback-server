// NodeHost.cs
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using OVS.Rollback.Configuration;
using OVS.Rollback.Utils;

namespace OVS.Rollback.Node
{
    internal static class NodeHost
    {
        public static async Task<int> Run(string[] args)
        {
            // Touching Singletons loads the engine's configuration (its appsettings.json beside this executable, env
            // overrides, the port from argv[1] when given) and its logging. The node's own file then replaces it,
            // with the same env overrides; nothing has read the configuration for keeps yet.
            // Touch the engine before asking it for a logger. Every logger NewLogger hands out forwards to the root
            // Serilog logger that the engine's module initializer creates, and that initializer runs on the first
            // call into the engine's own code. In the ReadyToRun single-file build, NewLogger<Node>() alone was not
            // one: a generic instantiation over a type of this assembly is compiled into this assembly's image, so
            // the node's logger captured Serilog's silent default and every line of this class vanished, while the
            // engine's own lines (loggers made after initialization) were fine (2026-10-03). A non-generic member is a
            // call into the engine, and the version is one worth having anyway.
            string engineVersion = Statics.OVSRollbackVersion;
            // Players' addresses are masked in everything logged from here on (AddressMask): this log is what a player
            // shares for support. Nothing before this line knows any.
            AddressMask.Enable();
            var logger = Utilities.NewLogger<Node>();
            // The options reached the environment in Program.Main, before the engine was first touched (its module
            // initializer builds the HTTP helper that fetches match configs, which reads OVS_SERVER right then; exporting
            // here, after that touch, left the helper on node.appsettings.json's default, and his friend's node asked
            // the old prod instance for a match the 8420 instance owned: empty body, match timed out, 2026-10-03).
            // ApplyOptions below puts the same values into the loaded configuration, for everything that reads that.
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "node.appsettings.json");
            ServerConfiguration.Initialize(logger, settingsPath);
            var config = Singletons.Config;
            // OVS's own endpoints stay readable where the settings give them as addresses (the bench; prod uses names).
            // Before the options, whose warnings can name the server this node was built for.
            AddressMask.AddInfra(NodeBuild.Server);
            AddressMask.AddInfra(config.Node.Rendezvous);
            AddressMask.AddInfra(config.Node.RelayFallback);
            AddressMask.AddInfra(config.Server.BaseUrl);
            AddressMask.AddInfra(Singletons.SharedHTTPHelper.BaseUrl);
            if (!ApplyOptions(args, config, logger))
            {
                return 2;
            }
            // Before anything reads a setting that is the same for everyone in a match (the engine and the socket are
            // made below).
            await NodeLockdown.ApplyAsync(config, logger);
            logger.LogInformation("Match configs and reports go to {Server} (register URL {RegisterUrl})", Singletons.SharedHTTPHelper.BaseUrl, Singletons.SharedHTTPHelper.RegisterURL);
            logger.LogInformation("Node settings from {Path} and the command line: rendezvous '{Rendezvous}', relay '{Relay}', base URL {BaseUrl}",
                settingsPath, config.Node.Rendezvous, config.Node.RelayFallback, config.Server.BaseUrl);
            ushort port = Singletons.PortSetOnCommandLine ? Singletons.Port : config.Server.Port;

            logger.LogInformation("OVS Rollback Node version {Version}, engine built {CompileTime}", engineVersion, CompileTime.CompileDateTime);
            if (config.Server.FireMatchEvents)
            {
                logger.LogWarning("Server.FireMatchEvents is on; a node has no MatchUpdateKey, so every match event it sends will be rejected. Turn it off in appsettings.json.");
            }
            if (string.IsNullOrWhiteSpace(config.Node.Rendezvous))
            {
                logger.LogWarning("Node.Rendezvous is not set: every match with another human player will go to a relay (the server's, or Node.RelayFallback); a match against bots alone is served here.");
            }
            if (config.RiftCalculation.HostPingParity)
            {
                logger.LogInformation("Host ping parity on: this machine's game is told the slowest remote round trip as its ping, so its input delay matches");
            }

            // Not disposed: ProcessExit below runs after this method has returned, and Cancel on a disposed source
            // throws, which turned every clean exit into an abort (seen 2026-10-03, exit code 134).
            var cts = new CancellationTokenSource();
            // The engine's hosting container registers a console lifetime that marks SIGTERM and SIGINT as handled
            // and then does nothing with them (its host is never run), which would leave the node immune to a plain
            // kill. Own registrations run too, and these end the loop.
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            try
            {
                // Construction binds the socket and reads the watchdog token; either can refuse, and that is an error
                // to log like any other, not an unhandled exception.
                await using var node = new Node(logger, config, port);
                await node.RunAsync(cts.Token);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Node stopped on an error");
                return 1;
            }
            finally
            {
                Serilog.Log.CloseAndFlush();
            }
            return 0;
        }

        /// <summary>
        /// The "--option value" pairs after the positional port (see Program.cs), applied over the loaded settings.
        /// False, with the problem logged, for an option that is unknown, has no value, or has one that does not parse.
        /// </summary>
        internal static bool ApplyOptions(string[] args, ServerConfiguration config, ILogger logger)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string name = args[i];
                if (!name.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    logger.LogError("Option {Option} needs a value", name);
                    return false;
                }
                string value = args[++i];
                switch (name)
                {
                    case "--port-file":
                        config.Node.PortFile = value;
                        break;
                    case "--parent-token":
                        config.Node.ParentToken = value;
                        break;
                    case "--parent-timeout":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
                        {
                            logger.LogError("--parent-timeout needs a positive number of seconds, not {Value}", value);
                            return false;
                        }
                        config.Node.ParentTimeoutSeconds = seconds;
                        break;
                    case "--server":
                        if (NodeBuild.Unlocked)
                        {
                            config.Server.BaseUrl = value;
                        }
                        else if (!string.Equals(value.TrimEnd('/'), NodeBuild.Server, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogWarning("--server {Server} is ignored: this node only talks to {Built}, the server it was built for; a match from another server will not find its config here", value, NodeBuild.Server);
                        }
                        break;
                    case "--rendezvous":
                        config.Node.Rendezvous = value;
                        break;
                    case "--log-file":
                        // Already in use: Program.Main exported it, and the engine opened the file at first touch.
                        break;
                    default:
                        logger.LogError("Unknown option {Option}", name);
                        return false;
                }
            }
            return true;
        }
    }
}
