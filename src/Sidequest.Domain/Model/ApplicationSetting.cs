namespace Sidequest.Domain.Model;

/// <summary>Persisted allowlisted business setting; deployment secrets are not application settings.</summary>
public sealed class ApplicationSetting : Entity
{
    /// <summary>Allowlisted business configuration key.</summary>
    public string Key { get; set; } = "";
    /// <summary>Serialized configuration value validated according to its key.</summary>
    public string Value { get; set; } = "";
}
