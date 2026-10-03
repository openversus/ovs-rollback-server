// Program.cs: OVS.Rollback.Node [port] [--port-file FILE] [--parent-token N] [--parent-timeout SECONDS] [--server URL] [--rendezvous HOST:PORT]
//
// The options override node.appsettings.json and the environment (Node__* / Server__BaseUrl); the mod that starts
// the node passes its settings this way because, under Proton, Wine's "start /unix" hands a Linux program the
// Unix environment, not the Windows one (checked 2026-10-03), while the command line crosses intact.
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
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args) => NodeHost.Run(args).GetAwaiter().GetResult();
    }
}
