#!/usr/bin/env bash
# FluentFin MPV: libmpv-2.dll from MSYS2's mpv package, with every non-Windows DLL it loads, and the exact sources of
# all of them. What it makes and how to check it: vendor/libmpv/VENDORED.md. Run it in MSYS2's UCRT64 shell, or through
# build-libmpv.ps1:
#
#   build-libmpv.sh --out <dll dir> [--sources <dir>] [--install-pinned | --write-pins]
#
#   --out             the DLL folder to fill (must be empty or not exist): libmpv-2.dll, its DLLs, BUILD-MANIFEST.txt
#   --sources         also download the source package (<pkgbase>-<ver>.src.tar.zst, signature checked) of every
#                     package a DLL comes from, into <dir> (with its .sig), each package's licence files into <dir>/licenses,
#                     and write <dir>/SOURCES.txt
#   --install-pinned  install the versions in pins.txt first (pacman -U from repo.msys2.org)
#   --write-pins      record the packages this run shipped as pins.txt (a new build: update VENDORED.md too)
#
# Nothing is compiled: the DLLs are MSYS2's, byte for byte. vulkan-1.dll (MSYS2's vulkan-loader, the Khronos loader) is
# shipped too, so libmpv loads on a PC without a Vulkan driver (FluentFin renders with d3d11).
set -euo pipefail

out= sources= install_pinned=0 write_pins=0
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --sources) sources="$2"; shift 2 ;;
    --install-pinned) install_pinned=1; shift ;;
    --write-pins) write_pins=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$out" ] || { echo "usage: $0 --out <dir> [--sources <dir>] [--install-pinned | --write-pins]" >&2; exit 2; }
[ "${MSYSTEM:-}" = UCRT64 ] || { echo "run this in MSYS2's UCRT64 environment (MSYSTEM=UCRT64)" >&2; exit 2; }
out=$(cygpath -u "$out")
[ -z "$sources" ] || sources=$(cygpath -u "$sources")
here=$(dirname "$(readlink -f "$0")")
pins="$here/pins.txt"   # "<package> <version>" of every package a shipped DLL comes from, as built
repo=https://repo.msys2.org/mingw/ucrt64

