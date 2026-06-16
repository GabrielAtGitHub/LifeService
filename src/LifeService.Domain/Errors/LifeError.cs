namespace LifeService.Domain.Errors;

/// <summary>
/// Canonical error codes surfaced by the service. The API maps these to HTTP status codes
/// without ever leaking stack traces (see SYSTEM_SPECIFICATION.md §10).
/// </summary>
public enum LifeErrorCode
{
    BoardNotFound,
    BoardQuarantined,
    ActiveCellLimitExceeded,
    StatesLimitExceeded,
    InvalidRange,
    InternalError,
}

/// <summary>
/// Domain exception carrying a stable <see cref="LifeErrorCode"/>. Application/API layers
/// translate this into a deterministic, non-leaky error response.
/// </summary>
public sealed class LifeException : Exception
{
    public LifeException(LifeErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public LifeErrorCode Code { get; }

    public static LifeException BoardNotFound(BoardId id) =>
        new(LifeErrorCode.BoardNotFound, $"Board '{id}' was not found.");

    public static LifeException BoardQuarantined(BoardId id, string reason) =>
        new(LifeErrorCode.BoardQuarantined, $"Board '{id}' is quarantined: {reason}");

    public static LifeException ActiveCellLimitExceeded(int count, int max) =>
        new(LifeErrorCode.ActiveCellLimitExceeded, $"Active cell count {count} exceeds limit {max}.");

    public static LifeException StatesLimitExceeded(int requested, int max) =>
        new(LifeErrorCode.StatesLimitExceeded, $"Requested {requested} states exceeds limit {max}.");

    public static LifeException InvalidRange(long from, long to) =>
        new(LifeErrorCode.InvalidRange, $"Invalid label range [{from}, {to}].");
}
