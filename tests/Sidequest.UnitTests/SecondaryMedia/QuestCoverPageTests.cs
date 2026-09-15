using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Media;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Media;
using Sidequest.Web.Components.Pages.Quests;
using Sidequest.Web.Components.Quests;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Exercises real Quest page composition and parent-owned text/version state independently of storage transport.</summary>
public sealed class QuestCoverPageTests : BunitContext
{
    private readonly QuestStub quests = new();
    private readonly MediaStub media = new();

    /// <summary>Registers strict service doubles and real Fluent components before selecting the renderer mode.</summary>
    public QuestCoverPageTests()
    {
        Services.AddFluentUIComponents();
        Services.AddSingleton<IQuestService>(quests);
        Services.AddSingleton<IEventService>(new EventStub());
        Services.AddSingleton<IMediaService>(media);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>A cover update changes the concurrency token and image without recreating the editor or discarding unsaved text.</summary>
    /// <param name="remove">Whether the committed update removes rather than replaces the cover.</param>
    /// <returns>A task completing after the actual text form submits its preserved input with the new cover rowversion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoverCommit_PreservesUnsavedTextAndSavesWithNewVersion(bool remove)
    {
        SetRendererInfo(new("Server", true));
        var page = Render<QuestEdit>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        var editor = page.FindComponent<QuestEditor>();
        var initial = editor.Instance.Initial;
        await page.InvokeAsync(() => editor.FindComponents<FluentTextField>().First().Instance.ValueChanged.InvokeAsync("Unsaved changed title"));
        var cover = page.FindComponent<CoverEditor>();
        Guid? asset = remove ? null : Guid.NewGuid();
        media.Result = new(asset, "AAAAAAAAAAI=");
        if (remove)
            cover.Find("button").Click();
        else
            await UploadAsync(page);
        Assert.Equal((quests.Detail.Summary.Id, "AAAAAAAAAAE="), Assert.Single(media.Calls));
        Assert.Same(initial, page.FindComponent<QuestEditor>().Instance.Initial);
        Assert.Same(cover.Instance, page.FindComponent<CoverEditor>().Instance);
        Assert.Equal(asset, cover.Instance.AssetId);
        Assert.Equal("AAAAAAAAAAI=", cover.Instance.Version);
        Assert.Equal(1, quests.Reads);

        editor.Find("form").Submit();

        var saved = Assert.Single(quests.Edits);
        Assert.Equal(quests.Detail.Summary.Id, saved.Id);
        Assert.Equal("AAAAAAAAAAI=", saved.Version);
        Assert.Equal("Unsaved changed title", saved.Input.Title);
        Assert.Equal("Protected description sentinel", saved.Input.Description);
        Assert.EndsWith($"/quests/{saved.Id}", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    /// <summary>In-flight cover changes block even a queued text-save callback, then release it after the committed version arrives.</summary>
    /// <returns>A task completing after no competing edit and one permitted post-cover edit.</returns>
    [Fact]
    public async Task CoverBusy_BlocksCompetingSaveUntilVersionIsCommitted()
    {
        SetRendererInfo(new("Server", true));
        var page = Render<QuestEdit>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        var cover = page.FindComponent<CoverEditor>().Instance;
        var editor = page.FindComponent<QuestEditor>();
        await page.InvokeAsync(() => cover.BusyChanged.InvokeAsync(true));
        Assert.True(editor.Instance.Busy);
        Assert.True(editor.Find("fieldset").HasAttribute("disabled"));
        await page.InvokeAsync(() => editor.Instance.Save.InvokeAsync(new QuestEditorModel { Title = "Queued save" }));
        Assert.Empty(quests.Edits);
        await page.InvokeAsync(() => cover.CoverChanged.InvokeAsync(new(null, "AAAAAAAAAAM=")));
        await page.InvokeAsync(() => cover.BusyChanged.InvokeAsync(false));
        Assert.False(editor.Instance.Busy);
        editor.Find("form").Submit();
        Assert.Equal("AAAAAAAAAAM=", Assert.Single(quests.Edits).Version);
    }

    /// <summary>Access loss clears protected text/images and does not leave reload stuck behind a disposed child's busy flag.</summary>
    /// <param name="code">Explicit account or resource access-loss category.</param>
    /// <returns>A task completing after redaction, stale-callback rejection and an available explicit reload.</returns>
    [Theory]
    [InlineData(ErrorCode.Forbidden)]
    [InlineData(ErrorCode.NotFound)]
    public async Task CoverAccessLoss_ClearsParentAndIgnoresLateCompletion(ErrorCode code)
    {
        SetRendererInfo(new("Server", true));
        var page = Render<QuestEdit>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        var cover = page.FindComponent<CoverEditor>().Instance;
        media.Failure = new DomainException(code, "Unavailable.");
        await UploadAsync(page);
        await page.InvokeAsync(() => cover.CoverChanged.InvokeAsync(new(Guid.NewGuid(), "stale")));
        await page.InvokeAsync(() => cover.BusyChanged.InvokeAsync(true));
        Assert.Empty(page.FindComponents<QuestEditor>());
        Assert.Empty(page.FindComponents<CoverEditor>());
        Assert.Empty(page.FindAll("img"));
        Assert.DoesNotContain("Protected description sentinel", page.Markup);
        Assert.DoesNotContain("Original title sentinel", page.Markup);
        Assert.False(page.FindComponent<FluentButton>().Instance.Disabled);
        Assert.NotEmpty(page.Find("[role=alert]").TextContent);
        Assert.Empty(quests.Edits);
    }

    /// <summary>Conflict preserves edits and blocks writes until an explicit reload obtains a new authorized snapshot.</summary>
    /// <returns>A task completing after preserved input, rejected stale save, explicit reload and a fresh cover editor.</returns>
    [Fact]
    public async Task CoverConflict_RequiresExplicitReloadWithoutDiscardingTextAutomatically()
    {
        SetRendererInfo(new("Server", true));
        var page = Render<QuestEdit>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        var editor = page.FindComponent<QuestEditor>();
        await page.InvokeAsync(() => editor.FindComponents<FluentTextField>().First().Instance.ValueChanged.InvokeAsync("Unsaved conflict title"));
        var cover = page.FindComponent<CoverEditor>().Instance;
        media.Failure = new DomainException(ErrorCode.Conflict, "The cover changed.");
        await UploadAsync(page);
        Assert.Equal("Unsaved conflict title", editor.FindComponents<FluentTextField>().First().Instance.Value);
        Assert.True(editor.Instance.Busy);
        await page.InvokeAsync(() => editor.Instance.Save.InvokeAsync(new QuestEditorModel { Title = "Rejected save" }));
        Assert.Empty(quests.Edits);
        Assert.Equal(1, quests.Reads);
        quests.Detail = quests.Detail with { Summary = quests.Detail.Summary with { Version = "AAAAAAAAAAQ=", Title = "Reloaded title" } };
        var reload = page.FindComponents<FluentButton>().Single(x => x.Markup.Contains("Reload current version", StringComparison.Ordinal));
        Assert.False(reload.Instance.Disabled);
        await page.InvokeAsync(() => reload.Instance.OnClick.InvokeAsync());
        Assert.Equal(2, quests.Reads);
        Assert.NotSame(cover, page.FindComponent<CoverEditor>().Instance);
        Assert.Equal("AAAAAAAAAAQ=", page.FindComponent<CoverEditor>().Instance.Version);
        Assert.Equal("Reloaded title", page.FindComponent<QuestEditor>().FindComponents<FluentTextField>().First().Instance.Value);
        Assert.False(page.FindComponent<QuestEditor>().Instance.Busy);
    }

    /// <summary>Callbacks captured for an earlier route cannot overwrite or disable another Quest's editor.</summary>
    /// <returns>A task completing after a route change and rejected old success, busy, conflict and access-loss callbacks.</returns>
    [Fact]
    public async Task PreviousRouteCallbacks_CannotAlterNewQuestState()
    {
        SetRendererInfo(new("Server", true));
        var page = Render<QuestEdit>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        var old = page.FindComponent<CoverEditor>().Instance;
        quests.Detail = quests.Detail with { Summary = quests.Detail.Summary with { Id = Guid.NewGuid(), Title = "Other Quest" } };
        page.Render(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        await page.InvokeAsync(() => old.CoverChanged.InvokeAsync(new(Guid.NewGuid(), "stale")));
        await page.InvokeAsync(() => old.BusyChanged.InvokeAsync(true));
        await page.InvokeAsync(() => old.ConflictDetected.InvokeAsync());
        await page.InvokeAsync(() => old.AccessLost.InvokeAsync(ErrorCode.NotFound));
        var current = page.FindComponent<CoverEditor>().Instance;
        Assert.Equal(quests.Detail.Summary.Id, current.QuestId);
        Assert.Equal(quests.Detail.Summary.CoverAssetId, current.AssetId);
        Assert.Equal(quests.Detail.Summary.Version, current.Version);
        Assert.False(page.FindComponent<QuestEditor>().Instance.Busy);
        Assert.Empty(page.FindAll("[role=alert]"));
    }

    /// <summary>Prerendered pages never expose an enabled file input or text submit.</summary>
    [Fact]
    public void Prerender_DisablesBothEditors()
    {
        SetRendererInfo(new("Static", false));
        var page = Render<QuestEdit>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        Assert.True(page.Find("input[type=file]").HasAttribute("disabled"));
        Assert.True(page.FindComponent<QuestEditor>().Instance.Busy);
    }

    /// <summary>Draft creation exposes the save-first explanation rather than a usable upload against an empty identifier.</summary>
    [Fact]
    public void UnsavedDraft_ExplainsWhyCoverUploadIsDisabled()
    {
        SetRendererInfo(new("Server", true));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/quests/create?eventId={EventStub.Id}");
        var page = Render<QuestEdit>();
        Assert.Equal(Guid.Empty, page.FindComponent<CoverEditor>().Instance.QuestId);
        Assert.True(page.Find("input[type=file]").HasAttribute("disabled"));
        Assert.Contains("Save your draft before adding a cover.", page.Markup);
    }

    /// <summary>Cards and details preserve the explicit moderation flag when displaying the authorized cover.</summary>
    /// <param name="moderation">Whether the separate audited display path was requested.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSurfaces_UseAuthorizedCoverAndExplicitModeration(bool moderation)
    {
        SetRendererInfo(new("Server", true));
        quests.Detail = quests.Detail with
        {
            Summary = quests.Detail.Summary with
            {
                Status = QuestStatus.Active, IsOwner = !moderation, CanModerate = moderation,
                AttendeeCount = moderation ? null : 0, FollowerCount = moderation ? null : 0
            },
            Attendees = moderation ? null : [], Followers = moderation ? null : [], Invitees = moderation ? null : []
        };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/quests/{quests.Detail.Summary.Id}?moderation={moderation}");
        var card = Render<QuestCard>(p => p.Add(x => x.Item, quests.Detail.Summary).Add(x => x.Moderation, moderation));
        var detail = Render<QuestDetails>(p => p.Add(x => x.Id, quests.Detail.Summary.Id));
        foreach (var image in new[] { card.FindComponent<QuestCover>().Instance, detail.FindComponent<QuestCover>().Instance })
        {
            Assert.Equal(quests.Detail.Summary.CoverAssetId, image.AssetId);
            Assert.Equal(quests.Detail.Summary.Title, image.QuestTitle);
            Assert.Equal(moderation, image.Moderation);
        }
    }

    private sealed class QuestStub : IQuestService
    {
        internal QuestDetail Detail { get; set; } = new(new(Guid.NewGuid(), EventStub.Id, "Parent Event", "Original title sentinel", "Room",
            new(2026, 7, 15, 10, 0, 0, TimeSpan.Zero), new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            "Europe/Prague", QuestStatus.Draft, QuestVisibility.Public, 0, 0, null, ParticipationStatus.None, true, false,
            "AAAAAAAAAAE=", Guid.NewGuid()), "Protected description sentinel", "", [], [], [], []);
        internal int Reads { get; private set; }
        internal List<(Guid Id, string Version, QuestInput Input)> Edits { get; } = [];
        /// <inheritdoc />
        public Task<QuestDetail> GetAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Detail.Summary.Id, id);
            Reads++;
            return Task.FromResult(Detail);
        }
        /// <inheritdoc />
        public Task EditAsync(Guid id, string version, QuestInput input, CancellationToken cancellationToken = default)
        {
            Edits.Add((id, version, input));
            return Task.CompletedTask;
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<QuestHistoryItem>> HistoryAsync(Guid id, bool moderation = false, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<QuestHistoryItem>>([]);
        /// <inheritdoc />
        public Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<PageResult<QuestSummary>> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page, QuestDateFilter dates, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<Guid> CreateAsync(Guid eventId, QuestInput input, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task ChangeStatusAsync(Guid id, string version, QuestStatus target, string reason, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task ParticipateAsync(Guid id, ParticipationCommand command, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task InviteAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RevokeInvitationAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RemoveAttendeeAsync(Guid id, Guid userId, string reason, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task AddOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RemoveOwnerAsync(Guid id, Guid userId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<IReadOnlyList<OfflineQuest>> GetOfflineJoinedAsync(CancellationToken cancellationToken = default) => throw Unexpected();
    }

    private static Task UploadAsync(IRenderedComponent<QuestEdit> page) => page.InvokeAsync(() =>
        page.FindComponent<InputFile>().Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([new BrowserImage()])));

    private sealed class MediaStub : IMediaService
    {
        internal CoverUpdate Result { get; set; } = new(Guid.NewGuid(), "AAAAAAAAAAI=");
        internal DomainException? Failure { get; set; }
        internal List<(Guid Id, string Version)> Calls { get; } = [];
        /// <inheritdoc />
        public Task<CoverUpdate> UploadCoverAsync(Guid questId, string expectedVersion, Stream content, CancellationToken cancellationToken = default) =>
            ChangeAsync(questId, expectedVersion);
        /// <inheritdoc />
        public Task<CoverUpdate> RemoveCoverAsync(Guid questId, string expectedVersion, CancellationToken cancellationToken = default) =>
            ChangeAsync(questId, expectedVersion);
        /// <inheritdoc />
        public Task<MediaContent> ReadAsync(Guid assetId, bool moderation = false, CancellationToken cancellationToken = default) => throw Unexpected();

        private Task<CoverUpdate> ChangeAsync(Guid id, string version)
        {
            Calls.Add((id, version));
            return Failure is { } failure ? Task.FromException<CoverUpdate>(failure) : Task.FromResult(Result);
        }
    }

    private sealed class BrowserImage : IBrowserFile
    {
        /// <inheritdoc />
        public string Name => "cover.png";
        /// <inheritdoc />
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        /// <inheritdoc />
        public long Size => 3;
        /// <inheritdoc />
        public string ContentType => "image/png";
        /// <inheritdoc />
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) => new MemoryStream([1, 2, 3]);
    }

    private sealed class EventStub : IEventService
    {
        internal static readonly Guid Id = Guid.Parse("10000000-0000-0000-0000-000000000001");
        /// <inheritdoc />
        public Task<PageResult<EventSummary>> ListAsync(EventListKind kind, PageRequest page, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PageResult<EventSummary>([new(Id, "Parent Event", "", new(2026, 7, 15), new(2026, 7, 16), "Europe/Prague", EventStatus.Active, [], true, true, "AAAAAAAAAAE=")], 1, page.Page, page.PageSize));
        /// <inheritdoc />
        public Task<PageResult<MembershipSummary>> ListMembersAsync(Guid eventId, PageRequest page, CancellationToken cancellationToken = default) => Task.FromResult(new PageResult<MembershipSummary>([], 0, page.Page, page.PageSize));
        /// <inheritdoc />
        public Task<EventDetail> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<Guid> CreateAsync(EventInput input, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task EditAsync(Guid id, string version, EventInput input, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task ChangeStatusAsync(Guid id, string version, EventStatus target, string reason, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task DeleteDraftAsync(Guid id, string version, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<IReadOnlyList<DuplicateEvent>> FindDuplicatesAsync(string name, DateOnly start, DateOnly end, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RequestMembershipAsync(Guid eventId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task WithdrawRequestAsync(Guid requestId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task DecideRequestAsync(Guid requestId, bool approve, string reason, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<PageResult<RequestSummary>> ListRequestsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task AddMemberAsync(Guid eventId, Guid directoryObjectId, bool restore, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RemoveMemberAsync(Guid eventId, Guid userId, string reason, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task LeaveAsync(Guid eventId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task InviteAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RespondToInvitationAsync(Guid invitationId, bool accept, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<PageResult<EventInvitationSummary>> ListInvitationsAsync(Guid? eventId, PageRequest page, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task AddOwnerAsync(Guid eventId, Guid directoryObjectId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task RemoveOwnerAsync(Guid eventId, Guid userId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<Guid> StartBulkAsync(Guid eventId, Guid groupId, BulkMode mode, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<BulkOperationSummary> GetBulkAsync(Guid operationId, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) => throw Unexpected();
        /// <inheritdoc />
        public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(string query, CancellationToken cancellationToken = default) => throw Unexpected();
    }

    private static InvalidOperationException Unexpected() => new("Unexpected service call in the Quest cover composition fixture.");
}
