# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

[![CI](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml/badge.svg)](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml)

Seiko is a general-purpose package manager under Hosihikari: any project can use it to distribute sei. **Seiko** is the product name; `sei` (星) is the unit name — one package is a sei, and the English plural is also `sei`. The CLI command is `sk`, short for Seiko.

## Install

Linux and macOS:

```bash
curl -fsSL https://github.com/Hosihikari/Seiko/releases/latest/download/install.sh | sh
```

Windows PowerShell:

```powershell
irm https://github.com/Hosihikari/Seiko/releases/latest/download/install.ps1 | iex
```

The installer selects the latest stable release, verifies it against `SHA256SUMS`, and installs to `~/.local/bin` on Unix or `%LOCALAPPDATA%\Seiko\bin` on Windows. It does not modify `PATH` unless `--modify-path` (or `-ModifyPath`) is passed. Use `--version vX.Y.Z` to pin a release, `--dir <path>` for another directory, and `--dry-run` to inspect the plan.

Linux binaries require glibc (Ubuntu 22.04 or newer); musl/Alpine is not supported in this release. The binaries are not code-signed, so a browser download may trigger Gatekeeper or SmartScreen warnings; the installer uses the release assets directly.

Verify a downloaded archive:

```bash
gh attestation verify sk-0.1.0-linux-x64.tar.gz --repo Hosihikari/Seiko
sha256sum -c SHA256SUMS --ignore-missing
```

Upgrade by re-running the installer; uninstall by deleting `sk` (or `sk.exe`) and removing the install directory from `PATH` if it was added.

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
