using System.Net;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Media.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Media;
using Sidequest.IntegrationTests.CoreQuests;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryMedia;

/// <summary>Executes actual media cleanup with the Azure SDK and migrated SQL queue to verify immediate dead letters versus recoverable retries.</summary>
[Collection("SecondaryMedia")]
public sealed class MediaCleanupQueueTests
{
    /// <summary>Permanent configuration/policy failures dead-letter on attempt one; transient failures retain their immutable cleanup intent and succeed on a later claimed retry.</summary>
    /// <param name="failureKind">Missing configuration, public container, denied credentials, server outage, network failure or bounded timeout.</param>
    /// <param name="permanent">Whether SQL must dead-letter immediately instead of scheduling retry.</param>
    /// <returns>Completion after real claim, handler failure, queue classification, lease fencing and optional successful retry.</returns>
    [Theory]
    [InlineData("missing", true)]
    [InlineData("public", true)]
    [InlineData("authorization", true)]
    [InlineData("server", false)]
    [InlineData("network", false)]
    [InlineData("timeout", false)]
    public async Task CleanupFailure_UsesActualQueueDeadLetterOrRetry(string failureKind, bool permanent)
    {
        var database = new SqlTestDatabase();
        await database.InitializeAsync();
        try
        {
            var scenario = await MediaScenario.CreateAsync(database, draft: true);
            using var invalid = new MemoryStream([1, 2, 3]);
            var version = await scenario.VersionAsync();
            Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
                scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, invalid))).Code);
            ScheduledWork original;
            MediaAsset pending;
            await using (var read = database.CreateContext())
            {
                original = await read.ScheduledWork.SingleAsync();
                pending = await read.MediaAssets.SingleAsync();
            }
            scenario.Clock.Now = pending.CreatedUtc.AddHours(24);
            using var transport = new CleanupTransport(failureKind);
            using var http = new HttpClient(transport);
            var pipeline = new BlobClientOptions { Transport = new HttpClientTransport(http) };
            pipeline.Diagnostics.IsLoggingContentEnabled = true;
            pipeline.Retry.Delay = TimeSpan.Zero;
            pipeline.Retry.MaxDelay = TimeSpan.Zero;
            var storage = new AzurePrivateMediaStorage(failureKind == "missing" ? new() : new PrivateMediaOptions
            {
                ContainerName = "covers",
                ConnectionString = $"DefaultEndpointsProtocol=https;AccountName=synthetic;AccountKey={Convert.ToBase64String(new byte[32])};EndpointSuffix=core.windows.net",
                OperationTimeout = failureKind == "timeout" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5)
            }, pipeline);
            var factory = new QuestTestFactory(database);
            var queue = new SqlWorkQueue(factory, scenario.Clock, new DurableWorkOptions());
            var cleanup = new MediaCleanupHandler(factory, storage, scenario.Clock);
            var lease = await queue.ClaimAsync("scheduled");
            Assert.NotNull(lease);
            Assert.Equal(original.Id, lease.Id);
            var failure = await Assert.ThrowsAsync<DomainException>(() => cleanup.ExecuteAsync(lease.Id, default));
            Assert.Equal(ErrorCode.DependencyUnavailable, failure.Code);
            Assert.Equal(permanent, failure.IsPermanentDependencyFailure);
            Assert.True(await queue.FailAsync(lease, failure));
            Assert.False(await queue.CompleteAsync(lease));
            ScheduledWork failed;
            await using (var read = database.CreateContext())
            {
                failed = await read.ScheduledWork.SingleAsync();
                Assert.Equal(permanent ? WorkStatus.DeadLetter : WorkStatus.Pending, failed.Status);
                Assert.Equal(1, failed.Attempts);
                Assert.Null(failed.LeaseId);
                Assert.Null(failed.LeaseUntilUtc);
                Assert.Equal(original.PayloadJson, failed.PayloadJson);
                Assert.Equal(original.DeduplicationKey, failed.DeduplicationKey);
                Assert.Equal(MediaStatus.Failed, (await read.MediaAssets.SingleAsync()).Status);
                Assert.DoesNotContain("private-provider-sentinel", failed.LastError!);
                Assert.False(await read.AuditEntries.AnyAsync(x => x.Action == "MediaCleanupCompleted"));
            }
            if (permanent)
            {
                scenario.Clock.Now = scenario.Clock.Now.AddDays(1);
                Assert.Null(await queue.ClaimAsync("scheduled"));
                return;
            }
            Assert.True(failed.DueUtc > scenario.Clock.Now);
            Assert.Null(await queue.ClaimAsync("scheduled"));
            transport.FailureKind = "healthy";
            scenario.Clock.Now = failed.DueUtc;
            var retry = await queue.ClaimAsync("scheduled");
            Assert.NotNull(retry);
            Assert.Equal(lease.Id, retry.Id);
            Assert.NotEqual(lease.Token, retry.Token);
            Assert.Equal(2, retry.Attempts);
            await cleanup.ExecuteAsync(retry.Id, default);
            Assert.True(await queue.CompleteAsync(retry));
            await using var completed = database.CreateContext();
            var work = await completed.ScheduledWork.SingleAsync();
            Assert.Equal(WorkStatus.Completed, work.Status);
            Assert.Equal(2, work.Attempts);
            Assert.Null(work.LeaseId);
            Assert.Equal(original.PayloadJson, work.PayloadJson);
            Assert.False(await completed.MediaAssets.AnyAsync());
            Assert.Single(await completed.AuditEntries.Where(x => x.Action == "MediaCleanupCompleted").ToListAsync());
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private sealed class CleanupTransport(string failureKind) : HttpMessageHandler
    {
        internal string FailureKind { get; set; } = failureKind;

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailureKind == "network")
                throw new HttpRequestException("private-provider-sentinel");
            if (FailureKind == "timeout")
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var status = FailureKind switch
            {
                "authorization" => HttpStatusCode.Forbidden,
                "server" => HttpStatusCode.ServiceUnavailable,
                _ => request.Method == HttpMethod.Delete ? HttpStatusCode.Accepted : HttpStatusCode.OK
            };
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            response.Headers.Add("ETag", "\"synthetic\"");
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
            if (FailureKind == "public")
                response.Headers.Add("x-ms-blob-public-access", "blob");
            if ((int)status >= 400)
                response.Headers.Add("x-ms-error-code", "private-provider-sentinel");
            return response;
        }
    }
}
