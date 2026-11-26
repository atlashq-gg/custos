#!/bin/sh
# Tests install.sh with a temporary home folder.
#
#   sh test-install.sh              Test with a fake AppImage.
#   sh test-install.sh <AppImage>   Test with this AppImage.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
home=$(mktemp -d)
trap 'rm -rf "$home"' EXIT
export HOME="$home"
unset XDG_DATA_HOME
bin="$home/.local/bin/custos"
data="$home/.local/share"
entry="$data/applications/gg.atlashq.custos.desktop"
old_entry="$data/applications/custos.desktop"

fail() {
    printf 'FAIL: %s\n' "$1" >&2
    exit 1
}

appimage=${1:-}
if [ -z "$appimage" ]; then
    # A fake AppImage. It extracts the files of a Tauri AppImage that install.sh reads.
    appimage="$home/Custos_fake_amd64.AppImage"
    cat >"$appimage" <<'FAKE'
#!/bin/sh
[ "$1" = --appimage-extract ] || exit 1
root=squashfs-root/usr/share
mkdir -p "$root/applications" "$root/icons/hicolor/32x32/apps" "$root/icons/hicolor/128x128/apps"
printf '[Desktop Entry]\nType=Application\nName=Custos\nExec=custos\nIcon=custos\nMimeType=x-scheme-handler/custos\n' \
    >"$root/applications/Custos.desktop"
printf 'icon' >"$root/icons/hicolor/32x32/apps/custos.png"
printf 'icon' >"$root/icons/hicolor/128x128/apps/custos.png"
FAKE
    chmod 755 "$appimage"
fi

installed() {
    [ -x "$bin" ] || fail "$bin is not executable"
    cmp -s "$appimage" "$bin" || fail "$bin is not the AppImage"
    grep -qx "Exec=\"$bin\"" "$entry" || fail "the entry does not start $bin"
    grep -qx 'Icon=custos' "$entry" || fail "the entry has no icon"
    if grep -q '^MimeType=' "$entry"; then fail "the entry has a MimeType"; fi
    set -- "$data"/icons/hicolor/*/apps/custos.png
    [ -f "$1" ] || fail "no icon"
    [ ! -e "$old_entry" ] || fail "the entry of an older version stays"
    set -- "$home"/.local/bin/.custos.*
    [ ! -e "$1" ] || fail "a temporary file stays: $1"
}

sh "$here/install.sh" "$appimage"
installed
echo "ok: install"

# A second run replaces the installed version, and the entry of an older version.
printf 'old' >"$bin"
printf '[Desktop Entry]\n' >"$old_entry"
sh "$here/install.sh" "$appimage"
installed
echo "ok: replace"

# The script of the repository has no version, so it cannot download.
if sh "$here/install.sh" 2>/dev/null; then fail "the script without a version downloaded"; fi
echo "ok: no download without a version"

# The removal keeps the configuration.
mkdir -p "$home/.config/custos"
printf 'service_url = "https://example.com"\n' >"$home/.config/custos/config.toml"
printf '[Desktop Entry]\n' >"$data/applications/custos-handler.desktop"
printf '[Desktop Entry]\n' >"$old_entry"
sh "$here/install.sh" --uninstall
for file in "$bin" "$entry" "$old_entry" "$data/applications/custos-handler.desktop"; do
    [ ! -e "$file" ] || fail "$file stays after the removal"
done
set -- "$data"/icons/hicolor/*/apps/custos.png
[ ! -e "$1" ] || fail "an icon stays after the removal: $1"
[ -f "$home/.config/custos/config.toml" ] || fail "the removal deleted the configuration"
echo "ok: uninstall"
