#!/bin/sh
# Takes a bundle produced by scripts/site-sync.sh on a machine with no route
# to GitHub, and gets its commits pushed to origin from here.
#
# Usage: ./scripts/apply-bundle.sh <path-to-bundle> [branch-name]
#   branch-name defaults to the branch recorded in the bundle itself.
#
# Run this in an up-to-date clone of the repo, on a machine that *can*
# reach GitHub.

set -e

bundle_file="$1"
if [ -z "$bundle_file" ]; then
    echo "Usage: $0 <path-to-bundle> [branch-name]" >&2
    exit 1
fi

repo_root=$(git rev-parse --show-toplevel)
cd "$repo_root"

echo "==> Verifying bundle..."
git bundle verify "$bundle_file"

branch=${2:-$(git bundle list-heads "$bundle_file" | head -1 | sed 's#.*/##')}
import_branch="site-import/$branch"

echo "==> Fetching '$branch' from the bundle into local branch '$import_branch'..."
git fetch "$bundle_file" "$branch:$import_branch"

echo
echo "Commits are now on local branch '$import_branch'. Review them first:"
echo "  git log origin/$branch..$import_branch"
echo
echo "Once you're happy, push them onto the real branch, e.g.:"
echo "  git push origin $import_branch:$branch"
echo
echo "Then delete the local import branch:"
echo "  git branch -d $import_branch"
