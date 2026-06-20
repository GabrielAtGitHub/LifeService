# SYSTEM_SPECIFICATION.md

LifeService — Conway's Game of Life System Specification

This document defines the authoritative system blueprint for the LifeService project.
It describes the domain model, architecture, compute design, persistence model, API
surface, invariants, error handling, and testing strategy. Claude Code must treat this
document as the source of truth for how the system works.

---

# 1. Overview

**Project name:** `LifeService`  
**Purpose:** A production‑ready, scalable Conway’s Game of Life web service with:
- persistent solutions  
- steady‑state and oscillation detection  
- quarantining of unstable or failing boards  
- deterministic, testable compute engine  

The system must maintain a clean, layered architecture and support:
- configurable limits  
- robust error handling  
- unit and integration testing  
- local and cloud deployment (Azure/AWS)

---

# 2. Tech Stack

- **Language:** C# 13 (pinned via `Directory.Build.props`)
- **Runtime:** .NET 10 (`net10.0`)
- **Framework:** ASP.NET Core Web API
- **Persistence (prod):** SQL/NoSQL (SQL Server, PostgreSQL, Cosmos DB, DynamoDB)
- **Persistence (dev):** SQLite (default for relational)
- **Cache (optional):** Redis (quarantine + solution summary)
- **Tests:** xUnit or NUnit + FluentAssertions

---

# 3. Solution Structure

```
LifeService.Api
    ASP.NET Core Web API
    DI configuration, exception middleware, endpoints

LifeService.Application
    Use cases, orchestration, ILifeComputeService implementation

LifeService.Domain
    Core models, invariants, interfaces (no infrastructure)

LifeService.Infrastructure
    Storage providers, compute provider (map/reduce), Redis/DB integration

LifeService.Tests.Unit
    Domain + application unit tests

LifeService.Tests.Integration
    API + infrastructure integration tests
```

---

# 4. Domain Model

### Core Types

- `BoardId : record struct Guid`
- `LifeCell : record struct (int X, int Y)`
- `LifeStateLabel : record struct long`

### `LifeState`
- `BoardId BoardId`
- `LifeStateLabel Label`
- `IReadOnlyCollection<LifeCell> ActiveCells`

### `StoredBoardState`
- `BoardId BoardId`
- `LifeStateLabel Label`
- `IReadOnlyCollection<LifeCell> ActiveCells`
- `DateTimeOffset CreatedAt` — when the board was created (initial state uploaded)
- A board's first state plus its creation time, as surfaced by the listing endpoint (creation order).

### `BoardCreationResult`
- `BoardId BoardId`
- `bool Created` — `false` when an existing board with an identical initial cell set was returned
  (idempotent upload).

### `BoardFingerprint` (static)
- `string Compute(IReadOnlyCollection<LifeCell>)` — deterministic, order-independent, duplicate-free
  key over the **exact** live coordinates. It is **not** translation-invariant: translated boards are
  distinct state sets. Backs content-addressed board creation.

### `PagedResult<T>`
- `IReadOnlyList<T> Items`
- `int Page` — 1-based page number
- `int PageSize`
- `long TotalCount` — total items across all pages

### `SolutionStatus`
- `StableSteadyState`
- `OscillationSteadyState`
- `Incomplete`

### `SolutionSummary`
- `BoardId BoardId`
- `SolutionStatus Status`
- `LifeStateLabel LastComputedLabel`
- `LifeStateLabel? OscillationPeriodStart`
- `int? OscillationPeriodLength`

### `SteadyStateResult`
- `SolutionSummary Summary`
- `IReadOnlyList<LifeState> ComputedStates` — the trajectory computed while seeking a steady state
  (successors of the starting state, in label order). The service persists these so the summary's
  `LastComputedLabel` always refers to a stored state.

### `QuarantineInfo`
- `BoardId BoardId`
- `DateTimeOffset QuarantinedAt`
- `string Reason`
- `int RetryCount`

---

# 5. Interfaces (IoC Boundaries)

## 5.1 Compute Service (API‑Facing)

```csharp
public interface ILifeComputeService
{
    // Idempotent by content: an identical initial cell set returns the existing board id
    // (BoardCreationResult.Created == false) without creating a new board.
    Task<BoardCreationResult> UploadInitialStateAsync(
        IReadOnlyCollection<LifeCell> activeCells,
        CancellationToken ct);

    Task<LifeState> GetNextStateAsync(BoardId boardId, CancellationToken ct);

    Task<SolutionSummary> GetFinalStateAsync(BoardId boardId, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetNextNStatesAsync(
        BoardId boardId,
        int n,
        CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetStatesInRangeAsync(
        BoardId boardId,
        long fromLabel,
        long toLabel,
        CancellationToken ct);

    // First state (label 0) of every stored board, in creation order, paginated.
    Task<PagedResult<StoredBoardState>> ListInitialStatesAsync(int page, int pageSize, CancellationToken ct);

    Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct);
}
```

