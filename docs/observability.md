# Observability

The service emits structured logs, OpenTelemetry-compatible metrics, and distributed traces from a
single engine namespace, per `CLAUDE.md` §5 and [`SYSTEM_SPECIFICATION.md`](../SYSTEM_SPECIFICATION.md) §12.

## Logging

`LifeComputeService` uses `ILogger<T>` with structured fields and per-request scopes. Every operation
log carries the required fields:

| Field | Meaning |
| --- | --- |
| `boardId` | The board the operation acted on |
| `operation` | `UploadInitialState`, `GetNextState`, `GetFinalState`, `GetNextNStates`, `GetStatesInRange`, `ClearQuarantine` |
| `status` | `ok` / `rejected` / failure |
| `durationMs` | Wall-clock duration of the operation |

A logging **scope** (`boardId`, `operation`) wraps each operation so all nested logs are correlated.
Quarantine events are logged with their reason and retry context:

- interim failures → `LogWarning` with `failure N/Max`,
- threshold reached → `LogError` "Board {BoardId} quarantined after {RetryCount} failures".

Domain rejections (`LifeException`) are logged at **Warning** (expected, deterministic outcomes);
unexpected exceptions are logged at **Error** by the global middleware and surfaced as `InternalError`
without leaking stack traces to clients.

## Metrics

Defined in `LifeService.Domain.Diagnostics.LifeMetrics`, registered under the meter
**`GameOfLife.Engine`** (`System.Diagnostics.Metrics`):

| Instrument | Type | Meaning |
| --- | --- | --- |
| `states_computed` | `Counter<long>` | Number of generations computed |
| `active_cells` | `UpDownCounter<long>` | Delta to the live-cell count of the latest computed state |
| `quarantined_boards` | `Counter<long>` | Boards placed into quarantine |

### Collecting metrics

Because the meter is standard `System.Diagnostics.Metrics`, any OpenTelemetry exporter can collect it:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("GameOfLife.Engine"))
    .WithTracing(t => t.AddSource("GameOfLife.Engine"));
```

## Tracing

`LifeService.Domain.Diagnostics.LifeDiagnostics` exposes the `ActivitySource` **`GameOfLife.Engine`**.
Each service operation and each engine computation starts an `Activity` tagged with:

| Tag | Source |
| --- | --- |
| `operation` | The use-case / engine step name |
| `boardId` | Board identifier |
| `status` | Outcome (`ok`, error code) on completion |
| `activeCells` | Live-cell count of a computed state (engine spans) |

Spans nest naturally: a service-level span (e.g. `GetFinalState`) contains the engine spans
(`ComputeUntilSteadyOrLimit` → `ComputeNextState`), giving an end-to-end trace per request.

## Mapping to CLAUDE.md requirements

| Requirement | Implementation |
| --- | --- |
| Structured logging with `boardId`/`operation`/`status`/`durationMs` | `LifeComputeService.LogSuccess` + scopes |
| Log quarantine events with reason and context | `QuarantineOnFailureAsync` |
| Metrics under `GameOfLife.Engine` | `LifeMetrics` |
| `states_computed`, `active_cells`, `quarantined_boards` | `LifeMetrics` instruments |
| `ActivitySource` `GameOfLife.Engine` with `boardId`/`operation` tags | `LifeDiagnostics.StartOperation` |
