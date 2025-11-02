#!/usr/bin/env python3
"""
compose-replace-db.py

Replace the 'database' service in a Docker Compose YAML file with a new database service config.

Usage: compose-replace-db.py compose.yml "database_service_yaml" > output.yml
"""

import sys
import io
from ruamel.yaml import YAML

def main():
    if len(sys.argv) != 3:
        print("Usage: compose-replace-db.py compose.yml 'database_service_yaml'", file=sys.stderr)
        sys.exit(1)
    
    compose_path = sys.argv[1]
    db_service_yaml = sys.argv[2]
    
    yaml = YAML()
    yaml.preserve_quotes = True
    yaml.width = 4096
    
    # Load main compose file
    try:
        with open(compose_path) as f:
            compose = yaml.load(f) or {}
    except Exception as e:
        print(f"Failed to load {compose_path}: {e}", file=sys.stderr)
        sys.exit(1)
    
    # Remove leading/trailing whitespace, then dedent for proper YAML parsing
    db_service_yaml = db_service_yaml.strip()
    
    # Find the minimum indentation level in the content
    lines = db_service_yaml.split('\n')
    min_indent = float('inf')
    for line in lines:
        if line.strip():  # Skip empty lines
            indent = len(line) - len(line.lstrip())
            if indent < min_indent:
                min_indent = indent
    
    if min_indent == float('inf'):
        min_indent = 0
    
    # Remove the common indentation from all lines
    db_service_yaml_dedented = '\n'.join(
        line[min_indent:] if len(line) > min_indent else line
        for line in lines
    )
    
    # Load database service from YAML string
    try:
        db_service = yaml.load(io.StringIO(db_service_yaml_dedented)) or {}
    except Exception as e:
        print(f"Failed to parse database service: {e}", file=sys.stderr)
        sys.exit(1)
    
    # Replace database service
    if 'services' not in compose:
        compose['services'] = {}
    
    if db_service:
        # If db_service is {'database': {...}}, extract the inner config
        if isinstance(db_service, dict) and 'database' in db_service and len(db_service) == 1:
            # Extract just the service definition
            db_service = db_service['database']
        compose['services']['database'] = db_service
    elif 'database' in compose['services']:
        del compose['services']['database']
    
    # Output merged compose
    yaml.dump(compose, sys.stdout)

if __name__ == '__main__':
    main()
