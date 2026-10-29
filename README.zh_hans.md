# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

[![CI](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml/badge.svg)](https://github.com/Hosihikari/Seiko/actions/workflows/ci.yml)

Seiko 是星光的安装侧工具，与星光框架、星光投影并列。**Seiko** 是产品名，`sei`（星）是单位名——一个包就是一个 sei，英文单复数都写作 `sei`。

Seiko 的安装侧包管理器实现。CLI 命令是 `sk`，即 Seiko 的缩写。

当前切片：

- `sk init`：生成 `seiko.conf` 模板。
- `sk sync`：读 `seiko.conf`，从本地静态索引解析 sei，按 `selectors` 选构件，校验 SHA-256，解包到内容寻址存储，部署到安装根，写 `seiko.lock.json`。
- `sk sync --frozen`：按 `seiko.lock.json` 回放，不重新解析；`--locked` 在锁过期时报错。
- `sk list`：读锁列出已安装的 sei。
- `sk rollback`：从内容寻址存储恢复上一代部署。
- `sk search <term>`：在配置的索引里搜索 sei 名。
- `sk info <name>`：显示版本、依赖与构件数。
- 源可以是本地路径或 http(s) 索引；私有源用 `tokenEnv` 提供 bearer token。
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
