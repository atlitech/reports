#!/usr/bin/env bash
# Runs the Atli.Reports benchmark suite: the Docker load comparison against Gotenberg and,
# optionally, the in-process BenchmarkDotNet micro-benchmarks. See benchmarks/README.md.
#
# Needs only Docker (with Compose v2) and the .NET SDK from global.json. Works with the bash 3.2
# that ships with macOS.

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: benchmarks/run.sh [--quick | --full] [options]

Modes (load comparison; every option below overrides the mode's value):
  --quick                 Default. Concurrency 1,16; 3 s warm-up; 10 s measured. About 6 min
                          (longer while a target saturates: each failing request waits out the
                          30 s client deadline).
  --full                  Concurrency 1,4,16,64; 5 s warm-up; 30 s measured. About 25 min.

Selection:
  --targets LIST          atli,gotenberg (default both)
  --fixtures LIST         invoice,long-table,chart,assets (default all)
  --concurrency LIST      Closed-loop worker counts, e.g. 1,4,16,64
  --warmup SECONDS        Warm-up per cell (at most 4 workers, drained before measuring)
  --duration SECONDS      How long workers start requests in the measured phase
  --timeout SECONDS       Client deadline per request (default 30 = Gotenberg's API timeout)
  --keep-going            Also measure levels above one that produced no valid PDF

Limits (identical for both targets):
  --cpus N                Default 2
  --memory SIZE           Default 2g (swap disabled)

Suites:
  --micro                 Also run the BenchmarkDotNet micro-benchmarks: ~1.5 min quick, ~3 min
                          full. Needs Chrome or Edge installed on this machine; Linux hosts may
                          need ReportsEngine__Browser__NoSandbox=true.
  --micro-only            Run only the micro-benchmarks

Output and build:
  --out DIR               Results directory (default benchmarks/results)
  --name NAME             Results base name (default <yyyy-mm-dd>-<short-sha>)
  --note TEXT             Note printed at the top of the results (e.g. machine conditions)
  --no-build              Reuse the existing Atli image instead of rebuilding it
  -h, --help              Show this help

Environment:
  ATLI_IMAGE              Atli image tag (default atli-reports-server:bench, built from
                          src/Atli.Reports.Server/Dockerfile)
  GOTENBERG_IMAGE         Gotenberg image (default: the pinned 8.37.0-chromium digest)
  GOTENBERG_ARGS          Extra Gotenberg flags, e.g. "--chromium-restart-after=0"
  benchmarks/load/atli.env  Optional ReportsEngine__* settings for the Atli container
EOF
}

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo_root=$(cd "$script_dir/.." && pwd)
compose_file="$script_dir/load/compose.yaml"
project_name=atli-reports-bench

mode=quick
targets=atli,gotenberg
fixtures=invoice,long-table,chart,assets
concurrency=""
warmup=""
duration=""
timeout=30
cpus=2
memory=2g
suite=load
build=1
keep_going=0
note=""
name=""
out="$script_dir/results"
atli_image="${ATLI_IMAGE:-atli-reports-server:bench}"
gotenberg_image="${GOTENBERG_IMAGE:-gotenberg/gotenberg:8.37.0-chromium@sha256:0d28ae9a96441588ef739623726bd500ad0720b77266c6f1351a13e333fbd61c}"

need_value() {
  if [ $# -lt 2 ] || [ -z "$2" ]; then
    echo "Missing value for $1" >&2
    exit 2
  fi
}

while [ $# -gt 0 ]; do
  case "$1" in
    --quick) mode=quick ;;
    --full) mode=full ;;
    --targets) need_value "$@"; targets="$2"; shift ;;
    --fixtures) need_value "$@"; fixtures="$2"; shift ;;
    --concurrency) need_value "$@"; concurrency="$2"; shift ;;
    --warmup) need_value "$@"; warmup="$2"; shift ;;
    --duration) need_value "$@"; duration="$2"; shift ;;
    --timeout) need_value "$@"; timeout="$2"; shift ;;
    --keep-going) keep_going=1 ;;
    --cpus) need_value "$@"; cpus="$2"; shift ;;
    --memory) need_value "$@"; memory="$2"; shift ;;
    --micro) suite=all ;;
    --micro-only) suite=micro ;;
    --out) need_value "$@"; out="$2"; shift ;;
    --name) need_value "$@"; name="$2"; shift ;;
    --note) need_value "$@"; note="$2"; shift ;;
    --no-build) build=0 ;;
    -h | --help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

case "$mode" in
  quick)
    : "${concurrency:=1,16}"
    : "${warmup:=3}"
    : "${duration:=10}"
    ;;
  full)
    : "${concurrency:=1,4,16,64}"
    : "${warmup:=5}"
    : "${duration:=30}"
    ;;
esac

host_load() {
  if [ -r /proc/loadavg ]; then
    cut -d' ' -f1-3 /proc/loadavg
  else
    sysctl -n vm.loadavg 2>/dev/null | tr -d '{}' | sed 's/^ *//;s/ *$//'
  fi
}

has_target() {
  case ",$targets," in
    *",$1,"*) return 0 ;;
    *) return 1 ;;
  esac
}

