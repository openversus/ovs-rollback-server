using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OVS.Rollback.Configuration;
using OVS.Rollback.Utils;
using Xunit;
using static OVS.Rollback.Configuration.NodeConfigMerge;

namespace OVS.Rollback.P2P.Tests;

public class NodeLockdownTests
{
    // ── Signatures ──

    private static (string PublicKey, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    [Fact]
    public void A_signature_verifies_only_for_its_body_and_its_key()
    {
        var (publicKey, key) = NewKey();
        var (otherPublicKey, _) = NewKey();
        byte[] body = Encoding.UTF8.GetBytes("{\"version\":1,\"config\":{}}");
        string signature = Convert.ToBase64String(key.SignData(body, HashAlgorithmName.SHA256));

        Assert.True(ServerSignature.Verify(publicKey, body, signature));
        Assert.False(ServerSignature.Verify(publicKey, Encoding.UTF8.GetBytes("{\"version\":1,\"config\":{} }"), signature));
        Assert.False(ServerSignature.Verify(otherPublicKey, body, signature));
        Assert.False(ServerSignature.Verify(publicKey, body, null));
        Assert.False(ServerSignature.Verify(publicKey, body, "not base64!"));
        Assert.False(ServerSignature.Verify("", body, signature));
        // The DER form is not the wire's (IEEE P1363 r||s): refused, not guessed at.
        Assert.False(ServerSignature.Verify(publicKey, body, Convert.ToBase64String(key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))));
    }

    [SkippableFact]
    public void A_signature_made_the_way_the_TS_server_makes_it_verifies()
    {
        // crypto.sign("sha256", body, { key, dsaEncoding: "ieee-p1363" }), with a PKCS#8 PEM key: the server's exact call.
        Skip.If(!CanRun("node", "--version"), "needs Node.js on PATH");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pem = key.ExportPkcs8PrivateKeyPem();
        string body = "{\"matchId\":\"m\",\"players\":[{\"player_index\":0}]}";
        string script = "const c=require('crypto');let i='';process.stdin.on('data',d=>i+=d).on('end',()=>{const {pem,body}=JSON.parse(i);"
            + "process.stdout.write(c.sign('sha256',Buffer.from(body),{key:pem,dsaEncoding:'ieee-p1363'}).toString('base64'));});";
        string signature = Run("node", ["-e", script], new JsonObject { ["pem"] = pem, ["body"] = body }.ToJsonString());

        Assert.True(ServerSignature.Verify(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Encoding.UTF8.GetBytes(body), signature));
    }

    // ── Classification ──

    [Fact]
    public void Every_setting_has_a_scope_and_only_the_server_url_is_fixed_by_the_build()
    {
        var unscoped = new List<string>();
        var build = new List<string>();
        foreach (var section in Sections())
        {
            foreach (var setting in Settings(section.PropertyType))
            {
                try
                {
                    if (ScopeOf(section.PropertyType, setting) == NodeScope.Build) build.Add($"{section.Name}.{setting.Name}");
                }
                catch (InvalidOperationException)
                {
                    unscoped.Add($"{section.Name}.{setting.Name}");
                }
            }
        }
        Assert.Empty(unscoped);
        Assert.Equal(["Server.BaseUrl"], build);
    }

