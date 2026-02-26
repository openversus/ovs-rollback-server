// Utilities.cs
namespace Rollback.Utils;

public static class Utilities
{
    public static string Hostname { get; } =
        Environment.GetEnvironmentVariable(
            OperatingSystem.IsWindows() ? "COMPUTERNAME" : "HOSTNAME")
        ?? "unknown";

    public const string ServiceName = "RollbackServer";

    public static string LogPrefix { get; } = $"[{Hostname}.{ServiceName}]: ";
}

