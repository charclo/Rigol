#!/bin/sh
# Get commits made on this machine back to GitHub -- works whether or not
# this machine (e.g. a customer's production PC) has a route to github.com.
#
# Usage: ./scripts/site-sync.sh [branch]
#   branch defaults to the current branch.
#
# What it does:
#   1. Tries a normal `git push -u origin <branch>`. If that works, done.
#   2. If it fails -- most likely no route to GitHub from this site -- it
#      instead writes a portable `git bundle` file containing exactly the
#      commits on <branch> that origin doesn't have yet, and tells you what
#      to do with it. (If the push instead failed for some other reason,
#      e.g. a rejected non-fast-forward push, fix that instead of using the
#      bundle it still creates.)
#
# Carry the resulting file off this machine (USB stick, shared drive,
# email, ...) and hand it to scripts/apply-bundle.sh on a connected machine
# to get it pushed to GitHub from there.

set -e

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

branch=${1:-$(git rev-parse --abbrev-ref HEAD)}

echo "==> Trying to push '$branch' to origin..."
if git push -u origin "$branch"; then
    echo "Pushed directly -- done, no bundle needed."
    exit 0
fi

echo
echo "==> Push failed. If that's because this site has no route to GitHub,"
echo "    here's a bundle file instead. (If it failed for another reason --"
echo "    e.g. a rejected non-fast-forward push -- fix that instead; the"
echo "    bundle below still works, but pushing directly is simpler.)"
echo

mkdir -p site-bundles
timestamp=$(date +%Y%m%d-%H%M%S)
safe_branch=$(echo "$branch" | tr '/' '-')
bundle_file="site-bundles/${safe_branch}-${timestamp}.bundle"

# Bundle only what origin doesn't have yet, if we know that; otherwise bundle
# the whole branch.
if git rev-parse --verify -q "refs/remotes/origin/$branch" >/dev/null; then
    range="origin/$branch..$branch"
else
    range="$branch"
fi

git bundle create "$bundle_file" "$range"

echo "Wrote $bundle_file"
echo
echo "Copy this single file off this machine and run, on a connected machine:"
echo "  ./scripts/apply-bundle.sh $bundle_file $branch"
