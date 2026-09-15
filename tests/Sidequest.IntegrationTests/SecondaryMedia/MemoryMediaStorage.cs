using System.Collections.Concurrent;
using Sidequest.Application.Media;

namespace Sidequest.IntegrationTests.SecondaryMedia;

internal sealed class MemoryMediaStorage : IPrivateMediaStorage
{
    internal ConcurrentDictionary<string, byte[]> Blobs { get; } = new();
    internal Func<Task>? AfterWrite { get; set; }
    internal Func<Task>? AfterRead { get; set; }
    internal Func<Task>? BeforeDelete { get; set; }
    internal int Writes { get; private set; }
    internal int Reads { get; private set; }
    internal int Deletes { get; private set; }

    /// <inheritdoc />
    public async Task WriteAsync(string blobName, ReadOnlyMemory<byte> data, string contentType, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("image/png", contentType);
        Writes++;
        Assert.True(Blobs.TryAdd(blobName, data.ToArray()));
        if (AfterWrite is not null)
            await AfterWrite();
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        var bytes = Blobs[blobName].ToArray();
        if (AfterRead is not null)
            await AfterRead();
        return new MemoryStream(bytes);
    }

    /// <inheritdoc />
    public async Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Deletes++;
        if (BeforeDelete is not null)
            await BeforeDelete();
        Blobs.TryRemove(blobName, out _);
    }
}
