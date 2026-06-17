using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace LifeService.Tests.Integration;

/// <summary>
/// Test host that pins the in-memory storage provider, keeping integration tests hermetic and
/// independent of the environment-specific SQLite configuration.
/// </summary>
public sealed class LifeApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Life:Storage:Provider", "InMemory");
    }
}

/// <summary>
/// End-to-end API + infrastructure integration tests (SYSTEM_SPECIFICATION.md §11) exercising
/// positive flows (upload → next → final) and negative flows (limits, invalid IDs, ranges).
/// </summary>
public class LifeApiTests : IClassFixture<LifeApiFactory>
{
    private readonly LifeApiFactory _factory;

    public LifeApiTests(LifeApiFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static object Board(params (int X, int Y)[] cells) =>
        new { cells = cells.Select(c => new { x = c.X, y = c.Y }).ToArray() };

    private async Task<Guid> UploadAsync(HttpClient client, params (int X, int Y)[] cells)
    {
        var response = await client.PostAsJsonAsync("/api/life/boards", Board(cells));
        response.EnsureSuccessStatusCode();
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("boardId").GetGuid();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadThenNext_AdvancesGeneration()
    {
        var client = _factory.CreateClient();
        var id = await UploadAsync(client, (1, 0), (1, 1), (1, 2));

        var response = await client.PostAsync($"/api/life/boards/{id}/next", null);
        response.EnsureSuccessStatusCode();

        var state = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, state.GetProperty("label").GetInt64());
        Assert.Equal(3, state.GetProperty("activeCells").GetArrayLength());
    }

    [Fact]
    public async Task GetFinal_ForBlinker_ReportsOscillation()
    {
        var client = _factory.CreateClient();
        // A distinct blinker location: the in-memory store is shared across this class's tests and
        // uploads are now idempotent by content, so each test that mutates a board uses its own seed.
        var id = await UploadAsync(client, (10, 0), (10, 1), (10, 2));

        var response = await client.GetAsync($"/api/life/boards/{id}/final");
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OscillationSteadyState", summary.GetProperty("status").GetString());
        Assert.Equal(2, summary.GetProperty("oscillationPeriodLength").GetInt32());
    }

    [Fact]
    public async Task GetNext_OnUnknownBoard_Returns404()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsync($"/api/life/boards/{Guid.NewGuid()}/next", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetStatesInRange_InvalidRange_Returns400()
    {
        var client = _factory.CreateClient();
        var id = await UploadAsync(client, (0, 0));

        var response = await client.GetAsync($"/api/life/boards/{id}/states?from=10&to=1");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Upload_BeyondActiveCellLimit_Returns422()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Life:Limits:MaxActiveCells", "2"))
            .CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/life/boards", Board((0, 0), (1, 1), (2, 2)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UploadDuplicate_ReturnsExistingBoard_With200()
    {
        var client = _factory.CreateClient();
        var seed = Board((30, 30), (31, 31), (32, 32));

        var first = await client.PostAsJsonAsync("/api/life/boards", seed);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("boardId").GetGuid();

        // Re-uploading the identical state returns the existing board id with 200 OK (idempotent).
        var second = await client.PostAsJsonAsync("/api/life/boards", seed);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("boardId").GetGuid();

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task ListBoards_ReturnsFirstStateOfEachBoard_InCreationOrder_Paginated()
    {
        // Isolated host so the shared in-memory store from sibling tests doesn't skew the totals.
        var factory = _factory.WithWebHostBuilder(_ => { });
        var client = factory.CreateClient();

        var id1 = await UploadAsync(client, (1, 1));
        var id2 = await UploadAsync(client, (2, 2));
        var id3 = await UploadAsync(client, (3, 3));

        var first = await client.GetAsync("/api/life/boards?page=1&pageSize=2");
        first.EnsureSuccessStatusCode();
        var page1 = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(3, page1.GetProperty("totalCount").GetInt64());
        Assert.Equal(1, page1.GetProperty("page").GetInt32());
        Assert.Equal(2, page1.GetProperty("pageSize").GetInt32());
        var items = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, s => Assert.Equal(0, s.GetProperty("label").GetInt64()));
        Assert.All(items, s => Assert.True(s.TryGetProperty("createdAt", out _)));
        // Boards come back in creation order.
        Assert.Equal(new[] { id1, id2 }, items.Select(s => s.GetProperty("boardId").GetGuid()).ToArray());

        var second = await client.GetAsync("/api/life/boards?page=2&pageSize=2");
        second.EnsureSuccessStatusCode();
        var page2 = await second.Content.ReadFromJsonAsync<JsonElement>();
        var page2Items = page2.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { id3 }, page2Items.Select(s => s.GetProperty("boardId").GetGuid()).ToArray());
    }

    [Fact]
    public async Task ListBoards_InvalidPagination_Returns400()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/life/boards?page=0&pageSize=10");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Quarantine_WhenNone_Returns204()
    {
        var client = _factory.CreateClient();
        var id = await UploadAsync(client, (0, 0));

        var response = await client.GetAsync($"/api/life/boards/{id}/quarantine");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