# Prerequisites.
command -v dotnet >/dev/null 2>&1 || { echo "The .NET SDK (dotnet) is required." >&2; exit 1; }
if [ "$suite" != micro ]; then
  command -v docker >/dev/null 2>&1 || { echo "Docker is required." >&2; exit 1; }
  docker info >/dev/null 2>&1 || { echo "The Docker daemon is not reachable." >&2; exit 1; }
  docker compose version >/dev/null 2>&1 || { echo "Docker Compose v2 is required." >&2; exit 1; }
fi

mkdir -p "$out"
out=$(cd "$out" && pwd)
if [ -z "$name" ]; then
  base="$(date -u +%Y-%m-%d)-$(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  name="$base"
  suffix=2
  while [ -e "$out/$name.md" ] || [ -e "$out/$name-micro.md" ]; do
    name="$base-$suffix"
    suffix=$((suffix + 1))
  done
fi

compose() {
  ATLI_IMAGE="$atli_image" GOTENBERG_IMAGE="$gotenberg_image" \
    docker compose --file "$compose_file" --project-name "$project_name" \
    --profile atli --profile gotenberg "$@"
}

cleanup() {
  if [ "$suite" != micro ]; then
    compose down --remove-orphans --timeout 5 >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM

started=$(date +%s)
echo "Atli.Reports benchmarks: mode=$mode suite=$suite name=$name"
echo "Host load average: $(host_load)"

if [ "$suite" != micro ]; then
  if has_target atli; then
    if [ "$build" = 1 ]; then
      echo "Building $atli_image from src/Atli.Reports.Server/Dockerfile..."
      compose build atli
    elif ! docker image inspect "$atli_image" >/dev/null 2>&1; then
      echo "$atli_image does not exist; run without --no-build." >&2
      exit 1
    fi
  fi
  if has_target gotenberg && ! docker image inspect "$gotenberg_image" >/dev/null 2>&1; then
    echo "Pulling $gotenberg_image..."
    docker pull "$gotenberg_image"
  fi

  echo "Building the load driver..."
  dotnet build "$script_dir/Atli.Reports.Benchmarks.Load/Atli.Reports.Benchmarks.Load.csproj" \
    --configuration Release --nologo --verbosity quiet

  driver_args=(
    run
    --mode "$mode"
    --targets "$targets"
    --fixtures "$fixtures"
    --concurrency "$concurrency"
    --warmup "$warmup"
    --duration "$duration"
    --timeout "$timeout"
    --cpus "$cpus"
    --memory "$memory"
    --atli-image "$atli_image"
    --gotenberg-image "$gotenberg_image"
    --compose-file "$compose_file"
    --project-name "$project_name"
    --out "$out"
    --name "$name"
  )
  if [ "$keep_going" = 1 ]; then
    driver_args+=(--keep-going)
  fi
  if [ -n "$note" ]; then
    driver_args+=(--note "$note")
  fi
  dotnet run --no-build --configuration Release \
    --project "$script_dir/Atli.Reports.Benchmarks.Load/Atli.Reports.Benchmarks.Load.csproj" \
    -- "${driver_args[@]}"
fi

if [ "$suite" != load ]; then
  micro_dir="$out/$name.work/micro"
  mkdir -p "$micro_dir"
  micro_args=(--artifacts "$micro_dir")
  if [ "$mode" = quick ]; then
    micro_args+=(--quick)
  fi
  load_before=$(host_load)
  echo "Running the micro-benchmarks (BenchmarkDotNet, in-process)..."
  dotnet run --configuration Release \
    --project "$script_dir/Atli.Reports.Benchmarks/Atli.Reports.Benchmarks.csproj" \
    -- "${micro_args[@]}"
  report=$(ls "$micro_dir"/results/*-report-github.md 2>/dev/null | head -n 1 || true)
  if [ -z "$report" ]; then
    echo "BenchmarkDotNet wrote no report into $micro_dir/results." >&2
    exit 1
  fi
  {
    echo "# Micro-benchmarks: Atli.Reports.Engine in-process — $(date -u +%Y-%m-%d) ($(git -C "$repo_root" rev-parse --short HEAD 2>/dev/null || echo unknown))"
    echo
    if [ -n "$note" ]; then
      echo "> $note"
      echo
    fi
    echo "- **Mode:** $mode · BenchmarkDotNet, in-process toolchain, one conversion per invocation"
    echo "- **Engine commit:** \`$(git -C "$repo_root" log -1 --format='%h %s' -- src/Atli.Reports.Engine 2>/dev/null || echo unknown)\`"
    echo "- **Host load average:** $load_before before, $(host_load) after"
    echo "- **Browser:** the Chrome/Edge installed on the host (not the server image's chrome-headless-shell); *Allocated* counts the benchmark process's managed allocations only"
    echo
    cat "$report"
  } >"$out/$name-micro.md"
  echo "Wrote $out/$name-micro.md"
fi

elapsed=$(($(date +%s) - started))
echo "Done in $((elapsed / 60)) min $((elapsed % 60)) s. Results: $out/$name*"
