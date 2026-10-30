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

安装器选择最新稳定版，用 `SHA256SUMS` 校验，把 `sk` 与 `seiko-mcp` 装到 `~/.local/bin`（Unix）或 `%LOCALAPPDATA%\Seiko\bin`（Windows）。默认不改 `PATH`；传 `--modify-path`（PowerShell 为 `-ModifyPath`）才会追加。`--version vX.Y.Z` 钉版本，`--dir <path>` 换目录，`--dry-run` 只打印计划。

Linux 二进制要求 glibc（Ubuntu 22.04 及以上）；本版本不支持 musl/Alpine。二进制未做代码签名，浏览器手动下载可能触发 Gatekeeper 或 SmartScreen 警告；安装脚本直接使用 Release 资产。

校验下载的归档：

```bash
gh attestation verify sk-0.2.1-linux-x64.tar.gz --repo Hosihikari/Seiko
sha256sum -c SHA256SUMS --ignore-missing
```

升级即重跑安装器；卸载删除 `sk` 与 `seiko-mcp`（Windows 为 `.exe`），如曾加入 PATH 再删除对应目录。

当前切片：

- `sk init`：生成 `seiko.conf` 模板，默认写入官方索引源 `official`。
- `sk sync`：读 `seiko.conf`，从静态索引（本地或 HTTPS）解析 sei，按 `selectors` 选构件，校验 SHA-256，解包到内容寻址存储，部署到安装根，写 `seiko.lock.json`。
- `sk sync --frozen`：按 `seiko.lock.json` 回放，不重新解析；声明的直接集合必须与锁中的直接条目一致；`--locked` 在锁过期时报错；`sk install` 是别名。
- `sk add <name>[@range]` / `sk remove <name>` / `sk update [name] [--latest]`：修改直接声明并同步。
- `sk why <name>`：显示到某个 sei 的依赖链。
- `sk outdated`：显示范围内与整体的新版本。
- `sk list`：读锁列出已安装的 sei。
- `sk rollback`：从内容寻址存储恢复上一代部署。
- `sk search <term>`：在配置的索引里搜索 sei 名。
- `sk info <name>`：显示版本、依赖与构件数。
- 源可以是本地路径或 http(s) 索引；官方索引是 `https://raw.githubusercontent.com/Hosihikari/SeiIndex/main/index.json`；私有源用 `tokenEnv` 提供 bearer token。token 只发给索引同源地址与该源 `tokenHosts` 中列出的主机（https）；其他主机上的构件不会收到它。
- `sk index verify [path] [--check-artifacts] [--strict]`：校验索引目录的 schema、名称、版本、构件 URL 与 SHA-256；`--check-artifacts` 还会下载构件并校验字节与根级 `sei.json`，`--strict` 把警告也视为失败。
- 下载使用全局 HTTP 请求预算（`--jobs`，默认 4）、带退避的重试、大文件 Range 分块、`--proxy`，中断后可从 `<store>/.partial` 跨进程续传。
- `sk source add|remove|list`：管理源（`--token-env` 与可重复的 `--token-host`）；`sk config get|set rid`：读写平台覆盖。
- `sk cache status|verify|clean`：查看存储、校验构件，或清理所有已注册安装根当前锁与保留的回滚代都未引用的构件。
- `sk doctor [--clean]`：检查锁、存储、部署状态与残留 `.part`。
- `--json`：输出 `{schemaVersion, command, data}` 信封；错误也使用同一信封并带错误码。
- `-v/--verbose` 输出调试日志与展开堆栈；`-q/--quiet` 静默进度与日志输出。进度条只在交互式 stdout 显示。
- `seiko-mcp`：stdio 上的 MCP server；默认只读，`--allow-mutations` 才开放 `sync` 与 `rollback`。
- 载荷文件按文件系统能力部署：优先 reflink（APFS、btrfs、XFS），否则同卷硬链接，最后复制。存储为每个文件记录 SHA-256，`sk sync` 拒绝损坏的构件，`sk cache verify` 也会校验载荷。硬链接的载荷文件不能原地编辑，否则会同时改到存储。

## MCP server

`seiko-mcp` 通过 stdio 暴露同一组操作。默认只读；带 `--allow-mutations` 启动才会注册变更工具（`sync`、`rollback`、`add`、`remove`、`update`、源/配置/缓存修改）。每个变更调用仍需显式传入 `root`。`--root <path>` 设置默认安装根，缺省为当前目录。

Claude Code：

```bash
claude mcp add seiko -- seiko-mcp --root /path/to/project
```

Claude Desktop（`claude_desktop_config.json`）：

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

在 `args` 中加入 `"--allow-mutations"` 可启用变更工具。`seiko-mcp --help` 列出选项，`seiko-mcp --version` 打印构建版本。

## 官方索引

官方静态索引维护在 [Hosihikari/SeiIndex](https://github.com/Hosihikari/SeiIndex)。`sk init` 默认把它写成 `official` 源；删除或替换该条目即可改用其他索引。任何同布局的静态 HTTPS 索引或本地目录都可以作为源，私有源使用同一套机制。

## 构建与测试

```bash
dotnet build Seiko.slnx
dotnet test tests/Seiko.Tests/Seiko.Tests.csproj
dotnet publish src/CommandLine/Seiko.CommandLine.csproj -c Release -r win-x64 -p:PublishAot=true
dotnet publish src/McpServer/Seiko.McpServer.csproj -c Release -r win-x64 -p:PublishAot=true
```

## 布局

- `src/Seiko`：模型、配置、源、解析、存储、部署、锁。
- `src/CommandLine`：命令行。
- `src/McpServer`：stdio 上的 MCP server。
- `tests/Seiko.Tests`：fixture 与 sync 测试。

## 许可证

GPL-3.0，见 [LICENSE.md](LICENSE.md)。
