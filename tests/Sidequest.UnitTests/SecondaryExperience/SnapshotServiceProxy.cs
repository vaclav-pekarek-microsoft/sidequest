using System.Reflection;

namespace Sidequest.UnitTests.SecondaryExperience;

internal class SnapshotServiceProxy : DispatchProxy
{
    /// <summary>Constructs the test-only interface proxy used by explicit HTTP-boundary assertions.</summary>
    public SnapshotServiceProxy() { }
    internal Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        Handler(targetMethod ?? throw new InvalidOperationException("Missing test method."), args);

    internal static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var instance = DispatchProxy.Create<T, SnapshotServiceProxy>();
        ((SnapshotServiceProxy)(object)instance).Handler = handler;
        return instance;
    }
}
