#!/bin/sh
# Installs Custos for the current user, or removes it. Needs no root rights.
#
#   sh install.sh               Download the AppImage of this release and install it.
#   sh install.sh <AppImage>    Install this AppImage.
#   sh install.sh --uninstall   Remove Custos. The configuration and the tokens stay.
#
# The AppImage goes to ~/.local/bin/custos. A second run replaces it. The launcher entry and the
# icons go to $XDG_DATA_HOME (default: ~/.local/share).
set -eu

# The release workflow writes the version and the SHA-256 digest of its AppImage here.
VERSION="@VERSION@"
SHA256="@SHA256@"
REPOSITORY="atlashq-gg/custos"

BIN_DIR="$HOME/.local/bin"
TARGET="$BIN_DIR/custos"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
APPLICATIONS="$DATA_DIR/applications"
ICONS="$DATA_DIR/icons/hicolor"
# The name of the entry is the application ID (the identifier in tauri.conf.json). The
# GlobalShortcuts portal registers Custos by this ID, and refuses an ID without an entry.
ENTRY="$APPLICATIONS/gg.atlashq.custos.desktop"
# The name of the entry of the older versions.
OLD_ENTRY="$APPLICATIONS/custos.desktop"
# Custos writes this file at each start. It makes Custos the handler of the login link custos://.
HANDLER="$APPLICATIONS/custos-handler.desktop"

fail() {
    printf 'install.sh: %s\n' "$1" >&2
    exit 1
}

usage() {
    cat <<EOF
Usage:
  sh install.sh               Download the AppImage of Custos $VERSION and install it.
  sh install.sh <AppImage>    Install this AppImage.
  sh install.sh --uninstall   Remove Custos. The configuration and the tokens stay.
EOF
}

update_database() {
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$APPLICATIONS" || true
    fi
}

uninstall() {
    rm -f "$TARGET" "$ENTRY" "$OLD_ENTRY" "$HANDLER" "$ICONS"/*/apps/custos.png
    update_database
    echo "Removed Custos. The configuration in ~/.config/custos and the tokens stay."
}

# Downloads the AppImage of this release to $1, and makes sure that its digest is correct.
download() {
    case "$VERSION" in
        @*) fail "this copy of the script has no version. Give the path of an AppImage." ;;
    esac
    url="https://github.com/$REPOSITORY/releases/download/custos-$VERSION/Custos_${VERSION}_amd64.AppImage"
    echo "Downloading $url"
    if command -v curl >/dev/null 2>&1; then
        curl --fail --location --proto '=https' --output "$1" "$url"
    elif command -v wget >/dev/null 2>&1; then
        wget --output-document "$1" "$url"
    else
        fail "cannot download the AppImage. Install curl or wget, or give the path of an AppImage."
    fi
    digest=$(sha256sum "$1" | cut -d ' ' -f 1)
    [ "$digest" = "$SHA256" ] || fail "the SHA-256 digest of the download is $digest, not $SHA256."
}

install_custos() {
    [ "$(uname -m)" = x86_64 ] || fail "Custos runs only on x86_64."
    mkdir -p "$BIN_DIR" "$APPLICATIONS"
    # A temporary file in the target folder, so that one rename replaces the old file. A Custos that
    # runs continues with the old file.
    new=$(mktemp "$BIN_DIR/.custos.XXXXXX")
    work=$(mktemp -d)
    trap 'rm -rf "$new" "$work"' EXIT
    trap 'exit 1' INT TERM
    if [ -n "${1:-}" ]; then
        cp "$1" "$new"
    else
        download "$new"
    fi
    chmod 755 "$new"

    # The launcher entry and the icons come from the AppImage. The extraction needs no FUSE.
    (
        cd "$work"
        "$new" --appimage-extract 'usr/share/applications/*.desktop' >/dev/null
        "$new" --appimage-extract 'usr/share/icons' >/dev/null
    )
    set -- "$work"/squashfs-root/usr/share/applications/*.desktop
    [ -f "$1" ] || fail "the AppImage has no desktop entry."
    # The entry starts the installed file. It has no MimeType: the handler of custos:// is the file
    # that Custos writes at each start, with the same path.
    sed -e "s|^Exec=.*|Exec=\"$TARGET\"|" -e '/^MimeType=/d' "$1" >"$ENTRY"
    rm -f "$OLD_ENTRY"
    for icon in "$work"/squashfs-root/usr/share/icons/hicolor/*/apps/custos.png; do
        [ -f "$icon" ] || continue
        size=$(basename "$(dirname "$(dirname "$icon")")")
        mkdir -p "$ICONS/$size/apps"
        cp "$icon" "$ICONS/$size/apps/custos.png"
    done

    mv -f "$new" "$TARGET"
    update_database
    echo "Installed Custos in $TARGET. If Custos runs, quit it and start it again."
}

case "${1:-}" in
    -h | --help) usage ;;
    --uninstall) uninstall ;;
    -*) usage >&2 && exit 2 ;;
    *) install_custos "${1:-}" ;;
esac
