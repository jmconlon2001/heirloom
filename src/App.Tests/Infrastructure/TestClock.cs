namespace FamilyMemories.Tests.Infrastructure;

/// <summary>A clock tests can move forward.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public TestClock() : this(DateTimeOffset.UtcNow) { }
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now = now.Add(by);
}
