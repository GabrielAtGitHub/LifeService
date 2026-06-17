# Running & Using LifeService Locally

How to configure, run, and call the service on your machine. See [`README.md`](../README.md) for
architecture and [`docs/persistence.md`](persistence.md) for storage details.

## Prerequisites

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`).
- No external services are required: the default development configuration uses a local SQLite
  database file. Redis/SQL backends are optional (see [`docs/persistence.md`](persistence.md)).

## Configuration

All settings live under the `Life` section (see [`SYSTEM_SPECIFICATION.md`](../SYSTEM_SPECIFICATION.md) §6).
Defaults are defined in `src/LifeService.Api/appsettings.json`; the `Development` environment
overrides storage to SQLite in `appsettings.Development.json`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Life:Limits:MaxActiveCells` | `10000` | Max candidate cells (cells with a live neighbour) per generation (else `ActiveCellLimitExceeded`) |
| `Life:Limits:MaxStatesPerRequest` | `1000` | Max states per batch / range request |
| `Life:Limits:MaxRetriesPerBoard` | `3` | Failures before a board is quarantined |
| `Life:Compute:WorkerMinCellsPerTask` | `128` | Minimum chunk size before the engine goes parallel |
| `Life:Compute:ThreadPoolFactor` | `2.0` | Worker cap = `ProcessorCount × ThreadPoolFactor` |
| `Life:Storage:Provider` | `InMemory` (prod) / `Sqlite` (dev) | Storage backend |
| `Life:Storage:SqliteConnectionString` | `Data Source=life.db` | SQLite connection (when `Provider=Sqlite`) |

### Ways to override configuration

```bash
# 1. Environment variables (double underscore = section separator)
Life__Limits__MaxActiveCells=500 dotnet run --project src/LifeService.Api

# 2. Command-line arguments
dotnet run --project src/LifeService.Api -- --Life:Storage:Provider=InMemory

# 3. Edit appsettings.json / appsettings.Development.json directly
```

## Running the service

```bash
# from the repository root
dotnet run --project src/LifeService.Api
```

By default this uses the **Development** environment (set in `Properties/launchSettings.json`), which:

- listens on **http://localhost:5062** (the `http` profile),
- selects the **SQLite** storage provider, creating `life.db` on first run,
- exposes the **OpenAPI** document at `http://localhost:5062/openapi/v1.json`.

To also serve HTTPS, use the `https` profile (adds `https://localhost:7191`):

```bash
dotnet run --project src/LifeService.Api --launch-profile https
```

Health check:

```bash
curl http://localhost:5062/health      # -> Healthy
```

## Client request interactions

The walkthrough below uses `curl` against the `http` profile and a vertical **blinker**
(an oscillator with period 2). Examples use a POSIX shell (bash / Git Bash); for the equivalent in
an editor, open [`src/LifeService.Api/LifeService.Api.http`](../src/LifeService.Api/LifeService.Api.http)
with the VS / VS Code REST Client. The same flow is also available as a **Postman collection**,
[`src/LifeService.Api/LifeService.postman_collection.json`](../src/LifeService.Api/LifeService.postman_collection.json)
— import it into Postman, or run it headless with Newman:

```bash
dotnet run --project src/LifeService.Api                 # serve on http://localhost:5062
newman run src/LifeService.Api/LifeService.postman_collection.json
```

The upload request captures the new `boardId` into a collection variable that the later requests reuse.

> PowerShell users: `curl` is an alias for `Invoke-WebRequest` and quotes JSON differently — prefer
> `curl.exe` with single-quoted bodies, or use the `.http` file.

### 1. Upload an initial board

```bash
curl -s -X POST http://localhost:5062/api/life/boards \
  -H "Content-Type: application/json" \
  -d '{"cells":[{"x":1,"y":0},{"x":1,"y":1},{"x":1,"y":2}]}'
```

```jsonc
// 201 Created
{ "boardId": "3fa85f64-5717-4562-b3fc-2c963f66afa6" }
```

> **Idempotent upload.** Uploads are content-addressed by the exact set of cells. Re-posting the same
> body returns the *same* `boardId` with `200 OK` instead of `201 Created` (no duplicate board is
> created), and all subsequent calls operate on that board's current state set. A translated copy of
> the pattern is treated as a different board.

