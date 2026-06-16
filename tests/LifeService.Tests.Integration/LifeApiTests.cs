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
        var id = await UploadAsync(client, (1, 0), (1, 1), (1, 2));

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
    public async Task Quarantine_WhenNone_Returns204()
    {
        var client = _factory.CreateClient();
        var id = await UploadAsync(client, (0, 0));

        var response = await client.GetAsync($"/api/life/boards/{id}/quarantine");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
