using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;
using Sidequest.Infrastructure.Background;
using Sidequest.Web.Components.Events;

namespace Sidequest.UnitTests.CoreEvents;

/// <summary>Guards the intentionally removed moderation and one-time group-operation public surface.</summary>
public sealed class RemovedFeatureSurfaceTests
{
    /// <summary>Quest list kinds, route components and service contracts expose no moderation discriminator.</summary>
    [Fact]
    public void QuestModeration_PublicSurfaceIsAbsent()
    {
        Assert.DoesNotContain(Enum.GetNames<QuestListKind>(), name =>
            name.Contains("Moderation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(IQuestService).GetMethods(), method =>
            method.GetParameters().Any(parameter => parameter.Name?.Contains("moderation", StringComparison.OrdinalIgnoreCase) == true));
        var web = typeof(DirectoryPicker).Assembly;
        foreach (var typeName in new[]
        {
            "Sidequest.Web.Components.Pages.Quests.QuestDetailsRoute",
            "Sidequest.Web.Components.Pages.Quests.QuestDetails",
            "Sidequest.Web.Components.Quests.QuestCard",
            "Sidequest.Web.Components.Quests.QuestManagement"
        })
            Assert.DoesNotContain(web.GetType(typeName)!.GetProperties(), property =>
                property.Name.Contains("Moderation", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Group picker, Event service, directory gateway, pages and durable worker expose no removed bulk/group operation.</summary>
    [Fact]
    public void OneTimeGroupOperations_PublicSurfaceAndDispatchAreAbsent()
    {
        Assert.DoesNotContain(typeof(IEventService).GetMethods(), method =>
            method.Name.Contains("Bulk", StringComparison.OrdinalIgnoreCase) ||
            method.GetParameters().Any(parameter => parameter.Name?.Contains("group", StringComparison.OrdinalIgnoreCase) == true));
        Assert.DoesNotContain(typeof(IDirectoryGateway).GetMethods(), method =>
            method.Name.Contains("Group", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(DirectoryPicker).GetProperties(), property =>
            property.Name.Contains("Group", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Mode", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(WorkTypes).GetFields(), field =>
            field.Name.Contains("Bulk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(DurableWorkRunner).Assembly.GetTypes(), type =>
            type.Name.Contains("BulkMembershipHandler", StringComparison.OrdinalIgnoreCase));
        var web = typeof(DirectoryPicker).Assembly;
        Assert.Null(web.GetType("Sidequest.Web.Components.Pages.Events.EventBulkPage"));
        Assert.Null(web.GetType("Sidequest.Web.Components.Pages.Events.EventBulkProgressPage"));
    }
}
