using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LifeService.Tests.Integration;

/// <summary>
/// Exercises the EF Core SQLite storage provider end-to-end against a real (temporary) database
/// file, verifying that board state persists across separate HTTP requests / DbContext scopes.
/// </summary>
public sealed class SqliteStorageTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"life-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;

    public SqliteStorageTests()
    {
        var path = _dbPath;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Life:Storage:Provider", "Sqlite");
            builder.UseSetting("Life:Storage:SqliteConnectionString", $"Data Source={path}");
        });
    }

    [Fact]
    public async Task Upload_Next_Final_PersistAcrossRequests()
    {
        var client = _factory.CreateClient();

        // Upload a blinker in one request...
        var upload = await client.PostAsJsonAsync("/api/life/boards", new
        {
            cells = new[] { new { x = 1, y = 0 }, new { x = 1, y = 1 }, new { x = 1, y = 2 } },
        });
        upload.EnsureSuccessStatusCode();
        var boardId = (await upload.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("boardId").GetGuid();

        // ...advance it in a second request (reads persisted state back from SQLite)...
        var next = await client.PostAsync($"/api/life/boards/{boardId}/next", null);
        next.EnsureSuccessStatusCode();
        Assert.Equal(1, (await next.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("label").GetInt64());

        // ...and compute to steady state in a third request.
        var final = await client.GetAsync($"/api/life/boards/{boardId}/final");
        final.EnsureSuccessStatusCode();
        Assert.Equal("OscillationSteadyState",
            (await final.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Best-effort cleanup of the temp database file.
        }
    }
}
