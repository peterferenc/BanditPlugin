#!/usr/bin/env bash
# Push wiki/*.md to the GitHub wiki.
#
# The wiki is a second git repository alongside the code one, and GitHub only
# creates it once the first page exists. If this fails with "Repository not
# found", open https://github.com/peterferenc/BanditPlugin/wiki, save any page,
# and run it again.
set -euo pipefail

WIKI_REMOTE="https://github.com/peterferenc/BanditPlugin.wiki.git"
PAGES="$(cd "$(dirname "$0")" && pwd)"
CHECKOUT="$(mktemp -d)"
trap 'rm -rf "$CHECKOUT"' EXIT

git clone --depth 1 "$WIKI_REMOTE" "$CHECKOUT"
find "$CHECKOUT" -maxdepth 1 -name '*.md' -delete
cp "$PAGES"/*.md "$CHECKOUT/"

cd "$CHECKOUT"
if git diff --quiet && git diff --cached --quiet && [ -z "$(git status --porcelain)" ]; then
    echo "Wiki already matches wiki/ - nothing to push."
    exit 0
fi

git add -A
git commit -m "Update wiki from README"
git push
echo "Pushed to https://github.com/peterferenc/BanditPlugin/wiki"
