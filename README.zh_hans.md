# Seiko

[English](README.md) | [简体中文](README.zh_hans.md)

[![CI](https://github.com/Hosihikari/seiko/actions/workflows/ci.yml/badge.svg)](https://github.com/Hosihikari/seiko/actions/workflows/ci.yml)

Seiko 的安装侧包管理器实现。CLI 命令是 `sk`。

当前切片：

- `sk init`：生成 `seiko.conf` 模板。
- `sk sync`：读 `seiko.conf`，从本地静态索引解析 sei，按 `selectors` 选构件，校验 SHA-256，解包到内容寻址存储，投影到安装根，写 `seiko.lock.json`。
- `sk sync --frozen`：按 `seiko.lock.json` 回放，不重新解析；`--locked` 在锁过期时报错。
- `sk list`：读锁列出包。
- `sk rollback`：从内容寻址存储恢复上一代投影。
- `sk search <term>`：在配置的索引里搜索包名。
- `sk info <name>`：显示版本、依赖与构件数。
- 源可以是本地路径或 http(s) 索引；私有源用 `tokenEnv` 提供 bearer token。
- `seiko-mcp`：stdio 上的 MCP server；默认只读，`--allow-mutations` 才开放 `sync` 与 `rollback`。

尚未实现：硬链接/重排优化、公共索引仓。

## 构建与测试

```bash
dotnet build Seiko.slnx
dotnet test tests/Seiko.Core.Tests/Seiko.Core.Tests.csproj
dotnet publish src/Cli/Seiko.Cli.csproj -c Release -r win-x64 -p:PublishAot=true
```

## 布局

- `src/Core`：模型、配置、源、解析、存储、投影、锁。
- `src/Cli`：命令行。
- `src/Mcp`：MCP server 的占位项目。
- `tests/Seiko.Core.Tests`：fixture 与 sync 测试。

## 许可证

GPL-3.0，见 [LICENSE.md](LICENSE.md)。
