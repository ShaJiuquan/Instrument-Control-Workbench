#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
output=
runtime=
while [[ $# -gt 0 ]]; do
  case "$1" in
    --output) [[ $# -ge 2 && -z "$output" ]] || exit 2; output="$2"; shift 2 ;;
    --runtime) [[ $# -ge 2 && -z "$runtime" ]] || exit 2; runtime="$2"; shift 2 ;;
    *) echo "Usage: package-cli.sh --output ABSOLUTE_DIRECTORY --runtime osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64" >&2; exit 2 ;;
  esac
done
[[ "$output" == /* && ! -e "$output" && ! -L "$output" ]] || { echo "Output must be a new absolute directory." >&2; exit 2; }
case "$output" in */../*|*/./*|*/..|*/.) echo "Output cannot contain dot path segments." >&2; exit 2 ;; esac
case "$runtime" in osx-arm64|osx-x64|linux-x64|linux-arm64|win-x64) ;; *) echo "Unsupported runtime." >&2; exit 2 ;; esac
dotnet publish "$root/src/InstrumentControlWorkbench.Cli/InstrumentControlWorkbench.Cli.csproj" -c Release -r "$runtime" --self-contained true -o "$output" -warnaserror
executable="$output/InstrumentControlWorkbench.Cli"
[[ "$runtime" != win-x64 ]] || executable="$executable.exe"
[[ -s "$executable" && -s "$output/InstrumentControlWorkbench.Cli.runtimeconfig.json" && -s "$output/InstrumentControlWorkbench.Cli.deps.json" ]] || { echo "Publish output incomplete." >&2; exit 1; }
revision=unknown
if repository_root="$(git -C "$root" rev-parse --show-toplevel 2>/dev/null)" && [[ "$repository_root" == "$root" ]]; then revision="$(git -C "$root" rev-parse HEAD)"; fi
printf 'runtime=%s\nsource_commit=%s\n' "$runtime" "$revision" > "$output/build-info.txt"
echo "CLI package: $output"
