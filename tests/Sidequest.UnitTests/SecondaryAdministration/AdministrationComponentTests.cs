using Bunit;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Administration;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Administration;

namespace Sidequest.UnitTests.SecondaryAdministration;

/// <summary>Tests accessible dynamic feedback, safe synthetic rendering and the actual shared page lifecycle without live providers.</summary>
public sealed class AdministrationComponentTests : BunitContext
{
    /// <summary>Dynamic errors are displayed as encoded text rather than the property name or executable markup.</summary>
    [Fact]
    public void FeedbackBindsDynamicErrorsAndEncodesMarkup()
    {
        var cut = Render<AdminFeedback>(p => p.Add(x => x.Error, "<script>conflict</script>").Add(x => x.Status, "Unsaved edits retained."));
        Assert.Equal("<script>conflict</script>", cut.Find("[role=alert]").TextContent);
        Assert.Empty(cut.FindAll("script"));
        Assert.Equal("Unsaved edits retained.", cut.Find("[role=status]").TextContent);
    }

    /// <summary>The real template renderer's synthetic output remains inert when displayed in the HTML preview component.</summary>
    [Fact]
    public void PreviewRendersOnlyValidatedSyntheticOutput()
    {
        var value = BusinessEmailRules.Render(BusinessEmailRules.Default("quest.invitation"),
            NotificationKind.QuestInvitation, "<img src=x>", null);
        var cut = Render<TemplatePreview>(p => p.Add(x => x.Preview, value));
        Assert.Empty(cut.FindAll("img,script,iframe,a"));
        Assert.Contains("<img src=x>", cut.Find("strong").TextContent, StringComparison.Ordinal);
        Assert.Contains("Synthetic preview — not sent", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("You have a Quest invitation.", cut.Find("pre").TextContent, StringComparison.Ordinal);
    }

    /// <summary>Static prerender controls remain disabled; attached interactive controls track pending operations without delay hacks.</summary>
    /// <param name="interactive">Whether the renderer has attached event handlers.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlsStayDisabledUntilInteractiveAndIdle(bool interactive)
    {
        SetRendererInfo(new(interactive ? "Server" : "Static", interactive));
        var cut = Render<AdministrationProbe>();
        Assert.Equal(!interactive, cut.Find("button").HasAttribute("disabled"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cut.InvokeAsync(() => cut.Instance.ExecuteAsync(gate.Task));
        cut.Render();
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        gate.SetResult();
        await pending;
        cut.Render();
        Assert.Equal(!interactive, cut.Find("button").HasAttribute("disabled"));
    }

    /// <summary>Conflict retains unsaved input and reports the real message; a subsequent forbidden outcome clears protected state.</summary>
    [Fact]
    public async Task ConflictPreservesDraftAndForbiddenClearsProtectedState()
    {
        SetRendererInfo(new("Server", true));
        var cut = Render<AdministrationProbe>();
        await cut.InvokeAsync(() => cut.Instance.ExecuteAsync(Task.FromException(new DomainException(ErrorCode.Conflict, "Another revision won."))));
        cut.Render();
        Assert.Equal("Original unsaved draft", cut.Find("input").GetAttribute("value"));
        Assert.Equal("Another revision won.", cut.Find("[role=alert]").TextContent);
        Assert.Contains("Protected loaded data", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Instance.ExecuteAsync(Task.FromException(new DomainException(ErrorCode.Forbidden, "Administrator access is required."))));
        cut.Render();
        Assert.DoesNotContain("Protected loaded data", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("Administrator access is required.", cut.Find("[role=alert]").TextContent);
    }

    /// <summary>Focused navigation reuses the existing redacted replay screen instead of introducing another recovery boundary.</summary>
    [Fact]
    public void AdministrationLinksReuseExistingRedactedReplay()
    {
        Services.AddFluentUIComponents();
        var cut = Render<AdminPanel>();
        Assert.Equal("/notifications/failures", cut.Find("a[href='/notifications/failures']").GetAttribute("href"));
        Assert.Equal(5, cut.FindAll("nav a").Count);
    }

    private sealed class AdministrationProbe : AdminPageBase
    {
        private bool loaded = true;
        private string draft = "Original unsaved draft";

        /// <summary>Runs one controlled operation through the production feedback/conflict lifecycle.</summary>
        /// <param name="operation">Deterministic test operation.</param>
        /// <returns>The handled operation completion task.</returns>
        public Task ExecuteAsync(Task operation) => RunAsync(() => operation);

        /// <inheritdoc />
        protected override void ClearSensitiveState()
        {
            loaded = false;
            draft = "";
        }

        /// <inheritdoc />
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "disabled", Disabled);
            builder.CloseElement();
            builder.OpenElement(2, "input");
            builder.AddAttribute(3, "value", draft);
            builder.CloseElement();
            builder.OpenComponent<AdminFeedback>(4);
            builder.AddComponentParameter(5, nameof(AdminFeedback.Error), Error);
            builder.CloseComponent();
            if (loaded) builder.AddContent(6, "Protected loaded data");
        }
    }
}
