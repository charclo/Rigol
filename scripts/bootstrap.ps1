#!/usr/bin/env pwsh
# One-time setup for this repo's git submodules (currently NET_IPTE_SCPI).
# Run this once after cloning:
#
#   ./scripts/bootstrap.ps1
#
# See scripts/bootstrap.sh for what this does and why.
#
# Reusing this in another driver repo (e.g. a DMM driver) that also uses
# NET_IPTE_SCPI as a submodule: copy this file and .githooks/ over, add the
# submodule the normal way (`git submodule add <url> <path>`), and run this
# script. See README.md for the full checklist.

$ErrorActionPreference = "Stop"

$repoRoot = git rev-parse --show-toplevel
Set-Location $repoRoot

Write-Host "==> Initializing submodules..."
git submodule update --init --recursive

Write-Host "==> Enabling automatic submodule sync on checkout/pull..."
git config submodule.recurse true

Write-Host "==> Installing git hooks (.githooks)..."
git config core.hooksPath .githooks

Write-Host "Done. Submodules are ready and will stay in sync automatically."
