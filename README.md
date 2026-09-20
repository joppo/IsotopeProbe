# IsotopeProbe

A .NET 10 application that runs Nuclei through a CLI or an owned Web scan queue, persists execution lifecycle
state and findings in PostgreSQL, and browses saved results through CLI queries
or a local Razor Pages website. Findings are saved one at a
time as JSONL arrives, so later failure or cancellation does not discard earlier
committed findings.

## Execution lifecycle

| Status | Meaning |
| --- | --- |
| Queued | Owned Web submission saved; no scanner has started. |
| Running | Saved before launching Nuclei; final outcome has not been persisted. |
| Succeeded | Nuclei exited with code 0, output processing and all finding writes finished, and the terminal update committed. Zero findings is valid. |
| Failed | Startup, parsing, finding persistence, process exit, or shutdown failed. Earlier saved findings remain. |
| Cancelled | Cancellation was handled and the terminal update committed. Earlier saved findings remain. |

`EnqueuedAt`, `StartedAt` and `CompletedAt` use UTC. Queued work has no start time.
`CompletedAt` and `ExitCode` are nullable:
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
- `IsotopeProbe.Web` is an ASP.NET Core Razor Pages host for owned scan submission and read-only
  execution/finding browsing. Its background worker uses a fresh Core scope per job.
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
local container can be used). Nuclei is required by both CLI scans and the Web worker, but not result queries.
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

```run queries
select "Id", "Target", "StartedAt", "CompletedAt", "StandardError", "FailureReason", "Status", "OwnerUserId" from scan_executions ORDER BY "Id" DESC LIMIT 1;
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
applies to login, logout and scan submission forms. Responses containing application data use
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


## Start scans from Web

Sign-in → **New scan** → POST → owned PostgreSQL `Queued` execution → immediate
redirect to execution details. A single ASP.NET Core `BackgroundService` claims the
oldest queued Web record and runs Nuclei through Core. Refresh pages manually to see
status and findings. No scanner runs inside an HTTP request; the CLI is never invoked
by Web. Users can select only operator-configured target IDs, and prepared, enabled template
profiles. Arbitrary public URL scanning, template uploads, credentials and extra scanner
arguments are not supported by the UI.

The authenticated internal user ID supplies ownership. A Data Protection token binds
each form's nonce to that user. Reposting the same valid submission returns that user's
original execution, including after completion or configuration changes. A different
user cannot reuse that token. Persist Data Protection keys as described above to retain
valid forms across host restarts. The unique `(OwnerUserId, SubmissionId)` index adds a
database safeguard. All browsing still uses ownership-filtered queries; groups confer
no additional access.

Admissions use a PostgreSQL transaction-scoped advisory lock (`724196381`). Every
submission takes it before checking duplicates and counting queued/running Web rows.
At READ COMMITTED isolation, the next admission sees the previous committed insert;
concurrent requests cannot bypass limits. The worker claims with `FOR UPDATE SKIP
LOCKED`, ordered by enqueue time then ID, and commits Running plus the actual start
time before launching Nuclei. Competing workers cannot claim the same row. This does
**not** impose a global execution limit across replicas: deploy **one Web worker
instance**. It defaults to executing one job at a time. Direct CLI scans bypass Web queue limits.

### Configuration and local walkthrough

`IsotopeProbe.Web/appsettings.Development.json` supplies the existing local example:
`local-sanity` → `http://localhost:8085`. Profiles are configured separately in
`profiles.json`; see versioned-profile setup below. Other environments have no allowed targets until configured.
All settings are under `WebScans`; environment variables use double underscores:

| Setting | Default | Meaning |
| --- | --- | --- |
| `Targets` | Empty outside Development | Entries with unique `Id`, `Name`, HTTP(S) `Url` without credentials |
| `PerUserLimit` | `1` | Queued plus Running Web scans per owner |
| `QueueLimit` | `20` | Queued Web scans globally |
| `ConcurrentScans` | `1` | Executing jobs per Web instance, 1–20 |
| `TimeoutSeconds` | `600` | Execution timeout, 1–86400 seconds |
| `PollSeconds` | `2` | Worker wait between attempts, 1–60 seconds |
| `ExecutablePath` | `nuclei` | Executable on Web's PATH or absolute path |

