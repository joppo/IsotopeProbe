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

## Architecture

This is a simple .NET console application with PostgreSQL persistence.
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

## Specs

The location is ~/z/p/nuclei/isotopeprobe
The console app should be created in here with that name - IsotopeProbe
