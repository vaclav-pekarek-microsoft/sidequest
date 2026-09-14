namespace Sidequest.Domain.Model;

/// <summary>Base for persisted records identified independently of directory identities.</summary>
/// <remarks>Entities and their mutable rowversion arrays are not thread-safe. Keep tracked instances within their owning
/// operation and do not read or mutate them concurrently with persistence or another writer.</remarks>
public abstract class Entity
{
    /// <summary>Application-generated internal identifier; not an Entra object ID.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Opaque SQL rowversion concurrency token; empty before persistence and not a timestamp.</summary>
    public byte[] Version { get; set; } = [];
}
