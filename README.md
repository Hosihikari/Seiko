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

The installer selects the latest stable release, verifies it against `SHA256SUMS`, and installs `sk` and `seiko-mcp` to `~/.local/bin` on Unix or `%LOCALAPPDATA%\Seiko\bin` on Windows. It does not modify `PATH` unless `--modify-path` (or `-ModifyPath`) is passed. Use `--version vX.Y.Z` to pin a release, `--dir <path>` for another directory, and `--dry-run` to inspect the plan.

Linux binaries require glibc (Ubuntu 22.04 or newer); musl/Alpine is not supported in this release. The binaries are not code-signed, so a browser download may trigger Gatekeeper or SmartScreen warnings; the installer uses the release assets directly.

Verify a downloaded archive:

```bash
gh attestation verify sk-0.2.1-linux-x64.tar.gz --repo Hosihikari/Seiko
sha256sum -c SHA256SUMS --ignore-missing
```

Releases before 0.2.1 contain only `sk`; pinning one with `--version` installs the CLI without `seiko-mcp`. Upgrade by re-running the installer; uninstall by deleting `sk` and `seiko-mcp` (or `sk.exe` and `seiko-mcp.exe`) and removing the install directory from `PATH` if it was added.

Current slice:

- `sk init` — writes a `seiko.conf` template with the official index as source `official`.
- `sk sync` — reads `seiko.conf`, resolves sei from a static index (local or HTTPS), selects an artifact by `selectors`, verifies SHA-256, extracts into the content-addressed store, deploys files into the install root, and writes `seiko.lock.json`.
- `sk sync --frozen` — replays `seiko.lock.json` without resolving; the declared direct set must match the lock's direct entries; `--locked` fails if the lock is out of date; `sk install` is an alias.
- `sk add <name>[@range]` / `sk remove <name>` / `sk update [name] [--latest]` — edit direct declarations and sync. `sync`, `add`, and `update` accept `--latest-installable` to skip versions with no artifact for the resolved graph; the default remains the highest version.
- `sk why <name>` — shows the dependency chains to a sei.
- `sk outdated` — shows newer in-range, overall, and latest installable versions.
- `sk list` — lists installed sei from the lock.
- `sk rollback [--to <n>] [--proxy <url>]` — restores the previous deployment generation from the content-addressed store; missing target artifacts are downloaded again.
- `sk search <term>` — searches sei names in the configured indexes.
- `sk info <name>` — shows versions, dependencies, and artifact counts.
- Sources may be a local path or an http(s) index; the official index is `https://raw.githubusercontent.com/Hosihikari/SeiIndex/main/index.json`. Private sources use host-keyed bearer tokens: set `SEIKO_TOKEN_<HOST>` (uppercase host, non-alphanumeric characters become `_`), for example `SEIKO_TOKEN_PACKAGES_EXAMPLE_COM`. Tokens are only sent over HTTPS to the index host or to hosts listed in the source's `tokenHosts`; project configuration cannot name an arbitrary environment variable.
- `sk index verify [path] [--check-artifacts] [--strict]` — validates an index directory: schema, names, versions, artifact URLs, SHA-256, and with `--check-artifacts` the artifact bytes and the embedded `sei.json`; `--strict` also fails on warnings.
- Downloads use a global HTTP request budget (`--jobs`, default 4), retries with backoff, Range chunking for large artifacts, `--proxy`, and cross-process resume from `<store>/.partial`.
- `sk source add|remove|list` — manage sources (`--token-env` and repeated `--token-host`); `sk config get|set rid` — read or write the platform override.
- `sk cache status|verify|clean` — inspect the store, verify artifacts, or remove artifacts not referenced by any registered root's current lock or its retained rollback generations. Sync and rollback re-download artifacts whose digest metadata or payload is missing or corrupt.
- `sk doctor [--clean]` — checks the lock, store, deployment, and residual partial downloads.
- `--json` — emits a `{schemaVersion, command, data}` envelope for machine use; errors use the same envelope with an error code.
- `-v/--verbose` adds debug logs and demystified stack traces; `-q/--quiet` suppresses progress and log output. Progress bars appear only on an interactive stdout.
- `seiko-mcp` — MCP server over stdio; read-only tools by default, `--allow-mutations` adds the mutating tools (`sync`, `rollback`, `add`, `remove`, `update`, source/config/cache changes). `--allowed-root` and `--allowed-store` optionally restrict mutation paths.
- Payload files are deployed by reflink when the filesystem supports it (APFS, btrfs, XFS), otherwise by hardlink on the same volume, otherwise by copy. The store records a SHA-256 for every file, so a corrupt cached artifact is downloaded again and `sk cache verify` checks the payload too. Hardlinked payload files must not be edited in place; editing them also edits the store.
- Unix modes are limited to `0000–0777`: setuid, setgid, and sticky bits are rejected by deployment and `sk index verify`. Extraction is capped at 100,000 entries, 1 GiB per entry, and 4 GiB total.
- Payload paths must be unique ignoring case. A deployment that would write both `A.txt` and `a.txt` is rejected, so roots stay consistent across case-sensitive and case-insensitive volumes.

## MCP server

`seiko-mcp` exposes the same operations over stdio. It is read-only by default; start it with `--allow-mutations` to register the mutating tools (`sync`, `rollback`, `add`, `remove`, `update`, source/config/cache changes). Every mutating call still takes an explicit `root` argument. `--root <path>` sets the default install root; it defaults to the current directory.

Claude Code:

```bash
claude mcp add seiko -- seiko-mcp --root /path/to/project
```

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "seiko": {
      "command": "seiko-mcp",
      "args": ["--root", "/path/to/project"]
    }
  }
}
```

Add `"--allow-mutations"` to `args` to enable mutation tools. `seiko-mcp --help` lists the options and `seiko-mcp --version` prints the build.

## Official index

The official static index is maintained in [Hosihikari/SeiIndex](https://github.com/Hosihikari/SeiIndex). `sk init` writes it as the `official` source; remove or replace that entry to use another index. Any static HTTPS index or local directory with the same layout works, and private sources use the same mechanism.

## Build and test

```bash
dotnet build Seiko.slnx
dotnet test tests/Seiko.Tests/Seiko.Tests.csproj
dotnet publish src/CommandLine/Seiko.CommandLine.csproj -c Release -r win-x64 -p:PublishAot=true
dotnet publish src/McpServer/Seiko.McpServer.csproj -c Release -r win-x64 -p:PublishAot=true
```

## Layout

- `src/Seiko` — models, config, sources, resolution, store, deployment, lock.
- `src/CommandLine` — command line.
- `src/McpServer` — MCP server over stdio.
- `tests/Seiko.Tests` — fixtures and sync tests.

## License

GPL-3.0. See [LICENSE.md](LICENSE.md).
