using Microsoft.EntityFrameworkCore.Diagnostics;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreDelivery;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

internal static class ParticipationTestServices
{
    internal static QuestService Service(QuestScenario scenario, UserAccount? actor = null, params IInterceptor[] observers)
    {
        var writer = new ChangeWriter();
        return new QuestService(new ObservedContextFactory(scenario.Database, observers),
            new ResourceAccess(StubCurrentUser.For(actor ?? scenario.Seed.Other)), writer, scenario.Clock,
            new EventLifecycleReconciler(new QuestEventLifecycle(writer)));
    }
}
