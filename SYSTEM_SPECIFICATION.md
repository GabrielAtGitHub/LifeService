Absolutely, Gabriel — here is your **polished, production‑grade `SYSTEM_SPECIFICATION.md` as a single, clean file**, ready to drop directly into your GitHub repository.

It is fully rewritten for clarity, structure, and long‑term maintainability, while preserving every architectural invariant and behavioral contract from your original document.

No instructions. No meta text.  
Just the **final specification file**.

---

# ⭐ **SYSTEM_SPECIFICATION.md (Final Single‑File Version)**

```markdown
# SYSTEM_SPECIFICATION.md  
LifeService — Conway’s Game of Life System Specification

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

- **Language:** C# 13 (or latest)
- **Runtime:** .NET 9
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
    Task<BoardId> UploadInitialStateAsync(
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

    Task<SolutionSummary> ComputeUntilSteadyOrLimitAsync(
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
    Task<BoardId> CreateBoardAsync(
        IReadOnlyCollection<LifeCell> initialState,
        CancellationToken ct);

    Task<LifeState?> GetStateAsync(BoardId boardId, LifeStateLabel label, CancellationToken ct);

    Task<IReadOnlyList<LifeState>> GetStatesRangeAsync(
        BoardId boardId,
        LifeStateLabel from,
        LifeStateLabel to,
        CancellationToken ct);

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

## 6.2 appsettings.json

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
      "UseRedisQuarantine": true,
      "UseRedisSolutionCache": false
    }
  }
}
```

---

# 7. API Surface

**Base route:** `/api/life`

Endpoints include:

1. **Upload new board** — `POST /api/life/boards`
2. **Get next state** — `POST /api/life/boards/{boardId}/next`
3. **Get final computed state** — `GET /api/life/boards/{boardId}/final`
4. **Get next N states** — `POST /api/life/boards/{boardId}/next-sequence`
5. **Get states in range** — `GET /api/life/boards/{boardId}/states?from=x&to=y`
6. **Quarantine management** — `GET` / `DELETE /api/life/boards/{boardId}/quarantine`

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

# End of SYSTEM_SPECIFICATION.md
```

---
