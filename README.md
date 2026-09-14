# IsotopeProbe

A .NET 10 application that runs Nuclei through a CLI, persists execution lifecycle
state and findings in PostgreSQL, and browses saved results through CLI queries
or a local Razor Pages website. Findings are saved one at a
time as JSONL arrives, so later failure or cancellation does not discard earlier
committed findings.

## Execution lifecycle

| Status | Meaning |
| --- | --- |
| Running | Saved before launching Nuclei; final outcome has not been persisted. |
| Succeeded | Nuclei exited with code 0, output processing and all finding writes finished, and the terminal update committed. Zero findings is valid. |
| Failed | Startup, parsing, finding persistence, process exit, or shutdown failed. Earlier saved findings remain. |
| Cancelled | Cancellation was handled and the terminal update committed. Earlier saved findings remain. |

`StartedAt` and `CompletedAt` use UTC. `CompletedAt` and `ExitCode` are nullable:
a running scan has no completion time, and a scanner that never started has no
exit code. An exit code of zero alone does not imply success. `FailureReason`
contains a safe, bounded description of the failing stage and exception type,
plus the native startup error code or process exit code when available.

Ctrl+C is wired in CLI and propagated to Core. Core kills the scanner process tree
where supported, waits for shutdown, and saves Cancelled with an independent
cleanup token. A collected finding receives a write budget of 10 seconds even if
cancellation arrives during that write. Process shutdown and final-state persistence
each have a separate 10-second budget. Once the final outcome is chosen, Ctrl+C
during its write does not change it. The final update only affects a Running row,
so an existing terminal state cannot be overwritten.

If the initial database write fails, Nuclei is not launched. If final persistence
cannot be confirmed, CLI reports that explicitly and exits with failure; the row
may remain Running. Successful query commands return 0, scan failures and
unconfirmed persistence return 1, and handled cancellation returns 130.

A crash, SIGKILL, or power loss can leave Running behind. **Running is the last
persisted state, not proof that a process is alive.** `scans show` displays this
caveat. No startup sweep changes Running rows: another process may own them.
There are no heartbeats, recovery jobs, or automatic retries.

Stderr is drained concurrently with stdout and does not determine success by itself.
New `StandardError` values contain only a character-count summary; raw stderr and
exception messages are omitted because they can contain credentials, authentication
headers, or payloads. Finding JSON and all existing finding fields are retained as
before. Historical diagnostic strings are left unchanged by the migration.

## Project structure

- `IsotopeProbe.Cli` is the executable: argument validation, console output and
  exit codes, environment configuration, and explicit constructor wiring.
  Its `IsotopeProbeDbContextFactory` also supplies configuration to EF tools.
- `IsotopeProbe.Core` is the class library: `ScanService` runs and saves a scan;
  `Nuclei/` contains process execution, DTOs, and JSONL parsing; `Domain/` contains
  entities; `Persistence/` contains the DbContext and existing migrations.
- `IsotopeProbe.Web` is an ASP.NET Core Razor Pages host for read-only execution
  and finding browsing. It registers Core’s DbContext and query service per request.
- `IsotopeProbe.Tests` references CLI and Core to test CLI parsing and Core logic.

The dependencies are CLI → Core and Web → Core. Web does not reference or invoke
CLI. Existing Core namespaces are retained so the original migration history
remains intact. Configuration still comes only from
`ISOTOPEPROBE_CONNECTION_STRING`; no connection strings or secrets are committed.
Nuclei remains an external executable on PATH, with external template files.
The existing Docker services and their assets are unchanged.

## Persistence

- `IsotopeProbe.Core/Domain/ScanExecution.cs` and `IsotopeProbe.Core/Domain/Finding.cs` are EF entities with generated
  integer keys and a required one-to-many relationship.
- `IsotopeProbe.Core/Persistence/IsotopeProbeDbContext.cs` maps `scan_executions` and `findings`.
  Columns retain C# property names (quote them in SQL). Authors and Tags use
  `text[]`; RawJson uses `jsonb`. Succeeded and Duration are calculated only.
- `IsotopeProbe.Cli/IsotopeProbeDbContextFactory.cs` reads
  `ISOTOPEPROBE_CONNECTION_STRING` for both the console and EF design-time tools.
  Migration commands instantiate this factory without running the scanner.
- `IsotopeProbe.Core/Persistence/Migrations/` contains `InitialCreate` and
  `20260913204858_AddExecutionLifecycle`, their metadata, and the current snapshot.
  The lifecycle migration adds `Status` and `FailureReason`, and allows null
  completion times and exit codes. Historical statuses are derived from recorded
  exit codes: zero becomes Succeeded, nonzero becomes Failed. Existing timestamps,
  findings, and diagnostics are preserved. No EnsureCreated or automatic migration
  application is used. Downgrade is refused if the old schema cannot represent
  execution records without losing lifecycle information.
- `dotnet-tools.json` pins dotnet-ef to 10.0.4, matching EF Core and Design 10.0.4.
  The existing Npgsql EF provider remains 10.0.3.

