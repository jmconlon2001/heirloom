using FamilyMemories.Auth;
using FamilyMemories.Data;
using FamilyMemories.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyMemories.Tests;

public sealed class MagicLinkServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly AppFactory app = new(postgres);

    public ValueTask InitializeAsync() => app.InitializeAsync();
    public ValueTask DisposeAsync() => app.DisposeAsync();

    private async Task<T> Use<T>(Func<MagicLinkService, AppDbContext, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<MagicLinkService>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<AppUser> CreateUserAsync(string email) => await app.WithDbAsync(async db =>
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = email, UserName = email, NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(), DisplayName = "Rose", CreatedAt = DateTime.UtcNow,
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    });

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored()
    {
        var user = await CreateUserAsync("hash@example.test");
        var token = await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null));
        Assert.NotNull(token);
        Assert.True(token.Length >= 43, "token should carry 256 bits");

        var row = await app.WithDbAsync(db => db.MagicLinkTokens.SingleAsync(t => t.UserId == user.Id));
        Assert.Equal(MagicLinkService.Hash(token), row.TokenHash);
        Assert.DoesNotContain(token, Convert.ToBase64String(row.TokenHash));
    }

    [Fact]
    public async Task A_link_works_exactly_once()
    {
        var user = await CreateUserAsync("once@example.test");
        var token = await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/stories", null));

        Assert.Equal(MagicLinkFailure.None, await Use((links, _) => links.PeekAsync(token)));
        Assert.Equal(MagicLinkFailure.None, await Use((links, _) => links.PeekAsync(token))); // peeking doesn't use it up

        var first = await Use((links, _) => links.RedeemAsync(token, "10.0.0.1"));
        Assert.True(first.Succeeded);
        Assert.Equal(user.Id, first.User!.Id);
        Assert.Equal("/stories", first.ReturnPath);

        var second = await Use((links, _) => links.RedeemAsync(token, "10.0.0.1"));
        Assert.False(second.Succeeded);
        Assert.Equal(MagicLinkFailure.AlreadyUsed, second.Failure);
    }

    [Fact]
    public async Task Concurrent_redemptions_only_one_wins()
    {
        var user = await CreateUserAsync("race@example.test");
        var token = await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Use((links, _) => links.RedeemAsync(token, null))));
        Assert.Single(results, r => r.Succeeded);
    }

    [Fact]
    public async Task Links_expire_after_14_days()
    {
        var user = await CreateUserAsync("expire@example.test");
        var token = await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null));

        app.Clock.Advance(TimeSpan.FromDays(13.9));
        Assert.Equal(MagicLinkFailure.None, await Use((links, _) => links.PeekAsync(token)));
        app.Clock.Advance(TimeSpan.FromDays(0.2));
        Assert.Equal(MagicLinkFailure.Expired, (await Use((links, _) => links.RedeemAsync(token, null))).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task Unknown_tokens_are_rejected(string? token) =>
        Assert.Equal(MagicLinkFailure.NotFound, (await Use((links, _) => links.RedeemAsync(token, null))).Failure);

    [Fact]
    public async Task Locked_out_users_cannot_sign_in()
    {
        var user = await CreateUserAsync("locked@example.test");
        var token = await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null));
        await app.WithDbAsync(db => db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(10))));

        Assert.Equal(MagicLinkFailure.UserDisabled, (await Use((links, _) => links.RedeemAsync(token, null))).Failure);
    }

    [Fact]
    public async Task At_most_five_login_links_per_hour_per_user()
    {
        var user = await CreateUserAsync("flood@example.test");
        for (var i = 0; i < MagicLinkService.MaxLinksPerUserPerHour; i++)
            Assert.NotNull(await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null)));

        Assert.Null(await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null)));

        app.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.NotNull(await Use((links, _) => links.IssueAsync(user, MagicLinkPurpose.Login, "/", null)));
    }
}
