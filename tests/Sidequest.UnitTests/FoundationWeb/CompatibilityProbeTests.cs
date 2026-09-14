using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Components;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Exercises real Fluent components and Blazor validation without relying on browser JavaScript execution.</summary>
/// <remarks>Each test owns its renderer. UI callbacks run through the renderer dispatcher, not context-free continuations.</remarks>
public sealed class CompatibilityProbeTests : BunitContext
{
    /// <summary>Registers Fluent services and permits package-owned JavaScript calls in the bUnit renderer.</summary>
    public CompatibilityProbeTests()
    {
        Services.AddFluentUIComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Verifies that an empty submission displays required-field errors and leaves the dialog closed.</summary>
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

    /// <summary>Verifies valid binding, dialog content, and close feedback without persistence at label-length boundaries.</summary>
    /// <param name="length">The valid label length in characters.</param>
    /// <returns>A task completing after the bound input and dialog callbacks are exercised.</returns>
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

    /// <summary>Verifies that an 81-character label is rejected and correcting it clears errors and opens the preview.</summary>
    /// <returns>A task completing after invalid and corrected submissions are rendered.</returns>
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
