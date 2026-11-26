using System.Net;
using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FamilyMemories.Tests;

public sealed class UpdateCheckTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private sealed class StubGitHub(string json) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpRequestMessage? Seen { get; private set; }
        public HttpClient CreateClient(string name) => new(this);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    [Theory]
    [InlineData("0.12.0", "0.11.0", true)]
    [InlineData("0.11.0", "0.11.0", false)]
    [InlineData("0.10.2", "0.11.0", false)]
    [InlineData("1.0.0", "0.0.0-dev", false)]   // development builds never nag
    [InlineData("1.0.0", "dev", false)]
    public void Compares_versions(string candidate, string current, bool newer) => Assert.Equal(newer, UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public async Task A_newer_release_shows_on_the_admin_home_and_the_request_says_nothing_about_the_family()
    {
        var github = new StubGitHub("""{"tag_name":"v99.0.0","html_url":"https://github.com/jmconlon2001/heirloom/releases/tag/v99.0.0"}""");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var checker = new UpdateChecker(github, scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                scope.ServiceProvider.GetRequiredService<AppOptions>(), TimeProvider.System, NullLogger<UpdateChecker>.Instance);
            await checker.CheckAsync();
        }
        Assert.Equal("https://api.github.com/repos/jmconlon2001/heirloom/releases/latest", github.Seen!.RequestUri!.ToString());
        Assert.Equal("Heirloom-update-check/1", github.Seen.Headers.UserAgent.ToString());
        Assert.Null(github.Seen.Headers.Authorization);

        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var home = WebUtility.HtmlDecode(await admin.Http.GetStringAsync("/admin"));
        if (UpdateChecker.IsNewer("99.0.0", AppOptions.Version))
            Assert.Contains("Version 99.0.0 is available", home);
        else
            Assert.DoesNotContain("is available", home);   // a development build never nags
    }
}
