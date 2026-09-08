#!/usr/bin/env python3
"""Run the existing serial simulator XCTest command with bounded finalization."""

import argparse
import json
import math
import os
import signal
import subprocess
import sys
import time
from pathlib import Path


class TestEvents:
    """Read Xcode's -resultStreamPath JSONL, including partially written lines."""

    def __init__(self):
        self.active = set()
        self.tests = []
        self.first_started = None
        self.last_finished = None
        self.idle_since = None
        self.invocation_finished = False

    def read(self, stream, now):
        while True:
            offset = stream.tell()
            line = stream.readline()
            if not line.endswith("\n"):
                stream.seek(offset)
                return
            event = json.loads(line)
            name = event["name"]["_value"]
            payload = event.get("structuredPayload", {})
            if name == "invocationFinished":
                self.invocation_finished = True
            if name == "testStarted":
                identifier = payload["testIdentifier"]["identifier"]["_value"]
                self.active.add(identifier)
                if self.first_started is None:
                    self.first_started = now
                self.idle_since = None
            elif name == "testFinished":
                test = payload["test"]
                identifier = test["identifier"]["_value"]
                duration = float(test["duration"]["_value"])
                if not math.isfinite(duration) or duration < 0:
                    raise ValueError("Invalid test duration in result stream")
                self.active.discard(identifier)
                self.tests.append({
                    "identifier": identifier,
                    "status": test["testStatus"]["_value"],
                    "reported_seconds": duration,
                })
                self.last_finished = now
                if not self.active:
                    self.idle_since = now


def option_values(arguments, option):
    values = []
    for index, argument in enumerate(arguments):
        if argument == option:
            if index + 1 == len(arguments):
                raise ValueError(f"{option} requires a value")
            values.append(arguments[index + 1])
    return values


def validate_arguments(arguments):
    if sum(arg in ("test", "test-without-building") for arg in arguments) != 1:
        raise ValueError("Specify one test or test-without-building action")
    for option in (
        "-collect-test-diagnostics", "-resultStreamPath", "-retry-tests-on-failure",
        "-run-tests-until-failure", "-test-iterations", "-test-repetition-relaunch-enabled",
    ):
        if any(arg.split("=")[0] == option for arg in arguments):
            raise ValueError(f"{option} is managed by the runner or permits test repetition")
    for option, expected in (
        ("-parallel-testing-enabled", "NO"), ("-test-timeouts-enabled", "YES"),
    ):
        if any(value != expected for value in option_values(arguments, option)):
            raise ValueError(f"{option} must be {expected}")
    destinations = option_values(arguments, "-destination")
    if len(destinations) != 1:
        raise ValueError("Specify exactly one resolved iOS Simulator destination")
    fields = dict(item.split("=", 1) for item in destinations[0].split(","))
    if fields.get("platform") != "iOS Simulator" or not fields.get("id"):
        raise ValueError("Use the shared resolver's iOS Simulator UDID destination")
    bundles = option_values(arguments, "-resultBundlePath")
    if len(bundles) != 1:
        raise ValueError("Specify exactly one -resultBundlePath to retain artifacts")
    bundle = Path(bundles[0])
    if bundle.exists():
        raise ValueError(f"Result bundle already exists: {bundle}")
    return bundle


