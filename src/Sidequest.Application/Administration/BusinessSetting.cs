namespace Sidequest.Application.Administration;

/// <summary>Nonsecret allowlisted email setting with optimistic update evidence.</summary>
/// <param name="Key">Stable business setting key.</param>
/// <param name="Value">Validated value or compiled default when no override exists.</param>
/// <param name="Version">Persisted rowversion, empty for a compiled default that has not been overridden.</param>
public sealed record BusinessSetting(string Key, string Value, byte[] Version);
