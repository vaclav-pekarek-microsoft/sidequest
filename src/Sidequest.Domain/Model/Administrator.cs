namespace Sidequest.Domain.Model;

/// <summary>Explicit global administrator assignment; grants no implicit Event membership or private content access.</summary>
public sealed class Administrator : Entity
{
    /// <summary>Internal account identifier receiving administrative privileges.</summary>
    public Guid UserId { get; set; }
}
