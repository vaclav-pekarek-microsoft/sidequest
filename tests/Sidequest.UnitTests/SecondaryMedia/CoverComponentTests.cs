using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application.Media;
using Sidequest.Domain.Rules;
using Sidequest.Web.Components.Media;

namespace Sidequest.UnitTests.SecondaryMedia;

/// <summary>Verifies the reusable editor's prerender gate, exact upload cap, parent ownership, and cancellation.</summary>
public sealed class CoverComponentTests : BunitContext
{
    /// <summary>Prerendering, unsaved drafts and a busy parent disable all media controls.</summary>
    /// <param name="interactive">Whether handlers have attached to the renderer.</param>
    /// <param name="persisted">Whether the parent has saved a draft.</param>
    /// <param name="busy">Whether another parent mutation is running.</param>
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public void ControlsRequireInteractivePersistedIdleParent(bool interactive, bool persisted, bool busy)
    {
        Services.AddSingleton<IMediaService>(new MediaStub());
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new(interactive ? "Server" : "Static", interactive));
        var component = Render<CoverEditor>(parameters => parameters
            .Add(x => x.QuestId, persisted ? Guid.NewGuid() : Guid.Empty)
            .Add(x => x.Version, "version").Add(x => x.QuestTitle, "Kayak outing")
            .Add(x => x.AssetId, Guid.NewGuid()).Add(x => x.IsBusy, busy));
        Assert.Equal(!interactive || !persisted || busy, component.Find("input[type=file]").HasAttribute("disabled"));
        Assert.Equal(!interactive || !persisted || busy, component.Find("button").HasAttribute("disabled"));
        Assert.Equal(interactive && persisted ? 1 : 0, component.FindComponents<InputFile>().Count);
        Assert.Equal("Cover for Kayak outing", component.Find("img").GetAttribute("alt"));
        Assert.Equal("Quest cover", component.Find(".cover-editor.section-box > .section-heading").TextContent);
    }

    /// <summary>A successful upload forwards exactly 2 MiB as the stream cap and returns a version without mutating parent parameters.</summary>
    /// <returns>Completion after the parent receives its committed cover update and balanced busy callbacks.</returns>
    [Fact]
    public async Task Upload_UsesExactLimitAndReturnsUpdateWithoutMutatingParameters()
    {
        var service = new MediaStub();
        Services.AddSingleton<IMediaService>(service);
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
        var busy = new List<bool>();
        CoverUpdate? received = null;
        var id = Guid.NewGuid();
        var component = Render<CoverEditor>(parameters => parameters.Add(x => x.QuestId, id)
            .Add(x => x.Version, "captured-version").Add(x => x.QuestTitle, "Unsaved title")
            .Add(x => x.CoverChanged, value => received = value).Add(x => x.BusyChanged, value => busy.Add(value)));
        var file = new BrowserImage();
        await component.InvokeAsync(() => component.FindComponent<InputFile>().Instance.OnChange
            .InvokeAsync(new InputFileChangeEventArgs([file])));
        Assert.Equal(2_097_152, file.Limit);
        Assert.Equal((id, "captured-version"), service.Call);
        Assert.Equal(service.Result, received);
        Assert.Equal(new[] { true, false }, busy);
        Assert.Equal("captured-version", component.Instance.Version);
        Assert.Null(component.Instance.AssetId);
        Assert.Equal("Unsaved title", component.Instance.QuestTitle);
        Assert.Contains("Cover updated.", component.Markup);
    }

    /// <summary>A conflict publishes no callback or stale cover, keeps parent values and reports actionable failure.</summary>
    /// <returns>Completion after conflict feedback and restored controls.</returns>
    [Fact]
    public async Task Conflict_PreservesParentVersionAndTitleWithoutSuccessCallback()
    {
        var service = new MediaStub { Failure = new DomainException(ErrorCode.Conflict, "Synthetic conflict.") };
        Services.AddSingleton<IMediaService>(service);
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
        var callbacks = 0;
        var conflicts = 0;
        var component = Render<CoverEditor>(parameters => parameters.Add(x => x.QuestId, Guid.NewGuid())
            .Add(x => x.Version, "old-version").Add(x => x.QuestTitle, "Unsaved draft")
            .Add(x => x.CoverChanged, _ => callbacks++).Add(x => x.ConflictDetected, () => conflicts++));
        await component.InvokeAsync(() => component.FindComponent<InputFile>().Instance.OnChange
            .InvokeAsync(new InputFileChangeEventArgs([new BrowserImage()])));
        Assert.Contains("Your text has been kept", component.Find("[role=alert]").TextContent);
        Assert.Equal("old-version", component.Instance.Version);
        Assert.Equal("Unsaved draft", component.Instance.QuestTitle);
        Assert.Equal(0, callbacks);
        Assert.Equal(1, conflicts);
        Assert.False(component.Find("input").HasAttribute("disabled"));
        Assert.DoesNotContain("Cover updated.", component.Markup);
    }

    /// <summary>The generated placeholder is labeled by Quest name; moderation changes only the mediated endpoint query, never a storage URL.</summary>
    [Fact]
    public void Display_DefaultAndModerationUseAccessiblePrivatePresentation()
    {
        var empty = Render<QuestCover>(parameters => parameters.Add(x => x.QuestTitle, "Board games"));
        Assert.Equal("https://placehold.co/600x400?text=Board%20games", empty.Find("img").GetAttribute("src"));
        Assert.Equal("Cover for Board games", empty.Find("img").GetAttribute("alt"));
        var id = Guid.NewGuid();
        var cover = Render<QuestCover>(parameters => parameters.Add(x => x.AssetId, id)
            .Add(x => x.QuestTitle, "Board games").Add(x => x.Moderation, true));
        Assert.Equal($"media/covers/{id:D}?moderation=true", cover.Find("img").GetAttribute("src"));
        Assert.Equal("Cover for Board games", cover.Find("img").GetAttribute("alt"));
        Assert.DoesNotContain("blob", cover.Markup, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parent disposal during the busy callback cancels before opening the file or calling storage, without accessing a disposed source.</summary>
    /// <returns>A task completing after the real upload callback safely observes disposal.</returns>
    [Fact]
    public async Task DisposalDuringBusyCallback_DoesNotOpenFileOrStartProviderWork()
    {
        var service = new MediaStub();
        Services.AddSingleton<IMediaService>(service);
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
        var component = Render<CoverEditor>(p => p.Add(x => x.QuestId, Guid.NewGuid())
            .Add(x => x.Version, "version").Add(x => x.QuestTitle, "Pending cover"));
        component.Render(p => p.Add(x => x.BusyChanged, async value =>
        {
            if (value)
                await component.Instance.DisposeAsync();
        }));
        var file = new BrowserImage();
        await component.InvokeAsync(() => component.FindComponent<InputFile>().Instance.OnChange
            .InvokeAsync(new InputFileChangeEventArgs([file])));
        Assert.Equal(0, file.Limit);
        Assert.Equal(Guid.Empty, service.Call.Quest);
        Assert.False(service.Entered.Task.IsCompleted);
        Assert.DoesNotContain("Cover updated.", component.Markup);
    }

    /// <summary>Pending uploads disable re-entry and announce progress; disposal cancels provider work without a success callback.</summary>
    /// <returns>Completion after renderer disposal cancellation reaches the service.</returns>
    [Fact]
    public async Task PendingUpload_DisablesControlsAndDisposalCancelsWork()
    {
        var service = new MediaStub { WaitForCancellation = true };
        Services.AddSingleton<IMediaService>(service);
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
        var callbacks = 0;
        var component = Render<CoverEditor>(parameters => parameters.Add(x => x.QuestId, Guid.NewGuid())
            .Add(x => x.Version, "old-version").Add(x => x.QuestTitle, "Pending cover")
            .Add(x => x.CoverChanged, _ => callbacks++));
        var pending = component.InvokeAsync(() => component.FindComponent<InputFile>().Instance.OnChange
            .InvokeAsync(new InputFileChangeEventArgs([new BrowserImage()])));
        await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        component.Render();
        Assert.True(component.Find("input").HasAttribute("disabled"));
        Assert.Contains("Processing cover", component.Find("[role=status]").TextContent);
        await component.InvokeAsync(async () => await component.Instance.DisposeAsync());
        await pending;
        Assert.True(service.ObservedCancellation);
        Assert.Equal(0, callbacks);
    }

    /// <summary>Denied operations hide the prior protected cover and notify the parent to clear its protected text state.</summary>
    /// <param name="code">Current resource or account access failure.</param>
    /// <returns>Completion after local redaction, disabled controls and the parent access-loss callback.</returns>
    [Theory]
    [InlineData(ErrorCode.NotFound)]
    [InlineData(ErrorCode.Forbidden)]
    public async Task AccessLoss_HidesCoverAndSignalsParent(ErrorCode code)
    {
        Services.AddSingleton<IMediaService>(new MediaStub { Failure = new DomainException(code, "Unavailable.") });
        JSInterop.Mode = JSRuntimeMode.Loose;
        SetRendererInfo(new("Server", true));
        ErrorCode? lost = null;
        var component = Render<CoverEditor>(parameters => parameters.Add(x => x.QuestId, Guid.NewGuid())
            .Add(x => x.Version, "version").Add(x => x.AssetId, Guid.NewGuid())
            .Add(x => x.QuestTitle, "Protected sentinel").Add(x => x.AccessLost, value => lost = value));
        Assert.Single(component.FindAll("img"));
        await component.InvokeAsync(() => component.FindComponent<InputFile>().Instance.OnChange
            .InvokeAsync(new InputFileChangeEventArgs([new BrowserImage()])));
        Assert.Empty(component.FindAll("img"));
        Assert.DoesNotContain("Protected sentinel", component.Markup);
        Assert.True(component.Find("input").HasAttribute("disabled"));
        Assert.Equal(code, lost);
    }

    private sealed class BrowserImage : IBrowserFile
    {
        internal long Limit { get; private set; }
        /// <inheritdoc />
        public string Name => "cover.png";
        /// <inheritdoc />
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        /// <inheritdoc />
        public long Size => 1;
        /// <inheritdoc />
        public string ContentType => "image/png";
        /// <inheritdoc />
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            Limit = maxAllowedSize;
            return new MemoryStream([1]);
        }
    }

    private sealed class MediaStub : IMediaService
    {
        internal CoverUpdate Result { get; } = new(Guid.NewGuid(), "new-version");
        internal Exception? Failure { get; init; }
        internal bool WaitForCancellation { get; init; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ObservedCancellation { get; private set; }
        internal (Guid Quest, string Version) Call { get; private set; }
        /// <inheritdoc />
        public async Task<CoverUpdate> UploadCoverAsync(Guid questId, string expectedVersion, Stream content, CancellationToken cancellationToken = default)
        {
            Call = (questId, expectedVersion);
            Entered.TrySetResult();
            if (WaitForCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }
            if (Failure is not null)
                throw Failure;
            return Result;
        }
        /// <inheritdoc />
        public Task<CoverUpdate> RemoveCoverAsync(Guid questId, string expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CoverUpdate(null, "removed-version"));
        /// <inheritdoc />
        public Task<MediaContent> ReadAsync(Guid assetId, bool moderation = false, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Components must use mediated HTTP display.");
    }
}
