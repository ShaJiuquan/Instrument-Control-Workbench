#!/usr/bin/env bash
set -euo pipefail

# Uses the project's existing .NET SDK and packages its self-contained publish
# output. No native dependency manager or UI code modification is required.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This packaging script must run on macOS." >&2
  exit 2
fi

case "$(uname -m)" in
  arm64) runtime_id=osx-arm64 ;;
  x86_64) runtime_id=osx-x64 ;;
  *) echo "Unsupported macOS architecture." >&2; exit 2 ;;
esac

revision=unknown
dirty=unknown
if repository_root="$(git -C "$root" rev-parse --show-toplevel 2>/dev/null)" &&
   [[ "$repository_root" == "$root" ]] && revision="$(git -C "$root" rev-parse HEAD 2>/dev/null)"; then
  dirty=false
  [[ -z "$(git -C "$root" status --porcelain)" ]] || dirty=true
else
  revision=unknown
fi
destination="$root/artifacts/macos/Instrument Workbench-${revision:0:7}.app"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output)
      [[ $# -ge 2 ]] || { echo "--output requires an absolute .app path." >&2; exit 2; }
      destination="$2"; shift 2 ;;
    --runtime)
      [[ $# -ge 2 ]] || { echo "--runtime requires osx-arm64 or osx-x64." >&2; exit 2; }
      runtime_id="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done
case "$runtime_id" in
  osx-arm64|osx-x64) ;;
  *) echo "Only osx-arm64 and osx-x64 are supported." >&2; exit 2 ;;
esac
[[ "$destination" == /*.app ]] || { echo "Output must be an absolute .app path." >&2; exit 2; }
[[ ! -e "$destination" && ! -L "$destination" ]] || { echo "Output already exists; choose a new --output path." >&2; exit 2; }

staging="$(mktemp -d "${TMPDIR:-/tmp}/instrument-workbench-macos.XXXXXX")"
trap 'rm -rf -- "$staging"' EXIT
bundle="$staging/Instrument Workbench.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"

dotnet publish "$root/src/InstrumentControlWorkbench.App/InstrumentControlWorkbench.App.csproj" \
  --configuration Release --runtime "$runtime_id" --self-contained true \
  -p:UseAppHost=true -warnaserror --output "$bundle/Contents/MacOS"

cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleDisplayName</key><string>Instrument Workbench</string>
  <key>CFBundleExecutable</key><string>InstrumentControlWorkbench.App</string>
  <key>CFBundleIdentifier</key><string>com.shajiuquan.instrumentcontrolworkbench</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Instrument Workbench</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>15.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
printf 'APPL????' > "$bundle/Contents/PkgInfo"
printf 'git_revision=%s\nworking_tree_dirty=%s\nruntime=%s\nconfiguration=Release\nself_contained=true\n' \
  "$revision" "$dirty" "$runtime_id" > "$bundle/Contents/Resources/build-info.txt"
plutil -lint "$bundle/Contents/Info.plist"
test -x "$bundle/Contents/MacOS/InstrumentControlWorkbench.App"
test -f "$bundle/Contents/MacOS/libhostfxr.dylib"
test -f "$bundle/Contents/MacOS/libcoreclr.dylib"
[[ ! -e "$destination" && ! -L "$destination" ]] || { echo "Output appeared during publish; choose a new --output path." >&2; exit 2; }
mkdir -p "$(dirname "$destination")"
mv -- "$bundle" "$destination"
printf 'Packaged: %s\n' "$destination"