## 5.2 Compute Provider (Map/Reduce)

```csharp
public interface ILifeComputeProvider
{
    Task<LifeState> ComputeNextStateAsync(LifeState current, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> ComputeNextNStatesAsync(
        LifeState current,
        int n,
        CancellationToken ct);

    // Returns the steady-state summary plus the computed trajectory (states the caller persists,
    // so LastComputedLabel always refers to a stored state).
    Task<SteadyStateResult> ComputeUntilSteadyOrLimitAsync(
        BoardId boardId,
        LifeState initial,
        int maxStates,
        CancellationToken ct);
}
```

## 5.3 Storage Provider

```csharp
public interface ILifeStorageProvider
{
    // Content-addressed: returns the existing board when an identical initial cell set
    // (per BoardFingerprint) was already created, so uploads are idempotent.
    Task<BoardCreationResult> CreateBoardAsync(
        IReadOnlyCollection<LifeCell> initialState,
        CancellationToken ct);

    Task<LifeState?> GetStateAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetStatesRangeAsync(
        BoardId boardId,
        LifeStateLabel from,
        LifeStateLabel to,
        CancellationToken ct);

    // Page of the label-0 state of every board, ordered by monotonic creation sequence,
    // with each board's creation timestamp and the total count.
    Task<PagedResult<StoredBoardState>> GetInitialStatesAsync(int page, int pageSize, CancellationToken ct);

    Task PersistStateAsync(LifeState state, CancellationToken ct);

    Task<SolutionSummary?> GetSolutionSummaryAsync(BoardId boardId, CancellationToken ct);

    Task PersistSolutionSummaryAsync(SolutionSummary summary, CancellationToken ct);

    Task<QuarantineInfo?> GetQuarantineAsync(BoardId boardId, CancellationToken ct);

    Task PersistQuarantineAsync(QuarantineInfo info, CancellationToken ct);

    Task ClearQuarantineAsync(BoardId boardId, CancellationToken ct);
}
```

---

# 6. Configuration

## 6.1 Options

```csharp
public sealed class LifeLimitsOptions
{
    public int MaxActiveCells { get; init; } = 10_000;
    public int MaxStatesPerRequest { get; init; } = 1_000;
    public int MaxRetriesPerBoard { get; init; } = 3;
}

public sealed class LifeComputeOptions
{
    public int WorkerMinCellsPerTask { get; init; } = 128;
    public double ThreadPoolFactor { get; init; } = 2.0;
}

public sealed class LifeStorageOptions
{
    public bool UseRedisQuarantine { get; init; } = true;
    public bool UseRedisSolutionCache { get; init; } = false;
}
```

Two further `Life:Storage` keys are read directly from configuration (not via
`LifeStorageOptions`) to select the storage provider at startup:

- **`Provider`** — `"InMemory"` (default) or `"Sqlite"`. Case-insensitive; any value other than
  `"Sqlite"` falls back to in-memory.
- **`SqliteConnectionString`** — used only when `Provider` is `"Sqlite"`; defaults to
  `"Data Source=life.db"`.

## 6.2 appsettings.json (defaults)

The committed `appsettings.json` carries the baseline limits, compute tuning, and an
**in-memory** storage provider so the service runs with zero external dependencies out of the box:

```json
{
  "Life": {
    "Limits": {
      "MaxActiveCells": 10000,
      "MaxStatesPerRequest": 1000,
      "MaxRetriesPerBoard": 3
    },
    "Compute": {
      "WorkerMinCellsPerTask": 128,
      "ThreadPoolFactor": 2.0
    },
    "Storage": {
      "Provider": "InMemory",
      "SqliteConnectionString": "Data Source=life.db",
      "UseRedisQuarantine": true,
      "UseRedisSolutionCache": false
    }
  }
}
```

## 6.3 Environment Configurations

Configuration is layered: `appsettings.json` (above) supplies the defaults, and each environment
overrides only what it needs via `appsettings.{Environment}.json`, environment variables
(`Life__Storage__Provider=Sqlite`), or test host settings. Storage selection is the main axis that
varies by environment.

