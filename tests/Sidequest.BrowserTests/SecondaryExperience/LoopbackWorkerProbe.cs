using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sidequest.BrowserTests.SecondaryExperience;

internal sealed class LoopbackWorkerProbe : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task serving;
    private int requests;

    internal LoopbackWorkerProbe()
    {
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Url = new Uri($"http://127.0.0.1:{endpoint.Port}/worker-origin-probe");
        serving = ServeAsync();
    }

    internal Uri Url { get; }
    internal string Body { get; } = $"sidequest-loopback-probe-{Guid.NewGuid():N}";
    internal int Requests => Volatile.Read(ref requests);

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                Interlocked.Increment(ref requests);
                await using var stream = client.GetStream();
                var headers = new byte[8192];
                var count = 0;
                while (count < headers.Length)
                {
                    var read = await stream.ReadAsync(headers.AsMemory(count), lifetime.Token);
                    if (read == 0) break;
                    count += read;
                    if (Encoding.ASCII.GetString(headers, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                var body = Encoding.ASCII.GetBytes(Body);
                var response = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n" +
                    "Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, lifetime.Token);
                await stream.WriteAsync(body, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener.Stop();
        try { await serving; }
        finally { lifetime.Dispose(); }
    }
}