    [Theory]
    // The fairness list as confirmed on 2026-10-04, a sample from each part of it.
    [InlineData("RiftCalculation", "HostPingParity", NodeScope.Fairness)]
    [InlineData("GameLogic", "DisconnectTimeoutSeconds", NodeScope.Fairness)]
    [InlineData("InputValidation", "MaxInputsPerSecond", NodeScope.Fairness)]
    [InlineData("DesyncDetection", "KickDesyncingPlayer", NodeScope.Fairness)]
    [InlineData("PingPhase", "TotalPings", NodeScope.Fairness)]
    [InlineData("Performance", "TargetFrameRate", NodeScope.Fairness)]
    [InlineData("Node", "ForwarderGraceSeconds", NodeScope.Fairness)]
    [InlineData("Server", "MaxPlayers", NodeScope.Fairness)]
    [InlineData("Networking", "ReceiveBufferSize", NodeScope.Fairness)]
    [InlineData("Networking", "HttpTimeoutSeconds", NodeScope.Fairness)]
    [InlineData("Node", "Rendezvous", NodeScope.Player)]
    [InlineData("Node", "PortFile", NodeScope.Player)]
    [InlineData("Networking", "DscpValue", NodeScope.Player)]
    [InlineData("Logging", "MinimumLevel", NodeScope.Player)]
    [InlineData("Server", "Port", NodeScope.Player)]
    public void The_confirmed_list_is_what_the_attributes_say(string section, string setting, NodeScope scope)
    {
        var sectionProperty = Sections().Single(s => s.Name == section);
        Assert.Equal(scope, ScopeOf(sectionProperty.PropertyType, sectionProperty.PropertyType.GetProperty(setting)!));
    }

    // ── The merge ──

    private static ServerConfiguration BuiltIn() => new()
    {
        RiftCalculation = new() { TargetRift = 0.5f, HostPingParity = true },
        Networking = new() { ReceiveBufferSize = 65536, DscpValue = 46 },
        Node = new() { Rendezvous = "", PunchTimeoutSeconds = 8 },
        Server = new() { BaseUrl = "https://prod.example", Port = 41234 },
        Logging = new() { MinimumLevel = "Information" },
    };

    private static ServerConfiguration Live(Action<ServerConfiguration>? edit = null)
    {
        var live = BuiltIn();
        edit?.Invoke(live);
        return live;
    }

