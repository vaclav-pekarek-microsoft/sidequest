using Microsoft.AspNetCore.Components;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Web.Experience;

namespace Sidequest.Web.Components.Experience;

/// <summary>Owns the signed-in Quest home projection and invitation participation commands.</summary>
public partial class QuestDashboard : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private ExperienceViewSubscription? experience;
    private QuestHomeData? data;
    private string? error;
    private int generation;
    private bool isJoining;
    private bool isLoading = true;

    [Inject] private QuestHomeService Home { get; set; } = null!;
    [Inject] private IQuestService Quests { get; set; } = null!;
    [Inject] private ExperienceCoordinator Experience { get; set; } = null!;
    [Inject] private ILogger<QuestDashboard> Logger { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnInitialized() =>
        experience = new(Experience, () => InvokeAsync(StateHasChanged), ReauthorizeAsync);

    /// <inheritdoc />
    protected override Task OnAfterRenderAsync(bool firstRender) =>
        RendererInfo.IsInteractive ? experience?.AfterRenderAsync() ?? Task.CompletedTask : Task.CompletedTask;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var request = ++generation;
        isLoading = true;
        error = null;
        data = null;
        try
        {
            var next = await Home.GetAsync(lifetime.Token);
            if (request != generation || lifetime.IsCancellationRequested)
                return;
            data = next;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (DomainException ex) when (request == generation)
        {
            error = ex.Message;
        }
        catch (Exception) when (request != generation)
        {
        }
        catch (Exception ex)
        {
            var reference = Guid.NewGuid().ToString("N");
            Logger.LogError(ex, "Quest home load failed. Reference {Reference}.", reference);
            error = $"Quest overview could not be loaded. Please retry. Reference: {reference}.";
        }
        finally
        {
            if (request == generation)
            {
                isLoading = false;
                if (experience is not null)
                    await experience.AfterOperationAsync();
            }
        }
    }

    private async Task JoinInvitationAsync(Guid questId)
    {
        if (data is null || isJoining || !RendererInfo.IsInteractive || !Experience.CanUseOnlineActions ||
            !data.Invitations.Any(quest => quest.Id == questId))
            return;

        isJoining = true;
        error = null;
        try
        {
            await Quests.ParticipateAsync(questId, ParticipationCommand.Join, lifetime.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (DomainException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            var reference = Guid.NewGuid().ToString("N");
            Logger.LogError(ex, "Quest invitation join failed for {QuestId}. Reference {Reference}.", questId, reference);
            error = $"The Quest could not be joined. Please retry. Reference: {reference}.";
        }
        finally
        {
            isJoining = false;
        }
    }

    private Task ReauthorizeAsync() => InvokeAsync(async () =>
    {
        if (lifetime.IsCancellationRequested)
            return;
        await LoadAsync();
        if (!lifetime.IsCancellationRequested)
            StateHasChanged();
    });

    /// <summary>Cancels pending operations and removes connection-coordinator subscriptions.</summary>
    /// <returns>A completed asynchronous disposal operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (experience is not null)
            await experience.DisposeAsync();
        generation++;
        await lifetime.CancelAsync();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
