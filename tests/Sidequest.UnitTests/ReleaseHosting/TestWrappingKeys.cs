using System.Security.Cryptography;
using Azure;
using Azure.Core.Cryptography;

namespace Sidequest.UnitTests.ReleaseHosting;

internal sealed class TestWrappingKeys : IKeyEncryptionKeyResolver, IDisposable
{
    internal const string KeyUri = "https://vault.vault.azure.net/keys/wrapping";
    private readonly Dictionary<string, WrappingKey> versions = new();
    private string version = "version-one";
    internal bool Unavailable { get; init; }
    internal List<string> Resolved { get; } = [];

    internal TestWrappingKeys() => versions.Add(version, new(KeyUri + "/" + version));
    internal IKeyEncryptionKeyResolver CreateResolver() => new Resolver(this);
    internal void Rotate()
    {
        version = "version-two";
        versions.Add(version, new(KeyUri + "/" + version));
    }

    /// <inheritdoc />
    public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable)
            throw new RequestFailedException(403, "Synthetic wrapping permission denied.");
        Resolved.Add(keyId);
        return versions[keyId == KeyUri ? version : new Uri(keyId).Segments[^1]];
    }

    /// <inheritdoc />
    public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolve(keyId, cancellationToken));

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var key in versions.Values)
            key.Dispose();
    }

    private sealed class Resolver(TestWrappingKeys owner) : IKeyEncryptionKeyResolver
    {
        /// <inheritdoc />
        public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default) =>
            owner.Resolve(keyId, cancellationToken);
        /// <inheritdoc />
        public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) =>
            owner.ResolveAsync(keyId, cancellationToken);
    }

    private sealed class WrappingKey(string keyId) : IKeyEncryptionKey, IDisposable
    {
        private readonly RSA key = RSA.Create(2048);
        /// <inheritdoc />
        public string KeyId { get; } = keyId;
        /// <inheritdoc />
        public byte[] WrapKey(string algorithm, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("RSA-OAEP", algorithm);
            return key.Encrypt(input.Span, RSAEncryptionPadding.OaepSHA1);
        }
        /// <inheritdoc />
        public byte[] UnwrapKey(string algorithm, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("RSA-OAEP", algorithm);
            return key.Decrypt(input.Span, RSAEncryptionPadding.OaepSHA1);
        }
        /// <inheritdoc />
        public Task<byte[]> WrapKeyAsync(string algorithm, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) =>
            Task.FromResult(WrapKey(algorithm, input, cancellationToken));
        /// <inheritdoc />
        public Task<byte[]> UnwrapKeyAsync(string algorithm, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnwrapKey(algorithm, input, cancellationToken));
        /// <inheritdoc />
        public void Dispose() => key.Dispose();
    }
}
