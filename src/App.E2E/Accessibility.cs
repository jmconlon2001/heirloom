using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace FamilyMemories.E2E;

/// <summary>Checks every e2e test applies (BUILD_PROMPT §4 and §13).</summary>
public static class Accessibility
{
    /// <summary>Fails on any serious or critical axe violation.</summary>
    public static async Task AssertAccessibleAsync(IPage page, string because)
    {
        var result = await page.RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22aa", "best-practice"] },
        });
        Assert.NotEmpty(result.Passes); // proves axe actually ran against the page
        var bad = result.Violations.Where(v => v.Impact is "serious" or "critical").ToList();
        Assert.True(bad.Count == 0, $"{because}: " + string.Join("\n", bad.Select(v =>
            $"[{v.Impact}] {v.Id}: {v.Help} -> {string.Join(", ", v.Nodes.Select(n => n.Html))}")));
    }

    public static async Task AssertNoHorizontalScrollAsync(IPage page)
    {
        var overflow = await page.EvaluateAsync<int>("document.documentElement.scrollWidth - document.documentElement.clientWidth");
        Assert.True(overflow <= 0, $"page scrolls sideways by {overflow}px");
    }

    /// <summary>Every visible button and button-styled link is at least 48×48 CSS pixels.</summary>
    public static async Task AssertTapTargetsAsync(IPage page)
    {
        foreach (var button in await page.Locator(":is(button, a.button, label.button):visible:not([aria-hidden=\"true\"])").AllAsync())
        {
            var box = await button.BoundingBoxAsync();
            if (box is null) continue;
            Assert.True(box.Height >= 48 && box.Width >= 48,
                $"tap target too small ({box.Width}×{box.Height}): {await button.InnerTextAsync()}");
        }
    }

    /// <summary>Full-page screenshots for human review, under TestResults/screenshots.</summary>
    public static Task ScreenshotAsync(IPage page, string name) =>
        page.ScreenshotAsync(new() { Path = Path.Combine(AppContext.BaseDirectory, "TestResults", "screenshots", name + ".png"), FullPage = true });
}
