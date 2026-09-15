using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Administration;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.UnitTests.SecondaryExperience;
using Sidequest.Web.Components.Pages.Administration;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryAdministration;

/// <summary>Exercises all four real pages and application read services with controlled authorization and read-only asynchronous query data.</summary>
public sealed class AdministrationReconnectTests : BunitContext
{
    private readonly ExperienceCoordinator coordinator = new();
    private readonly UserAccount actor = new() { TenantId = Guid.NewGuid(), DisplayName = "Original administrator" };
    private readonly List<UserAccount> users = [];
    private readonly List<Administrator> administrators = [];
    private readonly List<ApplicationSetting> settings =
    [
        new() { Key = BusinessEmailRules.BrandKey, Value = "Original brand", Version = [1] },
        new() { Key = BusinessEmailRules.ReplyToKey, Value = "original@example.invalid", Version = [3] }
    ];
    private readonly List<NotificationTemplate> templates = [Template(3)];
    private bool authorized = true;
    private int contextCalls;
    private int accessChecks;

    /// <summary>Uses actual application read orchestration; unsupported persistence or provider operations fail rather than returning fake success.</summary>
    public AdministrationReconnectTests()
    {
        users.Add(actor);
        users.Add(new() { TenantId = actor.TenantId, DisplayName = "Candidate account", Version = [7] });
        administrators.Add(new() { UserId = actor.Id, Version = [1] });
        var db = SnapshotServiceProxy.Create<ISidequestDbContext>((method, _) => method.Name switch
        {
            "get_Users" => new ReadSet<UserAccount>(users),
            "get_Administrators" => new ReadSet<Administrator>(administrators),
            "get_ApplicationSettings" => new ReadSet<ApplicationSetting>(settings),
            "get_NotificationTemplates" => new ReadSet<NotificationTemplate>(templates),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException($"Unexpected persistence operation: {method.Name}.")
        });
        var factory = SnapshotServiceProxy.Create<ISidequestDbContextFactory>((method, _) =>
        {
            Assert.Equal(nameof(ISidequestDbContextFactory.CreateAsync), method.Name);
            contextCalls++;
            return Task.FromResult(db);
        });
        var access = SnapshotServiceProxy.Create<IResourceAccess>((method, _) =>
        {
            Assert.Equal(nameof(IResourceAccess.RequireAdministratorAsync), method.Name);
            accessChecks++;
            return authorized ? Task.FromResult(actor) :
                Task.FromException<UserAccount>(new DomainException(ErrorCode.Forbidden, "Administrator access is required."));
        });
        var changes = SnapshotServiceProxy.Create<IChangeWriter>((_, _) =>
            throw new NotSupportedException("Read-only reconnect must not stage notifications."));
        Services.AddLogging();
        Services.AddFluentUIComponents();
        Services.AddSingleton(coordinator);
        Services.AddSingleton(new BusinessEmailService(factory, access, TimeProvider.System));
        Services.AddSingleton(new AdministrationService(factory, access, changes, TimeProvider.System, new()));
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
    }

