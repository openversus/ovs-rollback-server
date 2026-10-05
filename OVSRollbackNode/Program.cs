// Program.cs: OVS.Rollback.Node [port] [--port-file FILE] [--parent-token N] [--parent-timeout SECONDS] [--server URL] [--rendezvous HOST:PORT]
//                               [--log-file FILE]
//
// The options override node.appsettings.json and the environment (Node__* / Server__BaseUrl / Logging__LogFilePath);
// the mod that starts the node passes its settings this way because, under Proton, Wine's "start /unix" hands a Linux
// program the Unix environment, not the Windows one (checked 2026-10-03), while the command line crosses intact.
//
// The engine's assembly loads its configuration from the working directory the moment it is first touched, so
// Main only moves there and hands over; nothing in it may name an engine type.
using System.Runtime.CompilerServices;

namespace OVS.Rollback.Node
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Environment.CurrentDirectory = AppContext.BaseDirectory;
            ExportOptions(args);
            try
            {
                // The server asked for: --server (just exported) or the environment's. A locked node takes the trusted server
                // it names, else its default; an unlocked one takes that server's key, else the first.
                NodeBuild.Select(Environment.GetEnvironmentVariable("Server__BaseUrl") ?? Environment.GetEnvironmentVariable("OVS_SERVER"));
            }
            catch (TypeInitializationException e) when (e.InnerException is FormatException bad)
            {
                Console.Error.WriteLine($"This node build's trust list is broken: {bad.Message}");
                return 2;
            }
            if (!NodeBuild.Unlocked)
            {
                PinServer();
            }
            return Run(args);
        }

        /// <summary>
        /// The "--option value" pairs as environment overrides, set before the engine is first touched: its module
        /// initializer builds the configuration and the HTTP helper that fetches match configs, and the helper reads
        /// Server__BaseUrl / OVS_SERVER right then (NodeHost.ApplyOptions, which runs after, is too late for it: seen
        /// 2026-10-03 as a node asking the wrong server). The log file is decided at that touch too (Utilities.LogPath),
        /// so --log-file can only travel this way. Unknown options are left to ApplyOptions to report.
        /// </summary>
        private static void ExportOptions(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                string value = args[i + 1];
                switch (args[i])
                {
                    case "--server":
                        string server = value.TrimEnd('/');
                        Environment.SetEnvironmentVariable("Server__BaseUrl", server);
                        Environment.SetEnvironmentVariable("OVS_SERVER", server);
                        break;
                    case "--rendezvous": Environment.SetEnvironmentVariable("Node__Rendezvous", value); break;
                    case "--port-file": Environment.SetEnvironmentVariable("Node__PortFile", value); break;
                    case "--parent-token": Environment.SetEnvironmentVariable("Node__ParentToken", value); break;
                    case "--parent-timeout": Environment.SetEnvironmentVariable("Node__ParentTimeoutSeconds", value); break;
                    case "--log-file": Environment.SetEnvironmentVariable("Logging__LogFilePath", value); break;
                }
            }
        }

        /// <summary>
        /// A locked node talks only to a server it trusts (NodeBuild.Server, chosen from --server among the built-in ones):
        /// the match configs it hosts and its settings update come from there. Set here for the same reason as the options:
        /// the engine reads its server once, at first touch.
        /// </summary>
        private static void PinServer()
        {
            Environment.SetEnvironmentVariable("Server__BaseUrl", NodeBuild.Server);
            Environment.SetEnvironmentVariable("OVS_SERVER", NodeBuild.Server);
            Environment.SetEnvironmentVariable("mvsi_server", null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args) => NodeHost.Run(args).GetAwaiter().GetResult();
    }
}
