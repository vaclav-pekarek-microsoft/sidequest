namespace Sidequest.UnitTests.ReleaseOperations;

/// <summary>Serializes meter-listener tests so identical production meter names cannot mix observations across hosts.</summary>
[CollectionDefinition("Release operational metrics", DisableParallelization = true)]
public sealed class OperationalMetricsCollection;
