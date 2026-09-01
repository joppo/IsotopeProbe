# Project

This is a PoC for orchestrating Nuclei vulnerability scans.

## Current goal

Build the smallest possible vertical slice:

1. .NET console application starts Nuclei.
2. Nuclei scans a supplied target.
3. Nuclei outputs JSONL to stdout.
4. Application reads stdout line-by-line.
5. JSONL findings are deserialized into C# objects.
6. Results are printed to the console.

## Architecture

For now this is a simple .NET console application.

Do NOT introduce:
- Docker
- PostgreSQL
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