| Environment | Provider | Persistence | Selected by | Notes |
|-------------|----------|-------------|-------------|-------|
| **Local** (default run) | `InMemory` | Process memory (singleton); lost on restart | `appsettings.json` | No external dependencies; fastest path to a running service. |
| **Development** | `Sqlite` | File-based `life.db` | `appsettings.Development.json` (`ASPNETCORE_ENVIRONMENT=Development`) | Durable across restarts; schema created via `EnsureCreatedAsync` on startup. Delete `life.db*` to reset. |
| **Test** (integration) | `InMemory` | Process memory, per test host | `WebApplicationFactory` pins `Life:Storage:Provider=InMemory` | Hermetic and independent of the machine's environment config; no files written. |
| **Production** | Relational DB (or NoSQL) | Managed datastore + optional Redis | `appsettings.Production.json` / environment variables | See §6.4. |

## 6.4 Production Configuration

Production overrides the in-memory default with a durable, concurrency-safe datastore and (optionally)
Redis for quarantine state and the solution-summary cache.

### Settings

- **`Life:Storage:Provider`** — a production-grade provider (`"Sqlite"` is supported today; SQL
  Server / PostgreSQL / Cosmos DB / DynamoDB are the intended prod targets per §2, each wired through
  the same `ILifeStorageProvider` repository contract).
- **`Life:Storage:SqliteConnectionString`** (or the equivalent provider connection string) — points at
  the managed datastore rather than a local file. **Note:** SQLite is single-writer; for production
  concurrency prefer SQL Server / PostgreSQL and a provider whose `CreateBoardAsync` uses a native
  identity/sequence for the monotonic board sequence rather than `MAX(Sequence) + 1`.
- **`Life:Storage:UseRedisQuarantine`** — `true` to keep quarantine state in Redis so it is shared
  across instances.
- **`Life:Storage:UseRedisSolutionCache`** — `true` to cache solution summaries in Redis.
- **`Life:Limits`** — tighten `MaxActiveCells` / `MaxStatesPerRequest` to match the deployment's
  resource budget.
- Secrets (connection strings, Redis endpoints) must come from environment variables or a secret
  store, **never** from a committed `appsettings.*.json`.

### Example `appsettings.Production.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Life": {
    "Limits": {
      "MaxActiveCells": 50000,
      "MaxStatesPerRequest": 2000,
      "MaxRetriesPerBoard": 3
    },
    "Compute": {
      "WorkerMinCellsPerTask": 256,
      "ThreadPoolFactor": 2.0
    },
    "Storage": {
      "Provider": "Sqlite",
      "UseRedisQuarantine": true,
      "UseRedisSolutionCache": true
    }
  }
}
```

Provide the connection string and any Redis endpoint out-of-band, e.g.:

```bash
export ASPNETCORE_ENVIRONMENT=Production
export Life__Storage__SqliteConnectionString="Data Source=/var/lib/lifeservice/life.db"
```

---

# 7. API Surface

**Base route:** `/api/life`

Endpoints include:

1. **Upload new board** — `POST /api/life/boards`
   - **Idempotent by content.** A new board returns `201 Created`; re-uploading an identical cell
     set returns the previously created board id with `200 OK` (no new board, no double-counting of
     `active_cells`). Subsequent operations act on that board's current state set.
2. **List boards (first state, paginated)** — `GET /api/life/boards?page=&pageSize=`
   - Returns one record per stored board — its **first** state (label 0) with `boardId`, `label`,
     `activeCells` and `createdAt` — in **creation order** (oldest first), backed by a monotonic
     per-board sequence. `page` is 1-based (default 1); `pageSize` defaults to 50 and is capped at
     `MaxStatesPerRequest`. The response carries `{ items, page, pageSize, totalCount }`. Invalid
     paging → `InvalidRange` (400); oversized `pageSize` → `StatesLimitExceeded` (422).
3. **Get next state** — `POST /api/life/boards/{boardId}/next`
4. **Get final computed state** — `GET /api/life/boards/{boardId}/final`
5. **Get next N states** — `POST /api/life/boards/{boardId}/next-sequence`
6. **Get states in range** — `GET /api/life/boards/{boardId}/states?from=x&to=y`
7. **Quarantine management** — `GET` / `DELETE /api/life/boards/{boardId}/quarantine`

(Full request/response examples preserved from original spec.)

---

# 8. Compute Design (Map / Reduce)

### Sparse Representation
- Use `HashSet<LifeCell>` for the active set.

### Thread Pool
- `T = Environment.ProcessorCount * ThreadPoolFactor`

### Chunking
- Partition the **active** cells into chunks ≥ `WorkerMinCellsPerTask`.
- Worker count = `min(T, activeCount / WorkerMinCellsPerTask)` (floor division), so each worker
  receives at least `WorkerMinCellsPerTask` cells and small boards run single-threaded.

### Map (Scatter) Phase
- Each worker scatters into a **local** `Dictionary<LifeCell, int>`: for every active cell in its
  chunk, increment the neighbour count of each of its 8 neighbours.
- Local maps mean no shared writes, so the map phase is conflict-free.

### Reduce Phase
- Merge the per-worker maps by summing counts per cell. Summation is associative/commutative, so
  the merged result is independent of the partitioning (deterministic).
- If the merged candidate count `> MaxActiveCells` → error (`ActiveCellLimitExceeded`).

### Rule Phase
- For each `(cell, count)` in the merged map, the cell is alive next iff
  `(cell is active AND count ∈ {2, 3}) OR (count == 3)`.
- A live cell with no live neighbours never appears as a key in the map and therefore dies, as
  required.

---

# 9. Steady State Detection

- Maintain `Dictionary<string, LifeStateLabel>` keyed by canonical state representation.
- On each new state:
  - If unseen → record label.
  - If seen at same label → **stable steady state**.
  - If seen at different label → **oscillation**; period = `currentLabel - previousLabel`.

---

# 10. Error Handling

- Global exception middleware.
- Never leak stack traces.
- Error codes:
  - `BoardNotFound`
  - `BoardQuarantined`
  - `ActiveCellLimitExceeded`
  - `StatesLimitExceeded`
  - `InvalidRange`
  - `InternalError`

### Quarantine
- On repeated failures (up to `MaxRetriesPerBoard`), persist `QuarantineInfo`.
- Reject further requests with `BoardQuarantined`.

---

# 11. Testing Strategy

## Unit Tests
- Game of Life rules (still lifes, oscillators, spaceships)
- Limits enforcement
- Steady state detection
- Range validation

## Integration Tests
- In‑memory storage + test compute provider
- Positive flows (upload → next → final)
- Negative flows (limits, quarantine, invalid IDs)
- Quarantine lifecycle

## Manual / API Smoke Testing (Postman)

A Postman collection at **`src/LifeService.Api/LifeService.postman_collection.json`** mirrors the
endpoints in `src/LifeService.Api/LifeService.Api.http` and exercises the full happy path plus the
`BoardNotFound` negative case. It is the canonical way to smoke-test a running instance by hand or in
CI via [Newman](https://github.com/postmanlabs/newman).

### Structure

- **Collection variables:** `host` (defaults to `http://localhost:5062`) and `boardId` (populated at
  runtime). Point `host` at the target instance to test a non-local deployment.