Limits must be positive. Keep `ConcurrentScans=1` for this milestone's default deployment.
Admission captures the URL, timeout, profile identity/version, selected count and immutable
snapshot hash. Source changes do not change queued profile scans. Nuclei must remain
available to the worker; the engine itself is not pinned. A missing executable fails
the claimed job without stopping the queue worker.

```bash
# Set database credentials only in the environment; also configure Google as above.
read -rsp 'PostgreSQL connection string: ' ISOTOPEPROBE_CONNECTION_STRING
export ISOTOPEPROBE_CONNECTION_STRING
export WebScans__Targets__0__Id='local-sanity'
export WebScans__Targets__0__Name='Local test target (8085)'
export WebScans__Targets__0__Url='http://localhost:8085'
export ISOTOPEPROBE_PROFILES="$PWD/profiles.json"
# Prepare profiles as described below before submitting scans.
export WebScans__PerUserLimit=1
export WebScans__QueueLimit=20
export WebScans__ConcurrentScans=1
export WebScans__TimeoutSeconds=600
export WebScans__PollSeconds=2
export WebScans__ExecutablePath=nuclei

dotnet build IsotopeProbe.slnx --no-restore -m:1
dotnet ef database update --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli
dotnet run --project IsotopeProbe.Web --launch-profile IsotopeProbe.Web
```

Ensure the existing local test target is available on port 8085, then open
`https://localhost:7080`, sign in, select **New scan → Local test target (8085)**, and
submit. Details show the saved execution immediately (it may already be Running by
the time the browser loads). Refresh until terminal, then open a finding. In a second
signed-in browser account, the execution and finding URLs must both return 404.
Resending the same form must redirect to the original execution. A new form while
that user's scan is queued/running displays a capacity message.

`AddWebScanQueue` appends to the migration history: nullable start time, enqueue time,
source, submission nonce, template path/profile and timeout, plus queue/idempotency
indexes. Historical rows retain their timestamps/statuses and receive source `Cli`.
Status values remain string-mapped and the enum's existing numeric values are explicit
and unchanged. No startup migration runs. `ScanService` now shares the execution and
finalization body between direct CLI creation and an already-persisted claimed record;
findings keep the original execution ID.

### Timeout, shutdown and recovery

Timeout cancels Nuclei, kills its process tree through the existing runner, and records
Failed with an explicit timeout reason. Graceful host shutdown stops further claims,
cancels the active job and records Cancelled with an application-shutdown reason when
the database is available. Previously saved findings remain. The host allows 40 seconds
for shutdown, covering the existing 10-second finding-write, process-cleanup and
final-write budgets with margin. Queued rows remain queued for the next startup.

A crash/SIGKILL or database outage during finalization can leave Running records and
possibly a scanner process behind. There is no startup sweep, lease, heartbeat or
automatic retry. An unresolved Running Web scan continues to count against its owner's
limit. Inspect the selected execution and confirm its scanner **and children** have
stopped before explicitly recovering it:

```bash
dotnet run --project IsotopeProbe.Cli -- scans show 42
# Only after manually confirming the process is no longer running:
dotnet run --project IsotopeProbe.Cli -- scans recover-web 42 --confirmed-stopped
```

Recovery marks only that selected Running Web execution Failed, retains findings,
and refuses queued, terminal, missing or CLI executions. The confirmation flag records
operator intent; the command cannot establish process liveness. Recovery has no Web
route and does not retry the scan. Users may submit a fresh form afterwards.

### Queue verification

```bash
# Use a local test database whose user can create disposable schemas.
read -rsp 'PostgreSQL test connection string: ' ISOTOPEPROBE_TEST_CONNECTION_STRING
export ISOTOPEPROBE_TEST_CONNECTION_STRING
dotnet test IsotopeProbe.slnx --no-restore -m:1
# Focus on queue, token and HTTP submission tests:
dotnet test IsotopeProbe.slnx --no-restore -m:1 --filter 'FullyQualifiedName~WebQueueTests|FullyQualifiedName~SubmissionTokenTests|FullyQualifiedName~WebSubmission'
dotnet ef migrations has-pending-model-changes --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
```

Queue tests use disposable `queue_test_*` schemas and controlled local fake scanner
processes. HTTP tests use real cookie/Google/antiforgery middleware with mocked Google
responses and disable the worker to verify enqueue-only requests. Separate worker
tests verify execution, shutdown and restart. No test scans external targets.

