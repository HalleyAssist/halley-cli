#!/usr/bin/env bash
set -uo pipefail

if [ "$#" -eq 0 ]; then
  echo "Usage: $0 <command> [args...]" >&2
  exit 64
fi

repo_root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
search_roots=("$repo_root")

if [ -d /cores ]; then
  search_roots+=("/cores")
fi

marker_file="$(mktemp "${TMPDIR:-/tmp}/halley-core-check.XXXXXX")"
cleanup() {
  rm -f "$marker_file"
}
trap cleanup EXIT

touch "$marker_file"

find_new_core_dumps() {
  local root

  for root in "${search_roots[@]}"; do
    if [ ! -d "$root" ]; then
      continue
    fi

    find "$root" -type f \( -name 'core' -o -name 'core.*' \) -newer "$marker_file" -print 2>/dev/null
  done | sort -u
}

format_core_dump() {
  local path="$1"

  if stat --printf='%n (%s bytes, modified %y)\n' "$path" >/dev/null 2>&1; then
    stat --printf='%n (%s bytes, modified %y)\n' "$path"
    return
  fi

  stat -f '%N (%z bytes, modified %Sm)\n' -t '%Y-%m-%d %H:%M:%S %z' "$path"
}

command_exit=0
"$@" || command_exit=$?

mapfile -t core_dumps < <(find_new_core_dumps)

if [ "${#core_dumps[@]}" -gt 0 ]; then
  {
    echo "Core dumps were generated while running: $*"
    echo "Treat this as a failure and investigate the crash before proceeding."
    echo "Generated core dumps:"

    local_dump=""
    for local_dump in "${core_dumps[@]}"; do
      printf '  - '
      format_core_dump "$local_dump"
    done
  } >&2

  if [ "$command_exit" -eq 0 ]; then
    command_exit=86
  fi
fi

exit "$command_exit"
