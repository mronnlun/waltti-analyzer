using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WalttiAnalyzer.Core.Models;
using WalttiAnalyzer.Core.Services;
using Xunit;

namespace WalttiAnalyzer.Tests;

/// <summary>
/// Regression tests for the April 2026 collection outage: the Digitransit stops(ids:)
/// query returns a positional null for every stop that has been removed from the feed,
/// which crashed ProcessSlidingWindowBatch and aborted all remaining batches.
/// </summary>
public class CollectorTests
{
    private const string ApiUrl = "https://api.example.test/graphql";

    /// <summary>Returns queued responses in order; repeats the last one when exhausted.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public int RequestCount { get; private set; }

        public StubHandler(params string[] responses) => _responses = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static DigitransitClient MakeClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri(ApiUrl) },
            NullLogger<DigitransitClient>.Instance);

    private static CollectorService MakeCollector(TestDbFixture fixture, DigitransitClient client) =>
        new(NullLogger<CollectorService>.Instance, fixture.Db, client,
            Options.Create(new WalttiSettings { FeedId = "Vaasa", DigitransitApiKey = "test-key" }));

    private static string StopJson(string gtfsId, long serviceDay, int scheduledDeparture) => $$"""
        {
          "gtfsId": "{{gtfsId}}",
          "name": "Test stop",
          "stoptimesWithoutPatterns": [
            {
              "serviceDay": {{serviceDay}},
              "scheduledDeparture": {{scheduledDeparture}},
              "departureDelay": 120,
              "realtime": true,
              "realtimeState": "UPDATED",
              "headsign": "Keskusta",
              "trip": {
                "gtfsId": "Vaasa:testtrip1",
                "route": { "gtfsId": "Vaasa:3", "shortName": "3", "longName": "Gerby - Keskusta", "mode": "BUS" }
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task FetchSlidingWindow_FiltersNullStops()
    {
        var handler = new StubHandler("""
            {"data":{"stops":[null,{"gtfsId":"Vaasa:309392","name":"Gerbynmäentie","stoptimesWithoutPatterns":[]},null]}}
            """);
        var client = MakeClient(handler);

        var stops = await client.FetchSlidingWindowAsync(
            new List<string> { "Vaasa:gone1", "Vaasa:309392", "Vaasa:gone2" }, 1750000000, 1200);

        var stop = Assert.Single(stops);
        Assert.Equal("Vaasa:309392", stop.GetProperty("gtfsId").GetString());
    }

    [Fact]
    public async Task PollSlidingWindow_NullStop_StillCollectsValidStops()
    {
        using var fixture = new TestDbFixture();
        var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long serviceDay = nowUtc - 40000;

        // A removed-from-feed stop (null) alongside a valid stop in the same batch.
        var handler = new StubHandler($$$"""
            {"data":{"stops":[null,{{{StopJson("Vaasa:309392", serviceDay, 10000)}}}]}}
            """);
        var collector = MakeCollector(fixture, MakeClient(handler));

        var result = await collector.PollSlidingWindowAsync();

        Assert.Equal("ok", result["status"]);
        Assert.False(result.ContainsKey("failed_batches"));
        var obs = Assert.Single(fixture.Context.Observations.ToList());
        var stop = await fixture.Db.GetStopAsync("Vaasa:309392");
        Assert.Equal(stop!.Id, obs.StopId);
        Assert.Equal(2, obs.DelaySource); // departure in the past => measured
    }

    [Fact]
    public async Task PollSlidingWindow_FailedBatch_ContinuesWithRemainingBatches()
    {
        using var fixture = new TestDbFixture();
        var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long serviceDay = nowUtc - 40000;

        // 60 extra stops => two batches of (50, 11) ordered by name.
        for (int i = 1; i <= 60; i++)
            await fixture.Db.UpsertStopAsync($"Vaasa:{100000 + i}", $"Stop {i:D3}", null, null, null);

        // Batch 1: corrupt stoptime (realtime=true but no scheduledDeparture) => batch throws.
        // Batch 2: valid data => must still be collected.
        var corruptBatch = $$$"""
            {"data":{"stops":[{
              "gtfsId": "Vaasa:309392",
              "name": "Gerbynmäentie",
              "stoptimesWithoutPatterns": [{
                "serviceDay": {{{serviceDay}}},
                "realtime": true,
                "trip": { "gtfsId": "Vaasa:testtrip2", "route": { "gtfsId": "Vaasa:3" } }
              }]
            }]}}
            """;
        var validBatch = $$$"""
            {"data":{"stops":[{{{StopJson("Vaasa:100050", serviceDay, 10000)}}}]}}
            """;
        var handler = new StubHandler(corruptBatch, validBatch);
        var collector = MakeCollector(fixture, MakeClient(handler));

        var result = await collector.PollSlidingWindowAsync();

        Assert.Equal(2, handler.RequestCount);          // second batch was still queried
        Assert.Equal("ok", result["status"]);           // partial success is still success
        Assert.Equal(1, result["failed_batches"]);
        var obs = Assert.Single(fixture.Context.Observations.ToList());
        var stop = await fixture.Db.GetStopAsync("Vaasa:100050");
        Assert.Equal(stop!.Id, obs.StopId);             // batch 2 data landed
    }

    [Fact]
    public async Task GetAllStopIdsAsync_ExcludesStopsNotSeenByDiscovery()
    {
        using var fixture = new TestDbFixture();
        var nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await fixture.Db.UpsertStopAsync("Vaasa:999001", "Removed stop", null, null, null);
        var removed = await fixture.Db.GetStopAsync("Vaasa:999001");
        removed!.UpdatedAt = nowUtc - 8 * 24 * 3600; // last seen 8 days ago
        await fixture.Context.SaveChangesAsync();

        var active = await fixture.Db.GetAllStopIdsAsync("Vaasa", updatedSinceUnix: nowUtc - 7 * 24 * 3600);
        var all = await fixture.Db.GetAllStopIdsAsync("Vaasa");

        Assert.Contains("Vaasa:309392", active);
        Assert.DoesNotContain("Vaasa:999001", active);
        Assert.Contains("Vaasa:999001", all); // unfiltered query still returns it (history/UI)
    }
}
