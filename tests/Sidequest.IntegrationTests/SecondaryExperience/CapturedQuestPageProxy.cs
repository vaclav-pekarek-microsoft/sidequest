using System.Reflection;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;

namespace Sidequest.IntegrationTests.SecondaryExperience;

internal class CapturedQuestPageProxy : DispatchProxy
{
    /// <summary>Creates a test-only proxy reproducing the already-completed authorized page at a controlled revocation boundary.</summary>
    public CapturedQuestPageProxy() { }
    internal PageResult<QuestSummary>? CapturedPage { get; set; }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(IQuestService.ListAsync) ?
            Task.FromResult(CapturedPage ?? throw new InvalidOperationException("Missing captured page.")) : throw new NotSupportedException();
}
