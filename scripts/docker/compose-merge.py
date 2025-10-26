#!/usr/bin/env python3
"""
compose-merge.py

Merge two docker-compose YAML files using ruamel.yaml with safe merging rules:
- Top-level 'services': add missing services from addon; for existing services, merge fields:
  - lists are concatenated with deduplication (order-preserving)
  - environment is normalized and merged (addon overrides)
  - other scalar keys: base wins unless missing
- Top-level 'volumes' and 'networks' are merged (addon entries added)

Usage: compose-merge.py base.yml addon.yml > merged.yml
"""

import sys
from ruamel.yaml import YAML
from collections import OrderedDict

yaml = YAML()
yaml.preserve_quotes = True
yaml.width = 4096

def load(path):
    try:
        with open(path) as f:
            return yaml.load(f) or {}
    except Exception as e:
        print(f"Failed to load {path}: {e}", file=sys.stderr)
        return {}

def to_dict_env(env):
    # env can be a list of KEY=VAL or a mapping
    if env is None:
        return {}
    if isinstance(env, dict):
        return dict(env)
    d = {}
    for e in env:
        if isinstance(e, str) and '=' in e:
            k,v = e.split('=',1)
            d[k]=v
    return d

def from_dict_env(d, prefer_mapping=False):
    if prefer_mapping:
        return d
    # return list form
    return [f"{k}={v}" for k,v in d.items()]

def dedupe_list(a,b):
    # preserve order: items in a then any new items from b
    seen = set()
    out = []
    for x in (a or []):
        if x not in seen:
            seen.add(x); out.append(x)
    for x in (b or []):
        if x not in seen:
            seen.add(x); out.append(x)
    return out

def merge_service(base_svc, addon_svc):
    if base_svc is None:
        return addon_svc
    if addon_svc is None:
        return base_svc
    # Work on a copy
    out = base_svc
    # Merge environment
    base_env = to_dict_env(base_svc.get('environment'))
    addon_env = to_dict_env(addon_svc.get('environment'))
    merged_env = base_env.copy()
    merged_env.update(addon_env)
    # prefer mapping output if base used mapping
    prefer_map = isinstance(base_svc.get('environment'), dict)
    out['environment'] = from_dict_env(merged_env, prefer_mapping=prefer_map)

    # Merge networks
    out['networks'] = dedupe_list(base_svc.get('networks') or [], addon_svc.get('networks') or [])

    # Merge volumes
    out['volumes'] = dedupe_list(base_svc.get('volumes') or [], addon_svc.get('volumes') or [])

    # Merge lists like cap_drop, security_opt, devices
    for key in ('cap_drop','security_opt','devices','tmpfs'):
        if addon_svc.get(key) is not None:
            out[key] = dedupe_list(base_svc.get(key) or [], addon_svc.get(key) or [])

    # For other keys, set if missing in base
    for k,v in addon_svc.items():
        if k in ('environment','networks','volumes','cap_drop','security_opt','devices','tmpfs'):
            continue
        if k not in out or out.get(k) is None:
            out[k]=v
    return out

def merge(base, addon):
    out = base.copy()
    # Merge services
    base_services = base.get('services') or {}
    addon_services = addon.get('services') or {}
    if 'services' not in out:
        out['services'] = {}
    for name, svc in addon_services.items():
        if name in base_services:
            out['services'][name] = merge_service(base_services[name], svc)
        else:
            out['services'][name] = svc

    # Merge volumes/networks (mappings)
    for key in ('volumes','networks'):
        base_map = base.get(key) or {}
        addon_map = addon.get(key) or {}
        merged = base_map.copy()
        for k,v in (addon_map.items() if hasattr(addon_map,'items') else []):
            if k not in merged:
                merged[k]=v
        if merged:
            out[key]=merged

    # Merge other top-level keys conservatively: add if missing
    for k,v in addon.items():
        if k in ('services','volumes','networks'):
            continue
        if k not in out:
            out[k]=v

    return out

def main():
    if len(sys.argv) != 3:
        print("Usage: compose-merge.py base.yml addon.yml", file=sys.stderr)
        sys.exit(2)
    base = load(sys.argv[1])
    addon = load(sys.argv[2])
    merged = merge(base, addon)
    yaml.dump(merged, sys.stdout)

if __name__ == '__main__':
    main()
