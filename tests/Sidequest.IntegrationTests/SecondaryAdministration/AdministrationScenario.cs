using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Administration;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryAdministration;

internal sealed class AdministrationScenario : IAsyncDisposable
{
    internal SqlTestDatabase Database { get; } = new();
    internal ScenarioContextFactory Factory { get; private set; } = null!;
    internal FoundationSeed Seed { get; private set; } = null!;
    internal StubCurrentUser Current { get; private set; } = null!;
    internal DeliveryClock Clock { get; } = new(FoundationSeed.Now);
    internal DepartureRecoveryPolicy Gate { get; } = new() { Enabled = true, ProcedureReference = "SYNTHETIC-TEST-PROCEDURE" };
    internal UserAccount Replacement { get; private set; } = null!;
    internal ResourceAccess Access => new(Current);
    internal AdministrationService Service => new(Factory, Access, new ChangeWriter(), Clock, Gate);
    internal BusinessEmailService Email => new(Factory, Access, Clock);

    internal static async Task<AdministrationScenario> CreateAsync()
    {
        var scenario = new AdministrationScenario();
        var ready = false;
        try
        {
            await scenario.Database.InitializeAsync();
            scenario.Factory = new(scenario.Database);
            scenario.Seed = await FoundationSeed.CreateAsync(scenario.Database);
            scenario.Current = StubCurrentUser.For(scenario.Seed.User);
            await using var db = scenario.Database.CreateContext();
            var departed = await db.Users.SingleAsync(x => x.Id == scenario.Seed.Other.Id);
            departed.TenantId = scenario.Seed.User.TenantId;
            departed.IsEligible = false;
            departed.DepartureVerifiedUtc = FoundationSeed.Now.AddDays(-1);
            scenario.Replacement = FoundationSeed.NewUser();
            scenario.Replacement.TenantId = scenario.Seed.User.TenantId;
            scenario.Replacement.DisplayName = "Replacement Person";
            db.Users.Add(scenario.Replacement);
            db.Administrators.Add(new Administrator { UserId = scenario.Seed.User.Id });
            db.EventOwners.Add(new EventOwner { EventId = scenario.Seed.Event.Id, UserId = departed.Id });
            db.QuestOwners.Add(new QuestOwner { QuestId = scenario.Seed.Quest.Id, UserId = departed.Id });
            db.EventMemberships.Add(scenario.Seed.Membership(departed.Id));
            (await db.Quests.SingleAsync()).Visibility = QuestVisibility.Private;
            await db.SaveChangesAsync();
            ready = true;
            return scenario;
        }
        finally
        {
            if (!ready) await scenario.DisposeAsync();
        }
    }

    internal Task<RecoveryPreview> PreviewAsync(ResourceKind kind = ResourceKind.Quest) =>
        Service.PreviewRecoveryAsync(kind, kind == ResourceKind.Quest ? Seed.Quest.Id : Seed.Event.Id);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(Database.DisposeAsync());
}
