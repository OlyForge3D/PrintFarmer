#!/usr/bin/env python3
"""Hosted-only, one-invocation observer; never changes or repeats XCTest."""

import argparse
import json
import os
from pathlib import Path
import resource
import subprocess
import sys
import threading
import time


MAX_WINDOWS = 24
MAX_STORAGE = 50 * 1024 * 1024
FILE_LIMIT = 2 * 1024 * 1024
RECEIVERS = {"logd", "testmanagerd", "xcodebuild", "CoreSimulatorService"}


def processes(text):
    result = {}
    for line in text.splitlines():
        fields = line.strip().split(None, 1)
        if len(fields) == 2 and fields[0].isdigit():
            result[int(fields[0])] = fields[1]
    return result


def is_app(executable, udid):
    return (executable.startswith("/Users/")
            and f"/Library/Developer/CoreSimulator/Devices/{udid}/data/Containers/Bundle/Application/" in executable
            and executable.endswith("/PrintFarmer.app/PrintFarmer"))


def limit_file():
    resource.setrlimit(resource.RLIMIT_FSIZE, (FILE_LIMIT, FILE_LIMIT))


class Capture:
    def __init__(self, udid, output):
        self.udid = udid
        self.output = output
        self.stop = threading.Event()
        self.errors = []
        self.seen = set()
        self.windows = 0
        self.saturated = False

    def event(self, kind, **fields):
        with (self.output / "capture.jsonl").open("a") as stream:
            stream.write(json.dumps({"unixTime": time.time(), "kind": kind, **fields}) + "\n")

    def table(self):
        result = subprocess.run(
            ["/bin/ps", "-axo", "pid=,comm="],
            capture_output=True, text=True, timeout=3, check=True,
        )
        return processes(result.stdout)

    def verify(self, pid):
        result = subprocess.run(
            ["/bin/ps", "-p", str(pid), "-o", "comm="],
            capture_output=True, text=True, timeout=3, check=True,
        )
        if not is_app(result.stdout.strip(), self.udid):
            raise ValueError(f"PID {pid} no longer identifies the selected simulator app")

    def command(self, argv, target):
        started = time.time()
        # Bound each observer's output, not the test process or its evidence.
        with target.open("wb") as output:
            result = subprocess.run(
                [sys.executable, str(Path(__file__).resolve()), "--observer-command", *argv],
                stdout=output, stderr=subprocess.STDOUT, timeout=6, check=False,
            )
        self.event("observer-command", argv=argv, started=started,
                   finished=time.time(), exitCode=result.returncode, file=target.name)
        if result.returncode:
            raise RuntimeError(f"Observer exited {result.returncode}: {argv}; see {target}")

    def window(self, pid, table):
        self.verify(pid)
        self.seen.add(pid)
        self.windows += 1
        prefix = self.output / f"window-{self.windows:02d}-pid-{pid}"
        self.event("sample-start", pid=pid, executable=table[pid], simulator=self.udid)
        selected = {p: executable for p, executable in table.items()
                    if p == pid or Path(executable).name in RECEIVERS}
        prefix.with_suffix(".identities.json").write_text(json.dumps(selected, indent=2))
        pids = ",".join(str(p) for p in sorted(selected))
        self.command(
            ["/bin/ps", "-p", pids, "-o", "pid=,ppid=,state=,pcpu=,pmem=,etime=,comm="],
            prefix.with_suffix(".processes.txt"),
        )
        self.command(
            ["/usr/bin/sudo", "-n", "/usr/sbin/lsof", "-nP", "-p", pids, "-Fpcftn"],
            prefix.with_suffix(".descriptors.txt"),
        )
        self.verify(pid)
        self.command(
            ["/usr/bin/sample", str(pid), "1", "10"],
            prefix.with_suffix(".sample.txt"),
        )

    def monitor(self):
        try:
            while not self.stop.is_set() and self.windows < MAX_WINDOWS:
                if sum(p.stat().st_size for p in self.output.iterdir()) >= MAX_STORAGE - 8 * FILE_LIMIT:
                    raise RuntimeError("Observer storage budget reached; stopping collection")
                table = self.table()
                apps = [pid for pid, executable in table.items() if is_app(executable, self.udid)]
                if len(apps) > 1:
                    raise ValueError(f"Ambiguous selected-simulator app identity: {apps}")
                if apps:
                    self.window(apps[0], table)
                self.stop.wait(2)
            self.saturated = self.windows == MAX_WINDOWS
            if self.saturated:
                self.event("sample-budget-exhausted", windows=self.windows)
        except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
            self.errors.append(str(error))
            self.event("capture-error", error=str(error))

    def finish(self, test_status):
        if not self.seen:
            self.errors.append("No verified app PID observed; capture readiness not established")
        summary = {
            "appPids": sorted(self.seen), "windows": self.windows,
            "maxWindows": MAX_WINDOWS, "sampleSecondsPerWindow": 1,
            "storageBytes": sum(p.stat().st_size for p in self.output.iterdir()),
            "sampleBudgetExhausted": self.saturated, "errors": self.errors,
            "testExitCode": test_status,
            "interpretation": "No captured stall is inconclusive, not a verified fix.",
        }
        (self.output / "summary.json").write_text(json.dumps(summary, indent=2))
        print(json.dumps(summary), flush=True)
        return test_status if test_status else (2 if self.errors else 0)


def main():
    if sys.argv[1:2] == ["--observer-command"]:
        if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_OS") != "macOS":
            raise RuntimeError("Observer commands require hosted macOS")
        limit_file()
        os.execv(sys.argv[2], sys.argv[2:])
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--udid", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_OS") != "macOS":
        parser.error("Hosted macOS Actions execution required; local capture is not authorized")
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    expected = "-only-testing:PrintFarmerUITests/FilamentCoverageUITests/testDuplicateDisplayNamePrintersRemainDistinctWithoutCoverageBadges"
    if (command[:3] != ["python3", "scripts/run-tests.py", "--"]
            or command.count(expected) != 1
            or sum(value.startswith("-only-testing:") for value in command) != 1):
        parser.error("Only the approved single test through the bounded runner is permitted")
    args.output.mkdir(parents=True, exist_ok=False)
    capture = Capture(args.udid, args.output)
    monitor = threading.Thread(target=capture.monitor)
    monitor.start()
    try:
        status = subprocess.run(command, check=False).returncode
    finally:
        capture.stop.set()
        monitor.join()
    return capture.finish(status)


if __name__ == "__main__":
    sys.exit(main())
