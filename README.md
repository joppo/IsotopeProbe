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
- `IsotopeProbe.Tests` references all three projects to test CLI parsing, Core logic, and the Web authentication pipeline.

The dependencies are CLI → Core and Web → Core. Web does not reference or invoke
CLI. Existing Core namespaces are retained so the original migration history
remains intact. Database configuration comes from `ISOTOPEPROBE_CONNECTION_STRING`; Google credentials use Web user-secrets or environment variables. No real credentials are committed.
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

`IsotopeProbe.Core/Queries/TrustedScanQueryService.cs` accepts an
`IsotopeProbeDbContext` through its constructor, matching the project's existing
explicit wiring. `QueryModels.cs` contains materialized read models, with no
console dependencies or tracked entities. All query methods accept cancellation:

```csharp
using IsotopeProbe.Queries;

var queries = new TrustedScanQueryService(db);
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
Web uses `OwnedScanQueryService(db, new ScanUser(internalUserId))`; the host derives this ID exclusively from the validated cookie. It never registers the trusted query service. Both paths share an internal query implementation, with ownership on execution and finding query roots before filtering, counts, summaries, and pagination.

## Google sign-in and local users

Web requires Google sign-in by default, including execution and finding URLs.
ASP.NET Core's maintained Google handler performs the external OAuth flow with
protected state, correlation cookies and PKCE; its validated user-info identifier
is mapped to a local UUID. The application uses framework cookie authentication
without ASP.NET Core Identity's unused password and role tables. See the official
[Google handler setup](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins?view=aspnetcore-10.0)
and [cookie authentication guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0).

New tables:

| Table | Purpose |
| --- | --- |
| `users` | UUID key, nullable email/display name, UTC creation time. |
| `external_logins` | Composite primary key `(Provider, Subject)` and user FK. Google provider is `https://accounts.google.com`. |
| `groups` | Integer key, name, optional description, unique normalized name. Names are trimmed then uppercased with .NET invariant casing; maximum 100 characters. |
| `user_groups` | Composite primary key `(UserId, GroupId)` prevents duplicate memberships. |

`scan_executions.OwnerUserId` is nullable and references `users`; deleting an owner
with scans is restricted. Findings inherit ownership through their execution.
The new `AddUsersGroupsAndOwnership` migration preserves historical data and old
migration files. Existing scans remain unowned and invisible to every Web user.

First successful Google sign-in creates a user and external login in one database
transaction, then issues the session. Concurrent first callbacks converge on the
unique provider/subject key without orphan users. Subsequent sign-ins reuse that
identity and update profile data. Email is never used to identify or merge users.
New users have no groups and see an empty scan list. No approval or invitation is
required. Google tokens are not saved, and only openid/profile/email scopes are
requested. The Google OAuth handler validates state/correlation and obtains user
info directly from Google; it does not use an application-validated ID token or
an OIDC nonce flow.

### Configure and run Web

1. Create/select a Google Cloud project. In Google Auth Platform, configure
   Branding/consent details (app name, support email and developer contact).
2. Set Audience to External for personal Google accounts. While the app is in
   Testing, add both accounts you intend to test under Test users. An Internal
   audience restricts sign-in to the configured Google Workspace organization.
3. Create an OAuth client of type **Web application**. Set its authorized redirect
   URI to exactly **`https://localhost:7080/signin-google`**. This callback is
   handled by the authentication middleware, not a Razor Page. Match scheme,
   hostname, port and path exactly. No browser JavaScript client is used.
4. Configure the credentials using the project's existing UserSecretsId:

```bash
dotnet user-secrets set 'Authentication:Google:ClientId' '<client-id>' --project IsotopeProbe.Web
dotnet user-secrets set 'Authentication:Google:ClientSecret' '<client-secret>' --project IsotopeProbe.Web
dotnet dev-certs https --trust
# Set ISOTOPEPROBE_CONNECTION_STRING as described above in this terminal.
dotnet ef database update --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli
dotnet run --project IsotopeProbe.Web --launch-profile IsotopeProbe.Web
```

Open **https://localhost:7080/**. On Linux, install/trust the generated development
certificate in your browser if `dotnet dev-certs https --trust` requires additional
platform setup. The launch profile binds HTTPS on localhost and enables Development
so user-secrets load. Alternatively set `Authentication__Google__ClientId` and
`Authentication__Google__ClientSecret` environment variables (double underscores).
Do not put real credentials in repository files. Missing database or Google
configuration causes startup to fail clearly. Web never applies migrations.

Sign in, then open **Your account** (`/Account`) to see your internal UUID, profile,
and local group memberships. Only login, provider callback and suitable error pages
are anonymous. Logout is a CSRF-protected POST and clears the local session; Google
may remain signed in. Finding evidence remains encoded text.

### Attribute scans and assign historical ownership

**CLI and direct database access are trusted operator interfaces.** They can read
all data. Supplying an owner is attribution, not authentication of the CLI caller.
Copy the internal user UUID from that user's account page. Replace example UUIDs
and execution IDs below with actual records; Google subjects and emails are not
accepted as internal IDs.

```bash
# Assign only the explicitly selected unowned scans, atomically.
dotnet run --project IsotopeProbe.Cli -- scans assign --user <user-uuid> --executions 2 3
# Explicit ownership for a new scan; validated before Nuclei launches.
dotnet run --project IsotopeProbe.Cli -- http://localhost:8085 --owner <user-uuid> -templatepath "~/Templates/sanity/"
# Or set the default owner in the CLI environment; --owner takes precedence.
export ISOTOPEPROBE_OWNER_USER_ID='<user-uuid>'
dotnet run --project IsotopeProbe.Cli -- http://localhost:8085 -templatepath "~/Templates/sanity/"
```

