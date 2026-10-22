#!/usr/bin/env bash
# Fetches the external T-SQL corpus used by spike S2 at pinned commits (reproducible; not committed).
# Usage: tools/fetch-corpus.sh [target-dir]   (default: tests/Corpus/external)
set -euo pipefail

target="${1:-tests/Corpus/external}"
mkdir -p "$target"

# name | repository | commit | licence
sources=(
  "first-responder-kit|https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit|53c380edf86e93fa07bcb2f2caf82c3e0d20fb68|MIT"
  "maintenance-solution|https://github.com/olahallengren/sql-server-maintenance-solution|41617578643e722b790ea4d57ef80f8de38ae747|MIT"
  "sql-server-samples|https://github.com/microsoft/sql-server-samples|dac9747acaca4e54a3b5ea4c6de1c4be6c1b964a|MIT"
  "tsqlt|https://github.com/tSQLt-org/tSQLt|4a921d0dacfb1d66b3db124c58158c80e5e910e6|Apache-2.0"
)

for entry in "${sources[@]}"; do
  IFS='|' read -r name url commit licence <<<"$entry"
  dir="$target/$name"
  if [[ -d "$dir/.git" ]] && [[ "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" == "$commit" ]]; then
    echo "$name: up to date ($commit)"
    continue
  fi

  rm -rf "$dir"
  git init -q "$dir"
  git -C "$dir" remote add origin "$url"
  git -C "$dir" fetch -q --depth 1 --filter=blob:none origin "$commit"
  git -C "$dir" sparse-checkout set --no-cone '*.sql'
  git -C "$dir" checkout -q FETCH_HEAD
  echo "$name: $(find "$dir" -name '*.sql' | wc -l) files at $commit ($licence)"
done
