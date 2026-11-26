using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

[assembly: AssemblyFixture(typeof(FamilyMemories.Tests.Infrastructure.GotenbergFixture))]

namespace FamilyMemories.Tests.Infrastructure;

/// <summary>A real Gotenberg 8 (Chromium) for PDF tests, started once for the run.</summary>
public sealed class GotenbergFixture : IAsyncLifetime
{
    public const string Image = "gotenberg/gotenberg:8.40.0";

    private readonly IContainer container = new ContainerBuilder(Image)
        .WithCommand("gotenberg", "--api-timeout=300s")
        .WithPortBinding(3000, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(3000)))
        .Build();

    public string Url => $"http://{container.Hostname}:{container.GetMappedPublicPort(3000)}";

    public async ValueTask InitializeAsync() => await container.StartAsync();
    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}
