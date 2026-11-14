#!/usr/bin/env bash
set -euo pipefail

if [ "$#" -ne 4 ]; then
    echo "usage: make-host.sh <unity executable> <checkout> <host project> <log file>" >&2
    exit 2
fi

editor=$1
checkout=$2
host=$3
log=$4

if [ -e "$host" ]; then
    echo "$host already exists" >&2
    exit 1
fi

mkdir -p "$(dirname "$log")"
set +e
"$editor" -batchmode -nographics -quit -createProject "$host" -logFile "$log"
code=$?
set -e
if [ "$code" -ne 0 ] || [ ! -f "$host/Packages/manifest.json" ]; then
    echo "Unity could not create the host project (exit code $code)" >&2
    tail -n 100 "$log" >&2 || true
    exit 1
fi

python - "$host/Packages/manifest.json" <<'EOF'
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as file:
    manifest = json.load(file)
manifest.setdefault("dependencies", {})["com.unity.test-framework"] = "1.1.33"
with open(path, "w", encoding="utf-8", newline="\n") as file:
    json.dump(manifest, file, indent=2)
    file.write("\n")
EOF

cp -R "$checkout/Packages/dev.upwake.vetka" "$host/Assets/Vetka"
rm -f "$host/Assets/Vetka/package.json" "$host/Assets/Vetka/package.json.meta"
cp -R "$checkout/Assets/Tests" "$host/Assets/Tests"
cp "$checkout/Assets/Tests.meta" "$host/Assets/Tests.meta"
rm -rf "$host/Library"

echo "Host project: $host"
cat "$host/Packages/manifest.json"
