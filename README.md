# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

[![CI](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml/badge.svg)](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml)

Seiko is a general-purpose package manager under Hosihikari: any project can use it to distribute sei. **Seiko** is the product name; `sei` (星) is the unit name — one package is a sei, and the English plural is also `sei`. The CLI command is `sk`, short for Seiko.

Current slice:

- `sk init` — writes a `seiko.conf` template.
- `sk sync` — reads `seiko.conf`, resolves sei from a local static index, selects an artifact by `selectors`, verifies SHA-256, extracts into the content-addressed store, deploys files into the install root, and writes `seiko.lock.json`.
- `sk sync --frozen` — replays `seiko.lock.json` without resolving; `--locked` fails if the lock is out of date.
- `sk list` — lists installed sei from the lock.
- `sk rollback` — restores the previous deployment generation from the content-addressed store.
- `sk search <term>` — searches sei names in the configured indexes.
- `sk info <name>` — shows versions, dependencies, and artifact counts.
- Sources may be a local path or an http(s) index; `tokenEnv` supplies a bearer token for private sources.
- `seiko-mcp` — MCP server over stdio; read-only tools by default, `--allow-mutations` adds `sync` and `rollback`.

Not implemented yet: hardlink/reflink deployment and the public index repository.

## Build and test

```bash
dotnet build Seiko.slnx
dotnet test tests/Seiko.Tests/Seiko.Tests.csproj
dotnet publish src/CommandLine/Seiko.CommandLine.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Layout

- `src/Seiko` — models, config, sources, resolution, store, deployment, lock.
- `src/CommandLine` — command line.
- `src/McpServer` — MCP server over stdio.
- `tests/Seiko.Tests` — fixtures and sync tests.

## License

GPL-3.0. See [LICENSE.md](LICENSE.md).
