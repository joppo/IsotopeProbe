# IsotopeProbe

A .NET 10 console application that runs Nuclei and saves each completed execution
and its findings to PostgreSQL before reporting success or failure. A nonzero
Nuclei exit code does not discard findings. One SaveChangesAsync call saves the
execution graph transactionally. Startup, parsing, or cancellation exceptions
that prevent the runner from returning do not produce a saved execution.

## Persistence

- `Domain/ScanExecution.cs` and `Domain/Finding.cs` are EF entities with generated
  integer keys and a required one-to-many relationship.
- `Persistence/IsotopeProbeDbContext.cs` maps `scan_executions` and `findings`.
  Columns retain C# property names (quote them in SQL). Authors and Tags use
  `text[]`; RawJson uses `jsonb`. Succeeded and Duration are calculated only.
- `Persistence/IsotopeProbeDbContextFactory.cs` reads
  `ISOTOPEPROBE_CONNECTION_STRING` for both the console and EF design-time tools.
  Migration commands instantiate this factory without running the scanner.
- `Persistence/Migrations/` contains InitialCreate, its metadata, and the model
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
dotnet ef database update --project IsotopeProbe
```

With PostgreSQL running, start the existing local test target if needed, then run
the scan. Nuclei and its templates must already be installed and available on PATH.

```bash
docker compose -f docker/compose.yaml up -d --build nuclei-test-target
dotnet run --project IsotopeProbe -- http://localhost:8085 -templatepath "~/Templates/sanity/"
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

## Verification

```bash
dotnet build IsotopeProbe.slnx --no-restore
dotnet test IsotopeProbe.slnx --no-restore
```

Verified during implementation: successful build, all 8 tests passing, and generated
migration inspected. Tests cover parsing (including unmapped JSON), exit status,
EF mappings, and tracking a failed execution with its findings without a database.
Not verified here: applying the migration to a live PostgreSQL instance, a live
Nuclei scan, or reading saved rows back from PostgreSQL. Run the local steps above
to verify those paths.
