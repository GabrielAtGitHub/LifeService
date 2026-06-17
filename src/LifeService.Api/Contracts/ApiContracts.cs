using LifeService.Domain;

namespace LifeService.Api.Contracts;

/// <summary>A live cell coordinate as exchanged over the wire.</summary>
public readonly record struct CellDto(int X, int Y);

/// <summary>Request body for uploading an initial board state.</summary>
public sealed record UploadBoardRequest(IReadOnlyList<CellDto> Cells);

/// <summary>Response describing a single computed state.</summary>
public sealed record LifeStateResponse(Guid BoardId, long Label, IReadOnlyList<CellDto> ActiveCells);

/// <summary>A page of results plus the metadata needed to fetch further pages.</summary>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount);

/// <summary>Response describing the solution summary for a board.</summary>
public sealed record SolutionSummaryResponse(
    Guid BoardId,
    string Status,
    long LastComputedLabel,
    long? OscillationPeriodStart,
    int? OscillationPeriodLength);

/// <summary>Response describing a quarantine record.</summary>
public sealed record QuarantineResponse(
    Guid BoardId,
    DateTimeOffset QuarantinedAt,
    string Reason,
    int RetryCount);

/// <summary>Deterministic error envelope (no stack traces, SYSTEM_SPECIFICATION.md §10).</summary>
public sealed record ErrorResponse(string Code, string Message);

/// <summary>Mapping helpers between domain types and DTOs.</summary>
public static class ContractMappings
{
    public static IReadOnlyCollection<LifeCell> ToDomain(this IReadOnlyList<CellDto> cells) =>
        cells.Select(c => new LifeCell(c.X, c.Y)).ToList();

    public static LifeStateResponse ToResponse(this LifeState state) =>
        new(state.BoardId.Value,
            state.Label.Value,
            state.ActiveCells.Select(c => new CellDto(c.X, c.Y)).ToList());

    public static PagedResponse<LifeStateResponse> ToResponse(this PagedResult<LifeState> page) =>
        new(page.Items.Select(ToResponse).ToList(),
            page.Page,
            page.PageSize,
            page.TotalCount);

    public static SolutionSummaryResponse ToResponse(this SolutionSummary summary) =>
        new(summary.BoardId.Value,
            summary.Status.ToString(),
            summary.LastComputedLabel.Value,
            summary.OscillationPeriodStart?.Value,
            summary.OscillationPeriodLength);

    public static QuarantineResponse ToResponse(this QuarantineInfo info) =>
        new(info.BoardId.Value, info.QuarantinedAt, info.Reason, info.RetryCount);
}