Verified for the Web queue milestone: solution build succeeded without warnings or
errors; all **97 tests passed**, none skipped, against local PostgreSQL in disposable
schemas. This includes the full HTTP submission → worker → persisted finding path
with a controlled scanner and mocked Google responses. EF reported no pending model
changes, and `git diff --check` passed. The main application database was not migrated.
Live Nuclei sanity scans were verified on 2026-09-19 against `http://localhost:8085`
through both the CLI and Core Web queue dispatcher. Both completed as Succeeded
with exit code 0 and five persisted findings each (four high, one medium).
The live queue check also verified duplicate-submission idempotency and owned result
access. These checks used a disposable PostgreSQL schema that was removed afterward.
Real Google sign-in remains a manual check.

## Persistent targets and history

A `Target` is an application identity owned by one local user. Each has an integer
ID, name, exact URL, and `CreatedAtUtc`. The database permits one target per owner
and exact URL; another user gets a separate target even for the same URL. URL
comparison uses PostgreSQL's deterministic `C` collation. No scheme, host, path,
query, case, or trailing-slash normalization is performed.

An execution retains its owner and optionally references a target through
`TargetId`. A composite foreign key enforces matching owners, and a check constraint
forbids a linked execution without an owner. Referenced targets cannot be deleted.
Findings still belong to executions. There is no ownership transfer, URL editing,
target management UI, shared access through groups, or finding deduplication.

The execution's `Target` string remains the actual scanned URL snapshot. Linking
history never replaces it or changes template paths/profiles, timeout, status,
timestamps, IDs, or saved evidence. The queue worker continues executing captured
inputs, even if the operator changes the allowed-target configuration afterward.

**A stored target is not permission to scan.** Web still accepts only the current
server-configured allowed-target ID and the protected submission token. Core resolves
that allowed URL, then finds or inserts the authenticated user's persistent target
and queues a linked execution in the same transaction. Concurrent insertions reuse
the database's unique owner/URL row. The allowed-target string ID used by the form
is distinct from the persistent target's integer ID. Posting a persistent ID or an
arbitrary URL does not select a scanning destination.

Owned CLI scans use the same resolver before launching Nuclei. Existing commands and
options remain available, including `--owner` and `ISOTOPEPROBE_OWNER_USER_ID`.
Unowned CLI scans still have no target link and are invisible through Web.
`scans assign --user <uuid> --executions <id> [<id> ...]` now links usable URLs as
part of its existing all-or-nothing transaction; missing or already-owned executions
still cause the whole assignment to fail. CLI-created and backfilled targets do not
add entries to the Web allowlist.

### Backfill and upgrade

`20260919133329_AddPersistentTargets` appends to the existing migration history.
Within the migration transaction it groups owned executions by owner and exact stored
URL, creates one target per group with the URL as its initial name, and links the
executions. `CreatedAtUtc` on these targets is **the migration transaction time**;
the original creation time is unknown. New targets record their insertion time.

For this milestone, the legacy resolver/backfill rule considers an input usable when
it is an absolute HTTP(S) URL with a DNS/IPv4-style host or bracketed IPv6-style host,
an optional numeric port, no credentials, and no whitespace. The original string
is preserved. This is a conservative identity rule, not a connectivity check.
Unowned executions and legacy inputs outside that rule remain unlinked. Assignment
preserves such stored inputs. New owned CLI scans always create/reuse an identity
for the exact supplied target string, including host-only inputs already accepted by
Nuclei; this preserves CLI compatibility without enabling arbitrary Web destinations.
`TargetId` remains nullable. Queued and Running legacy rows are
linked when eligible without changing their captured inputs or lifecycle state.

Before upgrading, back up the database and stop **all writers**: stop accepting Web
submissions, allow active scans to finish where practical, stop the Web worker and
CLI scan/assignment processes, and keep old binaries stopped. Graceful Web shutdown
may record active work as Cancelled; it leaves queued work queued. An unresolved
Running execution remains inspectable and follows the existing explicit recovery
procedure. Do not mark it recovered until its scanner and children are confirmed
stopped. The migration itself does not cancel, retry, recover, or launch scans.

From the repository root, with the existing connection string exported:

