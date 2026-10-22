#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
  printf 'Usage: %s <cli-publish-dir> <desktop-publish-dir> <output.deb>\n' "${0##*/}" >&2
}

if [[ $# -ne 3 ]]; then
  usage
  exit 2
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "$script_dir/.." && pwd -P)"
cli_dir="$(cd -- "$1" && pwd -P)"
desktop_dir="$(cd -- "$2" && pwd -P)"
output_path="$3"
output_parent="$(dirname -- "$output_path")"
mkdir -p -- "$output_parent"
output_parent="$(cd -- "$output_parent" && pwd -P)"
output_path="$output_parent/$(basename -- "$output_path")"

for command_name in dpkg-deb python3; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required command not found: %s\n' "$command_name" >&2
    exit 1
  fi
done

if [[ -e "$output_path" ]]; then
  printf 'Refusing to overwrite existing output: %s\n' "$output_path" >&2
  exit 1
fi

cli_binary="$cli_dir/wattledger"
desktop_binary="$desktop_dir/WattLedger.Linux"
if [[ ! -x "$cli_binary" ]]; then
  printf 'Expected executable not found: %s\n' "$cli_binary" >&2
  exit 1
fi
if [[ ! -x "$desktop_binary" ]]; then
  printf 'Expected executable not found: %s\n' "$desktop_binary" >&2
  exit 1
fi

package_version="$(python3 - "$repo_root/Directory.Build.props" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
version = root.findtext(".//Version", "").strip()
if not version:
    raise SystemExit("Directory.Build.props does not define Version")
print(version)
PY
)"
if [[ ! "$package_version" =~ ^[0-9][0-9A-Za-z.+:~-]*$ ]]; then
  printf 'Version is not valid for Debian package metadata: %s\n' "$package_version" >&2
  exit 1
fi

stage_dir="$(mktemp -d "${TMPDIR:-/tmp}/wattledger-deb.XXXXXXXX")"
trap 'rm -rf -- "$stage_dir"' EXIT
package_root="$stage_dir/package"
install -d "$package_root/DEBIAN" \
  "$package_root/usr/bin" \
  "$package_root/usr/lib/wattledger/desktop" \
  "$package_root/usr/share/applications" \
  "$package_root/usr/share/doc/wattledger" \
  "$package_root/usr/share/icons/hicolor/scalable/apps"

install -m 0755 "$cli_binary" "$package_root/usr/bin/wattledger"
cp -a "$desktop_dir/." "$package_root/usr/lib/wattledger/desktop/"
chmod -R go-w "$package_root/usr/lib/wattledger/desktop"
install -m 0755 "$script_dir/../packaging/wattledger-desktop" "$package_root/usr/bin/wattledger-desktop"
install -m 0644 "$script_dir/../packaging/wattledger.svg" "$package_root/usr/share/icons/hicolor/scalable/apps/wattledger.svg"
install -m 0644 "$script_dir/../packaging/wattledger.desktop" "$package_root/usr/share/applications/wattledger.desktop"
install -m 0644 "$repo_root/LICENSE" "$package_root/usr/share/doc/wattledger/copyright"
install -m 0644 "$repo_root/THIRD_PARTY_NOTICES.md" "$package_root/usr/share/doc/wattledger/THIRD_PARTY_NOTICES.md"

cat > "$package_root/DEBIAN/control" <<EOF
Package: wattledger
Version: $package_version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: WattLedger contributors
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libgtk-3-0 | libgtk-3-0t64, libx11-6, libx11-xcb1, libxi6, libxrender1, libxrandr2, libxcursor1, libxinerama1, libice6, libsm6, libfontconfig1, libfreetype6, libicu72 | libicu74 | libicu76, libssl3 | libssl3t64, xwayland
Description: Local-first laptop battery energy journal
 Records battery energy, sessions, and health locally through a desktop app and CLI.
EOF

dpkg-deb --root-owner-group --build "$package_root" "$output_path"
printf 'Created %s\n' "$output_path"
