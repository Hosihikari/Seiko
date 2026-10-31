#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"

cat > "$tmp/bin/uname" <<'EOF'
#!/bin/sh
case "${1:-}" in
  -s) echo Linux ;;
  -m) printf '%s\n' "${MOCK_MACHINE:?}" ;;
  *) echo Linux ;;
esac
EOF

cat > "$tmp/bin/ldd" <<'EOF'
#!/bin/sh
printf '%s\n' "${MOCK_LDD:-ldd (Ubuntu GLIBC 2.35-0ubuntu3) 2.35}"
EOF

cat > "$tmp/bin/curl" <<'EOF'
#!/bin/sh
out=""
prev=""
for arg in "$@"; do
  if [ "$prev" = "-o" ]; then
    out="$arg"
  fi
  prev="$arg"
done
if [ -n "$out" ]; then
  for rid in linux-arm linux-arm64 linux-x64 linux-musl-x64; do
    echo "0000000000000000000000000000000000000000000000000000000000000000  sk-0.0.0-$rid.tar.gz"
  done > "$out"
fi
EOF

chmod +x "$tmp/bin/uname" "$tmp/bin/ldd" "$tmp/bin/curl"

run() {
  machine=$1
  libc=$2
  PATH="$tmp/bin:$PATH" MOCK_MACHINE="$machine" MOCK_LDD="$libc" \
    sh "$root/install.sh" --version v0.0.0 --base-url http://127.0.0.1:1 --dry-run 2>&1
}

fail() {
  echo "installer-detect: $*" >&2
  exit 1
}

if out=$(run armv6l "ldd (Ubuntu GLIBC 2.35) 2.35" 2>/dev/null); then
  fail "armv6l was accepted"
else
  printf '%s\n' "$out" | grep -q "armv6" || fail "armv6l rejection did not mention armv6"
fi

if out=$(run armv7l "ldd (Ubuntu GLIBC 2.35) 2.35"); then
  printf '%s\n' "$out" | grep -q "rid: linux-arm" || fail "armv7l did not select linux-arm"
else
  fail "armv7l was rejected: $out"
fi

if out=$(run armv8l "ldd (Ubuntu GLIBC 2.35) 2.35"); then
  printf '%s\n' "$out" | grep -q "rid: linux-arm" || fail "armv8l did not select linux-arm"
else
  fail "armv8l was rejected: $out"
fi

if out=$(run armv7l "musl libc (armv7l)" 2>/dev/null); then
  fail "musl arm32 was accepted"
else
  printf '%s\n' "$out" | grep -q "musl" || fail "musl arm32 rejection did not mention musl"
fi

echo "installer-detect: ok"
