#!/usr/bin/env python3
"""Retain read-only main-thread samples for one simulator's UI-test app."""

import argparse
import re
import signal
import subprocess
import time
from pathlib import Path
from uuid import UUID


def app_pids(process_listing, device_id):
    marker = f"/CoreSimulator/Devices/{device_id}/"
    return [
        int(pid)
        for line in process_listing.splitlines()
        if len(fields := line.strip().split(maxsplit=1)) == 2
        for pid, command in [fields]
        if pid.isdecimal() and marker in command
        and re.search(r"/PrintFarmer\.app/PrintFarmer(?:\s|$)", command)
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", required=True, type=UUID)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    stopping = False

    def stop(_signal, _frame):
        nonlocal stopping
        stopping = True

    signal.signal(signal.SIGTERM, stop)
    signal.signal(signal.SIGINT, stop)
    seen = set()
    samples = []
    deadline = time.monotonic() + 1440
    try:
        while not stopping and time.monotonic() < deadline:
            listing = subprocess.check_output(["ps", "-axo", "pid=,command="], text=True)
            for pid in app_pids(listing, str(args.device).upper()):
                if pid in seen:
                    continue
                seen.add(pid)
                print(f"Sampling app pid={pid} uptime={time.monotonic()}", flush=True)
                output = args.output / f"app-{pid}.sample.txt"
                log = (args.output / f"app-{pid}.sample.log").open("w")
                # Cover startup and the first AX query without asking the main
                # thread to cooperate. The samples include all thread stacks.
                process = subprocess.Popen(
                    ["sample", str(pid), "30", "100", "-file", str(output)],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                samples.append((process, log))
            time.sleep(0.5)
    finally:
        for process, log in samples:
            if process.poll() is None:
                process.send_signal(signal.SIGINT)
            try:
                status = process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.terminate()
                status = process.wait(timeout=5)
            print(f"sample pid={process.pid} exit={status}", flush=True)
            log.close()
    if not seen:
        raise RuntimeError("No selected-simulator PrintFarmer process was observed; no stacks captured")


if __name__ == "__main__":
    main()
