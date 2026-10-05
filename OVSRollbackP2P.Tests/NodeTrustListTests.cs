using Xunit;

namespace OVS.Rollback.P2P.Tests;

public class NodeTrustListTests
{
    private const string Prod = "https://prod.openversus.org/";
    private const string Testing = "http://prod.openversus.org:8420/";
    private static readonly List<NodeTrustEntry> Trust = NodeTrustList.Parse($"{Prod} PRODKEY\n  {Testing}   TESTKEY ");

    [Fact]
    public void Parses_pairs_in_order_without_trailing_slashes()
    {
        Assert.Equal([new NodeTrustEntry("https://prod.openversus.org", "PRODKEY"), new NodeTrustEntry("http://prod.openversus.org:8420", "TESTKEY")], Trust);
        Assert.Empty(NodeTrustList.Parse(""));
        Assert.Empty(NodeTrustList.Parse(null));
    }

    [Theory]
    [InlineData("https://prod.openversus.org/")]
    [InlineData("https://prod.openversus.org/ PRODKEY http://prod.openversus.org:8420/")]
    [InlineData("prod.openversus.org PRODKEY")]
    [InlineData("ftp://prod.openversus.org PRODKEY")]
    public void Refuses_a_list_that_is_not_url_key_pairs(string text)
    {
        Assert.Throws<FormatException>(() => NodeTrustList.Parse(text));
    }

    [Theory]
    // As the game's ServerUrl may be written: with or without the slash, any case, the default port spelled out.
    [InlineData("http://prod.openversus.org:8420/", "TESTKEY")]
    [InlineData("http://PROD.openversus.org:8420", "TESTKEY")]
    [InlineData("https://prod.openversus.org", "PRODKEY")]
    [InlineData("https://prod.openversus.org:443/", "PRODKEY")]
    [InlineData("HTTPS://Prod.OpenVersus.org/", "PRODKEY")]
    public void Selects_the_server_the_mod_names(string requested, string key)
    {
        var entry = NodeTrustList.Select(Trust, requested, out bool matched);

        Assert.True(matched);
        Assert.Equal(key, entry!.PublicKey);
    }

    [Theory]
    [InlineData("http://prod.openversus.org/")]          // the right host on another port is another server
    [InlineData("https://prod.openversus.org:8420/")]    // and another scheme
    [InlineData("http://127.0.0.1:18000/")]
    [InlineData("not a url")]
    public void Gives_an_unknown_server_the_default_and_says_so(string requested)
    {
        var entry = NodeTrustList.Select(Trust, requested, out bool matched);

        Assert.False(matched);
        Assert.Equal("PRODKEY", entry!.PublicKey);
    }

    [Fact]
    public void Without_a_request_the_default_is_used_and_nothing_is_unmatched()
    {
        Assert.Equal("PRODKEY", NodeTrustList.Select(Trust, null, out bool matched)!.PublicKey);
        Assert.False(matched);
        Assert.Null(NodeTrustList.Select([], Prod, out _));
    }
}
