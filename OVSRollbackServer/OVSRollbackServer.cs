// Program.cs
using Microsoft.Extensions.Logging;
using OVS;
using OVS.Rollback.Core;
using OVS.Rollback.Utils;
using System;
using System.Runtime.CompilerServices;

namespace OVS.Rollback
{
    public class Server
    {
        private static readonly ILogger<Server> logger = Utilities.NewLogger<Server>();
        protected internal static CancellationTokenSource cts = new CancellationTokenSource();
        public static ushort Port = Constants.GameServerPort;
        public static int MaxPlayers = Constants.MaxPlayers;
        public static readonly string LogPrefix = Utilities.LogPrefix;

        public static async Task<int> Main(string[] args)
        {

            if (args.Length > 0)
            {
                if (ushort.TryParse(args[0], out var p))
                {
                    Port = p;
                }
                else
                {
                    logger.LogWarning($"{LogPrefix} Invalid port number. Using default: {Port}");
                }
            }

            if (args.Length > 1)
            {
                if (int.TryParse(args[1], out var mp) && mp is > 0 and <= 4)
                {
                    MaxPlayers = mp;
                }
                else
                {
                    logger.LogWarning($"{LogPrefix} Max players must be between 1 and 4. Using default: {MaxPlayers}");
                }
            }

            ILogger<RollbackServer> rollbackLogger = Utilities.NewLogger<RollbackServer>();

            Console.CancelKeyPress += (_, token) => {
                token.Cancel = true;
                cts.Cancel();
            };

            AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

            try
            {
                await using var server = new RollbackServer(logger: rollbackLogger, port: Port, maxPlayers: MaxPlayers);
                server.Start();

                logger.LogInformation($"{LogPrefix} Server running. Press Ctrl+C to stop.");

                try
                {
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException)
                {

                }

                logger.LogInformation($"{LogPrefix} Shutting down server...");
                await server.StopAsync();
            }
            catch (Exception ex)
            {
                logger.LogError($"{LogPrefix} Error: {ex.Message}");
                return 1;
            }

            return 0;
        }
    }
}
