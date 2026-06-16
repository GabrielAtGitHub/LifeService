using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LifeService.Tests.Integration;

/// <summary>
/// End-to-end test of a complete user journey through the public HTTP API, exercising the whole
/// stack (endpoints → middleware → application → compute engine → storage) in process.
///
/// Scenario: a user uploads a blinker oscillator, steps it forward one generation, fast-forwards a
/// few more, reads back the persisted history, computes the steady state, and checks quarantine.
/// Run locally with:
///   dotnet test tests/LifeService.Tests.Integration --filter FullyDescribesABlinkerLifecycle
/// </summary>
public class FullScenarioE2ETests : IClassFixture<LifeApiFactory>
{
    private readonly LifeApiFactory _factory;

    public FullScenarioE2ETests(LifeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task FullyDescribesABlinkerLifecycle()
    {
        var client = _factory.CreateClient();

        // A vertical blinker: a period-2 oscillator.
        var vertical = new HashSet<(int, int)> { (1, 0), (1, 1), (1, 2) };
        var horizontal = new HashSet<(int, int)> { (0, 1), (1, 1), (2, 1) };

        // 0. The service is healthy.
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // 1. Upload the initial board.
        var upload = await client.PostAsJsonAsync("/api/life/boards", new
        {
            cells = vertical.Select(c => new { x = c.Item1, y = c.Item2 }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var boardId = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("boardId").GetGuid();
        Assert.NotEqual(Guid.Empty, boardId);

        // 2. The persisted initial state (label 0) matches what we uploaded.
        var initial = await GetStatesAsync(client, boardId, 0, 0);
        Assert.Single(initial);
        Assert.Equal(0, initial[0].GetProperty("label").GetInt64());
        Assert.Equal(vertical, CellsOf(initial[0]));

        // 3. Step one generation: the blinker flips to horizontal at label 1.
        var nextResponse = await client.PostAsync($"/api/life/boards/{boardId}/next", null);
        nextResponse.EnsureSuccessStatusCode();
        var next = await nextResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, next.GetProperty("label").GetInt64());
        Assert.Equal(horizontal, CellsOf(next));

        // 4. Fast-forward three more generations (labels 2, 3, 4).
        var seqResponse = await client.PostAsync($"/api/life/boards/{boardId}/next-sequence?n=3", null);
        seqResponse.EnsureSuccessStatusCode();
        var sequence = (await seqResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(3, sequence.Count);
        Assert.Equal([2L, 3L, 4L], sequence.Select(s => s.GetProperty("label").GetInt64()).ToArray());
        // Period 2: even labels are vertical, odd labels are horizontal.
        Assert.Equal(vertical, CellsOf(sequence[0]));   // label 2
        Assert.Equal(horizontal, CellsOf(sequence[1])); // label 3
        Assert.Equal(vertical, CellsOf(sequence[2]));   // label 4

        // 5. Read back the full persisted history [0..4] in order.
        var history = await GetStatesAsync(client, boardId, 0, 4);
        Assert.Equal([0L, 1L, 2L, 3L, 4L], history.Select(s => s.GetProperty("label").GetInt64()).ToArray());

        // 6. Compute the steady state: the engine detects the period-2 oscillation.
        var finalResponse = await client.GetAsync($"/api/life/boards/{boardId}/final");
        finalResponse.EnsureSuccessStatusCode();
        var summary = await finalResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OscillationSteadyState", summary.GetProperty("status").GetString());
        Assert.Equal(2, summary.GetProperty("oscillationPeriodLength").GetInt32());

        // 7. The board was never quarantined.
        var quarantine = await client.GetAsync($"/api/life/boards/{boardId}/quarantine");
        Assert.Equal(HttpStatusCode.NoContent, quarantine.StatusCode);

        // 8. A request against an unknown board is rejected deterministically.
        var unknown = await client.PostAsync($"/api/life/boards/{Guid.NewGuid()}/next", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var error = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BoardNotFound", error.GetProperty("code").GetString());
    }

    private static async Task<List<JsonElement>> GetStatesAsync(
        HttpClient client, Guid boardId, long from, long to)
    {
        var response = await client.GetAsync($"/api/life/boards/{boardId}/states?from={from}&to={to}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private static HashSet<(int, int)> CellsOf(JsonElement state)
    {
        var cells = new HashSet<(int, int)>();
        foreach (var c in state.GetProperty("activeCells").EnumerateArray())
        {
            cells.Add((c.GetProperty("x").GetInt32(), c.GetProperty("y").GetInt32()));
        }

        return cells;
    }
}
