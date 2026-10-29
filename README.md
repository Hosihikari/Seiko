# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

[![CI](https://github.com/Hosihikari/seiko/actions/workflows/ci.yml/badge.svg)](https://github.com/Hosihikari/seiko/actions/workflows/ci.yml)

Install-side package manager for the Seiko package format. The CLI command is `sk`.

Current slice:

- `sk init` — writes a `seiko.conf` template.
- `sk sync` — reads `seiko.conf`, resolves seis from a local static index, selects an artifact by `selectors`, verifies SHA-256, extracts into the content-addressed store, projects files into the install root, and writes `seiko.lock.json`.
- `sk sync --frozen` — replays `seiko.lock.json` without resolving; `--locked` fails if the lock is out of date.
- `sk list` — lists packages from the lock.
- `sk rollback` — restores the previous generation from the content-addressed store.
- `sk search <term>` — searches package names in the configured indexes.
- `sk info <name>` — shows versions, dependencies, and artifact counts.

Not implemented yet: HTTP sources, MCP, hardlink/reflink projection, and the public index repository.

## Build and test

```bash
dotnet build Seiko.slnx
dotnet test tests/Seiko.Core.Tests/Seiko.Core.Tests.csproj
dotnet publish src/Cli/Seiko.Cli.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Layout

- `src/Core` — models, config, sources, resolution, store, projection, lock.
- `src/Cli` — command line.
- `src/Mcp` — placeholder MCP server project.
- `tests/Seiko.Core.Tests` — fixtures and sync tests.

## License

GPL-3.0. See [LICENSE.md](LICENSE.md).
