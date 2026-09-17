using System.Security.Cryptography;
using System.Xml.Linq;
using Azure.Core.Cryptography;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Sidequest.Web.Hosting;

namespace Sidequest.UnitTests.ReleaseHosting;

/// <summary>Verifies explicit Azure-hosting boundaries, encrypted durable key behavior and metrics-only authenticated configuration without cloud calls.</summary>
public sealed class AzureHostingTests
{
    /// <summary>Restores HTTPS only from a private platform proxy and never trusts a public or unknown peer's scheme/host claims.</summary>
    /// <param name="peer">Observed immediate proxy address, or null for an unknown transport.</param>
    /// <param name="expectedScheme">Expected request scheme seen by authentication.</param>
    /// <returns>A task completing after the actual forwarded-header pipeline executes.</returns>
    [Theory]
    [InlineData("10.1.2.3", "https")]
    [InlineData("172.16.1.2", "https")]
    [InlineData("192.168.1.2", "https")]
    [InlineData("20.1.2.3", "http")]
    [InlineData(null, "http")]
    public async Task StagingProxyAcceptsOnlyPrivatePeerScheme(string? peer, string expectedScheme)
    {
        var values = Values();
        values["Hosting:Azure:AppServiceProxyEnabled"] = "true";
        var services = new ServiceCollection().AddLogging();
        services.AddSidequestAzureHosting(Configuration(values),
            new TestEnvironment { EnvironmentName = Environments.Staging }, _ => "true");
        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);
        var filter = Assert.Single(provider.GetServices<IStartupFilter>());
        filter.Configure(app => app.Run(context =>
        {
            Assert.Equal(expectedScheme, context.Request.Scheme);
            Assert.Equal("approved.azurewebsites.net", context.Request.Host.Value);
            return Task.CompletedTask;
        }))(builder);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("approved.azurewebsites.net");
        context.Connection.RemoteIpAddress = peer is null ? null : System.Net.IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "untrusted.invalid";
        await builder.Build()(context);
    }

    /// <summary>Prevents the staging proxy opt-in from changing default production hosting behavior.</summary>
    [Fact]
    public void AppServiceProxyIsExplicitAndStagingOnly()
    {
        var values = Values();
        Assert.False(AzureHostingSettings.Load(Configuration(values), new TestEnvironment())!.AppServiceProxyEnabled);
        values["Hosting:Azure:AppServiceProxyEnabled"] = "true";
        Assert.Throws<InvalidOperationException>(() => AzureHostingSettings.Load(Configuration(values), new TestEnvironment()));
        values["Hosting:Azure:AppServiceProxyEnabled"] = "not-a-boolean";
        Assert.Throws<InvalidOperationException>(() => AzureHostingSettings.Load(Configuration(values),
            new TestEnvironment { EnvironmentName = Environments.Staging }));
    }

    /// <summary>Ordinary hosts do not register Azure resources or telemetry unless deliberately enabled.</summary>
    /// <param name="flag">An absent or explicitly disabled deployment flag.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledHosting_DoesNotChangeServices(string? flag)
    {
        var values = Values();
        values["Hosting:Azure:Enabled"] = flag;
        var services = new ServiceCollection();
        Assert.Same(services, services.AddSidequestAzureHosting(Configuration(values), new TestEnvironment()));
        Assert.Empty(services);
    }

    /// <summary>Malformed and incomplete deployment settings fail before any Azure registration, without echoing supplied values.</summary>
    /// <param name="key">Configuration key to invalidate.</param>
    /// <param name="value">Invalid or missing value for that key.</param>
    [Theory]
    [InlineData("Hosting:Azure:Enabled", "private-sentinel")]
    [InlineData("Authentication:Mode", "Development")]
    [InlineData("Hosting:DataProtection:ApplicationName", null)]
    [InlineData("Hosting:DataProtection:ApplicationName", " spaced ")]
    [InlineData("Hosting:DataProtection:BlobUri", "http://account.blob.core.windows.net/container/keys.xml")]
    [InlineData("Hosting:DataProtection:BlobUri", "https://account.blob.core.windows.net/container/keys.xml?sig=private-sentinel")]
    [InlineData("Hosting:DataProtection:BlobUri", "https://user:private-sentinel@account.blob.core.windows.net/container/keys.xml")]
    [InlineData("Hosting:DataProtection:BlobUri", "https://account.blob.core.windows.net.evil.invalid/container/keys.xml")]
    [InlineData("Hosting:DataProtection:BlobUri", "https://account.blob.core.windows.net/container/keys.xml#private-sentinel")]
    [InlineData("Hosting:DataProtection:BlobUri", "https://account.blob.core.windows.net:444/container/keys.xml")]
    [InlineData("Hosting:DataProtection:BlobUri", "https://account.blob.core.windows.net/container/")]
    [InlineData("Hosting:DataProtection:KeyUri", "https://vault.vault.azure.net/keys/wrapping/version")]
    [InlineData("Hosting:DataProtection:KeyUri", "https://vault.vault.azure.net/secrets/wrapping")]
    [InlineData("Hosting:DataProtection:KeyUri", "https://vault.vault.azure.net/keys/wrapping/")]
    [InlineData("Hosting:DataProtection:KeyUri", "https://vault.vault.azure.net/keys/%2F")]
    [InlineData("Hosting:DataProtection:KeyUri", "https://vault.vault.azure.net/keys/wrapping?private-sentinel")]
    [InlineData("Hosting:DataProtection:KeyUri", "https://vault.vault.azure.net.evil.invalid/keys/wrapping")]
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING", " ")]
    public void InvalidSettings_FailBeforePartialRegistration(string key, string? value)
    {
        var values = Values();
        values[key] = value;
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddSidequestAzureHosting(Configuration(values), new TestEnvironment()));
        Assert.DoesNotContain("private-sentinel", error.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    /// <summary>Valid native cloud endpoints and the exact maximum discriminator are retained without normalization across unrelated environments.</summary>
    /// <param name="blobHost">Native Azure storage cloud suffix under test.</param>
    /// <param name="keyHost">Corresponding Key Vault cloud suffix.</param>
    [Theory]
    [InlineData("account.blob.core.windows.net", "vault.vault.azure.net")]
    [InlineData("account.blob.core.usgovcloudapi.net", "vault.vault.usgovcloudapi.net")]
    [InlineData("account.blob.core.chinacloudapi.cn", "vault.vault.azure.cn")]
    public void ValidSettings_RetainStableIdentityAndVersionlessEndpoints(string blobHost, string keyHost)
    {
        var values = Values();
        values["Hosting:DataProtection:ApplicationName"] = new string('a', 128);
        values["Hosting:DataProtection:BlobUri"] = $"https://{blobHost}/data-protection/keys.xml";
        values["Hosting:DataProtection:KeyUri"] = $"https://{keyHost}/keys/wrapping";
        var settings = Assert.IsType<AzureHostingSettings>(AzureHostingSettings.Load(Configuration(values), new TestEnvironment()));
        Assert.Equal(new string('a', 128), settings.ApplicationName);
        Assert.Equal(values["Hosting:DataProtection:BlobUri"], settings.BlobUri.AbsoluteUri);
        Assert.Equal(values["Hosting:DataProtection:KeyUri"], settings.KeyUri.AbsoluteUri);
        Assert.Equal(values["APPLICATIONINSIGHTS_CONNECTION_STRING"], settings.TelemetryConnectionString);
    }

    /// <summary>Development cannot opt into cloud credentials, and overlong application discriminators are rejected explicitly.</summary>
    [Fact]
    public void DevelopmentAndOverlongDiscriminator_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => AzureHostingSettings.Load(Configuration(Values()),
            new TestEnvironment { EnvironmentName = Environments.Development }));
        var values = Values();
        values["Hosting:DataProtection:ApplicationName"] = new string('a', 129);
        Assert.Throws<InvalidOperationException>(() => AzureHostingSettings.Load(Configuration(values), new TestEnvironment()));
    }

    /// <summary>The wrapping-key name accepts Azure's exact length limit and rejects the adjacent over-limit value.</summary>
    /// <param name="length">ASCII key-name length.</param>
    /// <param name="valid">Whether the native key-name limit is respected.</param>
    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public void WrappingKeyName_RespectsAzureLengthBoundary(int length, bool valid)
    {
        var values = Values();
        values["Hosting:DataProtection:KeyUri"] = $"https://vault.vault.azure.net/keys/{new string('k', length)}";
        if (valid)
            Assert.Equal(length, AzureHostingSettings.Load(Configuration(values), new TestEnvironment())!.KeyUri.Segments[2].Length);
        else
            Assert.Throws<InvalidOperationException>(() => AzureHostingSettings.Load(Configuration(values), new TestEnvironment()));
    }

    /// <summary>Exporter configuration uses managed identity, disables local spooling and does not install an automatic trace provider.</summary>
    [Fact]
    public void Metrics_AreNamedAuthenticatedAndDoNotEnableAutomaticTraces()
    {
        var services = new ServiceCollection().AddLogging();
        var values = Values();
        services.AddSidequestAzureHosting(Configuration(values), new TestEnvironment(), _ => "TRUE");
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<AzureMonitorExporterOptions>>()
            .Get(AzureHostingRegistration.MetricsOptionsName);
        Assert.Equal(values["APPLICATIONINSIGHTS_CONNECTION_STRING"], options.ConnectionString);
        Assert.IsType<ManagedIdentityCredential>(options.Credential);
        Assert.True(options.DisableOfflineStorage);
        Assert.False(options.EnableLiveMetrics);
        Assert.False(options.EnableStandardMetrics);
        Assert.False(options.EnablePerformanceCounters);
        var before = services.Count;
        Assert.Throws<InvalidOperationException>(() =>
            services.AddSidequestAzureHosting(Configuration(values), new TestEnvironment(), _ => "true"));
        Assert.Equal(before, services.Count);
    }

    /// <summary>SDK diagnostic opt-outs must be present in the real process boundary, not merely in application configuration.</summary>
    /// <param name="variable">SDK process variable whose opt-out is missing or malformed.</param>
    /// <param name="value">Value observed at the process boundary.</param>
    [Theory]
    [InlineData(AzureHostingRegistration.StatsbeatDisabledVariable, null)]
    [InlineData(AzureHostingRegistration.StatsbeatDisabledVariable, "false")]
    [InlineData(AzureHostingRegistration.StatsbeatDisabledVariable, " true ")]
    [InlineData(AzureHostingRegistration.SdkStatsDisabledVariable, null)]
    [InlineData(AzureHostingRegistration.SdkStatsDisabledVariable, "private-sentinel")]
    public void DiagnosticsOptOut_RequiresProcessEnvironmentBeforeRegistration(string variable, string? value)
    {
        var values = Values();
        values[variable] = "true";
        var services = new ServiceCollection();
        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddSidequestAzureHosting(Configuration(values), new TestEnvironment(),
                name => name == variable ? value : "true"));
        Assert.Contains(variable, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sentinel", error.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    /// <summary>The actual Azure Data Protection providers persist encrypted keys and read them across host restarts and wrapping-key rotation.</summary>
    [Fact]
    public void EncryptedKeyRing_SurvivesRestartAndWrappingKeyRotation()
    {
        var blob = new MemoryKeyRingBlob();
        using var wrapping = new TestWrappingKeys();
        string original;
        using (var first = Provider(blob, wrapping))
        {
            original = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("synthetic session");
        }
        Assert.NotNull(blob.Contents);
        using var storedBytes = new MemoryStream(blob.Contents!);
        var stored = XDocument.Load(storedBytes);
        Assert.DoesNotContain(stored.Descendants(), element => element.Name.LocalName == "masterKey");
        Assert.Contains(stored.Descendants(), element => element.Name.LocalName == "encryptedKey");
        wrapping.Rotate();
        using (var restarted = Provider(blob, wrapping))
        {
            Assert.Equal("synthetic session",
                restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Unprotect(original));
            restarted.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        }
        using (var third = Provider(blob, wrapping))
        {
            var protector = third.GetRequiredService<IDataProtectionProvider>().CreateProtector("session");
            Assert.Equal("synthetic session", protector.Unprotect(original));
            var current = protector.Protect("rotated session");
            Assert.Equal("rotated session", protector.Unprotect(current));
        }
        Assert.Contains(TestWrappingKeys.KeyUri, wrapping.Resolved);
        Assert.Contains(TestWrappingKeys.KeyUri + "/version-one", wrapping.Resolved);
        Assert.Contains(TestWrappingKeys.KeyUri + "/version-two", wrapping.Resolved);
        using var differentApplication = Provider(blob, wrapping, "Sidequest:other-environment");
        Assert.Throws<CryptographicException>(() =>
            differentApplication.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Unprotect(original));
    }

    /// <summary>Key Vault wrapping failure cannot cause an unencrypted key-ring write or a successful protected session.</summary>
    [Fact]
    public void WrappingFailure_DoesNotPersistPlaintextOrReturnProtectedData()
    {
        var blob = new MemoryKeyRingBlob();
        using var wrapping = new TestWrappingKeys { Unavailable = true };
        using var provider = Provider(blob, wrapping);
        Assert.Throws<CryptographicException>(() =>
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("synthetic session"));
        Assert.Null(blob.Contents);
    }

    /// <summary>Denied key-ring storage cannot silently switch to a local or ephemeral key store.</summary>
    [Fact]
    public void StorageFailure_DoesNotFallBackToLocalKeys()
    {
        var blob = new MemoryKeyRingBlob { Unavailable = true };
        using var wrapping = new TestWrappingKeys();
        using var provider = Provider(blob, wrapping);
        Assert.Throws<CryptographicException>(() =>
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("synthetic session"));
        Assert.Null(blob.Contents);
    }

    private static ServiceProvider Provider(MemoryKeyRingBlob blob, TestWrappingKeys keys, string? applicationName = null)
    {
        var values = Values();
        if (applicationName is not null)
            values["Hosting:DataProtection:ApplicationName"] = applicationName;
        var services = new ServiceCollection().AddLogging();
        services.AddKeyedSingleton<BlobClient>(AzureHostingRegistration.DataProtectionClientKey, blob);
        services.AddKeyedSingleton<IKeyEncryptionKeyResolver>(AzureHostingRegistration.DataProtectionClientKey, keys.CreateResolver());
        services.AddSidequestAzureHosting(Configuration(values), new TestEnvironment(), _ => "true");
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> Values() => new()
    {
        ["Hosting:Azure:Enabled"] = "true",
        ["Authentication:Mode"] = "Entra",
        ["Hosting:DataProtection:ApplicationName"] = "Sidequest:synthetic-hosting",
        ["Hosting:DataProtection:BlobUri"] = "https://account.blob.core.windows.net/data-protection/keys.xml",
        ["Hosting:DataProtection:KeyUri"] = TestWrappingKeys.KeyUri,
        ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=11111111-1111-4111-8111-111111111111"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TestEnvironment : IHostEnvironment
    {
        /// <inheritdoc />
        public string EnvironmentName { get; set; } = Environments.Production;
        /// <inheritdoc />
        public string ApplicationName { get; set; } = "Sidequest.Web";
        /// <inheritdoc />
        public string ContentRootPath { get; set; } = "";
        /// <inheritdoc />
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
