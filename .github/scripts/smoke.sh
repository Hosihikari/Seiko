#!/usr/bin/env bash
set -euo pipefail

sk=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
mcp=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
run_prefix="${3:-}"

run() {
    if [ -n "$run_prefix" ]; then
        # shellcheck disable=SC2086
        $run_prefix "$@"
    else
        "$@"
    fi
}

run "$sk" --version
run "$sk" --help >/dev/null
run "$mcp" --version
run "$mcp" --help >/dev/null

root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
cat > "$root/seiko.lock.json" <<'JSON'
{
  "schemaVersion": 1,
  "target": { "rid": "smoke" },
  "sources": ["official"],
  "sei": [
    {
      "name": "smoke",
      "version": "1.0.0",
      "source": "official",
      "direct": true,
      "artifact": { "sha256": "", "selectors": null },
      "dependencies": []
    }
  ]
}
JSON
out=$(cd "$root" && printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list","arguments":{}}}' \
    | run "$mcp" --allow-mutations)
printf '%s\n' "$out" | grep -q '"name":"seiko-mcp"'
printf '%s\n' "$out" | grep -q '"rollback"'
printf '%s\n' "$out" | grep -q 'smoke'
