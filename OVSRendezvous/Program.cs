// Program.cs: OVS.Rollback.Rendezvous [port] [--ttl-minutes N] [--relay host:port]
//
// Nodes register here (UDP) with their match id, key and player index; the answer carries the registrant's public
// mapping and every other node of the match. Match keys are accepted as presented for now (AcceptAllValidator);
// a validator against the match store comes with the cloud integration.
using Microsoft.Extensions.Logging;
using OVS.Rollback.P2P;

int port = 41235;
int ttlMinutes = 10;
string? relayArg = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ttl-minutes" when i + 1 < args.Length && int.TryParse(args[i + 1], out var ttl):
            ttlMinutes = ttl;
            i++;
            break;
        case "--relay" when i + 1 < args.Length:
            relayArg = args[++i];
            break;
        default:
            if (!int.TryParse(args[i], out port) || port is < 1 or > 65535)
            {
                Console.Error.WriteLine("usage: OVS.Rollback.Rendezvous [port] [--ttl-minutes N] [--relay host:port]");
                return 2;
            }
            break;
    }
}

using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss.fff "; o.SingleLine = true; }).SetMinimumLevel(LogLevel.Information));
var log = loggers.CreateLogger("Rendezvous");

var relay = LocalCandidates.Parse(relayArg);
if (relayArg is not null && relay is null)
{
    log.LogError("--relay {Relay} does not resolve", relayArg);
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var service = new RendezvousService(port, new AcceptAllValidator(), new RendezvousRegistry(TimeSpan.FromMinutes(ttlMinutes)), log, relay);
await service.RunAsync(cts.Token);
log.LogInformation("Rendezvous stopped");
return 0;
