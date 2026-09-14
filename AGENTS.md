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
8. Saved results can be browsed locally through read-only Razor Pages.

## Architecture

The application is split into IsotopeProbe.Cli (executable startup, arguments,
configuration, wiring, and console presentation) and IsotopeProbe.Core (scan
orchestration, Nuclei execution/parsing, domain entities, and EF persistence), plus
IsotopeProbe.Web (local read-only Razor Pages).
CLI and Web reference Core; Core must not reference either host, and Web must not
reference CLI. Web calls Core query services, never launches scans, and never
applies migrations on startup.
Keep existing migrations and database schema unchanged during structural refactors.
Use EF Core migrations; do not use EnsureCreated. Keep connection credentials in environment variables.

Do NOT introduce:
- Docker
- web APIs
- message queues
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

- Usage: `IsotopeProbe <target> [-templatepath <path>]`.
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
