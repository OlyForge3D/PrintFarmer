#!/usr/bin/env python3
"""Diagnostic-only: sample a selected simulator app after a missing heartbeat."""

import argparse
import json
import re
import selectors
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


def capture_event(line):
    try:
        event = json.loads(line)
    except json.JSONDecodeError:
        return None
    if not isinstance(event, dict):
        return None
    message = event.get("eventMessage")
    pid = event.get("processID")
    if not isinstance(message, str) or type(pid) is not int:
        return None
    match = re.fullmatch(
        r"PFARM_STALL_CAPTURE_(READY|MISSING) pid=(\d+)(?: beat=\d+ uptime=\d+\.\d+ stalled=\d+\.\d+)?",
        message,
    )
    if not match or int(match[2]) != pid or pid <= 0:
        return None
    return match[1], pid


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
    samples = []
    observed = set()
    sampled = set()
    deadline = time.monotonic() + 1440
    with (args.output / "stream.stderr.log").open("w") as stderr, \
            (args.output / "heartbeat.ndjson").open("w") as events:
        stream = subprocess.Popen(
            ["xcrun", "simctl", "spawn", str(args.device), "log", "stream",
             "--style", "ndjson", "--level", "default",
             "--predicate", 'process == "PrintFarmer" AND eventMessage BEGINSWITH "PFARM_STALL_CAPTURE_"'],
            stdout=subprocess.PIPE, stderr=stderr,
        )
        # Read bytes, not a buffered TextIOWrapper: multiple buffered lines
        # must not wait for the next OS-level readiness notification.
        pending = b""
        with selectors.DefaultSelector() as selector:
            selector.register(stream.stdout, selectors.EVENT_READ)
            try:
                while not stopping and time.monotonic() < deadline:
                    if not selector.select(timeout=0.5):
                        if stream.poll() is not None:
                            raise RuntimeError(f"Simulator log stream exited {stream.returncode}")
                        continue
                    chunk = stream.stdout.read1(65536)
                    if not chunk:
                        raise RuntimeError("Simulator log stream ended before capture monitor stopped")
                    pending += chunk
                    while b"\n" in pending:
                        line, pending = pending.split(b"\n", 1)
                        text = line.decode("utf-8", errors="replace")
                        events.write(text + "\n")
                        events.flush()
                        event = capture_event(text)
                        if event is None:
                            continue
                        kind, pid = event
                        listing = subprocess.check_output(["ps", "-axo", "pid=,command="], text=True)
                        if pid not in app_pids(listing, str(args.device).upper()):
                            print(f"Ignoring stale/foreign pid={pid}", flush=True)
                            continue
                        observed.add(pid)
                        if kind != "MISSING" or pid in sampled:
                            continue
                        sampled.add(pid)
                        output = args.output / f"app-{pid}.sample.txt"
                        log = (args.output / f"app-{pid}.sample.log").open("w")
                        print(f"Missing heartbeat: sampling pid={pid} hostUptime={time.monotonic():.3f}", flush=True)
                        process = subprocess.Popen(
                            ["sample", str(pid), "5", "10", "-file", str(output)],
                            stdout=log, stderr=subprocess.STDOUT,
                        )
                        samples.append((process, log, output))
            finally:
                stream.terminate()
                try:
                    stream.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    stream.kill()
                    stream.wait(timeout=5)
                stream.stdout.close()
                failed = []
                for process, log, output in samples:
                    try:
                        status = process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        process.terminate()
                        status = process.wait(timeout=5)
                    log.close()
                    print(f"sample pid={process.pid} exit={status} file={output}", flush=True)
                    if status != 0 or not output.exists() or output.stat().st_size == 0:
                        failed.append(str(output))
    if failed:
        raise RuntimeError(f"Missing/failed stack captures: {failed}")
    if not observed:
        raise RuntimeError("No selected-simulator heartbeat marker observed; capture path unverified")
    print(f"Observed {len(observed)} apps; captured {len(sampled)} missing-heartbeat windows", flush=True)


if __name__ == "__main__":
    main()
