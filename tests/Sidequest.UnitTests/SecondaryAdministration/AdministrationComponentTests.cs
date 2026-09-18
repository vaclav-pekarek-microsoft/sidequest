using Bunit;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Administration;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Administration;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryAdministration;

/// <summary>Tests accessible dynamic feedback, safe synthetic rendering and the actual shared page lifecycle without live providers.</summary>
public sealed class AdministrationComponentTests : BunitContext
{
    private readonly ExperienceCoordinator coordinator = new();
    private readonly OperationLogger logger = new();

    /// <summary>Provides the actual scoped connection coordinator and records unexpected-operation diagnostics.</summary>
    public AdministrationComponentTests()
    {
        Services.AddLogging();
        Services.AddSingleton(coordinator);
        Services.AddSingleton<ILogger<AdminPageBase>>(logger);
    }

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
        await coordinator.ReportConnectionAsync(true, null);
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
        await coordinator.ReportConnectionAsync(true, null);
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

    /// <summary>Contact labels use email first and describe missing contact data without substituting internal identifiers.</summary>
    /// <param name="email">Projected contact address, including absent-contact cases.</param>
    /// <param name="name">Projected display name, including an email-equivalent name.</param>
    /// <param name="expected">Exact visible contact label.</param>
    [Theory]
    [InlineData("person@example.invalid", "Person Name", "person@example.invalid (Person Name)")]
    [InlineData(" person@example.invalid ", " person@example.invalid ", "person@example.invalid")]
    [InlineData("person@example.invalid", null, "person@example.invalid")]
    [InlineData(null, "Person Name", "Contact unavailable (Person Name)")]
    [InlineData("", "", "Contact unavailable")]
    public void AccountLabelsShowEmailFirstAndTruthfulMissingContacts(string? email, string? name, string expected) =>
        Assert.Equal(expected, AccountLabel.Format(email, name));

    /// <summary>Offline and prerender callbacks never invoke application work or replay it on reconnection.</summary>
    /// <param name="interactive">Whether the component has attached interactive handlers.</param>
    /// <returns>Completion after rejected callbacks and the independent authorization-only reconnect.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineAndPrerenderActionsMakeNoCallsAndNeverReplay(bool interactive)
    {
        SetRendererInfo(new("Server", interactive));
        var cut = Render<AdministrationProbe>();
        var calls = 0;
        var reviews = 0;
        await cut.InvokeAsync(() => cut.Instance.PerformReview(() => reviews++));
        await cut.InvokeAsync(() => cut.Instance.PerformAsync(() => { calls++; return Task.CompletedTask; }));
        Assert.Equal(0, calls);
        Assert.Equal(0, reviews);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(0, calls);
        Assert.Equal(1, cut.Instance.Rechecks);
        await cut.InvokeAsync(() => cut.Instance.PerformReview(() => reviews++));
        await cut.InvokeAsync(() => cut.Instance.PerformAsync(() => { calls++; return Task.CompletedTask; }));
        Assert.Equal(interactive ? 1 : 0, calls);
        await coordinator.ReportConnectionAsync(false, null);
        await cut.InvokeAsync(() => cut.Instance.PerformReview(() => reviews++));
        await cut.InvokeAsync(() => cut.Instance.PerformAsync(() => { calls++; return Task.CompletedTask; }));
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(interactive ? 1 : 0, calls);
        Assert.Equal(interactive ? 1 : 0, reviews);
        Assert.Equal(2, cut.Instance.Rechecks);
    }

