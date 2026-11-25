using System.Net;
using FamilyMemories.Configuration;
using FamilyMemories.Web;
using Microsoft.Extensions.Configuration;

namespace FamilyMemories.Tests;

public sealed class AppOptionsTests
{
    private static AppOptions From(params (string Key, string Value)[] values) =>
        AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    [Fact]
    public void Trusted_proxies_accept_single_ips_and_cidr_ranges()
    {
        var networks = AppOptions.ParseNetworks("192.168.1.1, 10.0.0.0/8;fd00::/8");
        Assert.Equal(3, networks.Count);
        Assert.True(networks[0].Contains(IPAddress.Parse("192.168.1.1")));
        Assert.False(networks[0].Contains(IPAddress.Parse("192.168.1.2")));
        Assert.True(networks[1].Contains(IPAddress.Parse("10.20.30.40")));
        Assert.True(networks[2].Contains(IPAddress.Parse("fd12::1")));
    }

    [Fact]
    public void Empty_trusted_proxies_trusts_nobody() => Assert.Empty(AppOptions.ParseNetworks(""));

    [Theory]
    [InlineData("https://memories.example.com")]
    [InlineData("https://memories.example.com/")]
    public void Public_links_use_the_public_base_url(string baseUrl)
    {
        var options = From(("PUBLIC_BASE_URL", baseUrl));
        Assert.Equal("https://memories.example.com/account/magic-link?token=x",
            options.BuildUrl("/account/magic-link?token=x").ToString());
    }

    [Fact]
    public void Public_base_url_with_a_path_keeps_the_path()
    {
        var options = From(("PUBLIC_BASE_URL", "https://example.com/memories"));
        Assert.Equal("https://example.com/memories/account/login", options.BuildUrl("/account/login").ToString());
    }

    [Fact]
    public void Connection_string_is_built_from_postgres_variables()
    {
        var options = From(("POSTGRES_HOST", "db"), ("POSTGRES_DB", "fm"), ("POSTGRES_USER", "u"), ("POSTGRES_PASSWORD", "p"));
        Assert.Equal("Host=db;Port=5432;Database=fm;Username=u;Password=p", options.ConnectionString);
    }

    [Fact]
    public void Defaults_are_sensible()
    {
        var options = From();
        Assert.Equal("Family Memories", options.AppName);
        Assert.Equal("/data/keys", options.KeysDir);
        Assert.Equal(TimeSpan.FromDays(14), options.MagicLinkLifetime);
        Assert.Equal(TimeSpan.FromDays(180), options.RememberedDeviceLifetime);
        Assert.True(options.PreMigrationBackup);
        Assert.Equal(SmtpSecurity.StartTls, options.Smtp.Security);
    }
}

public sealed class DisplayPreferencesTests
{
    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("", 0, false)]
    [InlineData("s1.c0", 1, false)]
    [InlineData("s2.c1", 2, true)]
    [InlineData("s9.c1", 0, true)]
    [InlineData("garbage", 0, false)]
    public void Parses_cookie(string? value, int size, bool contrast)
    {
        var prefs = DisplayPreferences.Parse(value);
        Assert.Equal(size, prefs.TextSize);
        Assert.Equal(contrast, prefs.HighContrast);
    }

    [Fact]
    public void Round_trips() =>
        Assert.Equal(new DisplayPreferences(2, true), DisplayPreferences.Parse(new DisplayPreferences(2, true).ToCookieValue()));

    [Fact]
    public void Html_classes() => Assert.Equal("text-1 high-contrast", new DisplayPreferences(1, true).HtmlClasses);
}
