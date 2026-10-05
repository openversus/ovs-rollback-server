// NodeTrustList.cs
namespace OVS.Rollback.P2P
{
    /// <summary>One server a locked node may talk to, and the public key it signs with (base64 SubjectPublicKeyInfo).</summary>
    public sealed record NodeTrustEntry(string Server, string PublicKey);

    /// <summary>
    /// The servers a node build trusts (OVSRollbackNode.csproj: NodeTrust, written by build.sh from pki/&lt;env&gt;/): the
    /// first is the default, and the one the mod's server (--server, the game's ServerUrl) names is used for the run. Here,
    /// not in the node, because the node chooses before the engine is first touched, and this assembly has no engine in it.
    /// </summary>
    public static class NodeTrustList
    {
        /// <summary>
        /// "url key url key ...": whitespace-separated pairs, in order. Empty text is an empty list; anything else that is not
        /// whole pairs of an http(s) URL and a key is refused (a build with a broken list must not run as if it trusted nothing).
        /// </summary>
        public static List<NodeTrustEntry> Parse(string? text)
        {
            string[] parts = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length % 2 != 0)
            {
                throw new FormatException($"the trust list has {parts.Length} words; it must be pairs of a server URL and its public key");
            }
            var entries = new List<NodeTrustEntry>();
            for (int i = 0; i < parts.Length; i += 2)
            {
                if (!TryServer(parts[i], out _))
                {
                    throw new FormatException($"\"{parts[i]}\" in the trust list is not an http or https URL");
                }
                entries.Add(new NodeTrustEntry(parts[i].TrimEnd('/'), parts[i + 1]));
            }
            return entries;
        }

        /// <summary>
        /// The entry for <paramref name="requested"/> (matched by scheme, host and port, so a trailing slash, letter case or a
        /// spelled-out default port make no difference), else the first. <paramref name="matched"/> is false when a server was
        /// requested and none matches. Null only for an empty list.
        /// </summary>
        public static NodeTrustEntry? Select(IReadOnlyList<NodeTrustEntry> trust, string? requested, out bool matched)
        {
            matched = false;
            if (trust.Count == 0)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(requested))
            {
                foreach (var entry in trust)
                {
                    if (SameServer(entry.Server, requested.Trim()))
                    {
                        matched = true;
                        return entry;
                    }
                }
            }
            return trust[0];
        }

        /// <summary>Whether two server URLs name the same server: scheme, host (any case) and port.</summary>
        public static bool SameServer(string a, string b) =>
            TryServer(a, out var x) && TryServer(b, out var y)
            && x.Scheme == y.Scheme && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port;

        private static bool TryServer(string text, out Uri uri) =>
            Uri.TryCreate(text, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