    /// <summary>Reconnect waits for a pending operation and then authorization, blocking new actions until protected state is cleared.</summary>
    /// <returns>Completion after deterministic operation, authorization and host-refresh ordering assertions.</returns>
    [Fact]
    public async Task ReconnectAwaitsPendingOperationThenClearsRevokedStateBeforeHostRefresh()
    {
        SetRendererInfo(new("Server", true));
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<AdministrationProbe>();
        var operationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        cut.Instance.Check = async () =>
        {
            order.Add("authorization");
            checking.SetResult();
            await authorizationGate.Task;
            throw new DomainException(ErrorCode.Forbidden, "Administrator access is required.");
        };
        var operation = cut.InvokeAsync(() => cut.Instance.PerformAsync(async () =>
        {
            order.Add("operation");
            await operationGate.Task;
            order.Add("completed");
        }));
        await coordinator.ReportConnectionAsync(false, null);
        var reconnect = coordinator.ReportConnectionAsync(true, null);
        Assert.False(reconnect.IsCompleted);
        Assert.Equal(0, cut.Instance.Rechecks);
        var unexpectedCalls = 0;
        await cut.InvokeAsync(() => cut.Instance.PerformAsync(() => { unexpectedCalls++; return Task.CompletedTask; }));
        operationGate.SetResult();
        await operation;
        await checking.Task;
        Assert.False(reconnect.IsCompleted);
        cut.Render();
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        Assert.Equal(new[] { "operation", "completed", "authorization" }, order);
        var hostRefreshed = false;
        coordinator.SnapshotRefreshRequested += () =>
        {
            Assert.Equal("", cut.Find("input").GetAttribute("value"));
            Assert.DoesNotContain("Protected loaded data", cut.Markup, StringComparison.Ordinal);
            hostRefreshed = true;
            return Task.CompletedTask;
        };
        authorizationGate.SetResult();
        await reconnect;
        Assert.True(hostRefreshed);
        Assert.Equal(0, unexpectedCalls);
        Assert.Equal(1, cut.Instance.Rechecks);
    }

