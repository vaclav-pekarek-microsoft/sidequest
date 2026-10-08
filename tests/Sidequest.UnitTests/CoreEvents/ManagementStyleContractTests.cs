namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Guards the responsive and readable management-tab stylesheet contract.</summary>
public sealed class ManagementStyleContractTests
{
    /// <summary>Management tabs wrap without horizontal scrolling and retain explicit hover and selected colors.</summary>
    [Fact]
    public void ManagementTabs_WrapWithoutHorizontalOverflowAndPreserveReadableStates()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sidequest.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var style = File.ReadAllText(Path.Combine(root.FullName, "src", "Sidequest.Web", "wwwroot", "app.scss"));
        var start = style.IndexOf(".management-tabs {", StringComparison.Ordinal);
        var end = style.IndexOf(".management-list {", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var contract = style[start..end];

        Assert.Contains("flex-wrap: wrap", contract);
        Assert.Contains("overflow: visible", contract);
        Assert.DoesNotContain("overflow-x: auto", contract);
        Assert.Contains("&:hover:not(:disabled)", contract);
        Assert.Contains("color: var(--sq-blue-dark)", contract);
        Assert.Contains("button.selected", contract);
        Assert.Contains("color: var(--sq-blue)", contract);
        Assert.Contains("background: white", contract);
    }
}
