using Sidequest.Domain.Model;

namespace Sidequest.Application.Administration;

/// <summary>Content-free recovery confirmation for one explicitly supplied resource ID.</summary>
/// <param name="Kind">Event or Quest; no other resource namespace is supported.</param>
/// <param name="ResourceId">Caller-supplied identifier, not a resource discovery result.</param>
/// <param name="Token">Opaque concurrency digest of the resource, owner assignments and verified departure evidence.</param>
/// <remarks>No title, description, lifecycle, owner identity or private roster is exposed.</remarks>
public sealed record RecoveryPreview(ResourceKind Kind, Guid ResourceId, string Token);
