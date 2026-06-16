using LifeService.Api.Contracts;
using LifeService.Domain;
using LifeService.Domain.Abstractions;

namespace LifeService.Api.Endpoints;

/// <summary>
/// Maps the Game of Life HTTP surface under <c>/api/life</c> (SYSTEM_SPECIFICATION.md §7).
/// </summary>
public static class LifeEndpoints
{
    public static IEndpointRouteBuilder MapLifeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/life/boards").WithTags("Life");

        // 1. Upload new board.
        group.MapPost("/", async (
            UploadBoardRequest request, ILifeComputeService service, CancellationToken ct) =>
        {
            var boardId = await service.UploadInitialStateAsync(request.Cells.ToDomain(), ct);
            return Results.Created($"/api/life/boards/{boardId.Value}", new { boardId = boardId.Value });
        });

        // 2. Get next state.
        group.MapPost("/{boardId:guid}/next", async (
            Guid boardId, ILifeComputeService service, CancellationToken ct) =>
        {
            var state = await service.GetNextStateAsync(new BoardId(boardId), ct);
            return Results.Ok(state.ToResponse());
        });

        // 3. Get final (steady/limit) state summary.
        group.MapGet("/{boardId:guid}/final", async (
            Guid boardId, ILifeComputeService service, CancellationToken ct) =>
        {
            var summary = await service.GetFinalStateAsync(new BoardId(boardId), ct);
            return Results.Ok(summary.ToResponse());
        });

        // 4. Get next N states.
        group.MapPost("/{boardId:guid}/next-sequence", async (
            Guid boardId, int n, ILifeComputeService service, CancellationToken ct) =>
        {
            var states = await service.GetNextNStatesAsync(new BoardId(boardId), n, ct);
            return Results.Ok(states.Select(s => s.ToResponse()).ToList());
        });

        // 5. Get states in a label range.
        group.MapGet("/{boardId:guid}/states", async (
            Guid boardId, long from, long to, ILifeComputeService service, CancellationToken ct) =>
        {
            var states = await service.GetStatesInRangeAsync(new BoardId(boardId), from, to, ct);
            return Results.Ok(states.Select(s => s.ToResponse()).ToList());
        });

        // 6a. Inspect quarantine.
        group.MapGet("/{boardId:guid}/quarantine", async (
            Guid boardId, ILifeStorageProvider storage, CancellationToken ct) =>
        {
            var info = await storage.GetQuarantineAsync(new BoardId(boardId), ct);
            return info is null ? Results.NoContent() : Results.Ok(info.ToResponse());
        });

        // 6b. Clear quarantine.
        group.MapDelete("/{boardId:guid}/quarantine", async (
            Guid boardId, ILifeComputeService service, CancellationToken ct) =>
        {
            await service.ClearQuarantineAsync(new BoardId(boardId), ct);
            return Results.NoContent();
        });

        return app;
    }
}
