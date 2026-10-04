using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using OVS.Rollback.Utils;
using Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Serilog.Templates;
using Xunit;

namespace OVS.Rollback.P2P.Tests;

// AddressMask's switch is process-wide and only ever turns on, and its infra set only grows: each test registers its
// own addresses, and the one test that needs the switch off checks that first and turns it on itself.
public class AddressMaskTests
{
    private static readonly string SerilogConfig = Path.Combine(AppContext.BaseDirectory, "Configuration", "Logging", "Runtime", "serilog-config.json");

    [Theory]
    [InlineData("path to player 1 open at 172.21.0.5:41234 after 0.3 s", "path to player 1 open at X.X.X.5:41234 after 0.3 s")]
    [InlineData("player 2 is at 203.0.113.217:50123 (LAN: 192.168.1.20:41234, 10.0.0.7:41234); probing",
                "player 2 is at X.X.X.217:50123 (LAN: X.X.X.20:41234, X.X.X.7:41234); probing")]
    [InlineData("Received connection from IP address: 198.51.100.4", "Received connection from IP address: X.X.X.4")]
    [InlineData("this node's public address is 100.64.0.1.", "this node's public address is X.X.X.1.")]
    [InlineData("endpoint [::ffff:172.21.0.9]:41234", "endpoint [::ffff:X.X.X.9]:41234")]
    [InlineData("from 2001:db8::5 and [2001:db8:0:0:1:0:0:abcd]:41234", "from X:X:X:X:X:X:X:5 and [X:X:X:X:X:X:X:abcd]:41234")]
    [InlineData("a port too long 172.21.0.5:123456", "a port too long X.X.X.5:123456")]
    public void Masks_players_addresses_keeping_the_last_part_and_the_port(string text, string masked)
    {
        Assert.Equal(masked, AddressMask.Mask(text));
    }

    [Theory]
    [InlineData("Waiting for the game to connect to 127.0.0.1:41234")]
    [InlineData("Node listening on 0.0.0.0:41234 and [::1]:41234, [::]:5")]
    [InlineData("OVS Rollback Node version 1.0.0-default-version, engine built 2026-10-04T18:06:12")]
    [InlineData("[2026-10-04 18:06:12.345 +02:00][INF] Tick 13.47 ms, rift 0.50, frames 1.2.3.4.5")]
    [InlineData("Match 6ac2b820065a878d8971257c: forwarding the game to p2p.openversus.org:41235 (relay)")]
    [InlineData("MAC aa:bb:cc:dd:ee:ff, ratio 10:1, at 12:34:56")]
    public void Leaves_everything_else_alone(string text)
    {
        Assert.Equal(text, AddressMask.Mask(text));
    }

    [Fact]
    public void Shows_a_registered_endpoint_and_masks_its_address_on_any_other_port()
    {
        AddressMask.AddInfra(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41235));
        AddressMask.AddInfra(new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.11"), 7777));

        Assert.Equal("rendezvous 192.0.2.10:41235, peer X.X.X.10:50000, bare X.X.X.10",
            AddressMask.Mask("rendezvous 192.0.2.10:41235, peer 192.0.2.10:50000, bare 192.0.2.10"));
        Assert.Equal("relay 192.0.2.11:7777", AddressMask.Mask("relay 192.0.2.11:7777"));
    }

    [Theory]
    [InlineData("192.0.2.20:41235", "192.0.2.20:41235")]
    [InlineData(" http://192.0.2.21:8000/ ", "192.0.2.21:8000")]
    [InlineData("http://[2001:db8::21]:8000", "[2001:db8::21]:8000")]
    public void Registers_a_setting_that_gives_an_address(string setting, string shown)
    {
        Assert.NotEqual(shown, AddressMask.Mask(shown));
        AddressMask.AddInfra(setting);
        Assert.Equal(shown, AddressMask.Mask(shown));
    }

