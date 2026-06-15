#!/usr/bin/env bash
set -euo pipefail

format=0
strict_architecture=0

for arg in "$@"; do
  case "$arg" in
    --format) format=1 ;;
    --strict-architecture) strict_architecture=1 ;;
    *)
      echo "Unknown argument: $arg" >&2
      exit 2
      ;;
  esac
done

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

echo "== restore =="
dotnet restore InstrumentControlWorkbench.sln

echo "== build warnings-as-errors =="
dotnet build InstrumentControlWorkbench.sln --no-restore -warnaserror

echo "== test =="
dotnet test InstrumentControlWorkbench.sln --no-build

if [[ "$format" == "1" ]]; then
  echo "== format verify =="
  dotnet format InstrumentControlWorkbench.sln --verify-no-changes --verbosity minimal
fi

echo "== architecture scan =="
warnings=0

search_path() {
  local pattern="$1"
  local path="$2"

  if command -v rg >/dev/null 2>&1; then
    rg "$pattern" "$path" >/dev/null 2>&1
  else
    grep -R -E "$pattern" "$path" >/dev/null 2>&1
  fi
}

search_axaml_codebehind() {
  local pattern="$1"
  local path="$2"

  if command -v rg >/dev/null 2>&1; then
    rg "$pattern" "$path" --glob '*.axaml.cs' >/dev/null 2>&1
  else
    find "$path" -name '*.axaml.cs' -print0 | xargs -0 grep -E "$pattern" >/dev/null 2>&1
  fi
}

if search_path "using InstrumentControlWorkbench.Infrastructure" src/InstrumentControlWorkbench.App; then
  echo "WARN: App layer imports Infrastructure. Keep this isolated to composition code; ViewModels must not construct drivers."
  warnings=$((warnings + 1))
fi

if search_axaml_codebehind "InstrumentControlWorkbench.Application|InstrumentControlWorkbench.Infrastructure" src/InstrumentControlWorkbench.App/Views; then
  echo "WARN: View code-behind references Application/Infrastructure. Prefer ViewModel commands."
  warnings=$((warnings + 1))
fi

if search_path "Polyline|Canvas" src/InstrumentControlWorkbench.App/Views/MainWindow.axaml; then
  echo "WARN: MainWindow contains hard-coded plot placeholder geometry."
  warnings=$((warnings + 1))
fi

if [[ "$warnings" -gt 0 && "$strict_architecture" == "1" ]]; then
  echo "Architecture scan failed with $warnings warning(s)." >&2
  exit 1
fi

echo "Audit complete. Architecture warnings: $warnings"
