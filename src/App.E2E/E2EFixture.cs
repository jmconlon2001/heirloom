using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: AssemblyFixture(typeof(FamilyMemories.E2E.BrowsersFixture))]

namespace FamilyMemories.E2E;

/// <summary>Installs (first run only) and launches WebKit (iPad Safari) and Chromium.</summary>
public sealed class BrowsersFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser WebKit { get; private set; } = null!;
    public IBrowser Chromium { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var exit = Microsoft.Playwright.Program.Main(["install", "webkit", "chromium"]);
        if (exit != 0) throw new InvalidOperationException($"Playwright browser install failed ({exit})");

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        WebKit = await Playwright.Webkit.LaunchAsync();
        Chromium = await Playwright.Chromium.LaunchAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await WebKit.DisposeAsync();
        await Chromium.DisposeAsync();
        if (fakeMicChromium is not null) await fakeMicChromium.DisposeAsync();
        Playwright.Dispose();
    }

    /// <summary>iPad Safari: Playwright's iPad Pro 11 descriptor (834×1194, touch, WebKit).</summary>
    /// <summary>
    /// The iPad profile. With <paramref name="microphone"/>, uses WebKit when this platform's WebKit build can
    /// record (macOS); Playwright's Linux WebKit has no MediaRecorder/microphone, so on Linux CI the same iPad
    /// profile runs on Chromium with a fake microphone instead (see PROGRESS.md).
    /// </summary>
    public async Task<IBrowserContext> NewIPadAsync(Uri baseUrl, bool microphone = false)
    {
        var ipad = Playwright.Devices["iPad Pro 11"];
        ipad.BaseURL = baseUrl.ToString();
        var forceChromium = Environment.GetEnvironmentVariable("E2E_RECORD_BROWSER") == "chromium";
        if (microphone && (forceChromium || !await WebKitCanRecordAsync()))
        {
            var context = await (await FakeMicChromiumAsync()).NewContextAsync(ipad);
            await context.GrantPermissionsAsync(["microphone"]);
            return context;
        }
        var webkit = await WebKit.NewContextAsync(ipad);
        if (microphone) await webkit.GrantPermissionsAsync(["microphone"]);
        return webkit;
    }

    private bool? webKitCanRecord;
    private IBrowser? fakeMicChromium;

    private async Task<bool> WebKitCanRecordAsync()
    {
        if (webKitCanRecord is { } known) return known;
        await using var context = await WebKit.NewContextAsync();
        var page = await context.NewPageAsync();
        webKitCanRecord = await page.EvaluateAsync<bool>(
            "typeof MediaRecorder !== 'undefined' && !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia)");
        return webKitCanRecord.Value;
    }

    private async Task<IBrowser> FakeMicChromiumAsync() =>
        fakeMicChromium ??= await Playwright.Chromium.LaunchAsync(new()
        {
            Args = ["--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream"],
        });
}

/// <summary>The real app on a real Kestrel port, so a real browser can reach it.</summary>
public sealed class KestrelAppFactory : AppFactory
{
    public KestrelAppFactory(PostgresFixture postgres) : base(postgres) => UseKestrel(0);

    public Uri BaseUrl { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        StartServer();
        var address = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        BaseUrl = new Uri(address.Replace("[::]", "localhost").Replace("127.0.0.1", "localhost"));
    }
}