Capture the id for the next calls:

```bash
BOARD=$(curl -s -X POST http://localhost:5062/api/life/boards \
  -H "Content-Type: application/json" \
  -d '{"cells":[{"x":1,"y":0},{"x":1,"y":1},{"x":1,"y":2}]}' | jq -r .boardId)
```

### 1b. List stored boards (paginated)

List the first state (label 0) of every stored board, in **creation order** (oldest first). `page` is
1-based (default 1) and `pageSize` defaults to 50 (capped at `MaxStatesPerRequest`):

```bash
curl -s "http://localhost:5062/api/life/boards?page=1&pageSize=20"
```

```jsonc
// 200 OK — one record per board (its uploaded initial state + creation time), plus paging metadata
{
  "items": [
    {
      "boardId": "3fa85f64-...",
      "label": 0,
      "activeCells": [ { "x": 1, "y": 0 }, { "x": 1, "y": 1 }, { "x": 1, "y": 2 } ],
      "createdAt": "2026-06-17T01:30:00.123+00:00"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "totalCount": 1
}
```

### 2. Advance one generation

```bash
curl -s -X POST http://localhost:5062/api/life/boards/$BOARD/next
```

```jsonc
// 200 OK — the blinker flips from vertical to horizontal (label 1)
{
  "boardId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "label": 1,
  "activeCells": [ { "x": 0, "y": 1 }, { "x": 1, "y": 1 }, { "x": 2, "y": 1 } ]
}
```

### 3. Advance several generations at once

```bash
curl -s -X POST "http://localhost:5062/api/life/boards/$BOARD/next-sequence?n=4"
```

```jsonc
// 200 OK — an array of the next 4 states (labels 2..5)
[ { "boardId": "...", "label": 2, "activeCells": [ ... ] }, /* ... */ ]
```

### 4. Read persisted states in a label range

```bash
curl -s "http://localhost:5062/api/life/boards/$BOARD/states?from=0&to=2"
```

```jsonc
// 200 OK — stored states for labels 0, 1, 2
[
  { "boardId": "...", "label": 0, "activeCells": [ { "x": 1, "y": 0 }, { "x": 1, "y": 1 }, { "x": 1, "y": 2 } ] },
  { "boardId": "...", "label": 1, "activeCells": [ { "x": 0, "y": 1 }, { "x": 1, "y": 1 }, { "x": 2, "y": 1 } ] },
  { "boardId": "...", "label": 2, "activeCells": [ { "x": 1, "y": 0 }, { "x": 1, "y": 1 }, { "x": 1, "y": 2 } ] }
]
```

### 5. Compute to a steady state

```bash
curl -s http://localhost:5062/api/life/boards/$BOARD/final
```

```jsonc
// 200 OK — the blinker is detected as a period-2 oscillation
{
  "boardId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "status": "OscillationSteadyState",
  "lastComputedLabel": 7,
  "oscillationPeriodStart": 5,
  "oscillationPeriodLength": 2
}
```

### 6. Quarantine management

```bash
curl -s -i http://localhost:5062/api/life/boards/$BOARD/quarantine   # 204 No Content when not quarantined
curl -s -X DELETE http://localhost:5062/api/life/boards/$BOARD/quarantine   # 204 No Content (clears it)
```

### Error responses

Errors use a deterministic envelope and never leak stack traces:

```bash
# Unknown board -> 404
curl -s -X POST http://localhost:5062/api/life/boards/00000000-0000-0000-0000-000000000000/next
```

```jsonc
{ "code": "BoardNotFound", "message": "Board '00000000-0000-0000-0000-000000000000' was not found." }
```

| Situation | HTTP | `code` |
| --- | --- | --- |
| Unknown board | 404 | `BoardNotFound` |
| Board quarantined | 409 | `BoardQuarantined` |
| Too many active cells | 422 | `ActiveCellLimitExceeded` |
| Requested too many states | 422 | `StatesLimitExceeded` |
| Invalid `from`/`to` range | 400 | `InvalidRange` |
| Unexpected failure | 500 | `InternalError` |