Assignment fails without changing any selected scan if a user/scan is missing or
any scan is already owned, including by that same user. There is no Web assignment
operation. Without an option or configured owner, CLI prints that the new scan is
unowned, CLI-only, and invisible through Web. Nobody automatically receives old
scans on sign-in.

### Local groups

```bash
dotnet run --project IsotopeProbe.Cli -- groups create 'Analysts' 'Local analysis team'
# Use the group ID printed by create:
dotnet run --project IsotopeProbe.Cli -- groups add --user <user-uuid> --group 1
dotnet run --project IsotopeProbe.Cli -- groups memberships --user <user-uuid>
dotnet run --project IsotopeProbe.Cli -- groups remove --user <user-uuid> --group 1
```

Repeated adds are idempotent and the database prevents duplicate memberships.
Groups are local memberships, never inferred from Google groups or email domains.
Authentication identifies the user; ownership controls scan access; group membership
grants no scan access, even for a group called Administrators. Users can only view
their memberships on Web. There are no roles, group sharing, public user directory,
or Web administration pages.

### Browsing and session security

The landing page lists only the owner's executions, newest first, 20 per page.
Execution pages include owner-scoped severity summaries and paginated findings.
Severity query-string filters apply before counting and paging. Examples:
`https://localhost:7080/executions/2` and `https://localhost:7080/findings/5`.
Other users' resources and unknown IDs both return 404. Ownership is never accepted
from a URL, form or header. Unowned historical results are also hidden.

Application cookies use the `__Host-IsotopeProbe` name, Secure, HttpOnly, SameSite=Lax,
a non-sliding eight-hour ticket lifetime, and browser-session persistence. The
framework's secure SameSite=None correlation cookie is retained for external login.
Post-login destinations must pass framework local-URL validation. CSRF protection
applies to login and logout forms. Responses containing application data use
`Cache-Control: no-store`. Each cookie-authenticated request checks that the local
user still exists. Logout removes the browser cookie; there is no central session
revocation store in this milestone, so a previously stolen cookie remains valid
until expiry or user deletion.

Before public deployment, register an exact production HTTPS callback such as
`https://probe.example.com/signin-google`, preferably under separate production
Google credentials. Configure production consent/audience/publishing as Google
requires, real TLS certificates, secure secret storage and database permissions.
Behind a reverse proxy, explicitly configure forwarded headers for trusted proxies
and the original HTTPS scheme before redirection/authentication; do not trust
arbitrary forwarded headers. Restrict allowed hostnames in deployment configuration.

Persist ASP.NET Core Data Protection keys outside ephemeral deployment storage.
For a future container deployment, mount a durable, access-restricted key directory
and configure `AddDataProtection().PersistKeysToFileSystem(...)` with encryption at
rest (certificate or managed key service). Use the same application name and key
ring for instances that must accept the same cookies, and separate applications'
keys. Key loss invalidates sessions and in-progress login state; key disclosure
allows cookie forgery. No container or deployment assets are added here.

### Manual verification with two Google accounts

1. Complete the setup and migrate explicitly. Use separate browser profiles (or
   normal/private windows) for accounts A and B, both listed as Google test users.
2. Sign in as A. Verify an empty list and no groups; copy A's internal UUID. Sign
   out and back in: the UUID must stay the same.
3. Sign in as B in the other profile. Verify a different UUID and an empty list.
4. Assign one existing unowned execution to A and another to B with the CLI
   commands above, or run new scans with each `--owner`. Keep a third scan unowned.
5. Verify each account's list/counts, execution details, severity filters and
   findings contain only its own data. Paste A's execution and finding URLs into
   B's browser: both must return 404. The unowned scan must be hidden from both.
6. Create one group, add both UUIDs, and reload each account page. Membership should
   appear while scan isolation remains unchanged.
7. Sign out using the button. Revisiting a scan URL must go to login. Try
   `/Account/Login?returnUrl=https://example.com`: completing sign-in must return
   to the local home page. Cancel a Google login to check the failure page.

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
query filtering/pagination, and lifecycle handling. Integration tests use the existing PostgreSQL provider, real migrations, and the ASP.NET Core MVC test host.
Each test creates and then removes its own `query_test_*`, `lifecycle_test_*`, or `auth_test_*`
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

Authentication verification includes concurrent first-login provisioning, stable subject
identity without email merging, owner-scoped SQL queries/counts/pagination/severity
filters, shared-group isolation, hidden unowned scans, atomic ownership assignment,
and unique memberships. Web tests run the actual Google/cookie/CSRF middleware
with controlled Google HTTP responses, including anonymous redirects, correlation
failure, local return URLs, encoded evidence and logout cookie removal. This is
**mocked Google authentication**, not a real end-to-end Google login. Follow the
two-account manual procedure above to verify your own Google client configuration.

Verified for this authentication milestone: solution build succeeded with no warnings
or errors; all 85 tests passed against local PostgreSQL with none skipped. EF
reports no pending model changes. Tests applied migrations only inside disposable
test schemas; the main application database was not migrated. Google responses
were mocked and no real Google credentials or live sign-in were exercised.
