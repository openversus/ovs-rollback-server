// NodeBuild.cs
using System.Reflection;
using OVS.Rollback.P2P;

namespace OVS.Rollback.Node
{
    /// <summary>
    /// What this build of the node was made to trust (OVSRollbackNode.csproj: NodeTrust, NodeUnlocked), and the server this
    /// run uses. Names no engine type, so Program.Main can choose before the engine is first touched.
    /// </summary>
    internal static class NodeBuild
    {
        /// <summary>The servers this build trusts, each with the public key it signs with; the first is the default.</summary>
        public static IReadOnlyList<NodeTrustEntry> Trust { get; } = NodeTrustList.Parse(Metadata("NodeTrust"));

        /// <summary>
        /// A bench or Debug build: the player's values count for every setting and the server comes from the command line
        /// or the environment. Signatures are still checked when a key is built in.
        /// </summary>
        public static bool Unlocked { get; } = string.Equals(Metadata("NodeUnlocked"), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>The server asked for (--server, which the mod sets from the game's ServerUrl, else the environment's); null when none.</summary>
        public static string? Requested { get; private set; }

        /// <summary>Whether <see cref="Requested"/> is one of <see cref="Trust"/>.</summary>
        public static bool Matched { get; private set; }

        private static NodeTrustEntry? s_selected;

        /// <summary>This run's server: the trusted one <see cref="Requested"/> names, else the default; without a trailing slash.</summary>
        public static string Server => s_selected?.Server ?? "";

        /// <summary>The public key this run checks signatures with: <see cref="Server"/>'s; empty only in an unlocked build without one.</summary>
        public static string PublicKey => s_selected?.PublicKey ?? "";

        /// <summary>Chooses this run's entry; Program.Main calls it once, before the engine is first touched.</summary>
        public static void Select(string? requested)
        {
            Requested = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
            s_selected = NodeTrustList.Select(Trust, Requested, out bool matched);
            Matched = matched;
        }

        private static string? Metadata(string key) =>
            typeof(NodeBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
    }
}
