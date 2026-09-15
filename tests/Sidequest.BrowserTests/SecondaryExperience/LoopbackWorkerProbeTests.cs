namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Checks the worker test's alternate-origin HTTP probe without starting any browser or contacting an external host.</summary>
public sealed class LoopbackWorkerProbeTests
{
    /// <summary>The ephemeral probe binds only loopback, serves a distinguishable response and counts real HTTP contacts.</summary>
    /// <returns>Completion after two independent loopback requests and exact response/contact-count assertions.</returns>
    [Fact]
    public async Task ProbeIsLiveOnLoopbackAndCountsControlRequests()
    {
        await using var probe = new LoopbackWorkerProbe();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.True(probe.Url.IsLoopback);
        Assert.Equal("http", probe.Url.Scheme);
        Assert.Equal(0, probe.Requests);
        Assert.Equal(probe.Body, await client.GetStringAsync(probe.Url));
        Assert.Equal(probe.Body, await client.GetStringAsync(probe.Url));
        Assert.Equal(2, probe.Requests);
    }
}
