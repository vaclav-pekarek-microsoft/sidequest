using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Sidequest.UnitTests.ReleaseHosting;

internal sealed class MemoryKeyRingBlob : BlobClient
{
    private int revision;
    internal bool Unavailable { get; init; }
    internal byte[]? Contents { get; private set; }
    private ETag Tag => new(revision.ToString(CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override Response DownloadTo(Stream destination, BlobRequestConditions? conditions = null,
        StorageTransferOptions transferOptions = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable)
            throw new RequestFailedException(403, "Synthetic key-ring storage permission denied.");
        if (Contents is null)
            throw new RequestFailedException(404, "Synthetic key ring is absent.");
        if (conditions?.IfNoneMatch == Tag)
            return new BlobResponse(304, Tag);
        destination.Write(Contents);
        return new BlobResponse(200, Tag);
    }

    /// <inheritdoc />
    public override Response<BlobContentInfo> Upload(Stream content, BlobHttpHeaders? httpHeaders = null,
        IDictionary<string, string>? metadata = null, BlobRequestConditions? conditions = null,
        IProgress<long>? progressHandler = null, AccessTier? accessTier = null,
        StorageTransferOptions transferOptions = default, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((conditions?.IfNoneMatch == ETag.All && Contents is not null) ||
            (conditions?.IfMatch is { } expected && expected != Tag))
            throw new RequestFailedException(412,
                $"Synthetic key ring version conflict: match={conditions?.IfMatch}, absent={conditions?.IfNoneMatch}, current={Tag}.");
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        Contents = buffer.ToArray();
        revision++;
        return Response.FromValue(BlobsModelFactory.BlobContentInfo(Tag, DateTimeOffset.UtcNow, null, null, null, null, 0L),
            new BlobResponse(201, Tag));
    }

    private sealed class BlobResponse(int status, ETag tag) : Response
    {
        /// <inheritdoc />
        public override int Status => status;
        /// <inheritdoc />
        public override string ReasonPhrase => "";
        /// <inheritdoc />
        public override Stream? ContentStream { get; set; }
        /// <inheritdoc />
        public override string ClientRequestId { get; set; } = "";
        /// <inheritdoc />
        public override void Dispose() { }
        /// <inheritdoc />
        protected override bool ContainsHeader(string name) => name.Equals("ETag", StringComparison.OrdinalIgnoreCase);
        /// <inheritdoc />
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [new("ETag", tag.ToString("H"))];
        /// <inheritdoc />
        protected override bool TryGetHeader(string name, out string value)
        {
            value = ContainsHeader(name) ? tag.ToString("H") : "";
            return ContainsHeader(name);
        }
        /// <inheritdoc />
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            var found = TryGetHeader(name, out var value);
            values = found ? [value] : [];
            return found;
        }
    }
}