# 1. the packages: mpv and the Vulkan loader, at the pinned versions
if [ $install_pinned -eq 1 ]; then
  urls=()
  while read -r name ver; do
    [ -n "$name" ] && [ "${name:0:1}" != "#" ] || continue
    [ "$(pacman -Q "$name" 2>/dev/null | cut -d' ' -f2)" = "$ver" ] || urls+=("$repo/$name-$ver-any.pkg.tar.zst")
  done < "$pins"
  [ ${#urls[@]} -eq 0 ] || pacman -U --needed --noconfirm "${urls[@]}"
else
  pacman -S --needed --noconfirm mingw-w64-ucrt-x86_64-mpv mingw-w64-ucrt-x86_64-vulkan-loader
fi

# 2. libmpv-2.dll and everything it imports (objdump's import tables, breadth first), from /ucrt64/bin; FluentFin.Tests
# (LibmpvProbeTests) checks the import and delay-import tables of the copy next to the tests again
# A DLL not in /ucrt64/bin must be Windows' own: an API set, or in System32 and not one only a GPU driver puts there
# (vulkan-1.dll, OpenCL.dll, nvcuda.dll, ...: what is imported of those must come from MSYS2). The Windows DLLs used are
# listed at the end of BUILD-MANIFEST.txt.
driver_dlls=' vulkan-1 opencl nvcuda nvapi64 nvencodeapi64 nvcuvid amfrt64 libva libva_win32 libmfxhw64 libvpl '
sysdir=$(cygpath -u "${SYSTEMROOT:-C:\\Windows}")/System32
windows_used=" "
is_system_dll() {
  local n="${1,,}"; n="${n%.dll}"
  case "$n" in api-ms-win-*|ext-ms-win-*) return 0 ;; esac
  [[ "$driver_dlls" != *" $n "* ]] && [ -f "$sysdir/$1" ] && { [[ "$windows_used" == *" ${1,,} "* ]] || windows_used="$windows_used${1,,} "; }
}

if [ -e "$out" ] && [ -n "$(ls -A "$out")" ]; then echo "$out is not empty" >&2; exit 1; fi
mkdir -p "$out"
list=$(mktemp); trap 'rm -f "$list"' EXIT
queue=(/ucrt64/bin/libmpv-2.dll) seen=" "
while [ ${#queue[@]} -gt 0 ]; do
  f="${queue[0]}"; queue=("${queue[@]:1}")
  n=$(basename "$f")
  [[ "$seen" == *" ${n,,} "* ]] && continue
  seen="$seen${n,,} "
  echo "$f" >> "$list"
  for p in $(objdump -p "$f" | sed -n 's/^\s*DLL Name: *//p' | tr -d '\r' | sort -u); do
    if [ -f "/ucrt64/bin/$p" ]; then queue+=("/ucrt64/bin/$p")
    elif is_system_dll "$p"; then :
    else echo "$n imports $p: not in /ucrt64/bin and not a Windows DLL" >&2; exit 1
    fi
  done
done
grep -qi '/vulkan-1\.dll$' "$list" || echo /ucrt64/bin/vulkan-1.dll >> "$list"

manifest="$out/BUILD-MANIFEST.txt"
{
  echo "# FluentFin MPV libmpv: MSYS2 UCRT64 packages, nothing compiled. <dll> <bytes> <sha256> <package> <version>"
  while read -r f; do
    cp "$f" "$out/"
    owner=$(pacman -Qo "$f" | sed 's/^.* is owned by //')
    printf '%s %s %s %s\n' "$(basename "$f")" "$(stat -c %s "$f")" "$(sha256sum "$f" | cut -d' ' -f1)" "$owner"
  done < "$list"
  echo "# Windows DLLs imported (besides API sets):$windows_used"
} > "$manifest"

if [ $write_pins -eq 1 ]; then grep -v '^#' "$manifest" | awk '{ print $4, $5 }' | sort -u > "$pins"; fi
# the shipped packages must be the pinned ones (a rebuild with newer packages is a different build: --write-pins)
bad=0
while read -r dll size sha name ver; do
  [ "${dll:0:1}" = "#" ] && continue
  want=$(awk -v n="$name" '$1 == n { print $2 }' "$pins")
  if [ "$want" != "$ver" ]; then echo "pin: $dll comes from $name $ver, pins.txt says ${want:-nothing}" >&2; bad=1; fi
done < "$manifest"
echo "$(grep -vc '^#' "$manifest") DLLs, $(du -sb "$out" | cut -f1) bytes in $out"
[ $bad -eq 0 ] || { echo "the packages differ from pins.txt: see above" >&2; exit 1; }

# 3. sources: each package's MSYS2 source package (its upstream tarball, PKGBUILD and patches) and licence files
[ -n "$sources" ] || exit 0
mkdir -p "$sources/licenses"
manifest_src="$sources/SOURCES.txt"
{
  echo "# Sources of FluentFin MPV's libmpv folder ($(basename "$out")): every shipped DLL comes from one MSYS2 UCRT64"
  echo "# package, whose exact source is its MSYS2 source package (upstream tarball + PKGBUILD + patches), from"
  echo "# https://repo.msys2.org/mingw/sources/ (here, with its .sig: checked against MSYS2's keyring). Licence files: licenses/<package>/."
  echo "# component | version | licence (package metadata) | DLLs | file | SHA-256"
} > "$manifest_src"
for pkg in $(grep -v '^#' "$manifest" | awk '{ print $4 }' | sort -u); do
  ver=$(pacman -Q "$pkg" | cut -d' ' -f2)
  desc=$(ls -d /var/lib/pacman/local/"$pkg-$ver")/desc
  base=$(awk '/^%BASE%$/ { getline; print; exit }' "$desc")
  lic=$(awk '/^%LICENSE%$/ { while ((getline l) > 0 && l != "") printf "%s%s", (n++ ? " AND " : ""), l; exit }' "$desc")
  src="$base-$ver.src.tar.zst"
  if [ ! -s "$sources/$src" ]; then
    curl -fsSL --retry 3 -o "$sources/$src.part" "https://repo.msys2.org/mingw/sources/$src"
    mv "$sources/$src.part" "$sources/$src"
  fi
  [ -s "$sources/$src.sig" ] || curl -fsSL --retry 3 -o "$sources/$src.sig" "https://repo.msys2.org/mingw/sources/$src.sig"
  gpgv --keyring /etc/pacman.d/gnupg/pubring.gpg "$sources/$src.sig" "$sources/$src" 2>/dev/null \
    || { echo "bad signature: $src" >&2; exit 1; }
  dlls=$(awk -v p="$pkg" '$4 == p { printf "%s%s", (n++ ? "," : ""), $1 }' "$manifest")
  # licence files the package installs (share/licenses/...); otherwise the upstream tarball's COPYING/LICENSE files
  mkdir -p "$sources/licenses/$pkg"
  pacman -Qlq "$pkg" | grep -E '/share/licenses/.+[^/]$' | while read -r l; do cp "$l" "$sources/licenses/$pkg/"; done || true
  if [ -z "$(ls -A "$sources/licenses/$pkg")" ]; then
    tmp=$(mktemp -d)
    bsdtar -xf "$sources/$src" -C "$tmp"
    for tb in "$tmp"/*/*.tar.* "$tmp"/*/*.tgz "$tmp"/*/*.zip; do
      [ -f "$tb" ] || continue
      bsdtar -xf "$tb" -C "$tmp" --include='*COPYING*' --include='*LICENSE*' --include='*LICENCE*' --include='*Copyright*' 2>/dev/null || true
    done
    # sources that are git checkouts come as bare mirrors: the licence files at the commit (or tag) .SRCINFO pins
    rev=$(sed -n 's/.*#\(commit\|tag\)=\([^"'"'"' ]*\).*/\2/p' "$tmp"/*/.SRCINFO | head -1)
    for g in "$tmp"/*/*/; do
      [ -f "$g/HEAD" ] && [ -d "$g/objects" ] || continue
      git --git-dir="$g" ls-tree --name-only "${rev:-HEAD}" 2>/dev/null | grep -iE '^(copying|license|licence|copyright)' | while read -r l; do
        git --git-dir="$g" show "${rev:-HEAD}:$l" > "$sources/licenses/$pkg/$l"
      done
    done
    find "$tmp" -mindepth 2 -type f \( -iname '*copying*' -o -iname '*license*' -o -iname '*licence*' -o -iname 'copyright*' \) \
      -not -path '*/test*' -size -200k | head -20 | while read -r l; do
        cp "$l" "$sources/licenses/$pkg/$(echo "${l#$tmp/*/}" | tr '/' '_')"
      done
    rm -rf "$tmp"
  fi
  [ -n "$(ls -A "$sources/licenses/$pkg")" ] || echo "no licence file found for $pkg (see its source package)" >&2
  printf '%s | %s | %s | %s | %s | %s\n' "$pkg" "$ver" "$lic" "$dlls" "$src" "$(sha256sum "$sources/$src" | cut -d' ' -f1)" >> "$manifest_src"
done
echo "sources: $(grep -vc '^#' "$manifest_src") packages, $(du -sb "$sources" | cut -f1) bytes in $sources"
