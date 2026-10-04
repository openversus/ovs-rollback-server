// NodeScope.cs
namespace OVS.Rollback.Configuration
{
    /// <summary>
    /// Who decides a setting when the engine runs inside the P2P node on a player's machine (OVS.Rollback.Node's
    /// lockdown). The server ignores it. Every setting has exactly one, on its property or on its section's class.
    /// </summary>
    public enum NodeScope
    {
        /// <summary>
        /// The same for everyone in a match: the host's engine or node applies it to every player, so a player's own
        /// value could favor them or push the match to the relay. The node takes the server's signed update, else
        /// the value built into it; the file, the environment and the command line are not read for it.
        /// </summary>
        Fairness,
        /// <summary>The player's: their file, environment and command line win; the update only replaces a value they left at the built-in one.</summary>
        Player,
        /// <summary>Fixed when the node is built (the server it trusts); neither the player nor the update changes it.</summary>
        Build,
    }

    /// <summary>The <see cref="NodeScope"/> of a setting (on a property) or of every setting in a section (on its class).</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
    public sealed class NodeScopeAttribute(NodeScope scope) : Attribute
    {
        public NodeScope Scope { get; } = scope;
    }
}
