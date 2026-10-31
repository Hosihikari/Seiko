#!/bin/sh
set -eu

REPO="Hosihikari/Seiko"
VERSION=""
INSTALL_DIR=""
MODIFY_PATH=0
DRY_RUN=0
BASE_URL=""

usage() {
    cat <<'EOF'
Install sk and seiko-mcp (Seiko) from GitHub Releases.

Usage: install.sh [options]

Options:
  --version <tag>    Install a specific version (for example v0.1.0).
  --dir <path>       Install directory (default: ~/.local/bin).
  --modify-path      Append the install directory to the login shell profile.
  --dry-run          Resolve the release and checksum without installing.
  --base-url <url>   Use a custom release asset base URL (advanced).
  -h, --help         Show this help.

Linux (glibc or musl) and macOS are supported.
EOF
}

die() {
    printf 'sk-install: %s\n' "$*" >&2
    exit 1
}

need() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version)
            [ $# -ge 2 ] || die "--version requires a value"
            VERSION="$2"
            shift 2
            ;;
        --dir)
            [ $# -ge 2 ] || die "--dir requires a value"
            INSTALL_DIR="$2"
            shift 2
            ;;
        --modify-path)
            MODIFY_PATH=1
            shift
            ;;
        --dry-run)
            DRY_RUN=1
            shift
            ;;
        --base-url)
            [ $# -ge 2 ] || die "--base-url requires a value"
            BASE_URL="${2%/}"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            die "unknown option: $1"
            ;;
    esac
done

need curl
need tar
need awk
need grep

uname_s=$(uname -s)
uname_m=$(uname -m)

case "$uname_s" in
    Linux)
        case "$uname_m" in
            x86_64|amd64) arch=x64 ;;
            aarch64|arm64) arch=arm64 ;;
            armv7l|armv7|armv6l|armv8l|arm) arch=arm ;;
            *) die "unsupported Linux architecture: $uname_m" ;;
        esac
        if ls /lib/ld-musl-*.so.1 >/dev/null 2>&1 || ldd --version 2>&1 | grep -qi musl; then
            RID="linux-musl-$arch"
        else
            RID="linux-$arch"
        fi
        ;;
    Darwin)
        case "$uname_m" in
            arm64) RID=osx-arm64 ;;
            x86_64) RID=osx-x64 ;;
            *) die "unsupported macOS architecture: $uname_m" ;;
        esac
        ;;
    *)
        die "unsupported operating system: $uname_s; on Windows use install.ps1"
        ;;
esac

if [ -n "$BASE_URL" ] && [ -z "$VERSION" ]; then
    die "--base-url requires --version"
fi

if [ -z "$VERSION" ]; then
    latest_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest") \
        || die "could not resolve the latest release"
    TAG=${latest_url##*/}
    [ -n "$TAG" ] || die "could not resolve the latest release tag"
else
    case "$VERSION" in
        v*) TAG="$VERSION" ;;
        *) TAG="v$VERSION" ;;
    esac
fi

case "$TAG" in
    v[0-9]*) ;;
    *) die "invalid version tag: $TAG" ;;
esac
VERSION=${TAG#v}

if [ -z "$BASE_URL" ]; then
    BASE_URL="https://github.com/$REPO/releases/download/$TAG"
fi

ASSET="sk-$VERSION-$RID.tar.gz"
ASSET_URL="$BASE_URL/$ASSET"
SUMS_URL="$BASE_URL/SHA256SUMS"

if [ -z "$INSTALL_DIR" ]; then
    INSTALL_DIR="$HOME/.local/bin"
fi
TARGET="$INSTALL_DIR/sk"
MCP_TARGET="$INSTALL_DIR/seiko-mcp"

tmp_dir=$(mktemp -d)
cleanup() {
    rm -rf "$tmp_dir"
}
trap cleanup EXIT

detect_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        SHA256_CMD="sha256sum"
    elif command -v shasum >/dev/null 2>&1; then
        SHA256_CMD="shasum -a 256"
    else
        die "need sha256sum or shasum"
    fi
}
detect_sha256

sums_file="$tmp_dir/SHA256SUMS"
curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 15 "$SUMS_URL" -o "$sums_file" \
    || die "could not download SHA256SUMS from $SUMS_URL"
expected=$(awk -v f="$ASSET" '$2 == f || $2 == "*" f { print $1 }' "$sums_file" | head -n 1)
[ -n "$expected" ] || die "SHA256SUMS does not contain $ASSET"

if [ "$DRY_RUN" -eq 1 ]; then
    printf 'version: %s\nrid: %s\nasset: %s\nurl: %s\ntarget: %s\nmcp-target: %s\nsha256: %s\n' \
        "$VERSION" "$RID" "$ASSET" "$ASSET_URL" "$TARGET" "$MCP_TARGET" "$expected"
    exit 0
fi

archive="$tmp_dir/$ASSET"
curl -fL --retry 3 --retry-delay 2 --connect-timeout 15 "$ASSET_URL" -o "$archive" \
    || die "could not download $ASSET_URL"

actual=$($SHA256_CMD "$archive" | awk '{ print $1 }')
case "$actual" in
    "$expected"|"$(printf '%s' "$expected" | tr 'A-F' 'a-f')") ;;
    *) die "checksum mismatch for $ASSET" ;;
esac

tar -xzf "$archive" -C "$tmp_dir"
[ -f "$tmp_dir/sk" ] || die "archive does not contain sk"
[ -f "$tmp_dir/seiko-mcp" ] || die "archive does not contain seiko-mcp"

install_binary() {
    name="$1"
    tmp_target="$INSTALL_DIR/.$name.tmp.$$"
    cp "$tmp_dir/$name" "$tmp_target"
    chmod 755 "$tmp_target"
    mv -f "$tmp_target" "$INSTALL_DIR/$name"
}

mkdir -p "$INSTALL_DIR"
install_binary sk
printf 'installed sk %s (%s) to %s\n' "$VERSION" "$RID" "$TARGET"
install_binary seiko-mcp
printf 'installed seiko-mcp %s (%s) to %s\n' "$VERSION" "$RID" "$MCP_TARGET"

case ":$PATH:" in
    *":$INSTALL_DIR:"*) ;;
    *)
        if [ "$MODIFY_PATH" -eq 1 ]; then
            shell_name=$(basename "${SHELL:-}")
            case "$shell_name" in
                zsh) profile="$HOME/.zshrc" ;;
                bash) profile="$HOME/.bashrc" ;;
                fish)
                    mkdir -p "$HOME/.config/fish"
                    profile="$HOME/.config/fish/config.fish"
                    ;;
                *) profile="" ;;
            esac
            if [ "$shell_name" = fish ]; then
                managed_line="fish_add_path \"$INSTALL_DIR\""
            else
                managed_line="export PATH=\"$INSTALL_DIR:\$PATH\""
            fi

            if [ -n "$profile" ] && ! grep -Fxq "$managed_line" "$profile" 2>/dev/null; then
                printf '\n%s\n' "$managed_line" >> "$profile"
                printf 'updated %s; restart the shell or source it\n' "$profile"
            elif [ -z "$profile" ]; then
                printf 'could not detect the login shell; add this to PATH manually:\n  export PATH="%s:$PATH"\n' "$INSTALL_DIR"
            fi
        else
            printf 'add this to PATH if needed:\n  export PATH="%s:$PATH"\n' "$INSTALL_DIR"
        fi
        ;;
esac
