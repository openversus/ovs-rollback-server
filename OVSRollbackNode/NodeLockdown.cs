// NodeLockdown.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OVS.Rollback.Common;
using OVS.Rollback.Configuration;
using OVS.Rollback.Core;
using OVS.Rollback.Utils;

namespace OVS.Rollback.Node
{
    /// <summary>
    /// Keeps a player from giving themselves an edge through the node's settings. The settings every player in a match
    /// shares (NodeScope.Fairness) come from the server's signed update, else from the copy of node.appsettings.json
    /// built into the node, never from the player's file, environment or command line; the player's own settings stay
    /// theirs (NodeConfigMerge). Match configs are taken only with the server's signature (Node.FetchConfigAsync). What
    /// this cannot stop: a node rebuilt from source, which is the host's to run by design.
    /// </summary>
    internal static class NodeLockdown
    {
        private const string BuiltInResource = "node.appsettings.json";

        /// <summary>
        /// Whether <paramref name="body"/> came from the server: signed with the private half of the key this node was
        /// built with. An unlocked build without a key takes everything (it has nothing to check against, and says so
        /// at startup). <paramref name="why"/> says what was wrong.
        /// </summary>
        public static bool Trusted(byte[] body, string? signature, out string why)
        {
            why = "";
            if (NodeBuild.PublicKey.Length == 0)
            {
                if (NodeBuild.Unlocked)
                {
                    return true;
                }
                why = "this node was built without the server's key";
                return false;
            }
            if (string.IsNullOrWhiteSpace(signature))
            {
                why = "the server did not sign it";
                return false;
            }
            if (!ServerSignature.Verify(NodeBuild.PublicKey, body, signature))
            {
                why = "its signature does not match the server's key";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Applies the lockdown to <paramref name="live"/> (the loaded configuration, which every reader shares) before
        /// anything has read a fairness setting, and gives the shared HTTP client the locked timeout.
        /// </summary>
        public static async Task ApplyAsync(ServerConfiguration live, ILogger log)
        {
            if (NodeBuild.Unlocked)
            {
                log.LogWarning("Unlocked build (bench or Debug): the local configuration counts for every setting{Keys}",
                    NodeBuild.PublicKey.Length == 0 ? ", and nothing from the server is signature-checked" : "");
            }

            string builtInJson;
            using (var stream = typeof(NodeLockdown).Assembly.GetManifestResourceStream(BuiltInResource)
                ?? throw new InvalidOperationException($"the built-in {BuiltInResource} is missing from this build"))
            using (var reader = new StreamReader(stream))
            {
                builtInJson = await reader.ReadToEndAsync();
            }
            var builtIn = JsonSerializer.Deserialize(builtInJson, OVSJsonContext.Default.ServerConfiguration)
                ?? throw new InvalidOperationException($"the built-in {BuiltInResource} is empty");
            int builtInVersion = Version(JsonNode.Parse(builtInJson));

            JsonObject? update = await FetchUpdateAsync(builtIn.Networking.HttpTimeoutSeconds, builtInVersion, log);
            var changes = NodeConfigMerge.Apply(live, builtIn, update, NodeBuild.Unlocked);
            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case NodeConfigMerge.ChangeKind.PlayerValueOverridden:
                        log.LogWarning("{Setting}: the local value {Was} is not used; this setting is the same for everyone ({Now})", change.Setting, change.Was, change.Now);
                        break;
                    case NodeConfigMerge.ChangeKind.Unreadable:
                        log.LogWarning("{Setting}: the update's value {Value} is not one this setting can take; ignored", change.Setting, change.Now);
                        break;
                    default:
                        log.LogDebug("{Setting}: {Was} -> {Now} from the update", change.Setting, change.Was, change.Now);
                        break;
                }
            }
            int fromUpdate = changes.Count(c => c.Kind == NodeConfigMerge.ChangeKind.FromUpdate);
            log.LogInformation("Settings: {Source}; {FromUpdate} taken from the update, {Overridden} local value(s) not used",
                update is null ? "the built-in values (no update)" : "the server's update over the built-in values", fromUpdate,
                changes.Count(c => c.Kind == NodeConfigMerge.ChangeKind.PlayerValueOverridden));

            // The shared client was made when the engine was first touched, from settings the player can edit; it has sent
            // nothing yet (the update came through a client of its own), so its timeout can still be set.
            try
            {
                Singletons.SharedHTTPClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, live.Networking.HttpTimeoutSeconds));
            }
            catch (InvalidOperationException e)
            {
                log.LogError("The HTTP timeout could not be locked: {Error}", e.Message);
            }
        }

        /// <summary>The "config" object of the server's update, or null (logged) when there is none to trust.</summary>
        private static async Task<JsonObject?> FetchUpdateAsync(int timeoutSeconds, int builtInVersion, ILogger log)
        {
            string url = Singletons.SharedHTTPHelper.BaseUrl.TrimEnd('/') + Constants.Endpoints.OVSNodeConfig;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) };
                using var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    log.LogInformation("No settings update from {Url} (HTTP {Status}); using the built-in values", url, (int)response.StatusCode);
                    return null;
                }
                byte[] body = await response.Content.ReadAsByteArrayAsync();
                string? signature = response.Headers.TryGetValues(Constants.SignatureHeader, out var values) ? values.FirstOrDefault() : null;
                if (!Trusted(body, signature, out string why))
                {
                    log.LogWarning("The settings update from {Url} is not used: {Why}; using the built-in values", url, why);
                    return null;
                }
                var json = JsonNode.Parse(body, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
                int version = Version(json);
                if (version < builtInVersion)
                {
                    log.LogWarning("The settings update from {Url} is version {Version}, older than this node's own {BuiltIn}; using the built-in values", url, version, builtInVersion);
                    return null;
                }
                if (json?["config"] is not JsonObject config)
                {
                    log.LogWarning("The settings update from {Url} has no \"config\" object; using the built-in values", url);
                    return null;
                }
                log.LogInformation("Settings update version {Version} from {Url}", version, url);
                return config;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                log.LogInformation("No settings update from {Url} ({Error}); using the built-in values", url, e.Message);
                return null;
            }
        }

        /// <summary>The "version" (update) or "ConfigVersion" (built-in file) number at the top of <paramref name="json"/>; 0 when absent or not a number.</summary>
        private static int Version(JsonNode? json)
        {
            var node = json?["version"] ?? json?["ConfigVersion"];
            try
            {
                return node?.GetValue<int>() ?? 0;
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException)
            {
                return 0;
            }
        }
    }
}
