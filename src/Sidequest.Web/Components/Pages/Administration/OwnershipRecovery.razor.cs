using Microsoft.AspNetCore.Components;
using Sidequest.Application.Administration;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Pages.Administration;

/// <summary>Content-free, explicitly confirmed ownership recovery consuming externally verified departure evidence.</summary>
public partial class OwnershipRecovery
{
    [Inject] private AdministrationService Service { get; set; } = default!;
    private bool authorized;
    private ResourceKind kind = ResourceKind.Event;
    private string resourceId = "";
    private string query = "";
    private string selectedId = "";
    private string reason = "";
    private IReadOnlyList<AccountChoice>? choices;
    private RecoveryPreview? preview;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => CheckAccessAsync();

    private Task CheckAccessAsync() => RunAsync(async () =>
    {
        await Service.ListAsync(Lifetime);
        authorized = true;
    });

    private Task SearchAsync() => RunAsync(async () =>
    {
        choices = await Service.SearchAccountsAsync(query, Lifetime);
        selectedId = "";
    });

    private void ClearPreview() => preview = null;

    private Task PreviewAsync() => RunAsync(async () =>
    {
        preview = null;
        if (!Guid.TryParse(resourceId, out var id))
            throw new DomainException(ErrorCode.Validation, "Enter one valid resource GUID.");
        preview = await Service.PreviewRecoveryAsync(kind, id, Lifetime);
        Status = "Every current owner has verified departure evidence. Select a replacement and confirm the consequences.";
    });

    private Task RecoverAsync() => RunAsync(async () =>
    {
        if (preview is null) return;
        var target = choices?.SingleOrDefault(x => x.Id.ToString() == selectedId)
            ?? throw new DomainException(ErrorCode.Validation, "Select an eligible replacement from the trusted search.");
        await Service.RecoverAsync(preview, target.Id, target.Version, reason, Lifetime);
        preview = null;
        Status = "Ownership recovered and audited; required owner notification was queued. No participation or invitation was restored.";
    });

    /// <inheritdoc />
    protected override void ClearSensitiveState()
    {
        authorized = false;
        choices = null;
        preview = null;
        selectedId = "";
    }
}
