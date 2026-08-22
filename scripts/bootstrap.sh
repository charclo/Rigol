#!/bin/sh
# One-time setup for this repo's git submodules (currently NET_IPTE_SCPI).
# Run this once after cloning:
#
#   ./scripts/bootstrap.sh
#
# What it does:
#   1. Fetches and checks out all submodules, recursively.
#   2. Makes `git pull` / `git checkout` keep submodules in sync automatically
#      from now on (git's built-in `submodule.recurse` setting).
#   3. Points git at the hooks in .githooks/, which catch the workflows
#      `submodule.recurse` doesn't cover (e.g. `git fetch && git merge`).
#
# After this, you should never need to run `git submodule update --init`
# by hand again in this repo.
#
# Reusing this in another driver repo (e.g. a DMM driver) that also uses
# NET_IPTE_SCPI as a submodule: copy this file and .githooks/ over, add the
# submodule the normal way (`git submodule add <url> <path>`), and run this
# script. See README.md for the full checklist.

set -e

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

echo "==> Initializing submodules..."
git submodule update --init --recursive

echo "==> Enabling automatic submodule sync on checkout/pull..."
git config submodule.recurse true

echo "==> Installing git hooks (.githooks)..."
git config core.hooksPath .githooks

echo "Done. Submodules are ready and will stay in sync automatically."