def run(arguments, invocation_timeout=1440, finalization_timeout=120,
        flush_seconds=10, poll_seconds=0.1, executable="xcodebuild"):
    """Budgets include graceful interrupt time; never repeat a test invocation."""
    bundle = validate_arguments(arguments)
    for budget in (invocation_timeout, finalization_timeout):
        if not math.isfinite(budget) or budget <= flush_seconds:
            raise ValueError(f"Budgets must be finite and greater than {flush_seconds}s")
    stream_path = bundle.with_suffix(".events.jsonl")
    timing_path = bundle.with_suffix(".timing.json")
    bundle.parent.mkdir(parents=True, exist_ok=True)
    # Exclusive creation prevents overwriting evidence from an earlier invocation.
    with stream_path.open("x"), timing_path.open("x"):
        pass
    command = [
        executable, *arguments,
        "-collect-test-diagnostics", "never",
        "-test-timeouts-enabled", "YES",
        "-parallel-testing-enabled", "NO",
        "-disable-concurrent-destination-testing",
        "-resultStreamPath", str(stream_path),
    ]
    print(
        f"IOS_RUNNER: verbose simulator collection=never; post-test/restart budget="
        f"{finalization_timeout}s; invocation budget={invocation_timeout}s "
        f"(each includes {flush_seconds}s interrupt/flush)", flush=True,
    )
    events = TestEvents()
    started = time.monotonic()
    deadline = started + invocation_timeout
    reason = None
    error = None
    interrupted = None
    process = subprocess.Popen(command, start_new_session=True)

    def signal_process_group(sig):
        # This group belongs exclusively to this invocation, not simctl services.
        try:
            os.killpg(process.pid, sig)
        except ProcessLookupError:
            pass

    def cancel(signum, _frame):
        nonlocal interrupted
        interrupted = signum

    previous_handlers = {sig: signal.signal(sig, cancel)
                         for sig in (signal.SIGINT, signal.SIGTERM)}
    try:
        with stream_path.open() as stream:
            while True:
                now = time.monotonic()
                if error is None:
                    try:
                        events.read(stream, now)
                    except (ValueError, KeyError, TypeError) as exc:
                        error = str(exc)
                if process.poll() is not None:
                    if error is None:
                        try:
                            events.read(stream, time.monotonic())
                        except (ValueError, KeyError, TypeError) as exc:
                            error = str(exc)
                    break
                if reason is None:
                    if interrupted:
                        reason = "cancelled"
                        deadline = now + flush_seconds
                    elif error is not None:
                        reason = "invalid-result-stream"
                        deadline = now + flush_seconds
                    else:
                        deadline = started + invocation_timeout
                        next_reason = "invocation-timeout"
                        if events.idle_since is not None:
                            idle_deadline = events.idle_since + finalization_timeout
                            if idle_deadline < deadline:
                                deadline = idle_deadline
                                next_reason = "post-test-timeout"
                        if now >= deadline - flush_seconds:
                            reason = next_reason
                    if reason is not None:
                        print(f"IOS_RUNNER: {reason}; interrupting xcodebuild to flush "
                              "the result bundle (failure remains nonzero)", flush=True)
                        signal_process_group(signal.SIGINT)
                if reason is not None and now >= deadline:
                    signal_process_group(signal.SIGKILL)
                time.sleep(poll_seconds)
        native_exit = process.wait()
    finally:
        if process.poll() is None:
            signal_process_group(signal.SIGKILL)
            process.wait()
        elif reason is not None:
            # A descendant retaining stdout must not strand the caller's tee.
            signal_process_group(signal.SIGKILL)
        for sig, handler in previous_handlers.items():
            signal.signal(sig, handler)

    finished = time.monotonic()
    elapsed = finished - started
    reported = sum(test["reported_seconds"] for test in events.tests)
    status = native_exit if native_exit >= 0 else 128 - native_exit
    if reason == "cancelled":
        status = 128 + interrupted
    elif reason in ("invocation-timeout", "post-test-timeout"):
        status = 124
    elif error is not None or (status == 0 and (
        not events.tests or events.active or not events.invocation_finished
    )):
        status = 70
        reason = "incomplete-result-stream"
    elif status == 0 and any(test["status"] in ("Failure", "Failed")
                            for test in events.tests):
        status = 65
        reason = "failed-test-in-result-stream"
    summary = {
        "exit_code": status,
        "xcodebuild_exit_code": native_exit,
        "termination_reason": reason,
        "stream_error": error,
        "invocation_seconds": elapsed,
        "reported_test_seconds": reported,
        "non_test_overhead_seconds": max(0, elapsed - reported),
        "post_last_test_seconds": (finished - events.last_finished
                                   if events.last_finished is not None else None),
        "finalization_budget_seconds": finalization_timeout,
        "invocation_budget_seconds": invocation_timeout,
        "flush_seconds_included": flush_seconds,
        "verbose_simulator_diagnostics": "never",
        "tests": events.tests,
    }
    timing_path.write_text(json.dumps(summary, indent=2) + "\n")
    print(f"IOS_RUNNER: test-reported={reported:.3f}s; "
          f"non-test overhead={summary['non_test_overhead_seconds']:.3f}s; "
          f"post-last-test={summary['post_last_test_seconds']}; exit={status}; "
          f"timing={timing_path}", flush=True)
    return status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--invocation-timeout", type=float, default=1440)
    parser.add_argument("--finalization-timeout", type=float, default=120)
    parser.add_argument("xcodebuild_arguments", nargs=argparse.REMAINDER,
                        help="-- test[-without-building] and existing xcodebuild arguments")
    options = parser.parse_args()
    arguments = options.xcodebuild_arguments
    if arguments[:1] == ["--"]:
        arguments = arguments[1:]
    try:
        return run(arguments, options.invocation_timeout, options.finalization_timeout)
    except (ValueError, OSError) as exc:
        print(f"IOS_RUNNER: {exc}", file=sys.stderr)
        return 70


if __name__ == "__main__":
    sys.exit(main())
