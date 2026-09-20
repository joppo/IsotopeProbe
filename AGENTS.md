# Project

This is a PoC for orchestrating Nuclei vulnerability scans.

## Current goal

Build the smallest possible vertical slice:

1. .NET console application starts Nuclei.
2. Nuclei scans a supplied target.
3. Nuclei outputs JSONL to stdout.
4. Application reads stdout line-by-line.
5. JSONL findings are deserialized into C# objects.
6. Executions and findings are saved to PostgreSQL using EF Core and Npgsql.
7. Results are printed to the console.
8. Signed-in users can queue scans of configured targets and browse owned results through Razor Pages.

## Architecture

The application is split into IsotopeProbe.Cli (executable startup, arguments,
configuration, wiring, and console presentation) and IsotopeProbe.Core (scan
orchestration, Nuclei execution/parsing, domain entities, and EF persistence), plus
IsotopeProbe.Web (local Razor Pages and a single background scan worker).
CLI and Web reference Core; Core must not reference either host, and Web must not
reference CLI. Web pages submit owned work through Core to the PostgreSQL queue;
a scoped Core dispatcher runs it from a BackgroundService, never the HTTP request.
Web never applies migrations on startup. Web requires Google sign-in and uses only
OwnedScanQueryService, OwnedTargetQueryService, and OwnedScanComparisonService
with the internal user ID from the validated session;
TrustedScanQueryService, WebScanRecovery, and ownership/group mutation commands are CLI-only.
Groups grant no additional scan or target access. Unowned scans are invisible to Web.
Persistent targets are owned identity/history records, unique by owner and exact URL;
they never grant Web scanning permission. Executions retain captured URLs and scanner
inputs; nullable TargetId links must have the same owner, enforced in PostgreSQL.
Owned CLI scans, Web admission, and atomic assignment resolve targets through Core.
Do not add target transfer, URL editing, or deletion flows.
Web provisions local users on Google sign-in and supports POST logout; scan browsing
remains read-only. Web submissions accept only server-configured target/profile IDs and a
user-scoped protected idempotency token; never accept owner IDs or scanner arguments.
Keep queue admission and claiming in Core, hosting in Web. Web concurrency limits
apply to one worker instance, not direct CLI scans.
Keep existing migrations and database schema unchanged during structural refactors.
Use EF Core migrations; do not use EnsureCreated. Keep connection credentials in environment variables.

Do NOT introduce:
- Docker
- web APIs
- external message brokers
- scheduling
- microservices

These may be introduced later.

## Design principles

- Nuclei is an external scanning engine.
- Nuclei-specific DTOs should be separate from domain models.
- Keep orchestration logic under our control.
- Prefer simple, explicit C# over unnecessary abstractions.
- Do not add dependencies unless justified.

## Development

Before making significant architectural changes, explain the proposed change.
Add tests for parsing and non-trivial logic.

## Console arguments

- Usage: `IsotopeProbe <target> [-templatepath <path> | --profile <id>] [--owner <internal-user-uuid>]`.
- Profile scans use Core prepared immutable snapshots. Prepare with `profiles prepare <id>`;
  inspect with `profiles inspect <id>`. Retain snapshots and version bindings for all history.
- Accept `-templatepath` before or after the target and pass its value to Nuclei
  using `-t`, while retaining `-u <target> -jsonl -silent`.
- Keep template selection optional; omit `-t` when no path is supplied.
- Expand a leading `~/` to the user's home directory, including quoted paths.
  Preserve paths containing spaces as a single process argument.
- Local testing example:
  `dotnet run --project IsotopeProbe.Cli -- http://localhost:8085 -templatepath "~/Templates/sanity/"`.

## Specs

The location is ~/z/p/nuclei/isotopeprobe
The solution is IsotopeProbe.slnx, with IsotopeProbe.Cli, IsotopeProbe.Core, and IsotopeProbe.Web
application projects and the existing IsotopeProbe.Tests project.
