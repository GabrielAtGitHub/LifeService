# CLAUDE.md — Project Contract for Claude Code

This file defines the standing instructions, architectural constraints, documentation rules,
and GitHub‑specific behavior Claude Code must follow when working inside this repository.

Claude Code must treat this file as the authoritative instruction set for all code generation,
documentation updates, refactoring, and PR interactions.

---

# 1. Documentation Tasks

- Generate and maintain **developer‑facing architecture documentation** for the entire Game of Life system.
- Output format: **Markdown**, written to **README.md** at the project root.
- Audience: **developers**.
- Documentation must include:
  - System overview
  - Component boundaries (API, Application, Domain, Infrastructure)
  - Compute engine flow (map/reduce model)
  - Persistence model (dev + prod)
  - Observability model (logging + metrics + tracing)
  - Domain invariants and failure modes
  - Mermaid diagrams for architecture, data flow, and compute pipeline
- Keep `README.md` **synchronized** with code changes.
- When code changes affect architecture, update the documentation accordingly.
- **Include the Compute Design (map/reduce model) from SYSTEM_SPECIFICATION.md in all architecture documentation.**
- **Ensure README.md reflects the compute pipeline, map/reduce model, chunking, and steady‑state detection.**

---

# 2. GitHub Claude Code Instructions

These rules apply when Claude Code is used inside GitHub (PR comments, code generation,
refactoring, repository‑wide tasks).

### Behavior
- Follow all rules in this `CLAUDE.md`.
- Use `SYSTEM_SPECIFICATION.md` as the **authoritative system blueprint**.
- Maintain consistency between code, documentation, and the system specification.
- Modify only the necessary sections of files and summarize changes.
- Ask clarifying questions only when required.

### Documentation Generation
- Automatically update `README.md` when architecture or behavior changes.
- Use clear, concise developer‑oriented language.
- Include diagrams where appropriate.

### Code Generation
- Follow clean architecture:
  - Domain → Application → Infrastructure → API
- Generate deterministic, async, testable C# (.NET 9+).
- Respect IoC boundaries and dependency injection patterns.
- Enforce invariants defined in the system specification.

### PR Behavior
- Ensure PRs comply with:
  - Architecture rules
  - Domain invariants
  - Logging + metrics requirements
  - Persistence model
  - Testing requirements
- Suggest improvements using patterns defined in this file.

### Safety & Consistency
- Never contradict or remove instructions in `CLAUDE.md`.
- Maintain consistent terminology across all generated code and documentation.

---

# 3. Architecture Requirements

- System: Conway’s Game of Life (engine + API + persistence + observability).
- Architecture style: **clean architecture**, layered, testable, deterministic.
- Core components:
  - Compute engine (state transitions, invariants, safety)
  - Persistence layer (dev + prod)
  - API layer (HTTP endpoints)
  - Observability (logging + metrics + tracing)
- All generated code must be:
  - Async
  - Deterministic
  - Side‑effect controlled
  - Testable
  - Dependency‑injected
- **The compute engine must follow the map/reduce Compute Design defined in SYSTEM_SPECIFICATION.md.**

---

# 4. Persistence Model

### Production
- **Persistence (prod):** SQL/NoSQL (SQL Server, PostgreSQL, Cosmos DB, DynamoDB).
- Claude Code must adapt repository patterns to the chosen provider.

### Development
- **Persistence (dev):** SQLite (default for relational).
- Use a file‑based SQLite database for local development.
- Keep EF Core configuration compatible with both dev and prod providers unless impossible.

---

# 5. Observability Requirements

### Logging
- Use `ILogger<T>` with structured logging.
- Required fields:
  - `boardId`
  - `operation`
  - `status`
  - `durationMs`
- Log quarantine events with reason and context.
- Use logging scopes for per‑request correlation.

### Metrics
- Use `System.Diagnostics.Metrics` (OpenTelemetry‑compatible).
- Required instruments:
  - `states_computed` (Counter)
  - `active_cells` (UpDownCounter)
  - `quarantined_boards` (Counter)
- Register meters under: `GameOfLife.Engine`.

### Tracing
- Use `ActivitySource` named `GameOfLife.Engine`.
- Add tags for `boardId` and `operation`.

---

# 6. Code Generation Rules

- Use **C# 13** and **.NET 9+**.
- Use **async/await** everywhere.
- No blocking calls or sync‑over‑async.
- Use dependency injection for all services.
- Generate unit tests for:
  - Compute engine
  - Repository layer
  - API endpoints
  - Invariants
- **Implement the compute provider according to the Compute Design section in SYSTEM_SPECIFICATION.md.**
- **Use the map/reduce model, chunking rules, thread‑pool sizing, and reduce phase as defined in the specification.**
- **Ensure steady‑state detection matches the specification’s canonical‑state algorithm.**

---

# 7. Invariants (Must Be Enforced)

- State transitions must be deterministic.
- No mutation of input state.
- Quarantined boards must be logged and counted.
- Persistence operations must be idempotent.
- Observability must be present in all compute operations.

---

# 8. Developer Experience

- Provide clear explanations when generating code.
- When modifying files, summarize changes.
- When generating documentation, keep it concise and developer‑focused.
- Prefer Mermaid diagrams for architecture and flow.

---

# 9. File Placement Rules

- Architecture documentation → `README.md`
- System specification → `SYSTEM_SPECIFICATION.md`
- Observability docs → `docs/observability.md`
- Compute engine docs → `docs/engine.md`
- Persistence docs → `docs/persistence.md`

---

# 10. Behavior Summary

Claude Code must:
- Follow this contract for all tasks.
- Maintain architectural consistency.
- Keep documentation synchronized with code.
- Use `SYSTEM_SPECIFICATION.md` as the system blueprint.
- Use `README.md` as the generated architecture documentation.
