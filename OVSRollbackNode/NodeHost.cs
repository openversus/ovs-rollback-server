// NodeHost.cs
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
            var logger = Utilities.NewLogger<Node>();
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "node.appsettings.json");
            ServerConfiguration.Initialize(logger, settingsPath);
            var config = Singletons.Config;
            logger.LogInformation("Node settings from {Path}: rendezvous '{Rendezvous}', relay '{Relay}', base URL {BaseUrl}",
                settingsPath, config.Node.Rendezvous, config.Node.RelayFallback, config.Server.BaseUrl);
            ushort port = Singletons.PortSetOnCommandLine ? Singletons.Port : config.Server.Port;

            logger.LogInformation("OVS Rollback Node version {Version}, engine built {CompileTime}", Statics.OVSRollbackVersion, CompileTime.CompileDateTime);
            if (config.Server.FireMatchEvents)
            {
                logger.LogWarning("Server.FireMatchEvents is on; a node has no MatchUpdateKey, so every match event it sends will be rejected. Turn it off in appsettings.json.");
            }
            if (string.IsNullOrWhiteSpace(config.Node.Rendezvous) && string.IsNullOrWhiteSpace(config.Node.RelayFallback))
            {
                logger.LogError("Neither Node.Rendezvous nor Node.RelayFallback is set: the node could neither find a peer nor forward to a relay. Set at least one.");
                return 2;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            await using var node = new Node(logger, config, port);
            try
            {
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
    }
}
