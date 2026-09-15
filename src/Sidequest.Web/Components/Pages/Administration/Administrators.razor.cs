using Microsoft.AspNetCore.Components;
using Sidequest.Application.Administration;

namespace Sidequest.Web.Components.Pages.Administration;

/// <summary>Explicit local-account administrator assignment UI with confirmation and stale-version preservation.</summary>
public partial class Administrators
{
    [Inject] private AdministrationService Service { get; set; } = default!;
    private IReadOnlyList<AdministratorSummary>? assignments;
    private IReadOnlyList<AccountChoice>? choices;
    private AdministratorSummary? pendingRemoval;
    private AccountChoice? pendingAddition;
    private string query = "";
    private int pageNumber = 1;

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    private Task LoadAsync() => RunAsync(async () =>
    {
        assignments = await Service.ListAsync(Lifetime, new(pageNumber));
        pendingRemoval = null;
        pendingAddition = null;
    });

    private async Task PreviousAsync()
    {
        pageNumber = Math.Max(1, pageNumber - 1);
        await LoadAsync();
    }

    private async Task NextAsync()
    {
        pageNumber++;
        await LoadAsync();
    }

    private Task SearchAsync() => RunAsync(async () => choices = await Service.SearchAccountsAsync(query, Lifetime));

    private Task AddAsync() => RunAsync(async () =>
    {
        if (pendingAddition is null) return;
        await Service.AddAsync(pendingAddition.Id, pendingAddition.Version, Lifetime);
        pendingAddition = null;
        assignments = await Service.ListAsync(Lifetime, new(pageNumber));
        Status = "Administrator added and audited.";
    });

    private Task RemoveAsync() => RunAsync(async () =>
    {
        if (pendingRemoval is null) return;
        await Service.RemoveAsync(pendingRemoval.UserId, pendingRemoval.Version, Lifetime);
        pendingRemoval = null;
        assignments = await Service.ListAsync(Lifetime, new(pageNumber));
        Status = "Administrator removed and audited. Login does not restore this role.";
    });

    /// <inheritdoc />
    protected override void ClearSensitiveState()
    {
        assignments = null;
        choices = null;
        pendingRemoval = null;
        pendingAddition = null;
    }
}
