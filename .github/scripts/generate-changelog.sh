#!/usr/bin/env bash
# 按提交区间生成 changelog（同时作为发布说明）；区间为空时回退到仓库内文件。
set -euo pipefail

revision_range=""
fallback_path=".github/CHANGELOG.md"
output_path="release_notes.md"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --range)
      revision_range="${2:-}"
      shift 2
      ;;
    --fallback)
      fallback_path="${2:-}"
      shift 2
      ;;
    --output)
      output_path="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

server_url="${GITHUB_SERVER_URL:-https://github.com}"
repository="${GITHUB_REPOSITORY:-}"

if [ -n "${revision_range}" ]; then
  commits="$(git log --reverse --format='%H%x09%h%x09%s' "${revision_range}")"
else
  commits=""
fi

if [ -n "${commits}" ]; then
  {
    echo "## Changes"
    echo
    while IFS=$'\t' read -r commit short subject; do
      [ -n "${commit}" ] || continue
      if [ -n "${repository}" ]; then
        printf -- '- %s [`%s`](%s/%s/commit/%s)\n' \
          "${subject}" "${short}" "${server_url}" "${repository}" "${commit}"
      else
        printf -- '- %s [`%s`]\n' "${subject}" "${short}"
      fi
    done <<< "${commits}"
  } > "${output_path}"
  echo "Generated changelog from ${revision_range}."
else
  if [ ! -f "${fallback_path}" ]; then
    echo "::error::No commits available for '${revision_range}' and fallback ${fallback_path} is missing."
    exit 1
  fi
  cp "${fallback_path}" "${output_path}"
  echo "::warning::No commits available; using ${fallback_path} as the changelog."
fi

cat "${output_path}"