    private static JsonObject Update(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Without_an_update_a_players_fairness_values_go_back_to_the_built_in_ones_and_their_own_settings_stay()
    {
        var live = Live(c =>
        {
            c.RiftCalculation.TargetRift = 3.0f;
            c.Networking.ReceiveBufferSize = 512;
            c.Networking.DscpValue = 0;
            c.Node.Rendezvous = "p2p.example:41235";
        });

        var changes = Apply(live, BuiltIn(), null, unlocked: false);

        Assert.Equal(0.5f, live.RiftCalculation.TargetRift);
        Assert.Equal(65536, live.Networking.ReceiveBufferSize);
        Assert.Equal(0, live.Networking.DscpValue);
        Assert.Equal("p2p.example:41235", live.Node.Rendezvous);
        Assert.Equal(
            [new Change("Networking.ReceiveBufferSize", ChangeKind.PlayerValueOverridden, "512", "65536"),
             new Change("RiftCalculation.TargetRift", ChangeKind.PlayerValueOverridden, "3", "0.5")],
            changes.OrderBy(c => c.Setting));
    }

    [Fact]
    public void An_update_that_names_one_section_changes_only_that_section()
    {
        var live = Live(c => c.Networking.DscpValue = 0);

        var changes = Apply(live, BuiltIn(), Update("""{ "RiftCalculation": { "TargetRift": 1.5 } }"""), unlocked: false);

        Assert.Equal(1.5f, live.RiftCalculation.TargetRift);
        Assert.True(live.RiftCalculation.HostPingParity);
        Assert.Equal(65536, live.Networking.ReceiveBufferSize);
        Assert.Equal(0, live.Networking.DscpValue);
        Assert.Equal(8, live.Node.PunchTimeoutSeconds);
        Assert.Equal([new Change("RiftCalculation.TargetRift", ChangeKind.FromUpdate, "0.5", "1.5")], changes);
    }

    [Fact]
    public void A_fairness_value_from_the_update_wins_over_the_player_and_the_built_in_value()
    {
        var live = Live(c => c.Node.PunchTimeoutSeconds = 99);

        var changes = Apply(live, BuiltIn(), Update("""{ "node": { "punchTimeoutSeconds": 12 } }"""), unlocked: false);

        Assert.Equal(12, live.Node.PunchTimeoutSeconds);
        Assert.Equal([new Change("Node.PunchTimeoutSeconds", ChangeKind.PlayerValueOverridden, "99", "12")], changes);
    }

    [Fact]
    public void An_update_changes_a_players_own_setting_only_where_they_left_the_built_in_value()
    {
        var update = Update("""{ "Networking": { "DscpValue": 34 }, "Logging": { "MinimumLevel": "Debug" } }""");
        var untouched = Live();
        var changed = Live(c => c.Networking.DscpValue = 0);

        Apply(untouched, BuiltIn(), update, unlocked: false);
        Apply(changed, BuiltIn(), update, unlocked: false);

        Assert.Equal(34, untouched.Networking.DscpValue);
        Assert.Equal("Debug", untouched.Logging.MinimumLevel);
        Assert.Equal(0, changed.Networking.DscpValue);
        Assert.Equal("Debug", changed.Logging.MinimumLevel);
    }

    [Fact]
    public void An_unreadable_update_value_counts_as_absent()
    {
        var live = Live(c => c.Networking.ReceiveBufferSize = 512);

        var changes = Apply(live, BuiltIn(), Update("""{ "Networking": { "ReceiveBufferSize": "big" }, "RiftCalculation": { "HostPingParity": null } }"""), unlocked: false);

        Assert.Equal(65536, live.Networking.ReceiveBufferSize);
        Assert.True(live.RiftCalculation.HostPingParity);
        Assert.Contains(new Change("Networking.ReceiveBufferSize", ChangeKind.Unreadable, "512", "\"big\""), changes);
        Assert.Contains(new Change("RiftCalculation.HostPingParity", ChangeKind.Unreadable, "True", "null"), changes);
    }

    [Fact]
    public void The_server_url_is_the_builds_whatever_the_update_says()
    {
        var live = Live();

        var changes = Apply(live, BuiltIn(), Update("""{ "Server": { "BaseUrl": "http://elsewhere", "MaxPlayers": 4 } }"""), unlocked: false);

        Assert.Equal("https://prod.example", live.Server.BaseUrl);
        Assert.Equal(4, live.Server.MaxPlayers);
        Assert.DoesNotContain(changes, c => c.Setting == "Server.BaseUrl");
    }

    [Fact]
    public void An_update_naming_every_setting_at_its_built_in_value_reads_back_and_changes_nothing()
    {
        // Every property type the settings use (int, uint, byte, ushort, float, bool, string) through the JSON context.
        var update = new JsonObject();
        var builtIn = new ServerConfiguration();
        int named = 0;
        foreach (var section in Sections())
        {
            var json = new JsonObject();
            foreach (var setting in Settings(section.PropertyType))
            {
                json[setting.Name] = JsonValue.Create(setting.GetValue(section.GetValue(builtIn)));
                named++;
            }
            update[section.Name] = json;
        }

        var changes = Apply(new ServerConfiguration(), builtIn, update, unlocked: false);

        Assert.True(named > 60, $"only {named} settings named");
        Assert.Empty(changes);
    }

    [Fact]
    public void An_unlocked_build_keeps_the_players_fairness_values()
    {
        var live = Live(c => c.RiftCalculation.TargetRift = 3.0f);

        var changes = Apply(live, BuiltIn(), Update("""{ "RiftCalculation": { "TargetRift": 1.5, "HostPingParity": false } }"""), unlocked: true);

        Assert.Equal(3.0f, live.RiftCalculation.TargetRift);
        Assert.False(live.RiftCalculation.HostPingParity);
        Assert.Equal([new Change("RiftCalculation.HostPingParity", ChangeKind.FromUpdate, "True", "False")], changes);
    }

    private static bool CanRun(string file, params string[] args)
    {
        try
        {
            Run(file, args, "");
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string Run(string file, string[] args, string stdin)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} exited {process.ExitCode}: {process.StandardError.ReadToEnd()}");
        return output.Trim();
    }
}