```bash
# Stop the Web host and all CLI writers first; do not run old binaries afterward.
dotnet build IsotopeProbe.slnx --no-restore -m:1
dotnet ef database update 20260919133329_AddPersistentTargets \
  --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet ef migrations has-pending-model-changes \
  --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
# Restart only the upgraded host, with the existing Google and WebScans configuration.
dotnet run --project IsotopeProbe.Web --no-build --launch-profile IsotopeProbe.Web
```

Do not upgrade while old instances continue writing: they cannot populate the new
links. There is no startup migration or automatic catch-up backfill. Do not downgrade
this migration to recover from a deployment issue: its Down operation removes target
identity/history links (execution and finding rows survive).

To identify unlinked legacy rows after upgrading, run this read-only SQL in your
existing PostgreSQL client. Owned rows in this result require review of their stored
URL; unowned rows are intentionally unlinked. Do not rewrite historical URLs merely
to force a link.

```sql
SELECT "Id", "OwnerUserId", "Target", "Status"
FROM scan_executions
WHERE "TargetId" IS NULL
ORDER BY "Id";
```

### Web walkthrough and verification

Sign in and choose **Targets** to see your targets, execution counts, and the latest
execution's time, persisted status, and finding count. That count belongs to the
latest execution, not to a set of unique vulnerabilities across history. Targets
sort by recorded time then ID descending; execution history sorts by enqueue/start
time then ID descending, placing unknown times last.

Open a target for paginated history and follow an execution to its existing details
and findings. Execution details link back to target history when available. A target
whose exact URL is no longer allowed displays a history-only explanation. For an
allowed URL, **New scan** opens the existing allowlist selection form; select the URL
there. No target page creates a second scan-submission path. Unknown target IDs and
other users' target IDs both return 404.

Tests create and remove disposable PostgreSQL schemas; they do not reset the main
application tables. Export a test connection string for a local database account
permitted to create schemas, then run:

```bash
read -rsp 'PostgreSQL test connection string: ' ISOTOPEPROBE_TEST_CONNECTION_STRING
export ISOTOPEPROBE_TEST_CONNECTION_STRING
dotnet test IsotopeProbe.slnx --no-restore -m:1
# Optional focused rerun:
dotnet test IsotopeProbe.slnx --no-build --filter 'FullyQualifiedName~TargetTests|FullyQualifiedName~WebSubmission'
```

Verified for the persistent-target milestone on 2026-09-19: the solution built with
zero warnings/errors; all **105 tests passed**, none skipped, against disposable
PostgreSQL schemas. Tests applied the full migration history to empty schemas and
upgraded the previous queue migration with representative owned/unowned executions,
findings, queued/running work, and unusable URLs. The upgrade preserved existing
execution/finding/user data and backfilled the expected target links. HTTP tests
verified authenticated target navigation and cross-owner 404s. EF reported no pending
model changes, and `git diff --check` passed.