    /// <summary>Business reconnection refreshes current saved values, retains both drafts and original versions, requires explicit version adoption and clears on revocation.</summary>
    /// <returns>Completion after offline no-call, real read-service authorization, draft/version and denial assertions.</returns>
    [Fact]
    public async Task BusinessReconnectPreservesDraftVersionsAndClearsThemOnRevocation()
    {
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<BusinessEmail>();
        cut.Find("#email-brand").Change("Unsaved business brand");
        cut.Find("#email-reply").Change("unsaved@example.invalid");
        await ClickAsync(cut, "Review brand change");
        settings[0].Value = "Latest saved brand";
        settings[0].Version = [2];
        settings[1].Value = "latest@example.invalid";
        settings[1].Version = [4];
        await coordinator.ReportConnectionAsync(false, null);
        var before = contextCalls;
        await ClickAsync(cut, "Confirm setting change");
        Assert.Equal(before, contextCalls);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(before + 1, contextCalls);
        Assert.Equal(contextCalls, accessChecks);
        Assert.Equal("Unsaved business brand", cut.Find("#email-brand").GetAttribute("value"));
        Assert.Equal("unsaved@example.invalid", cut.Find("#email-reply").GetAttribute("value"));
        Assert.Contains("Latest saved brand", cut.Find("[aria-labelledby=current-settings]").TextContent, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 1 }, SettingState(cut, "settings", BusinessEmailRules.BrandKey).Version);
        Assert.Equal(new byte[] { 3 }, SettingState(cut, "settings", BusinessEmailRules.ReplyToKey).Version);
        Assert.Equal(new byte[] { 2 }, SettingState(cut, "currentSettings", BusinessEmailRules.BrandKey).Version);
        Assert.Null(State(cut, "pendingKey"));
        await coordinator.ReportConnectionAsync(false, null);
        await ClickAsync(cut, "Use latest versions, keep my edits");
        Assert.Equal(new byte[] { 1 }, SettingState(cut, "settings", BusinessEmailRules.BrandKey).Version);
        await coordinator.ReportConnectionAsync(true, null);
        await ClickAsync(cut, "Use latest versions, keep my edits");
        Assert.Equal(new byte[] { 2 }, SettingState(cut, "settings", BusinessEmailRules.BrandKey).Version);
        Assert.Equal(new byte[] { 4 }, SettingState(cut, "settings", BusinessEmailRules.ReplyToKey).Version);
        Assert.Equal("Unsaved business brand", cut.Find("#email-brand").GetAttribute("value"));
        Assert.Equal("unsaved@example.invalid", cut.Find("#email-reply").GetAttribute("value"));
        await ClickAsync(cut, "Review reply-to change");
        await RevokeAsync(cut);
        AssertCleared(cut, ["settings", "currentSettings", "pendingKey"], ["brand", "replyTo"]);
        Assert.Empty(cut.FindAll("#email-brand,#email-reply"));
    }

    /// <summary>Template reconnection keeps all wording and its original revision, refreshes history, drops preview/confirmation and clears everything on revocation.</summary>
    /// <returns>Completion after offline no-call, exact editor/history, explicit-review and denial assertions.</returns>
    [Fact]
    public async Task TemplateReconnectPreservesDraftRevisionAndClearsItOnRevocation()
    {
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<EmailTemplates>();
        cut.Find("#template-subject").Change("Unsaved {{Brand}}");
        cut.Find("#template-html").Change("<p>Unsaved {{Summary}}</p><p>{{CalendarGuidance}}</p>");
        cut.Find("#template-text").Change("Unsaved {{Summary}}\n{{CalendarGuidance}}");
        await ClickAsync(cut, "Preview with synthetic values");
        Assert.NotNull(State(cut, "preview"));
        await ClickAsync(cut, "Review new revision");
        templates.Add(Template(4));
        await coordinator.ReportConnectionAsync(false, null);
        var before = contextCalls;
        await ClickAsync(cut, "Confirm append revision");
        Assert.Equal(before, contextCalls);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(before + 1, contextCalls);
        Assert.Equal(contextCalls, accessChecks);
        Assert.Equal("Unsaved {{Brand}}", cut.Find("#template-subject").GetAttribute("value"));
        Assert.Equal("<p>Unsaved {{Summary}}</p><p>{{CalendarGuidance}}</p>", State(cut, "html"));
        Assert.Equal("Unsaved {{Summary}}\n{{CalendarGuidance}}", State(cut, "text"));
        Assert.Equal(3, State(cut, "revision"));
        Assert.Equal(4, State(cut, "latestRevision"));
        Assert.Equal(new[] { 4, 3, 0 }, Assert.IsAssignableFrom<IReadOnlyList<EmailTemplate>>(State(cut, "history")).Select(x => x.Revision));
        Assert.Null(State(cut, "preview"));
        Assert.Equal(false, State(cut, "confirming"));
        await coordinator.ReportConnectionAsync(false, null);
        await ClickAsync(cut, "Use latest revision as base, keep edits");
        Assert.Equal(3, State(cut, "revision"));
        await coordinator.ReportConnectionAsync(true, null);
        await ClickAsync(cut, "Use latest revision as base, keep edits");
        Assert.Equal(4, State(cut, "revision"));
        Assert.Equal("Unsaved {{Brand}}", State(cut, "subject"));
        Assert.Equal("<p>Unsaved {{Summary}}</p><p>{{CalendarGuidance}}</p>", State(cut, "html"));
        Assert.Equal("Unsaved {{Summary}}\n{{CalendarGuidance}}", State(cut, "text"));
        await ClickAsync(cut, "Preview with synthetic values");
        await ClickAsync(cut, "Review new revision");
        await RevokeAsync(cut);
        AssertCleared(cut, ["history", "preview"], ["subject", "html", "text"]);
        Assert.Equal(false, State(cut, "confirming"));
        Assert.Equal(0, State(cut, "revision"));
        Assert.Equal(0, State(cut, "latestRevision"));
        Assert.Empty(cut.FindAll("#template-subject,#template-html,#template-text"));
    }

    /// <summary>Administrator reconnect rereads assignments and invalidates stale selections without replaying offline add/remove or paging callbacks.</summary>
    /// <returns>Completion after exact assignment/version refresh, no-call and complete denial cleanup assertions.</returns>
    [Fact]
    public async Task AdministratorReconnectRefreshesAssignmentsAndDropsStaleConfirmations()
    {
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<Administrators>();
        cut.Find("#admin-search").Change("Candidate");
        await ClickAsync(cut, "Search eligible accounts");
        await ClickAsync(cut, "Review addition");
        await ClickAsync(cut, "Review removal");
        actor.DisplayName = "Current administrator";
        administrators[0].Version = [9];
        await coordinator.ReportConnectionAsync(false, null);
        var before = contextCalls;
        await ClickAsync(cut, "Confirm administrator addition");
        await ClickAsync(cut, "Confirm administrator removal");
        await ClickAsync(cut, "Next administrators");
        Assert.Equal(before, contextCalls);
        Assert.Equal(1, State(cut, "pageNumber"));
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(before + 1, contextCalls);
        Assert.Equal(contextCalls, accessChecks);
        var assignment = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AdministratorSummary>>(State(cut, "assignments")));
        Assert.Equal("Current administrator", assignment.DisplayName);
        Assert.Equal(new byte[] { 9 }, assignment.Version);
        AssertCleared(cut, ["choices", "pendingAddition", "pendingRemoval"], []);
        Assert.Equal("Candidate", cut.Find("#admin-search").GetAttribute("value"));
        await ClickAsync(cut, "Search eligible accounts");
        await ClickAsync(cut, "Review addition");
        await ClickAsync(cut, "Review removal");
        await RevokeAsync(cut);
        AssertCleared(cut, ["assignments", "choices", "pendingAddition", "pendingRemoval"], ["query"]);
        Assert.DoesNotContain("Current administrator", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Candidate account", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>Recovery reconnect checks administrator access, discards prior opaque evidence and selections, preserves valid input and clears it on revocation.</summary>
    /// <returns>Completion after offline recovery rejection, read-only reauthorization and complete sensitive-state cleanup.</returns>
    [Fact]
    public async Task RecoveryReconnectInvalidatesEvidenceAndClearsSensitiveStateOnRevocation()
    {
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<OwnershipRecovery>();
        var resourceId = Guid.NewGuid();
        cut.Find("#recovery-kind").Change(nameof(ResourceKind.Quest));
        cut.Find("#recovery-id").Change(resourceId.ToString());
        cut.Find("#recovery-reason").Change("Approved process reference");
        cut.Find("#replacement-search").Change("Candidate");
        await ClickAsync(cut, "Search eligible replacement");
        cut.Find("#replacement-account").Change(users[1].Id.ToString());
        SetState(cut, "preview", new RecoveryPreview(ResourceKind.Quest, resourceId, "prior opaque evidence"));
        cut.Render();
        await coordinator.ReportConnectionAsync(false, null);
        var before = contextCalls;
        await ClickAsync(cut, "Confirm ownership recovery");
        Assert.Equal(before, contextCalls);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(before + 1, contextCalls);
        Assert.Equal(contextCalls, accessChecks);
        AssertCleared(cut, ["choices", "preview"], ["selectedId"]);
        Assert.Equal(resourceId.ToString(), cut.Find("#recovery-id").GetAttribute("value"));
        Assert.Equal("Approved process reference", State(cut, "reason"));
        await ClickAsync(cut, "Search eligible replacement");
        cut.Find("#replacement-account").Change(users[1].Id.ToString());
        SetState(cut, "preview", new RecoveryPreview(ResourceKind.Quest, resourceId, "new opaque evidence"));
        await RevokeAsync(cut);
        AssertCleared(cut, ["choices", "preview"], ["selectedId", "resourceId", "reason", "query"]);
        Assert.Equal(false, State(cut, "authorized"));
        Assert.Equal(ResourceKind.Event, State(cut, "kind"));
        Assert.Empty(cut.FindAll("#recovery-id,#recovery-reason,#replacement-account"));
    }

    /// <summary>All four pages retain authorized prerender projections while every action remains explicitly disabled before interactive attachment.</summary>
    [Fact]
    public void PrerenderLoadsAllFourAuthorizedPagesWithoutEnablingActions()
    {
        SetRendererInfo(new("Static", false));
        var administratorPage = Render<Administrators>();
        var recoveryPage = Render<OwnershipRecovery>();
        var businessPage = Render<BusinessEmail>();
        var templatePage = Render<EmailTemplates>();
        Assert.Contains("Original administrator", administratorPage.Markup, StringComparison.Ordinal);
        Assert.Single(recoveryPage.FindAll("#recovery-id"));
        Assert.Equal("Original brand", businessPage.Find("#email-brand").GetAttribute("value"));
        Assert.Equal("Saved revision 3 {{Brand}}", templatePage.Find("#template-subject").GetAttribute("value"));
        AssertDisabled(administratorPage);
        AssertDisabled(recoveryPage);
        AssertDisabled(businessPage);
        AssertDisabled(templatePage);
        Assert.Equal(4, contextCalls);
        Assert.Equal(4, accessChecks);
    }

    /// <summary>An authorized reconnect exposes invalid saved settings as an explicit error while retaining the unsaved repair and original rowversions.</summary>
    /// <returns>Completion after exact validation feedback, draft preservation and fresh saved-value assertions.</returns>
    [Fact]
    public async Task InvalidSavedSettingAfterReconnectReportsErrorAndPreservesRepairDraft()
    {
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<BusinessEmail>();
        cut.Find("#email-brand").Change("Unsaved repair");
        settings[0].Value = "";
        settings[0].Version = [2];
        await coordinator.ReportConnectionAsync(false, null);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal("Use a 1–80 character message brand or one valid reply-to mailbox without a display name.",
            cut.Find("[role=alert]").TextContent);
        Assert.Equal("Unsaved repair", cut.Find("#email-brand").GetAttribute("value"));
        Assert.Equal(new byte[] { 1 }, SettingState(cut, "settings", BusinessEmailRules.BrandKey).Version);
        Assert.Equal("", SettingState(cut, "currentSettings", BusinessEmailRules.BrandKey).Value);
        Assert.Equal(new byte[] { 2 }, SettingState(cut, "currentSettings", BusinessEmailRules.BrandKey).Version);
        Assert.False(cut.FindComponents<FluentButton>().Single(x => x.Find("fluent-button").TextContent == "Review brand change").Instance.Disabled);
        Assert.Equal(2, accessChecks);
    }

    /// <summary>An invalid newly saved template is shown for explicit repair without silently replacing the draft or losing the validation error on reconnect.</summary>
    /// <returns>Completion after exact validation, original revision and refreshed invalid history assertions.</returns>
    [Fact]
    public async Task InvalidSavedTemplateAfterReconnectReportsErrorAndPreservesRepairDraft()
    {
        await coordinator.ReportConnectionAsync(true, null);
        var cut = Render<EmailTemplates>();
        cut.Find("#template-subject").Change("Unsaved repair {{Brand}}");
        var invalid = Template(4);
        invalid.Subject = "";
        templates.Add(invalid);
        await coordinator.ReportConnectionAsync(false, null);
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal("Subject must be 1–200 single-line characters; each body must be 1–2,000 characters.",
            cut.Find("[role=alert]").TextContent);
        Assert.Equal("Unsaved repair {{Brand}}", cut.Find("#template-subject").GetAttribute("value"));
        Assert.Equal(3, State(cut, "revision"));
        Assert.Equal(4, State(cut, "latestRevision"));
        Assert.Equal("", Assert.IsAssignableFrom<IReadOnlyList<EmailTemplate>>(State(cut, "history"))[0].Subject);
        Assert.Equal(2, accessChecks);
    }

    private async Task RevokeAsync<T>(IRenderedComponent<T> cut) where T : class, IComponent
    {
        await coordinator.ReportConnectionAsync(false, null);
        authorized = false;
        var before = accessChecks;
        await coordinator.ReportConnectionAsync(true, null);
        Assert.Equal(before + 1, accessChecks);
        Assert.Equal("Administrator access is required.", cut.Find("[role=alert]").TextContent);
    }

    private static Task ClickAsync<T>(IRenderedComponent<T> cut, string label) where T : class, IComponent =>
        cut.InvokeAsync(() => cut.FindComponents<FluentButton>()
            .Single(x => x.Find("fluent-button").TextContent.Trim() == label).Instance.OnClick.InvokeAsync(new MouseEventArgs()));

    private static void AssertDisabled<T>(IRenderedComponent<T> cut) where T : class, IComponent =>
        Assert.All(cut.FindComponents<FluentButton>(), button => Assert.True(button.Instance.Disabled));

    private static object? State<T>(IRenderedComponent<T> cut, string name) where T : class, IComponent =>
        Field<T>(name).GetValue(cut.Instance);

    private static void SetState<T>(IRenderedComponent<T> cut, string name, object value) where T : class, IComponent =>
        Field<T>(name).SetValue(cut.Instance, value);

    private static FieldInfo Field<T>(string name) =>
        typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Missing component state {name}.");

    private static BusinessSetting SettingState(IRenderedComponent<BusinessEmail> cut, string field, string key) =>
        Assert.IsAssignableFrom<IReadOnlyList<BusinessSetting>>(State(cut, field)).Single(x => x.Key == key);

    private static void AssertCleared<T>(IRenderedComponent<T> cut, string[] projections, string[] drafts) where T : class, IComponent
    {
        foreach (var field in projections) Assert.Null(State(cut, field));
        foreach (var field in drafts) Assert.Equal("", State(cut, field));
    }

    private static NotificationTemplate Template(int revision) => new()
    {
        Key = "quest.invitation",
        Revision = revision,
        Subject = $"Saved revision {revision} {{{{Brand}}}}",
        HtmlBody = "<p>{{Summary}}</p><p>{{CalendarGuidance}}</p>",
        TextBody = "{{Summary}}\n{{CalendarGuidance}}"
    };

    private sealed class ReadSet<T>(IEnumerable<T> rows) : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        private readonly AsyncQuery<T> query = new(rows);
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => ((IQueryable<T>)query).Expression;
        IQueryProvider IQueryable.Provider => new ReadQueryProvider(query);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => query.AsEnumerable().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => query.AsEnumerable().GetEnumerator();

        /// <inheritdoc />
        public override IEntityType EntityType => throw new NotSupportedException("Read-only projection does not inspect tracking metadata.");

        /// <inheritdoc />
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            query.GetAsyncEnumerator(cancellationToken);
    }

    private sealed class AsyncQuery<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        internal AsyncQuery(IEnumerable<T> rows) : base(rows) { }
        internal AsyncQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new ReadQueryProvider(this);

        /// <inheritdoc />
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ReadEnumerator<T>(this.AsEnumerable().GetEnumerator(), cancellationToken);
    }

    private sealed class ReadQueryProvider(IQueryProvider inner) : IQueryProvider
    {
        /// <inheritdoc />
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException("Only typed projection queries are expected.");
        /// <inheritdoc />
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new AsyncQuery<TElement>(expression);
        /// <inheritdoc />
        public object? Execute(Expression expression) => inner.Execute(expression);
        /// <inheritdoc />
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
    }

    private sealed class ReadEnumerator<T>(IEnumerator<T> inner, CancellationToken cancellationToken) : IAsyncEnumerator<T>
    {
        /// <inheritdoc />
        public T Current => inner.Current;
        /// <inheritdoc />
        public ValueTask<bool> MoveNextAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(inner.MoveNext());
        }
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
