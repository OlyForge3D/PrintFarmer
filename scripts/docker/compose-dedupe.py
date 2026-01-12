#!/usr/bin/env python3
"""
compose-dedupe.py - Post-process merged docker-compose YAML and dedupe list items.

This is a conservative, line-oriented deduper: it tracks the current mapping key
and the indentation of list items and skips duplicate `- item` lines that appear
within the same mapping key + indentation scope. It intentionally avoids a full
YAML parse to keep zero-dependency portability.

Usage: ./scripts/docker/compose-dedupe.sh < merged.yml > deduped.yml
"""

import sys
import re

key_re = re.compile(r'^(?P<indent>\s*)(?P<key>[A-Za-z0-9_\-]+):\s*$')
item_re = re.compile(r'^(?P<indent>\s*)-\s*(?P<item>.*)$')

cur_key = None
seen = set()
try:
    for raw in sys.stdin:
        line = raw.rstrip('\n')

        mkey = key_re.match(line)
        if mkey:
            cur_key = mkey.group('key')
            # reset seen when encountering a new mapping key
            seen.clear()
            print(line)
            continue

        mitem = item_re.match(line)
        if mitem and cur_key is not None:
            indent = len(mitem.group('indent'))
            item = mitem.group('item')
            scope_key = (cur_key, indent, item)
            if scope_key in seen:
                # skip duplicate list item in same key + indent scope
                continue
            seen.add(scope_key)
            print(line)
            continue

        # default: print line as-is
        print(line)
except BrokenPipeError:
    # allow piping to head/cut without error
    sys.exit(0)
