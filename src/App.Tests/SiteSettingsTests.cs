using System.Net;
using FamilyMemories.Admin;
using FamilyMemories.Configuration;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class SiteSettingsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);
    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private HttpClient NewDevice() => app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    [Fact]
    public async Task Admin_sets_the_name_help_phone_and_starting_display_for_everyone()
    {
        var admin = await Browser.SignedInAsync(app, AppFactory.AdminEmail);
        var form = await admin.Http.GetStringAsync("/admin/site");
        Assert.Contains("the .env file says \"(555) 010-0199\"", WebUtility.HtmlDecode(form));

        // A phone number has to be a phone number.
        var bad = WebUtility.HtmlDecode(await (await admin.Http.PostFormAsync("/admin/site", form,
            ("SiteName", ""), ("HelpName", ""), ("HelpPhone", "call me"), ("TextSize", "0"))).Content.ReadAsStringAsync());
        Assert.Contains("Please type a phone number using digits", bad);

        var saved = await admin.Http.PostFormAsync("/admin/site", form,
            ("SiteName", "The Conlon Family Stories"), ("HelpName", "your grandson Jacob"), ("HelpPhone", "+1 (555) 010-0123"),
            ("TextSize", "1"), ("HighContrast", "true"));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        // A device that has never chosen gets the family's starting display, and sees the new name and number.
        var landing = WebUtility.HtmlDecode(await NewDevice().GetStringAsync("/"));
        Assert.Contains("class=\"text-1 high-contrast\"", landing);
        Assert.Contains("<title>The Conlon Family Stories</title>", landing);
        Assert.Contains("Kept with Heirloom", landing);
        Assert.Contains("Call your grandson Jacob at <a href=\"tel:+15550100123\" class=\"help-phone\">+1 (555) 010-0123</a>", landing);

        // A device that has chosen keeps its choice.
        var chosen = NewDevice();
        chosen.DefaultRequestHeaders.Add("Cookie", "fm_display=s0.c0");
        Assert.Contains("class=\"text-0\"", await chosen.GetStringAsync("/"));

        // Emails follow straight away.
        await app.CreateUserAsync("Rose Smith", "rose@example.test", [Roles.Storyteller], PromptSetKind.Core);
        var rose = new Browser(app);
        await rose.SignInAsync("rose@example.test");
        var email = app.Email.To("rose@example.test").Last();
        Assert.Contains("The Conlon Family Stories", email.Subject + email.HtmlBody);
        Assert.Contains("Need help? Call your grandson Jacob at +1 (555) 010-0123.", email.TextBody + WebUtility.HtmlDecode(email.HtmlBody));

        // Emptying a box goes back to the .env value.
        form = await admin.Http.GetStringAsync("/admin/site");
        await admin.Http.PostFormAsync("/admin/site", form, ("SiteName", ""), ("HelpName", ""), ("HelpPhone", ""), ("TextSize", "0"));
        var options = app.Services.GetRequiredService<AppOptions>();
        Assert.Equal(("Heirloom", "Jacob", "(555) 010-0199", 0, false),
            (options.AppName, options.HelpName, options.HelpPhone, options.DefaultTextSize, options.DefaultHighContrast));
    }

    [Fact]
    public async Task Only_admins_can_change_site_settings()
    {
        await app.CreateUserAsync("Lily Smith", "lily@example.test", [Roles.Family]);
        var lily = await Browser.SignedInAsync(app, "lily@example.test");
        Assert.NotEqual(HttpStatusCode.OK, (await lily.Http.GetAsync("/admin/site")).StatusCode);
    }

    [Fact]
    public void Saved_settings_override_the_env_file_and_empty_ones_fall_back()
    {
        var options = AppOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APP_NAME"] = "Smith Stories", ["HELP_NAME"] = "David", ["HELP_PHONE"] = "555-0100",
        }).Build());
        new SiteSettings(null, "Ann", null, 2, true).ApplyTo(options);
        Assert.Equal(("Smith Stories", "Ann", "555-0100", 2, true),
            (options.AppName, options.HelpName, options.HelpPhone, options.DefaultTextSize, options.DefaultHighContrast));
    }

    [Theory]
    [InlineData("(555) 010-0199", true)]
    [InlineData("+44 20 7946 0958", true)]
    [InlineData("555.010.0199 ext. 12", true)]
    [InlineData("call me", false)]
    [InlineData("12", false)]
    public void Recognises_phone_numbers(string text, bool ok) => Assert.Equal(ok, SiteSettings.IsPhoneNumber(text));
}
