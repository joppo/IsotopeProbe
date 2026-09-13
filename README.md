# IsotopeProbe

A .NET 10 console application that runs Nuclei and saves each completed execution
and its findings to PostgreSQL before reporting success or failure. A nonzero
Nuclei exit code does not discard findings. One SaveChangesAsync call saves the
execution graph transactionally. Startup, parsing, or cancellation exceptions
that prevent the runner from returning do not produce a saved execution.

## Project structure

- `IsotopeProbe.Cli` is the executable: argument validation, console output and
  exit codes, environment configuration, and explicit constructor wiring.
  Its `IsotopeProbeDbContextFactory` also supplies configuration to EF tools.
- `IsotopeProbe.Core` is the class library: `ScanService` runs and saves a scan;
  `Nuclei/` contains process execution, DTOs, and JSONL parsing; `Domain/` contains
  entities; `Persistence/` contains the DbContext and existing migrations.
- `IsotopeProbe.Tests` references both projects to test CLI parsing and Core logic.

The dependency is CLI → Core. Existing Core namespaces are retained so the
migration and snapshot remain unchanged. Configuration still comes only from
`ISOTOPEPROBE_CONNECTION_STRING`; no appsettings files or secrets are copied.
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
- `IsotopeProbe.Core/Persistence/Migrations/` contains InitialCreate, its metadata, and the model
  snapshot. The migration creates the tables, identity keys, foreign key with
  cascade delete, and foreign-key index. No EnsureCreated or automatic migration
  application is used.
- `dotnet-tools.json` pins dotnet-ef to 10.0.4, matching EF Core and Design 10.0.4.
  The existing Npgsql EF provider remains 10.0.3.

The parser retains the original JSON object, including unmapped fields, in
RawJson. PostgreSQL jsonb preserves the JSON data rather than original whitespace
or property order. See [Npgsql JSON mapping](https://www.npgsql.org/efcore/mapping/json.html).

## Run locally

From the repository root, restore the tools and dependencies:

```bash
dotnet tool restore
dotnet restore IsotopeProbe.slnx
```

Enter the connection string for your existing PostgreSQL container interactively
so credentials are not recorded in shell history or committed to a file:

```bash
read -rsp 'PostgreSQL connection string: ' ISOTOPEPROBE_CONNECTION_STRING
printf '\n'
export ISOTOPEPROBE_CONNECTION_STRING
```

Connection string format: `Host=localhost;Port=5432;Database=<database>;Username=<user>;Password=<password>`.
Use the credentials and database already configured in your container.

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
SELECT e.*, e."ExitCode" = 0 AS succeeded,
       (SELECT count(*) FROM findings f WHERE f."ScanExecutionId" = e."Id") AS finding_count
FROM scan_executions e ORDER BY e."Id" DESC LIMIT 1;

SELECT Id, ScanExecutionId, TemplateId, Name, Severity, MatchedAt, TemplatePath, Authors, Tags, Type, Host, Port, Scheme, Url, IpAddress, Timestamp, MatcherStatus, Request FROM Findings;

SELECT Id, Target, StartedAt, CompletedAt, ExitCode, StandardError FROM scan_executions;

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

Scan lists show ID, target, start time in UTC, outcome derived from the stored
Nuclei exit code, and finding count. Scan details include start/completion times,
exit code, stored standard error, and counts by severity. Finding lists show ID,
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
console dependencies or tracked entities. All three methods accept cancellation:

```csharp
using IsotopeProbe.Queries;

var queries = new ScanQueryService(db);
var scans = await queries.ListScansAsync(skip: 0, take: 20, cancellationToken: cancellationToken);
var scan = await queries.GetScanAsync(id, cancellationToken);
var findings = await queries.ListFindingsAsync(id, skip: 0, take: 20,
    cancellationToken: cancellationToken);
```

`GetScanAsync` and `ListFindingsAsync` return null for an unknown scan. List results
contain `Items`, `TotalCount`, `Skip`, and `Take`. EF applies filtering, ordering,
pagination, and aggregate counts in PostgreSQL using read-only projections.
Counts and pages are separate queries, so concurrent writes can change totals
between reads. A future Web application can reference Core and supply a DbContext
and query service per request, then render these DTOs without invoking the CLI.

## Verification

```bash
dotnet build IsotopeProbe.slnx --no-restore
dotnet test IsotopeProbe.slnx --no-restore
# These checks require the connection-string variable but do not change the database.
dotnet ef dbcontext info --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet ef migrations list --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build --no-connect
dotnet ef migrations has-pending-model-changes --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
```

Tests cover argument validation, template selection and home-directory expansion,
JSONL parsing (including unmapped JSON), exit status, EF mappings, and tracking a
failed execution with findings. The existing migration remains
`20260911123031_InitialCreate`; moving it into Core does not require a new migration.

Verified after the split: build succeeded without warnings, all 23 tests passed,
EF discovered the existing DbContext and migration, and no pending model changes
were found. The localhost sanity scan saved execution 2 with five findings;
the saved rows and unchanged migration history were checked in PostgreSQL.

Query integration tests use the existing PostgreSQL provider and migrations, with
no new test dependencies. Each test creates a unique `query_test_*` schema and
drops that schema afterwards. Set a test connection string whose user can create
schemas (prefer a local test database):

```bash
read -rsp 'PostgreSQL test connection string: ' ISOTOPEPROBE_TEST_CONNECTION_STRING
printf '\n'
export ISOTOPEPROBE_TEST_CONNECTION_STRING
dotnet test IsotopeProbe.slnx --no-restore
```

Without this variable, the five PostgreSQL integration tests are explicitly
skipped; parsing and service-validation tests still run. Integration coverage
includes filtering, pagination, timestamp ties, severity aggregates, missing
versus empty scans, no entity tracking, and cancellation. No Nuclei scans are
performed by these tests.

Verified for the query commands: build succeeded with no warnings, all 55 tests
passed (including all five PostgreSQL tests), and EF reported no pending model
changes. CLI listing, details, pagination, missing IDs, and invalid arguments were
checked against the local database with Nuclei absent from PATH. No new scans
were launched during query verification.