- **Ordered requests** — run them top-to-bottom; later requests depend on earlier ones (item names
  match the collection):
  - *1. Health check* — `GET /health`.
  - *2. Upload board (blinker)* — `POST /api/life/boards`; asserts `200`/`201` and **captures the
    returned `boardId`** into the collection variable used by every subsequent request.
  - *2b. List boards (paginated)* — `GET /api/life/boards?page=1&pageSize=20`; asserts a `totalCount`
    paging field is present.
  - *3. Advance one generation* — `POST /{boardId}/next`.
  - *4. Advance next four generations* — `POST /{boardId}/next-sequence?n=4`.
  - *5. States in range* — `GET /{boardId}/states?from=0&to=20`.
  - *6. Compute final (steady state)* — `GET /{boardId}/final`; asserts the blinker's oscillation
    period is `2`.
  - *7. Inspect quarantine* / *8. Clear quarantine* — `GET` / `DELETE /{boardId}/quarantine`.
  - *9. Unknown board → 404* — asserts the deterministic `BoardNotFound` error body.
- **Test scripts:** every request asserts its expected status and `console.log`s the response body,
  so a Newman run produces a readable transcript of each step.

### Running with Newman

```bash
newman run src/LifeService.Api/LifeService.postman_collection.json
# Override the target host:
newman run src/LifeService.Api/LifeService.postman_collection.json \
  --env-var "host=https://lifeservice.example.com"
```

Because request 2 uploads a fixed blinker and uploads are **idempotent by content** (§5.3, §7), re-running
the collection against the same persistent store returns the existing board (`200`) rather than creating
a new one — the rest of the run proceeds against that board unchanged.

---

# 12. Operations

- Deployment: local, Azure App Service/Container Apps, AWS ECS/EKS
- Config via environment variables
- Health checks: `/health`
- Logging: per request (boardId, operation, status, duration); quarantine events
- Metrics: active cells, states computed, quarantined boards

---

# 13. Agent Tasks (Claude Code)

1. Scaffold solution structure  
2. Implement domain models + interfaces  
3. Implement compute provider (map/reduce)  
4. Implement storage providers (in‑memory + production)  
5. Wire up API + middleware  
6. Add configuration + DI  
7. Write unit + integration tests  
8. Add Dockerfile + deployment notes  

---
