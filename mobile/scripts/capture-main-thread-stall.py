#!/usr/bin/env python3
"""One-shot diagnostic wrapper; never changes or repeats the XCTest command."""

import argparse
import json
import subprocess
import sys
import threading
import time
from pathlib import Path


BUNDLE = "com.olyforge3d.printfarmer.ios"
MARKER = "Library/Caches/pfarm-main-thread-capture.json"
MAX_SAMPLES = 12
MAX_STORAGE = 50 * 1024 * 1024


def read_marker(path, now):
    if not path.exists():
        return None
    if path.stat().st_size > 4096:
        raise ValueError("Oversized capture marker")
    record = json.loads(path.read_text())
    if (type(record.get("pid")) is not int or record["pid"] <= 0
            or type(record.get("beat")) is not int or record["beat"] < 1):
        raise ValueError("Invalid capture process/beat identity")
    for key in ("unixTime", "uptime", "stalled"):
        value = record.get(key)
        if type(value) not in (int, float) or not 0 <= value < float("inf"):
            raise ValueError(f"Invalid capture {key}")
    if not 0 <= now - record["unixTime"] <= 3:
        return None
    return record


def verified_process(pid, udid, run=subprocess.run):
    result = run(
        ["/bin/ps", "-p", str(pid), "-o", "command="],
        capture_output=True, text=True, timeout=2, check=False,
    )
    command = result.stdout.strip()
    executable_result = run(
        ["/bin/ps", "-p", str(pid), "-o", "comm="],
        capture_output=True, text=True, timeout=2, check=False,
    )
    executable = executable_result.stdout.strip()
    if result.returncode or executable_result.returncode or not command:
        raise ValueError(f"Cannot resolve live process identity for pid {pid}")
    if f"/{udid}/" not in executable:
        raise ValueError(f"pid {pid} is not in selected simulator {udid}: {executable}")
    # macOS comm is the executable path without launch arguments. Neither
    # trailing arguments nor a target-looking argument can change its identity.
    if not executable.endswith("/PrintFarmer.app/PrintFarmer"):
        raise ValueError(f"pid {pid} is not the PrintFarmer app: {executable}")
    return command


class Capture:
    def __init__(self, udid, output):
        self.udid = udid
        self.output = output
        self.stop = threading.Event()
        self.error = None
        self.seen = set()
        self.sampled = set()
        self.samples = 0
        self.marker = None

    def event(self, kind, **fields):
        with (self.output / "capture.jsonl").open("a") as stream:
            stream.write(json.dumps({"unixTime": time.time(), "kind": kind, **fields}) + "\n")

    def sample(self, record):
        pid = record["pid"]
        command = verified_process(pid, self.udid)
        self.event("sampling", record=record, command=command, simulator=self.udid)
        self.sampled.add((pid, record["beat"]))
        # Two adjacent five-second windows finish before the unchanged 20s abort.
        for window in range(2):
            if self.stop.is_set():
                break
            if self.samples >= MAX_SAMPLES:
                raise RuntimeError("Diagnostic sample count limit reached")
            if sum(p.stat().st_size for p in self.output.iterdir() if p.is_file()) >= MAX_STORAGE:
                raise RuntimeError("Diagnostic storage limit reached")
            verified_process(pid, self.udid)
            target = self.output / f"pid-{pid}-beat-{record['beat']}-window-{window}.sample.txt"
            self.samples += 1
            started = time.time()
            result = subprocess.run(
                ["/usr/bin/sample", str(pid), "5", "10", "-file", str(target)],
                capture_output=True, text=True, timeout=8, check=False,
            )
            (target.with_suffix(".status.json")).write_text(json.dumps({
                "pid": pid, "simulator": self.udid, "command": command,
                "startedUnixTime": started, "finishedUnixTime": time.time(),
                "exitCode": result.returncode, "stdout": result.stdout, "stderr": result.stderr,
            }, indent=2))
            self.event("sample-finished", pid=pid, window=window, exitCode=result.returncode)
            if result.returncode or not target.exists() or target.stat().st_size == 0:
                raise RuntimeError(f"sample failed for pid {pid}: {result.stderr}")

    def monitor(self):
        try:
            while not self.stop.is_set():
                if self.marker is None:
                    result = subprocess.run(
                        ["xcrun", "simctl", "get_app_container", self.udid, BUNDLE, "data"],
                        capture_output=True, text=True, timeout=5, check=False,
                    )
                    if result.returncode:
                        self.event("waiting-for-install", status=result.returncode, stderr=result.stderr)
                        self.stop.wait(1)
                        continue
                    container = Path(result.stdout.strip())
                    if not container.is_absolute() or self.udid not in container.parts:
                        raise ValueError(f"Unexpected app container: {container}")
                    self.marker = container / MARKER
                    self.event("container", path=str(container), simulator=self.udid)
                record = read_marker(self.marker, time.time())
                if record is not None:
                    self.seen.add(record["pid"])
                    self.event("heartbeat", **record)
                    if record["stalled"] >= 2 and (record["pid"], record["beat"]) not in self.sampled:
                        self.sample(record)
                self.stop.wait(0.25)
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            self.error = str(error)
            self.event("capture-error", error=self.error)

    def finish(self):
        if self.error is None and not self.seen:
            self.error = "No fresh app markers observed; capture instrumentation did not establish readiness"
        self.event("summary", appPids=sorted(self.seen), sampledWindows=sorted(self.sampled),
                   samples=self.samples, error=self.error)
        if self.error:
            print(f"CAPTURE FAILED: {self.error}", file=sys.stderr)
        else:
            print(f"CAPTURE: {len(self.seen)} app processes, {self.samples} samples; "
                  "absence of a captured stall is inconclusive", flush=True)
        return 2 if self.error else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--udid", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("An existing bounded test-runner command is required")
    args.output.mkdir(parents=True, exist_ok=False)
    capture = Capture(args.udid, args.output)
    monitor = threading.Thread(target=capture.monitor)
    monitor.start()
    try:
        status = subprocess.run(command, check=False).returncode
    finally:
        capture.stop.set()
        monitor.join()
    capture_status = capture.finish()
    return status if status else capture_status


if __name__ == "__main__":
    sys.exit(main())