Live owned CLI and Core queue scans against `http://localhost:8085` both succeeded
with five persisted findings each, reused one target, and appeared together in its
history. The disposable live schema was removed. The application database was not
migrated; follow the stop/migrate/restart sequence above. Real Google sign-in remains
a manual check (automated HTTP tests mock Google's responses).

## Optional matcher names and scan comparison

`Finding.MatcherName` is a nullable string from Nuclei's top-level `matcher-name`.
It is distinct from the existing nullable Boolean `MatcherStatus` (`matcher-status`).
Missing, null, blank, and unexpected matcher-name value types become null; valid
nonblank strings retain their original case and surrounding characters. Only this
optional field has tolerant deserialization. Malformed JSON and unrelated field
errors still follow the existing parser rules. The parser retains the original raw
JSON string; PostgreSQL continues storing it as `jsonb`, as before. Neither ingestion
nor backfill rewrites saved evidence to manufacture a matcher name.

### Matcher-name upgrade and backfill

`20260919151713_AddMatcherName` adds one nullable column and runs a set-based SQL
backfill in the migration transaction. It extracts only nonblank top-level strings
from `findings."RawJson"`; nested values, scalars, arrays, nulls, numbers, Booleans,
and blank names are ignored. Its whitespace test matches .NET's whitespace characters.
RawJson is already `jsonb`: invalid JSON syntax cannot be stored in that column, but
valid JSON with an unsuitable shape is handled safely. The SQL includes a null-only
update guard. It never loads the findings table into application memory, and does not
change existing matcher status or evidence.

The backfill scans the findings table and updates qualifying rows, so plan a
maintenance window appropriate to its size (including PostgreSQL WAL and transaction
space). Back up the database, stop Web/worker and CLI writers, apply the migration,
and restart only upgraded binaries. Old writers do not persist matcher names.
There is no automatic startup migration or separate backfill command: the backfill
is part of this migration. Records without usable stored metadata remain null.

With `ISOTOPEPROBE_CONNECTION_STRING` already exported, run from the repository root:

```bash
dotnet build IsotopeProbe.slnx --no-restore -m:1
dotnet ef database update 20260919151713_AddMatcherName \
  --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet ef migrations has-pending-model-changes \
  --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet run --project IsotopeProbe.Web --no-build --launch-profile IsotopeProbe.Web
```

The previous persistent-target upgrade command is retained above for that milestone;
use the profile migration command below to upgrade to the current schema.

### Comparison rules and interpretation

Comparisons are read-only and computed on demand. Both executions must independently
belong to the signed-in user, reference the same non-null target, have persisted
Succeeded status and a completion timestamp, and be different executions. Groups
provide no additional access. Unknown and inaccessible executions return 404;
accessible but ineligible pairs receive a validation explanation.

The reusable Core identity policy uses a structured key of **TemplateId, MatchedAt,
and optional MatcherName**, with ordinal, case-sensitive equality. URLs are not
normalized: paths, query strings, scheme, case, and trailing slashes remain distinct.
Null, empty, and whitespace-only matcher names all mean absent. Two absent names
match by template/location; two present names must match exactly; an absent name is
never a wildcard for a present one. When name availability differs for a shared
template/location, a warning explains that missing historical metadata may account
for the apparent difference.

Blank/missing template IDs or matched locations cannot identify a finding safely.
Those records are excluded from the three categories and listed separately, with
counts and links to their stored evidence. New ingestion continues requiring those
fields; this exclusion handles incomplete historical or operator-imported records.

The categories compare sets of keys:

- **Newly detected:** present only in the newer execution.
- **Detected in both:** present in both executions.
- **No longer detected:** present only in the older/baseline execution.

Repeated records with one key form one comparison item, with occurrence counts and
paginated links to every associated finding. Raw finding-record counts are displayed
separately from unique comparison-item counts. All original records remain stored.
Severity, descriptions, and evidence are not identity fields. Older/newer severity
sets are shown side by side so a severity change does not become a new identity.
There is no persistent issue table, cross-scan deduplication of stored findings, or
saved comparison result.

**No longer detected does not mean fixed.** A successful process lifecycle does not
prove identical coverage or that every check succeeded. The page displays captured
URL, template path/profile, and timeout side by side, and warns if they differ.
New profile executions capture profile identity/version, template snapshot hash and the
observed Nuclei version. Comparisons warn about changed or missing provenance. Matching
snapshot hashes and engine versions mean matching recorded content and engine version,
not guaranteed identical runtime coverage. Legacy/custom scans retain unknown provenance.
No authentication secrets, request/response bodies, or raw JSON are fetched as
comparison metadata; evidence stays on the existing authorized finding pages.

### Choosing scans and performance limits

On a successful execution, choose **Compare with previous successful scan**. The
previous scan must have an earlier start timestamp, or the same timestamp and a
lower execution ID; failed, cancelled, queued, and running scans are skipped.
If no previous successful scan is known, the details page explains that. **Choose a
different successful baseline** opens a paginated list of other completed successful
executions of the owned target. If a manually chosen baseline does not precede the
newer scan, the page warns that category direction follows your explicit selection.
Execution IDs, start timestamps, and captured URLs label both sides.

The current PoC compares at most **25,000 finding records per execution**. Core
projects only ID, template ID, matched location, matcher name, and severity, and
fetches at most the limit plus one to detect overflow. Oversized comparisons are
rejected explicitly rather than silently truncated. Grouping and set comparison run
in memory with the shared .NET identity policy, avoiding differences between SQL
collation/whitespace rules and application equality. Work and memory scale with the
combined summary-field size of the two bounded sets, not their raw evidence size.
This is not a byte limit; unusually long identity fields still increase memory use.
A future high-volume implementation would need database-side grouping with equivalent
identity semantics before increasing this limit.

Category results and occurrence links are paginated (20 per page). Baseline selection
uses database-side filtering, ordering, and pagination. Comparison requests recompute
the bounded summary sets; nothing is cached or persisted. Cancellation propagates
through database reads and comparison work. Normal scans and finding browsing are
not subject to the comparison record limit.

### Local comparison verification

Automated tests use disposable PostgreSQL schemas. With an exported
`ISOTOPEPROBE_TEST_CONNECTION_STRING` for an account allowed to create schemas:

```bash
dotnet test IsotopeProbe.slnx --no-restore -m:1
# Focused comparison/parser/migration tests:
dotnet test IsotopeProbe.slnx --no-build \
  --filter 'FullyQualifiedName~MatcherNameTests|FullyQualifiedName~FindingComparisonTests|FullyQualifiedName~ScanComparisonTests|FullyQualifiedName~ComparisonPages'
# Controlled, reproducible output-fixture demonstration:
dotnet test IsotopeProbe.slnx --no-build \
  --filter FullyQualifiedName~LocalFixturesDemonstrateNewBothAndNoLongerDetected
```

`IsotopeProbe.Tests/Fixtures/Comparison/baseline.jsonl` and `newer.jsonl` are explicit
controlled scanner-output fixtures referring only to localhost:8085. The demonstration
ingests and persists them, then verifies one newly detected item, one recurring item,
and one no-longer-detected item. The recurring item has two baseline records and one
newer record, with a severity change. These are synthetic outputs, not a claim of a
live scan; the test does not modify the default test site or contact external targets.

For a manual Web walkthrough after upgrading, sign in, submit two scans of the
existing allowed local target, wait for both to succeed, open the newer execution,
and follow the comparison link. Browse each category and the occurrence links, then
choose another successful baseline if available. An unchanged target/template set
may produce only recurring findings. Review the coverage warnings even in that case.

Verified for this milestone on 2026-09-19: the solution built with zero warnings and
errors; all **130 tests passed**, none skipped, using disposable PostgreSQL schemas.
This includes upgrading the previous migration with representative raw JSON shapes,
full migration history on fresh schemas, Unicode blank-name handling, unchanged raw
evidence/matcher status, comparison categories and duplicate evidence, authorization
of both execution IDs, HTTP encoding, pagination, and explicit oversized-pair rejection.
The controlled JSONL fixture demonstration passed. EF reported no pending model
changes and `git diff --check` passed. The working application database was not
migrated or reset, and no external targets were scanned. Real Google sign-in and the
manual browser walkthrough remain operator checks; HTTP tests mock Google responses.

## Versioned scan profiles

Core resolves operator configuration into durable snapshots; CLI and Web share the
resolver and execution checks. There are no profile administration tables or browser
filesystem inputs. `profiles.json` contains the two initial profiles. Set
`ISOTOPEPROBE_PROFILES` to its **absolute path** in both hosts (Web uses a different
working directory). Copy that file for deployment and edit trusted paths before first
preparation. JSON property names are case-sensitive. IDs must be unique. Each definition
includes name, description, enabled flag, trusted root, explicit relative files,
exclusions, source version when known, and `http-get-v1` restrictions. Missing files
never broaden the selection.

### Initial selections and review

| Profile | Exact template IDs / filenames | Source and rationale |
| --- | --- | --- |
| `sanity`, version `1` | `dockercfg-config.yaml`, `git-config.yaml`, `laravel-env.yaml`, `mysql-config-exposure.yaml`, `netrc.yaml` | Existing `~/Templates/sanity/` content, preserved byte-for-byte in `templates/sanity/` for setup reproduction. Validates the local IsotopeProbe test setup, not general vulnerability coverage. Collection version is unknown for these local copies. |
| `standard-website`, version `1` | `dockercfg-config.yaml`, `git-config.yaml`, `mysql-config-exposure.yaml`, `netrc.yaml` | `http/exposures/configs/` in trusted local `~/nuclei-templates`, reviewed against installed v10.4.9. Recommended when prepared and enabled. |

The standard selection makes six possible GET requests: `/.dockercfg`,
`/.docker/config.json`, `/.git/config`, `/.my.cnf`, `/.netrc`, and `/_netrc`.
Review covered methods, paths, payloads, matchers and extractors, not only severity or
tags. These templates inspect exposed configuration using status, word, regex and
response-only DSL conditions; Git and netrc include regex extractors. They contain no
login attempts, writes, exploitation payloads, headless checks, fuzzing, code protocols,
denial-of-service checks or brute force. Laravel's 22 inline `.env` variants remain in
sanity but are excluded from the smaller standard selection. This is limited exposure
coverage, not comprehensive assessment or proof of security. Findings can contain
exposed secrets, as with existing finding evidence.

The backend deliberately supports a narrow standalone YAML subset: HTTP GET requests
under BaseURL, inline payload lists, supported matchers and regex extractors. External
payload files, scripts, workflows, variables, raw requests, dynamic request functions
and unsupported features/protocols are rejected. Required local assets are rejected
rather than silently omitted. YamlDotNet is the added dependency for structural parsing.
Preparation also runs `nuclei -validate` before publication; it does not scan a target.

Nuclei v3.11.1 was checked against its installed help/source and the
[official CLI documentation](https://github.com/projectdiscovery/nuclei/blob/v3.11.1/README.md).
Profile execution supplies explicit captured files, disables updates/downloads, OAST
and redirects, and uses empty configuration in an isolated process HOME/config/cache
and working directory. Host Nuclei flags, proxy variables and credentials are not
inherited. Temporary engine configuration is separate from durable template storage.
Legacy custom-path scans retain their existing process arguments and behavior.

### Setup, preparation and publishing versions

Run from the repository root with .NET 10 and Nuclei on PATH. For a fresh installation,
obtain the pinned collection explicitly, never from an HTTP request:

```bash
# Only if this checkout does not already exist; do not overwrite an installed collection.
git clone --branch v10.4.9 --depth 1 https://github.com/projectdiscovery/nuclei-templates.git "$HOME/nuclei-templates"
# For a new sanity setup; existing local files are not overwritten.
mkdir -p "$HOME/Templates/sanity"
cp -n templates/sanity/*.yaml "$HOME/Templates/sanity/"
export ISOTOPEPROBE_PROFILES="$PWD/profiles.json"
dotnet restore IsotopeProbe.slnx
dotnet build IsotopeProbe.slnx --no-restore
dotnet tool restore
dotnet run --project IsotopeProbe.Cli --no-build -- profiles prepare sanity
dotnet run --project IsotopeProbe.Cli --no-build -- profiles prepare standard-website
dotnet run --project IsotopeProbe.Cli --no-build -- profiles inspect standard-website
```

Edit `Root` before preparation if using another checkout location. `SourceVersion` is
operator-declared collection provenance: verify the tag/commit before setting it. Hashes,
not that label, establish captured bytes. Review behavior again after collection updates.
Bundled sanity copies originate from ProjectDiscovery nuclei-templates; see
`templates/LICENSE.md`.

Preparation prints the manifest, IDs, relative paths, SHA-256 hashes, creation time and
aggregate hash. Duplicate paths/content are deduplicated; conflicting IDs with different
bytes, empty selections, missing files, invalid YAML, unsupported dependencies and Nuclei
validation failures are rejected. `Exclusions` are exact relative filenames. The aggregate
hash covers the fixed serialized manifest without its creation time: profile identity,
version, definition hash, source version and sorted file entries.

`StorageDirectory` defaults to `~/.local/share/isotopeprobe/snapshots`. Use durable local
storage shared by the operator and both hosts, outside Web's public directory. Only
trusted operators should write snapshots/version bindings; Web needs read access.
Back up the whole directory alongside PostgreSQL. Do not use an ephemeral container
layer, temporary filesystem or independently replicated stores. Storage must support
atomic same-filesystem directory rename and no-overwrite file rename. Symlinks in source
or snapshot paths are unsupported.

Complete snapshots and ID/version bindings are published atomically. Identical preparation
reuses the existing snapshot. Conflicting concurrent preparations cannot rebind a version;
a losing preparation can leave complete unreferenced content, never a partial snapshot.
There is no automatic cleanup. **Retain every snapshot and version binding** referenced
by queued or historical executions, including after disabling/removing a profile.

To update content/selection, review it, increment `Version`, update `SourceVersion` when
known, and run `profiles prepare <id>` before restarting Web with that configuration.
Never delete bindings to force reuse of an existing version. Preparation reports content
drift under an unchanged version. Submission uses prepared bytes and does not reread
mutable sources. Changed configuration under an unchanged version is unavailable.
`Enabled=false` prevents new submissions without preventing queued work from running.
Configuration loads at startup; restart Web after edits. Zero-template profiles cannot
be prepared. An unavailable standard profile is explained on the form; sanity is never
silently selected instead.

### Schema, deployment and usage

`20260920100615_AddProfileSnapshots` adds nullable `ProfileId`, `ProfileVersion`,
`SnapshotHash`, `TemplateCount`, `TemplateSourceVersion` and `NucleiVersion` columns.
Existing `TemplateProfile` captures the display name. Snapshot timestamps and file hashes
live in durable manifests. Historical values are not inferred or rewritten. Web never
applies migrations automatically.

Stop Web/worker and CLI writers, back up database and snapshots, prepare the profiles,
then explicitly migrate with credentials in the environment:

```bash
read -rsp 'PostgreSQL connection string: ' ISOTOPEPROBE_CONNECTION_STRING
export ISOTOPEPROBE_CONNECTION_STRING
dotnet ef database update 20260920100615_AddProfileSnapshots \
  --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet ef migrations has-pending-model-changes \
  --project IsotopeProbe.Core --startup-project IsotopeProbe.Cli --no-build
dotnet run --project IsotopeProbe.Cli --no-build -- http://localhost:8085 --profile sanity
dotnet run --project IsotopeProbe.Cli --no-build -- http://localhost:8085 --profile standard-website
# Append --owner <internal-user-uuid> to make CLI results visible to that owner.
# Configure Google and allowed targets as above.
dotnet run --project IsotopeProbe.Web --no-build --launch-profile IsotopeProbe.Web
```

Existing queued rows with null `SnapshotHash` follow an explicitly legacy execution path
using their captured `TemplatePath` and timeout. They are **not converted** to profiles.
Draining before deployment is optional; otherwise preserve their original template
sources until completion. Their content/engine provenance remains unknown. Recorded
snapshots that are missing or altered fail, with no legacy-directory fallback. Existing
recovery instructions still apply to interrupted Running scans.

Web users choose an allowed target and prepared enabled profile; standard is the
recommended default when available. Version, files, snapshot reference, arguments and
ownership come from the server. Existing CSRF, idempotency, ownership and admission
limits remain. CLI `--profile <id>` and `-templatepath <path>` are mutually exclusive.
Custom paths and omitted template selection remain supported with unavailable profile
metadata.

Details and CLI `scans show <id>` distinguish **templates selected** from unknown
**templates loaded/executed** and unknown **checks completed**. Succeeded refers to the
existing process/persistence lifecycle, not completion of every selected check. The
engine binary is not pinned; its observed version is recorded at execution. If the
version query returns no recognizable version, it remains unknown. Comparisons report
changed IDs/versions, hashes, engines or missing provenance without blocking eligible
pairs or changing finding categories/identity. Matching recorded template content and
engine version never guarantee identical runtime coverage.

### Verification commands

Use an account allowed to create/drop disposable schemas. Tests do not reset the working
schema. Real profile checks are opt-in and scan only `http://localhost:8085`:

```bash
read -rsp 'Test PostgreSQL connection string: ' ISOTOPEPROBE_TEST_CONNECTION_STRING
export ISOTOPEPROBE_TEST_CONNECTION_STRING
dotnet test IsotopeProbe.slnx --no-restore
# Existing local target and both configured template sources must be available.
ISOTOPEPROBE_LIVE_PROFILE_TEST=1 dotnet test IsotopeProbe.slnx --no-build \
  --filter FullyQualifiedName~RealProfilesRunOnlyAgainstExistingLocalTarget
```

The live test prepares disposable snapshots, tests both CLI orchestration and Web queue
execution for both profiles, then removes its schema/snapshots. No external targets are
scanned. HTTP tests mock Google responses; real Google sign-in and a visual browser
walkthrough remain manual checks.

Verified on 2026-09-20: build succeeded; **144 tests passed, none skipped**, including
real profile runs with Nuclei v3.11.1. Sanity selected five templates and saved four
findings; standard selected four and saved three. CLI and Web queue finding counts
matched for each profile. These counts do not measure completed checks. Fresh and
populated disposable databases both migrated successfully; the populated check retained
legacy queued selection and null provenance. The broader suite also verifies preserved
owners, targets and findings through schema upgrades. EF reported no pending model
changes, and `git diff --check` passed. Read-only inspection found no Queued or Running
Web records in the working database; its schema was not migrated. Existing local test
target edits were left untouched. No external targets were scanned.
