# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

Install-side package manager for the Seiko package format.

Current slice:

- `seiko init` — writes a `seiko.conf` template.
- `seiko sync` — reads `seiko.conf`, resolves seis from a local static index, selects an artifact by `selectors`, verifies SHA-256, extracts into the content-addressed store, projects files into the install root, and writes `seiko.lock.json`.
- `seiko list` — lists packages from the lock.

Not implemented yet: HTTP sources, `--frozen`/`--locked`, rollback, MCP, hardlink/reflink projection, and the public index repository.

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