    /// <summary>Disposal cancels once, awaits late work, detaches reconnect and keeps a stable token readable after source disposal.</summary>
    /// <returns>Completion after repeated disposal and post-disposal callback rejection.</returns>
    [Fact]
    public async Task DisposalAwaitsLateOperationAndUnsubscribesIdempotentlyWithStableToken()
    {
        SetRendererInfo(new("Server", true));
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<AdministrationProbe>();
        var token = cut.Instance.Token;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationCalls = 0;
        using var registration = token.Register(() => cancellationCalls++);
        var operation = cut.InvokeAsync(() => cut.Instance.PerformAsync(async () =>
        {
            await gate.Task;
            Assert.Equal(token, cut.Instance.Token);
            Assert.True(cut.Instance.Token.IsCancellationRequested);
        }));
        await coordinator.ReportConnectionAsync(false, null);
        var reconnect = coordinator.ReportConnectionAsync(true, null);
        Assert.False(reconnect.IsCompleted);
        var disposal = cut.Instance.DisposeAsync().AsTask();
        Assert.Same(disposal, cut.Instance.DisposeAsync().AsTask());
        Assert.False(disposal.IsCompleted);
        Assert.True(token.IsCancellationRequested);
        await coordinator.ReportConnectionAsync(false, null);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(0, cut.Instance.Rechecks);
        gate.SetResult();
        await operation;
        await disposal;
        await reconnect;
        Assert.Equal(1, cancellationCalls);
        Assert.Equal(token, cut.Instance.Token);
        await cut.Instance.PerformAsync(() => throw new InvalidOperationException("Disposed callbacks must not execute."));
        cut.Render();
        Assert.Equal("", cut.Find("input").GetAttribute("value"));
        Assert.DoesNotContain("Protected loaded data", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>A second disconnect during authorization cannot release the current action gate using an older connection's result.</summary>
    /// <returns>Completion after a stale successful check and a fresh authorization for the next connection.</returns>
    [Fact]
    public async Task DisconnectDuringReauthorizationRequiresAnotherFreshCheck()
    {
        SetRendererInfo(new("Server", true));
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<AdministrationProbe>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cut.Instance.Check = () => { entered.SetResult(); return gate.Task; };
        await coordinator.ReportConnectionAsync(false, null);
        var reconnect = coordinator.ReportConnectionAsync(true, null);
        await entered.Task;
        await coordinator.ReportConnectionAsync(false, null);
        gate.SetResult();
        await reconnect;
        cut.Render();
        Assert.True(cut.Find("button").HasAttribute("disabled"));
        Assert.Equal(1, cut.Instance.Rechecks);
        cut.Instance.Check = () => Task.CompletedTask;
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(2, cut.Instance.Rechecks);
        Assert.False(cut.Find("button").HasAttribute("disabled"));
        Assert.Equal("Original unsaved draft", cut.Find("input").GetAttribute("value"));
    }

    /// <summary>Overlapping reconnect requests cannot finish using an authorization result started before the latest disconnect.</summary>
    /// <returns>Completion after both connection reports await fresh checks and no queued action is executed.</returns>
    [Fact]
    public async Task OverlappingReconnectsCannotCompleteUsingAnEarlierConnectionsCheck()
    {
        SetRendererInfo(new("Server", true));
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<AdministrationProbe>();
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cut.Instance.Check = () =>
        {
            if (cut.Instance.Rechecks == 1)
            {
                firstEntered.SetResult();
                return firstGate.Task;
            }
            latestEntered.TrySetResult();
            return latestGate.Task;
        };
        await coordinator.ReportConnectionAsync(false, null);
        var earlier = coordinator.ReportConnectionAsync(true, null);
        await firstEntered.Task;
        await coordinator.ReportConnectionAsync(false, null);
        var latest = coordinator.ReportConnectionAsync(true, null);
        firstGate.SetResult();
        await latestEntered.Task;
        Assert.False(earlier.IsCompleted);
        Assert.False(latest.IsCompleted);
        var calls = 0;
        await cut.InvokeAsync(() => cut.Instance.PerformAsync(() => { calls++; return Task.CompletedTask; }));
        latestGate.SetResult();
        await Task.WhenAll(earlier, latest);
        Assert.Equal(3, cut.Instance.Rechecks);
        Assert.Equal(0, calls);
        Assert.False(cut.Find("button").HasAttribute("disabled"));
    }

    /// <summary>Unexpected reconnect failures clear protected data and log the exception with the same safe correlation shown to the user.</summary>
    /// <returns>Completion after the logged failure and redacted feedback assertions.</returns>
    [Fact]
    public async Task UnexpectedReconnectFailureLogsCorrelationAndClearsProtectedState()
    {
        SetRendererInfo(new("Server", true));
        var cut = Render<AdministrationProbe>();
        var failure = new InvalidOperationException("PRIVATE provider failure");
        cut.Instance.Check = () => Task.FromException(failure);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Same(failure, logger.Failure);
        Assert.Equal(LogLevel.Error, logger.Level);
        Assert.NotNull(logger.Correlation);
        Assert.Contains($"Reference: {logger.Correlation}.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("", cut.Find("input").GetAttribute("value"));
        Assert.DoesNotContain("Protected loaded data", cut.Markup, StringComparison.Ordinal);
    }

    private sealed class AdministrationProbe : AdminPageBase
    {
        private bool loaded = true;
        private string draft = "Original unsaved draft";
        internal int Rechecks { get; private set; }
        internal Func<Task> Check { get; set; } = () => Task.CompletedTask;
        internal CancellationToken Token => Lifetime;
        internal Task PerformAsync(Func<Task> operation) => RunAsync(operation);
        internal void PerformReview(Action review) => Review(review);

        /// <inheritdoc />
        protected override async Task RefreshAfterReconnectAsync()
        {
            Rechecks++;
            await Check();
        }

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

    private sealed class OperationLogger : ILogger<AdminPageBase>
    {
        internal Exception? Failure { get; private set; }
        internal LogLevel Level { get; private set; }
        internal string? Correlation { get; private set; }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Failure = exception;
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            Correlation = Assert.IsType<string>(properties.Single(x => x.Key == "CorrelationId").Value);
        }
    }
}
