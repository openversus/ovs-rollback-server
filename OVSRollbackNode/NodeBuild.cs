// NodeBuild.cs
using System.Reflection;

namespace OVS.Rollback.Node
{
    /// <summary>
    /// What this build of the node was made to trust (OVSRollbackNode.csproj: NodeServer, NodeConfigPublicKey,
    /// NodeUnlocked). Names no engine type, so Program.Main can read it before the engine is first touched.
    /// </summary>
    internal static class NodeBuild
    {
        /// <summary>The server a locked node fetches match configs and its settings update from, without a trailing slash.</summary>
        public static string Server { get; } = (Metadata("NodeServer") ?? "").TrimEnd('/');

        /// <summary>The server's public key (base64 SubjectPublicKeyInfo); empty only in an unlocked build.</summary>
        public static string PublicKey { get; } = Metadata("NodeConfigPublicKey") ?? "";

        /// <summary>
        /// A bench or Debug build: the player's values count for every setting and the server comes from the command line
        /// or the environment. Signatures are still checked when a key is built in.
        /// </summary>
        public static bool Unlocked { get; } = string.Equals(Metadata("NodeUnlocked"), "true", StringComparison.OrdinalIgnoreCase);

        private static string? Metadata(string key) =>
            typeof(NodeBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
    }
}
