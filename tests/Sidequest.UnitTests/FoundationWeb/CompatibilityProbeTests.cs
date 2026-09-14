using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Components;

namespace Sidequest.UnitTests.FoundationWeb;

public sealed class CompatibilityProbeTests : BunitContext
{
    public CompatibilityProbeTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void EmptyFormShowsValidationErrorWithoutOpeningDialog()
    {
        var component = Render<CompatibilityProbe>();
        Assert.True(component.FindComponent<FluentDialog>().Instance.Hidden);
        component.Find("form").Submit();
        Assert.Contains("Enter a preview label", component.Find("[role=alert]").TextContent);
        Assert.Contains("required", component.Find(".validation-message").TextContent);
        Assert.True(component.FindComponent<FluentDialog>().Instance.Hidden);
        Assert.Empty(component.FindAll("[role=status]"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    public async Task ValidFluentInputOpensAndClosesPreviewWithoutSaving(int length)
    {
        var component = Render<CompatibilityProbe>();
        var text = new string('A', length);
        var field = component.FindComponent<FluentTextField>();
        await component.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(text));
        component.Find("form").Submit();
        Assert.Equal(text, field.Instance.Value);
        Assert.False(component.FindComponent<FluentDialog>().Instance.Hidden);
        Assert.Contains(text, component.FindComponent<FluentDialogBody>().Markup);
        Assert.Empty(component.FindAll(".validation-message"));
        var close = component.FindComponents<FluentButton>().Single(b => b.Markup.Contains("Close preview"));
        await component.InvokeAsync(() => close.Instance.OnClick.InvokeAsync());
        Assert.True(component.FindComponent<FluentDialog>().Instance.Hidden);
        Assert.Equal("Preview completed. Nothing was saved.", component.Find("[role=status]").TextContent);
    }

    [Fact]
    public async Task OverlongInputShowsErrorAndCanBeCorrected()
    {
        var component = Render<CompatibilityProbe>();
        var field = component.FindComponent<FluentTextField>();
        await component.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(new string('A', 81)));
        component.Find("form").Submit();
        Assert.Contains("80", component.Find(".validation-message").TextContent);
        Assert.True(component.FindComponent<FluentDialog>().Instance.Hidden);
        await component.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("Corrected"));
        component.Find("form").Submit();
        Assert.Empty(component.FindAll("[role=alert]"));
        Assert.False(component.FindComponent<FluentDialog>().Instance.Hidden);
    }
}
