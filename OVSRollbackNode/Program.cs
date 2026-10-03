// Program.cs: OVS.Rollback.Node [port]
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
