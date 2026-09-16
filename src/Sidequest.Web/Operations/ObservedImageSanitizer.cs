using Sidequest.Application.Media;

namespace Sidequest.Web.Operations;

internal sealed class ObservedImageSanitizer(IImageSanitizer inner, OperationalActivityMetrics metrics) : IImageSanitizer
{
    /// <inheritdoc />
    public Task<SanitizedImage> SanitizeAsync(Stream content, CancellationToken cancellationToken = default) =>
        metrics.ObserveAsync(OperationalActivity.ImageSanitization, () => inner.SanitizeAsync(content, cancellationToken), cancellationToken);
}
