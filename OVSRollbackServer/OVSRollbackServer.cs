// Program.cs
using Rollback;
using Rollback.Core;
using Rollback.Utils;

ushort port = Constants.GameServerPort;
int maxPlayers = Constants.MaxPlayers;
string prefix = Utilities.LogPrefix;

if (args.Length > 0)
{
    if (ushort.TryParse(args[0], out var p))
        port = p;
    else
        Console.Error.WriteLine($"{prefix}Invalid port number. Using default: {port}");
}

if (args.Length > 1)
{
    if (int.TryParse(args[1], out var mp) && mp is > 0 and <= 4)
        maxPlayers = mp;
    else
    {
        Console.Error.WriteLine(
            $"{prefix}Max players must be between 1 and 4. Using default: {maxPlayers}");
    }
}

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Prevent immediate termination
    cts.Cancel();
};

// Also handle SIGTERM (e.g., Docker/container stop)
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

try
{
    await using var server = new RollbackServer(port, maxPlayers);
    server.Start();

    Console.WriteLine($"{prefix}Server running. Press Ctrl+C to stop.");

    // Block until cancellation signal — replaces the C++ while(g_signal_status == 0) loop
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException) { }

    Console.WriteLine($"{prefix}Shutting down server...");
    await server.StopAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{prefix}Error: {ex.Message}");
    return 1;
}

return 0;

