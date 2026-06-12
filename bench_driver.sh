#!/usr/bin/env bash
# Driver: benchmark SimdPhraseBenchmark (SIMD path, forceNaive:false) at N=10000
# across first-parent main-line commits that contain the benchmark.
set -u

REPO=/home/user/simdphrase2
cd "$REPO"
ORIG_BRANCH="claude/simd-benchmark-results-dkqfg1"
OUT=/tmp/bench_runs
mkdir -p "$OUT"
PROJ=SimdPhrase2.Benchmarks/SimdPhrase2.Benchmarks.csproj
SIMD_FILTER='SimdPhrase2.Benchmarks.SimdPhraseBenchmark.*(N: 10000)'

# oldest -> newest first-parent commits since the benchmark was introduced
COMMITS=$(git log --reverse --first-parent --format=%h 4e6e43e^..HEAD)

echo "PROGRESS start commits: $(echo $COMMITS | tr '\n' ' ')"

for c in $COMMITS; do
  echo "PROGRESS ===== commit $c : $(git log -1 --format=%s $c | cut -c1-60)"
  git checkout -f "$c" >/dev/null 2>&1
  # clean previous build/artifacts to avoid contamination
  rm -rf "$REPO"/SimdPhrase2*/bin "$REPO"/SimdPhrase2*/obj 2>/dev/null

  blog="$OUT/$c.build.log"
  if dotnet build -c Release "$PROJ" >"$blog" 2>&1; then
    echo "PROGRESS $c build OK"
    rlog="$OUT/$c.simd.log"
    dotnet run -c Release --no-build --project "$PROJ" -- \
      --job short --filter "$SIMD_FILTER" \
      --artifacts "$OUT/$c.artifacts" >"$rlog" 2>&1
    echo "PROGRESS $c benchmark done rc=$?"
  else
    echo "COMPILE_FAIL $c"
    echo "DID_NOT_COMPILE" >"$OUT/$c.simd.log"
  fi
done

# Lucene comparison on the current (HEAD) commit only
git checkout -f "$ORIG_BRANCH" >/dev/null 2>&1
rm -rf "$REPO"/SimdPhrase2*/bin "$REPO"/SimdPhrase2*/obj 2>/dev/null
echo "PROGRESS ===== Lucene on HEAD ($(git rev-parse --short HEAD))"
if dotnet build -c Release "$PROJ" >"$OUT/lucene.build.log" 2>&1; then
  dotnet run -c Release --no-build --project "$PROJ" -- \
    --job short --filter 'SimdPhrase2.Benchmarks.LuceneBenchmark.*(N: 10000)' \
    --artifacts "$OUT/lucene.artifacts" >"$OUT/lucene.simd.log" 2>&1
  echo "PROGRESS lucene benchmark done rc=$?"
else
  echo "PROGRESS lucene build FAILED"
fi

echo "PROGRESS ALL_DONE"