    [Theory]
    [InlineData("p2p.openversus.org:41235")]
    [InlineData("https://prod.openversus.org/")]
    [InlineData("192.0.2.30")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_a_setting_without_an_address_and_port(string? setting)
    {
        AddressMask.AddInfra(setting);
        Assert.Equal("X.X.X.30:41235", AddressMask.Mask("192.0.2.30:41235"));
    }

    [Fact]
    public void Every_sink_in_the_shipped_config_formats_through_the_mask()
    {
        var formatters = JsonNode.Parse(File.ReadAllText(SerilogConfig))!.AsObject()
            .Descendants().Where(n => n.Key == "formatter").Select(n => (string?)n.Value!["type"]).ToList();
        Assert.Equal(2, formatters.Count);
        Assert.All(formatters, t => Assert.Equal("Serilog.Templates.AddressMaskingTemplate, OVS.Rollback.Server", t));
    }

    [Fact]
    public void The_shipped_config_writes_unchanged_until_the_node_turns_masking_on()
    {
        Assert.False(AddressMask.Enabled, "another test turned masking on first");
        string file = Path.Combine(Path.GetTempPath(), $"addressmask-{Guid.NewGuid():N}.log");
        try
        {
            var exception = new InvalidOperationException("send to 172.21.0.6:41234 failed");
            // One logger per phase, each disposed (flushed) before the switch moves: the file sink is asynchronous and
            // formats when it writes, so a line logged before Enable but written after it is masked too.
            using (var logger = ShippedLogger(file))
            {
                logger.Information("Datagram from {From} handled", new IPEndPoint(IPAddress.Parse("172.21.0.5"), 41234));
            }
            AddressMask.Enable();
            using (var logger = ShippedLogger(file))
            {
                logger.Information("Datagram from {From} handled", new IPEndPoint(IPAddress.Parse("172.21.0.5"), 41234));
                logger.Error(exception, $"Handling a datagram from {"172.21.0.7:41234"} failed; game at 127.0.0.1:41234");
            }
            string[] lines = File.ReadAllLines(file);
            Assert.Contains("Datagram from 172.21.0.5:41234 handled", lines[0]);
            Assert.Contains("Datagram from X.X.X.5:41234 handled", lines[1]);
            Assert.Contains("Handling a datagram from X.X.X.7:41234 failed; game at 127.0.0.1:41234", lines[2]);
            string all = string.Join('\n', lines.Skip(1));
            Assert.Contains("send to X.X.X.6:41234 failed", all);
            Assert.DoesNotContain("172.21.0.", all);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_formatter_writes_what_the_expression_template_writes_while_masking_is_off()
    {
        // Byte for byte in the relay: compare against the template it wraps, with no address in the line, so the result
        // holds whether or not the test above has turned masking on yet.
        const string template = "[{@t:yyyy-MM-dd HH:mm:ss.fff zzz}][{@l:u3}] {SourceContext}(): {@m:lj} \n{@x}";
        var e = new LogEvent(DateTimeOffset.Now, LogEventLevel.Warning, new Exception("boom"),
            new MessageTemplateParser().Parse("Tick {Ms} ms"), [new LogEventProperty("Ms", new ScalarValue(13.47))]);
        var expected = new StringWriter();
        new ExpressionTemplate(template).Format(e, expected);
        var actual = new StringWriter();
        new AddressMaskingTemplate(template).Format(e, actual);
        Assert.Equal(expected.ToString(), actual.ToString());
    }

    /// <summary>The root logger as DIContainer builds it, from the serilog-config.json that ships, its file sink at <paramref name="file"/>.</summary>
    private static Serilog.Core.Logger ShippedLogger(string file)
    {
        var config = new ConfigurationBuilder().AddJsonFile(SerilogConfig).Build();
        string key = config.AsEnumerable().First(kvp => kvp.Value == "__DO_NOT_EDIT_PLACEHOLDER_PATH__").Key;
        config = new ConfigurationBuilder()
            .AddJsonFile(SerilogConfig)
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = file })
            .Build();
        return new LoggerConfiguration().ReadFrom.Configuration(config).CreateLogger();
    }
}

file static class JsonNodeExtensions
{
    public static IEnumerable<KeyValuePair<string, JsonNode?>> Descendants(this JsonNode node)
    {
        if (node is JsonObject o)
        {
            foreach (var kvp in o)
            {
                yield return kvp;
                if (kvp.Value is not null)
                {
                    foreach (var d in kvp.Value.Descendants()) yield return d;
                }
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var item in a)
            {
                if (item is not null)
                {
                    foreach (var d in item.Descendants()) yield return d;
                }
            }
        }
    }
}
