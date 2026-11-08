"""Select a named modern simulator type, never the last legacy inventory entry."""

import argparse
import json
import sys


def select_device(inventory, family, preferences):
    for name in preferences.split(","):
        matches = [device for device in inventory["devicetypes"]
                   if device["name"] == name.strip() and device["productFamily"] == family]
        if len(matches) > 1:
            raise ValueError(f"Ambiguous simulator type: {name}")
        if matches:
            return matches[0]
    raise ValueError(f"No installed {family} simulator type matches explicit preferences")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--family", choices=("iPhone", "iPad"), required=True)
    parser.add_argument("--preferences", required=True)
    args = parser.parse_args()
    selected = select_device(json.load(sys.stdin), args.family, args.preferences)
    print(json.dumps({"name": selected["name"], "identifier": selected["identifier"]}))


if __name__ == "__main__":
    main()
