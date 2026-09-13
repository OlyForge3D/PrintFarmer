#!/usr/bin/env bash
set -euo pipefail

printf '%s\n' \
  'ERROR: publish-to-public.sh is retired; publication is owned by consolidated-release.yml.' \
  'Review VERSION on main (stable) or development (insider), then dispatch that workflow.' \
  'See docs/RELEASE_GUIDE.md. No branches, tags, releases, assets or containers were changed.' >&2
exit 2
