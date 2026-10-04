// NodeConfigMerge.cs
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OVS.Rollback.Common;

namespace OVS.Rollback.Configuration
{
    /// <summary>
    /// The P2P node's lockdown, applied to the configuration it loaded (its file, the environment, the command line):
    /// every <see cref="NodeScope.Fairness"/> setting becomes the server's update's value, else the one built into the
    /// node; a <see cref="NodeScope.Player"/> setting takes the update's value only where the player left the built-in
    /// one; <see cref="NodeScope.Build"/> is left alone. The update counts by presence: a setting the update's JSON
    /// does not name is not in it (deserializing it would turn every omitted setting into its class default, which is
    /// not the built-in value).
    /// </summary>
    internal static class NodeConfigMerge
    {
        /// <summary>One setting the merge changed, or could not take from the update.</summary>
        /// <param name="Setting">"Section.Property".</param>
        /// <param name="Kind">What happened to it.</param>
        /// <param name="Was">The value before, as text.</param>
        /// <param name="Now">The value after, as text (for <see cref="ChangeKind.Unreadable"/>: the update's JSON).</param>
        public sealed record Change(string Setting, ChangeKind Kind, string Was, string Now);

        public enum ChangeKind
        {
            /// <summary>The player had set a fairness setting; their value is not used.</summary>
            PlayerValueOverridden,
            /// <summary>The update's value replaced the built-in one (fairness, or a player setting left at the built-in value).</summary>
            FromUpdate,
            /// <summary>The update named the setting with a value of the wrong type; the setting fell back as if it were absent.</summary>
            Unreadable,
        }

        /// <summary>
        /// Applies the lockdown to <paramref name="live"/> in place. <paramref name="update"/> is the update's "config"
        /// object (sections by name, settings by name, case-insensitive), or null when there is none. When
        /// <paramref name="unlocked"/> (a bench build), every fairness setting is treated as the player's.
        /// </summary>
        public static List<Change> Apply(ServerConfiguration live, ServerConfiguration builtIn, JsonObject? update, bool unlocked)
        {
            var changes = new List<Change>();
            foreach (var section in Sections())
            {
                object liveSection = section.GetValue(live)!;
                object builtInSection = section.GetValue(builtIn)!;
                JsonObject? updateSection = TryFind(update, section.Name, out var sectionJson) ? sectionJson as JsonObject : null;
                foreach (var setting in Settings(section.PropertyType))
                {
                    NodeScope scope = ScopeOf(section.PropertyType, setting);
                    if (scope == NodeScope.Build)
                    {
                        continue;
                    }
                    if (unlocked)
                    {
                        scope = NodeScope.Player;
                    }

                    string name = $"{section.Name}.{setting.Name}";
                    object? current = setting.GetValue(liveSection);
                    object? builtInValue = setting.GetValue(builtInSection);
                    bool fromUpdate = false;
                    object? updated = null;
                    if (TryFind(updateSection, setting.Name, out var node))
                    {
                        if (TryRead(node, setting.PropertyType, out updated))
                        {
                            fromUpdate = true;
                        }
                        else
                        {
                            changes.Add(new Change(name, ChangeKind.Unreadable, Text(current), node?.ToJsonString() ?? "null"));
                        }
                    }

                    if (scope == NodeScope.Fairness)
                    {
                        object? target = fromUpdate ? updated : builtInValue;
                        if (Equals(current, target))
                        {
                            continue;
                        }
                        setting.SetValue(liveSection, target);
                        changes.Add(new Change(name, Equals(current, builtInValue) ? ChangeKind.FromUpdate : ChangeKind.PlayerValueOverridden, Text(current), Text(target)));
                    }
                    else if (fromUpdate && Equals(current, builtInValue) && !Equals(current, updated))
                    {
                        setting.SetValue(liveSection, updated);
                        changes.Add(new Change(name, ChangeKind.FromUpdate, Text(current), Text(updated)));
                    }
                }
            }
            return changes;
        }

        /// <summary>The configuration's sections: its settable class-typed properties.</summary>
        internal static IEnumerable<PropertyInfo> Sections() =>
            typeof(ServerConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.PropertyType.IsClass && p.PropertyType != typeof(string));

        /// <summary>A section's settings: its public settable properties.</summary>
        internal static IEnumerable<PropertyInfo> Settings(Type section) =>
            section.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite);

        /// <summary>The setting's scope: its own attribute, else its section's. A setting with neither is a bug the tests catch first.</summary>
        internal static NodeScope ScopeOf(Type section, PropertyInfo setting) =>
            (setting.GetCustomAttribute<NodeScopeAttribute>() ?? section.GetCustomAttribute<NodeScopeAttribute>())?.Scope
            ?? throw new InvalidOperationException($"{section.Name}.{setting.Name} has no NodeScope");

        /// <summary>Whether <paramref name="json"/> names <paramref name="name"/> (any case); a JSON null counts as named, with a null <paramref name="value"/>.</summary>
        private static bool TryFind(JsonObject? json, string name, out JsonNode? value)
        {
            value = null;
            if (json is null)
            {
                return false;
            }
            foreach (var (key, node) in json)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = node;
                    return true;
                }
            }
            return false;
        }

        private static bool TryRead(JsonNode? node, Type type, out object? value)
        {
            if (node is null)
            {
                value = null;
                return false;
            }
            try
            {
                // Every setting is a number, a bool or a string; a JSON null is not a value for any of them.
                value = node.Deserialize(type, OVSJsonContext.Default);
                return value is not null;
            }
            // NotSupportedException: a setting type the JSON context cannot read; ignored like a wrong value, never fatal.
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NotSupportedException)
            {
                value = null;
                return false;
            }
        }

        private static string Text(object? value) => value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }
}
