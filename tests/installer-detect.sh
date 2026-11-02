#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
TMPDIR=${TMPDIR:-/tmp}
export TMPDIR
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin" "$tmp/archive"
printf '#!/bin/sh\n' > "$tmp/archive/sk"
printf '#!/bin/sh\n' > "$tmp/archive/seiko-mcp"
(cd "$tmp" && tar -czf sk-0.0.0-linux-x64.tar.gz -C archive sk seiko-mcp)
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$tmp" && sha256sum sk-0.0.0-linux-x64.tar.gz > SHA256SUMS)
else
  (cd "$tmp" && shasum -a 256 sk-0.0.0-linux-x64.tar.gz > SHA256SUMS)
fi
for rid in linux-arm linux-arm64 linux-musl-x64; do
  echo "0000000000000000000000000000000000000000000000000000000000000000  sk-0.0.0-$rid.tar.gz" \
    >> "$tmp/SHA256SUMS"
done

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
[ -n "$out" ] || exit 0
case "$out" in
  *SHA256SUMS) cp "$MOCK_SUMS" "$out" ;;
  *.tar.gz) cp "$MOCK_ARCHIVE" "$out" ;;
  *) : > "$out" ;;
esac
EOF

chmod +x "$tmp/bin/uname" "$tmp/bin/ldd" "$tmp/bin/curl"

run() {
  machine=$1
  libc=$2
  PATH="$tmp/bin:$PATH" MOCK_MACHINE="$machine" MOCK_LDD="$libc" \
    MOCK_SUMS="$tmp/SHA256SUMS" MOCK_ARCHIVE="$tmp/sk-0.0.0-linux-x64.tar.gz" \
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

for name in sk seiko-mcp; do
  dir="$tmp/dir-target-$name"
  mkdir -p "$dir/$name"
  if out=$(PATH="$tmp/bin:$PATH" MOCK_MACHINE=x86_64 MOCK_LDD="ldd (Ubuntu GLIBC 2.35) 2.35" \
      sh "$root/install.sh" --version v0.0.0 --base-url http://127.0.0.1:1 --dir "$dir" --dry-run 2>&1); then
    fail "a directory $name target was accepted"
  else
    printf '%s\n' "$out" | grep -q "$name" || fail "directory $name rejection did not name the target"
  fi
done

case "$(uname -s)" in
  Linux|Darwin)
    special="$tmp/odd dir/it's-\$dollar-\`tick;semi(paren)"
    mkdir -p "$special"

    install_special() {
      home=$1
      shell=$2
      mkdir -p "$home"
      PATH="$tmp/bin:$PATH" MOCK_MACHINE=x86_64 MOCK_LDD="ldd (Ubuntu GLIBC 2.35) 2.35" \
        MOCK_SUMS="$tmp/SHA256SUMS" MOCK_ARCHIVE="$tmp/sk-0.0.0-linux-x64.tar.gz" \
        HOME="$home" SHELL="$shell" \
        sh "$root/install.sh" --version v0.0.0 --base-url http://127.0.0.1:1 \
          --dir "$special" --modify-path >"$tmp/install.out" 2>&1 \
        || fail "install with special characters failed: $(cat "$tmp/install.out")"
    }

    bash_home="$tmp/home-bash"
    install_special "$bash_home" /bin/bash
    [ -f "$bash_home/.bashrc" ] || fail "installer did not update .bashrc"
    got=$(HOME="$bash_home" PATH="/usr/bin:/bin" bash -c \
      '. "$HOME/.bashrc"; printf "%s" "${PATH%%:*}"')
    [ "$got" = "$special" ] || fail "bash PATH did not round-trip: $got"

    fish_home="$tmp/home-fish"
    install_special "$fish_home" /usr/bin/fish
    fish_config="$fish_home/.config/fish/config.fish"
    [ -f "$fish_config" ] || fail "installer did not update fish config"
    grep -Fq "fish_add_path '" "$fish_config" || fail "fish PATH line is not single-quoted"
    grep -Fq "it\\'s" "$fish_config" || fail "fish single quote was not escaped"
    ;;
  *)
    printf 'installer-detect: special-path install skipped on %s\n' "$(uname -s)" >&2
    ;;
esac

echo "installer-detect: ok"
