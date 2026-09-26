#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ "$(git branch --show-current)" != dev ]]; then
  printf '%s\n' 'Switch to dev before publishing.' >&2
  exit 1
fi
if [[ -n "$(git status --porcelain)" ]]; then
  printf '%s\n' 'Commit or remove local changes before publishing.' >&2
  exit 1
fi

git fetch origin
local_dev_commit="$(git rev-parse refs/heads/dev)"
remote_dev_commit="$(git rev-parse refs/remotes/origin/dev)"
if [[ "$local_dev_commit" != "$remote_dev_commit" ]]; then
  printf '%s\n' 'Push dev and confirm it is up to date before publishing.' >&2
  exit 1
fi

remote_main_commit="$(git rev-parse refs/remotes/origin/main)"
git push --force-with-lease="refs/heads/main:$remote_main_commit" origin \
  refs/heads/dev:refs/heads/main
