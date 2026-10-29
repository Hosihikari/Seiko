# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

[![CI](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml/badge.svg)](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml)

Seiko 是星光旗下的通用包管理器：任何项目都可以用它分发 sei。**Seiko** 是产品名，`sei`（星）是单位名——一个包就是一个 sei，英文单复数都写作 `sei`。CLI 命令是 `sk`，即 Seiko 的缩写。

## 安装

Linux 和 macOS：

```bash
curl -fsSL https://github.com/Hosihikari/Seiko/releases/latest/download/install.sh | sh
```

Windows PowerShell：

```powershell
irm https://github.com/Hosihikari/Seiko/releases/latest/download/install.ps1 | iex
```

安装器选择最新稳定版，用 `SHA256SUMS` 校验，Unix 装到 `~/.local/bin`，Windows 装到 `%LOCALAPPDATA%\Seiko\bin`。默认不改 `PATH`；传 `--modify-path`（PowerShell 为 `-ModifyPath`）才会追加。`--version vX.Y.Z` 钉版本，`--dir <path>` 换目录，`--dry-run` 只打印计划。

Linux 二进制要求 glibc（Ubuntu 22.04 及以上）；本版本不支持 musl/Alpine。二进制未做代码签名，浏览器手动下载可能触发 Gatekeeper 或 SmartScreen 警告；安装脚本直接使用 Release 资产。

校验下载的归档：

```bash
gh attestation verify sk-0.1.2-linux-x64.tar.gz --repo Hosihikari/Seiko
sha256sum -c SHA256SUMS --ignore-missing
```

升级即重跑安装器；卸载删除 `sk`（Windows 为 `sk.exe`），如曾加入 PATH 再删除对应目录。

当前切片：

- `sk init`：生成 `seiko.conf` 模板。
- `sk sync`：读 `seiko.conf`，从本地静态索引解析 sei，按 `selectors` 选构件，校验 SHA-256，解包到内容寻址存储，部署到安装根，写 `seiko.lock.json`。
- `sk sync --frozen`：按 `seiko.lock.json` 回放，不重新解析；`--locked` 在锁过期时报错；`sk install` 是别名。
- `sk add <name>[@range]` / `sk remove <name>` / `sk update [name] [--latest]`：修改直接声明并同步。
- `sk why <name>`：显示到某个 sei 的依赖链。
- `sk outdated`：显示范围内与整体的新版本。
- `sk list`：读锁列出已安装的 sei。
- `sk rollback`：从内容寻址存储恢复上一代部署。
- `sk search <term>`：在配置的索引里搜索 sei 名。
- `sk info <name>`：显示版本、依赖与构件数。
- 源可以是本地路径或 http(s) 索引；私有源用 `tokenEnv` 提供 bearer token。
- 下载使用全局 HTTP 请求预算（`--jobs`，默认 4）、带退避的重试、大文件 Range 分块、`--proxy`，中断后可从 `<store>/.partial` 跨进程续传。
- `sk doctor [--clean]`：检查锁、存储、部署状态与残留 `.part`。
- `--json`：输出 `{schemaVersion, command, data}` 信封；错误也使用同一信封并带错误码。
- `-v/--verbose` 输出调试日志与展开堆栈；`-q/--quiet` 静默进度与日志输出。进度条只在交互式 stdout 显示。
- `seiko-mcp`：stdio 上的 MCP server；默认只读，`--allow-mutations` 才开放 `sync` 与 `rollback`。

尚未实现：硬链接/重排优化、公共索引仓。

## 构建与测试

```bash
dotnet build Seiko.slnx
dotnet test tests/Seiko.Tests/Seiko.Tests.csproj
dotnet publish src/CommandLine/Seiko.CommandLine.csproj -c Release -r win-x64 -p:PublishAot=true
```

## 布局

- `src/Seiko`：模型、配置、源、解析、存储、部署、锁。
- `src/CommandLine`：命令行。
- `src/McpServer`：stdio 上的 MCP server。
- `tests/Seiko.Tests`：fixture 与 sync 测试。

## 许可证

GPL-3.0，见 [LICENSE.md](LICENSE.md)。
