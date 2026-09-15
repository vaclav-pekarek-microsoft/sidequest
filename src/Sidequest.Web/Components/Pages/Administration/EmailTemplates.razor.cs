using Microsoft.AspNetCore.Components;
using Sidequest.Application.Administration;

namespace Sidequest.Web.Components.Pages.Administration;

/// <summary>Versioned inert-template editing, synthetic preview and explicit conflict recovery without content lookup.</summary>
public partial class EmailTemplates
{
    [Inject] private BusinessEmailService Service { get; set; } = default!;
    private string selectedKey = "quest.invitation";
    private string loadedKey = "quest.invitation";
    private int revision;
    private int latestRevision;
    private int historyPage = 1;
    private string subject = "";
    private string html = "";
    private string text = "";
    private bool confirming;
    private IReadOnlyList<EmailTemplate>? history;
    private RenderedBusinessEmail? preview;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => InitializeAsync(LoadSelectedCoreAsync);

    private Task LoadSelectedAsync() => RunAsync(LoadSelectedCoreAsync);

    private async Task LoadSelectedCoreAsync()
    {
        var loaded = await Service.GetHistoryAsync(selectedKey, Lifetime, allowInvalidForEditing: true);
        loadedKey = selectedKey;
        history = loaded;
        revision = loaded[0].Revision;
        latestRevision = revision;
        historyPage = 1;
        CopyWording(loaded[0]);
        BusinessEmailRules.ValidateTemplate(loaded[0]);
    }

    /// <inheritdoc />
    protected override async Task RefreshAfterReconnectAsync()
    {
        var latest = await Service.GetHistoryAsync(loadedKey, Lifetime, allowInvalidForEditing: true);
        if (history is null)
        {
            revision = latest[0].Revision;
            CopyWording(latest[0]);
        }
        history = latest;
        latestRevision = latest[0].Revision;
        historyPage = 1;
        InvalidatePreview();
        ValidateLoadedConfiguration(() => BusinessEmailRules.ValidateTemplate(latest[0]));
    }

    private void CopyWording(EmailTemplate template)
    {
        subject = template.Subject;
        html = template.HtmlBody;
        text = template.TextBody;
        InvalidatePreview();
    }

    private void InvalidatePreview()
    {
        preview = null;
        confirming = false;
    }

    private EmailTemplate Draft() => new(loadedKey, revision, subject, html, text);

    private Task PreviewAsync() => RunAsync(async () => preview = await Service.PreviewAsync(Draft(), Lifetime));

    private Task RefreshHistoryAsync() => RunAsync(async () =>
    {
        history = await Service.GetHistoryAsync(loadedKey, Lifetime, allowInvalidForEditing: true);
        latestRevision = history[0].Revision;
        historyPage = 1;
        BusinessEmailRules.ValidateTemplate(history[0]);
    });

    private Task NextHistoryAsync() => RunAsync(async () =>
    {
        var next = historyPage + 1;
        history = await Service.GetHistoryAsync(loadedKey, Lifetime, new(next), allowInvalidForEditing: true);
        historyPage = next;
    });

    private Task PreviousHistoryAsync() => RunAsync(async () =>
    {
        var previous = Math.Max(1, historyPage - 1);
        history = await Service.GetHistoryAsync(loadedKey, Lifetime, new(previous), allowInvalidForEditing: true);
        historyPage = previous;
        if (previous == 1) latestRevision = history[0].Revision;
    });

    private void AcceptLatestVersion()
    {
        if (Disabled) return;
        if (history is not null) revision = latestRevision;
        InvalidatePreview();
    }

    private Task SaveAsync() => RunAsync(async () =>
    {
        revision = await Service.SaveTemplateAsync(Draft(), Lifetime);
        Lifetime.ThrowIfCancellationRequested();
        history = await Service.GetHistoryAsync(loadedKey, Lifetime, allowInvalidForEditing: true);
        latestRevision = history[0].Revision;
        historyPage = 1;
        confirming = false;
        preview = null;
        Status = $"Revision {revision} appended and audited. Existing history and queued delivery intent remain unchanged.";
    });

    /// <inheritdoc />
    protected override void ClearSensitiveState()
    {
        history = null;
        preview = null;
        confirming = false;
        subject = "";
        html = "";
        text = "";
        selectedKey = loadedKey = "quest.invitation";
        revision = latestRevision = 0;
        historyPage = 1;
    }
}
