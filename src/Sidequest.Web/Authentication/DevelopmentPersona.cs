namespace Sidequest.Web.Authentication;

/// <summary>Identifies a named synthetic account used exclusively by the development sign-in flow.</summary>
/// <param name="Name">The display name and local part used for the synthetic contact address.</param>
/// <param name="ObjectId">The fixed synthetic object identifier, not an identifier for a real workforce account.</param>
/// <remarks>Instances are immutable and may be shared across requests.</remarks>
public sealed record DevelopmentPersona(string Name, Guid ObjectId)
{
    /// <summary>Gets the lowercase contact address in the reserved <c>sample.invalid</c> domain.</summary>
    public string Email => $"{Name.ToLowerInvariant()}@sample.invalid";
}