The parser retains the original JSON object, including unmapped fields, in
RawJson. PostgreSQL jsonb preserves the JSON data rather than original whitespace
or property order. See [Npgsql JSON mapping](https://www.npgsql.org/efcore/mapping/json.html).

## Run locally

Install the .NET 10 SDK and have a PostgreSQL server/database available (the existing
local container can be used). Nuclei is required only for CLI scans, not browsing.
From the repository root, restore the tools and dependencies:

```bash
dotnet tool restore
dotnet restore IsotopeProbe.slnx
```

Enter the connection string for your PostgreSQL database interactively
so credentials are not recorded in shell history or committed to a file:

```bash
read -rsp 'PostgreSQL connection string: ' ISOTOPEPROBE_CONNECTION_STRING
printf '\n'
export ISOTOPEPROBE_CONNECTION_STRING
```

Connection string format: `Host=localhost;Port=5432;Database=<database>;Username=<user>;Password=<password>`.
Use the credentials and database configured in your PostgreSQL instance.

Apply the migration (this changes your database):

```bash
dotnet ef database update --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli
```

With PostgreSQL running, start the existing local test target if needed, then run
the scan. Nuclei and its templates must already be installed and available on PATH.

```bash
docker compose -f docker/compose.yaml up -d --build nuclei-test-target
dotnet run --project IsotopeProbe.Cli -- http://localhost:8085 -templatepath "~/Templates/sanity/"
```
```bash PSQL
docker compose exec postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
```
The application prints the saved execution ID. Findings depend on your installed
Nuclei templates; a successful scan with zero findings is valid.

Inspect the latest saved execution and its findings in the existing container:

```bash
docker exec -i isotope-postgres sh -c 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<'SQL'
SELECT e.*, e."Status" = 'Succeeded' AS succeeded,
       (SELECT count(*) FROM findings f WHERE f."ScanExecutionId" = e."Id") AS finding_count
FROM scan_executions e ORDER BY e."Id" DESC LIMIT 1;

SELECT "Id", "ScanExecutionId", "TemplateId", "Name", "Severity", "Authors", "Tags", "RawJson"
FROM findings
WHERE "ScanExecutionId" = (SELECT max("Id") FROM scan_executions)
ORDER BY "Id";
SQL
```

These SQL commands assume the connection string points to the database configured
by the container's POSTGRES_DB and POSTGRES_USER variables. For concurrent scans,
replace the latest-ID subquery with the execution ID printed by your run.

## Query saved results

Use the same `ISOTOPEPROBE_CONNECTION_STRING` environment variable as for scans.
These commands read PostgreSQL directly and do not launch Nuclei:

```bash
dotnet run --project IsotopeProbe.Cli -- scans list
dotnet run --project IsotopeProbe.Cli -- scans list --skip 20 --take 10
dotnet run --project IsotopeProbe.Cli -- scans show 2
dotnet run --project IsotopeProbe.Cli -- findings list --scan 2 --skip 0 --take 20
```

Replace `2` with an execution ID from `scans list`. Lists default to `--skip 0`
and `--take 20`; skip must be nonnegative and take must be between 1 and 100.
Scan IDs must be positive integers. Unknown, duplicate, or incomplete options
are rejected. Scan pages sort by start time descending, then ID descending;
finding pages sort by ID ascending within the selected execution.

Scan lists show ID, target, start time in UTC, persisted lifecycle status, and finding count. Scan details include start/completion times,
available exit code, failure details, stderr summary, and counts by severity. Finding lists show ID,
template ID, severity, and matched location, without JSON or request/response bodies.

Query commands return exit code 0 on success, including empty lists and inspecting
an execution whose scan failed. Unknown scans, invalid arguments, and database
errors return 1. An existing scan with no findings shows zero findings; an unknown
scan reports “not found”. Skipping beyond the available rows reports an empty page
and the total count.

### Reusing Core queries

`IsotopeProbe.Core/Queries/ScanQueryService.cs` accepts an
`IsotopeProbeDbContext` through its constructor, matching the project's existing
explicit wiring. `QueryModels.cs` contains materialized read models, with no
console dependencies or tracked entities. All query methods accept cancellation:

```csharp
using IsotopeProbe.Queries;

var queries = new ScanQueryService(db);
var scans = await queries.ListScansAsync(skip: 0, take: 20, cancellationToken: cancellationToken);
var scan = await queries.GetScanAsync(id, cancellationToken);
var findings = await queries.ListFindingsAsync(id, skip: 0, take: 20,
    cancellationToken: cancellationToken, severity: "high");
var finding = await queries.GetFindingAsync(findingId, cancellationToken);
```

`GetScanAsync` and `ListFindingsAsync` return null for an unknown scan. List results
contain `Items`, `TotalCount`, `Skip`, and `Take`. EF applies filtering, ordering,
pagination, and aggregate counts in PostgreSQL using read-only projections.
Counts and pages are separate queries, so concurrent writes can change totals
between reads. `ListFindingsAsync` optionally filters by an exact stored severity
before counting and paging; omitting it preserves CLI behavior. `GetFindingAsync`
returns an untracked details DTO, or null for an unknown positive ID. Description
and extracted results remain in RawJson and are prepared for display by Web.
No database schema changes are required.

## Browse results locally

After configuring `ISOTOPEPROBE_CONNECTION_STRING` and applying migrations as above,
run a scan against your local target:

```bash
dotnet run --project IsotopeProbe.Cli -- http://localhost:8085 -templatepath "~/Templates/sanity/"
dotnet run --project IsotopeProbe.Web --launch-profile IsotopeProbe.Web
```

Open **http://localhost:5080/**. The launch profile binds to localhost. In another
terminal, export the same connection variable before starting Web. Web does not
apply migrations at startup; use the explicit EF command in the setup section.

The landing page lists executions newest first, with 20 rows per page. Select an
execution to see its metadata, whole-execution severity counts, and paginated
findings. The severity filter is preserved across pages. Select a finding to see
stored fields, description, and expandable evidence/raw JSON. For example,
`http://localhost:5080/executions/2` and `http://localhost:5080/findings/5` work when
those IDs exist. Unknown IDs return HTTP 404; an execution with zero findings
has its own empty state. All execution times are labeled UTC; scanner timestamps
retain their stored offset, if supplied.

This milestone is **local and read-only, with no authentication**. Keep the host
bound to localhost. There are no scan-start/cancellation actions, background
workers, or automatic refresh. Reload to see newly saved results. Running means
only the last saved state; it does not establish that Nuclei is still alive.
Browsing invokes Core queries directly and never starts Nuclei or saves records.
Scanner and target content is rendered as encoded text, including request/response
bodies and raw JSON. Database failures show a generic setup/connectivity message
without connection strings or stack traces in the page.

## Verification

```bash
dotnet build IsotopeProbe.slnx --no-restore
dotnet test IsotopeProbe.slnx --no-restore
# These checks require the connection-string variable but do not change the database.
dotnet ef dbcontext info --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet ef migrations list --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build --no-connect
dotnet ef migrations has-pending-model-changes --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
```

Tests cover argument validation, template selection, JSONL parsing, EF mappings,
query filtering/pagination, and lifecycle handling. Integration tests use the
existing PostgreSQL provider and real migrations, without new dependencies.
Each test creates and then removes its own `query_test_*` or `lifecycle_test_*`
schema. Set a connection string whose user can create schemas, preferably in a
local test database:

```bash
read -rsp 'PostgreSQL test connection string: ' ISOTOPEPROBE_TEST_CONNECTION_STRING
printf '\n'
export ISOTOPEPROBE_TEST_CONNECTION_STRING
dotnet test IsotopeProbe.slnx --no-restore
# Run just lifecycle checks:
dotnet test IsotopeProbe.slnx --no-restore --filter FullyQualifiedName~LifecycleTests
```

Without the test connection variable, database tests are explicitly skipped.
Lifecycle process tests additionally require Linux, `/usr/bin/python3`, and
`/bin/sleep`; they use a controlled temporary fake Nuclei executable. They exercise
success with and without findings, startup failure, nonzero exit after findings,
stderr drainage/redaction, malformed output, finding-write failure, cancellation
and child shutdown, CLI SIGINT, CLI SIGKILL, finalization races, database-write
failure reporting, and migration of historical rows. No external targets are scanned.

For a manual local scan, apply migrations and run the existing sanity templates:

```bash
dotnet ef database update --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli
dotnet run --project IsotopeProbe.Cli -- http://localhost:8085 -templatepath "~/Templates/sanity/"
dotnet run --project IsotopeProbe.Cli -- scans list
dotnet run --project IsotopeProbe.Cli -- scans show 2
dotnet run --project IsotopeProbe.Cli -- findings list --scan 2
```

Replace `2` with the saved execution ID. To inspect a scan while it runs, use
`scans list`/`scans show` from another terminal. Press Ctrl+C during a sufficiently
long local scan to cancel it, then inspect its status and findings. The fake-scanner
automated tests provide deterministic cancellation and forced-termination checks
when the sanity scan completes too quickly for manual interruption.

Verified for lifecycle handling: all 71 automated tests passed with none skipped,
using local PostgreSQL and fake scanner processes on Linux. EF reports no pending
model changes. The tests apply migrations only inside temporary schemas; the main
database still needs the migration command above. No real Nuclei scan or manual
Ctrl+C test was run for this milestone. Actual database outages were not induced:
initial/final write failures were simulated with EF interceptors, while finding
write failure was exercised through a real PostgreSQL JSON rejection. Process
cleanup on other operating systems has not been verified.

Verified for the Web milestone: solution build succeeded with no warnings, and all
73 tests passed with local PostgreSQL (none skipped). HTTP smoke checks against an
isolated temporary schema covered execution/finding browsing, pagination, severity
filtering, empty states, unknown IDs, invalid pagination, HTML-like evidence, and
safe database-error responses in Development. The Web connection used read-only
transactions and before/after snapshots confirmed that browsing left all records
unchanged. No Nuclei process was launched by browsing. Browser tooling was not
available, so visual layout and interactive browser inspection remain manual checks;
long text and HTML encoding were checked in rendered HTTP responses. No external
targets were scanned, and the main database schema was not changed.
