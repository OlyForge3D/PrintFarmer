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
        self.failure_issues = []
        self.unclassified_issues = []
        self.first_started = None
        self.last_finished = None
        self.idle_since = None
        self.invocation_started = 0
        self.invocation_finished = 0
        self.action_started = 0
        self.action_finished = 0
        self.stream_error = None
        self.partial_line = False

    def read(self, stream, now):
        while True:
            offset = stream.tell()
            line = stream.readline()
            if not line.endswith("\n"):
                self.partial_line = bool(line)
                stream.seek(offset)
                return
            self.partial_line = False
            event = json.loads(line)
            name = event["name"]["_value"]
            payload = event.get("structuredPayload", {})
            if name == "invocationFinished":
                self.invocation_finished += 1
            elif name == "invocationStarted":
                self.invocation_started += 1
            elif name == "actionStarted":
                self.action_started += 1
            elif name == "actionFinished":
                self.action_finished += 1
            elif name == "issueEmitted":
                issue = payload.get("issue", {})
                test_case = issue.get("testCaseName", {}).get("_value")
                severity = payload.get("severity", {}).get("_value")
                if severity == "testFailure" and isinstance(test_case, str):
                    parts = test_case.rsplit(".", 2)
                    if len(parts) >= 2:
                        self.failure_issues.append(f"{parts[-2]}/{parts[-1]}")
                    else:
                        self.unclassified_issues.append(test_case)
                else:
                    self.unclassified_issues.append(
                        test_case if isinstance(test_case, str) else "unattributed issue"
                    )
            if name == "testStarted":
                identifier = payload["testIdentifier"]["identifier"]["_value"]
                if identifier in self.active:
                    raise ValueError(f"Test started while already active: {identifier}")
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
                if identifier not in self.active:
                    raise ValueError(f"Test finished without a matching start: {identifier}")
                self.active.discard(identifier)
                self.tests.append({
                    "identifier": identifier,
                    "status": test["testStatus"]["_value"],
                    "reported_seconds": duration,
                })
                self.last_finished = now
                if not self.active:
                    self.idle_since = now


class SharedBudgets:
    """One wall-clock and cumulative idle budget shared by all test attempts."""

    def __init__(self, started):
        self.started = started
        self.idle_spent = 0
        self.idle_started = None

    def observe(self, events, now):
        if events.active:
            if self.idle_started is not None:
                self.idle_spent += max(0, now - self.idle_started)
                self.idle_started = None
        elif events.idle_since is not None and self.idle_started is None:
            self.idle_started = events.idle_since

    def idle_elapsed(self, now):
        running_idle = max(0, now - self.idle_started) if self.idle_started else 0
        return self.idle_spent + running_idle


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
        values = option_values(arguments, option)
        if len(values) > 1 or any(value != expected for value in values):
            raise ValueError(f"{option} must be {expected}")
    if arguments.count("-disable-concurrent-destination-testing") > 1:
        raise ValueError("-disable-concurrent-destination-testing may appear only once")
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


def retry_selection(arguments, events, native_exit):
    """Return exact failed-test selectors only when the complete stream proves them."""
    if native_exit != 65 or events.stream_error or events.active:
        return []
    if (events.invocation_started != 1 or events.invocation_finished != 1
            or events.action_started != 1 or events.action_finished != 1
            or not events.tests):
        return []
    if any(test["status"] not in ("Success", "Failure", "Failed")
           for test in events.tests):
        return []

    history = {}
    for test in events.tests:
        history.setdefault(test["identifier"], []).append(test["status"])
    if any(len(statuses) != 1 for statuses in history.values()):
        return []
    failed = {
        identifier for identifier, statuses in history.items()
        if statuses[0] in ("Failure", "Failed")
    }
    if not failed or events.unclassified_issues:
        return []
    if set(events.failure_issues) != failed:
        return []

    selectors = [
        argument.split(":", 1)[1] for argument in arguments
        if argument.startswith("-only-testing:")
    ]
    if not selectors:
        return []
    targets = {selector.split("/", 1)[0] for selector in selectors
               if selector.count("/") >= 1}
    if len(targets) != 1 or any(selector.count("/") not in (1, 2)
                                for selector in selectors):
        return []
    target = next(iter(targets))
    if not target or any(not selector.startswith(f"{target}/") for selector in selectors):
        return []

    for identifier in history:
        parts = identifier.split("/")
        if (len(parts) != 2 or not parts[1].endswith("()")
                or not parts[0] or not parts[1][:-2]):
            return []
        method = parts[1][:-2]
        if not any(
            selector == f"{target}/{parts[0]}"
            or selector == f"{target}/{parts[0]}/{method}"
            for selector in selectors
        ):
            return []
    return [
        f"-only-testing:{target}/{identifier[:-2]}"
        for identifier in sorted(failed)
    ]


def summarize_attempts(tests, retry_failed_tests):
    attempts = {}
    for test in tests:
        attempts.setdefault(test["identifier"], []).append(test)
    invalid = [
        identifier for identifier, history in attempts.items()
        if len(history) > 1 and (
            not retry_failed_tests or len(history) > 2
            or history[0]["status"] not in ("Failure", "Failed")
        )
    ]
    flaky = [identifier for identifier, history in attempts.items()
             if identifier not in invalid
             and len(history) == 2 and history[-1]["status"] == "Success"]
    failed = [identifier for identifier, history in attempts.items()
              if identifier in invalid or history[-1]["status"] in ("Failure", "Failed")
              or (len(history) == 2 and history[-1]["status"] != "Success")]
    return attempts, flaky, failed, [
        f"Unexpected test repetition: {identifier}" for identifier in invalid
    ]


def run_invocation(command, stream_path, invocation_timeout, finalization_timeout,
                   flush_seconds, poll_seconds, started, budgets, attempt):
    """Run one xcodebuild process under the caller's shared wall-clock/idle budgets."""
    timing_path = stream_path.with_name(
        stream_path.name.removesuffix(".events.jsonl") + ".timing.json"
    )
    with stream_path.open("x"), timing_path.open("x"):
        pass
    events = TestEvents()
    reason = None
    error = None
    interrupted = None
    process_started = time.monotonic()
    try:
        process = subprocess.Popen(command, start_new_session=True)
    except OSError as exc:
        error = str(exc)
        events.stream_error = error
        summary = {
            "attempt": attempt,
            "exit_code": 70,
            "xcodebuild_exit_code": None,
            "termination_reason": "xcodebuild-launch-error",
            "stream_error": error,
            "invocation_seconds": time.monotonic() - process_started,
            "reported_test_seconds": 0,
            "non_test_overhead_seconds": 0,
            "post_last_test_seconds": None,
            "invocation_budget_seconds": invocation_timeout,
            "finalization_budget_seconds": finalization_timeout,
            "flush_seconds_included": flush_seconds,
            "tests": [],
        }
        timing_path.write_text(json.dumps(summary, indent=2) + "\n")
        print(f"IOS_RUNNER: attempt={attempt} could not launch xcodebuild; "
              f"timing={timing_path}", flush=True)
        return 70, None, events, summary, "xcodebuild-launch-error"

    def signal_process_group(sig):
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
                        events.stream_error = error
                budgets.observe(events, now)
                if process.poll() is not None:
                    if error is None:
                        try:
                            events.read(stream, time.monotonic())
                        except (ValueError, KeyError, TypeError) as exc:
                            error = str(exc)
                            events.stream_error = error
                    if events.partial_line and error is None:
                        error = "Truncated result-stream event"
                        events.stream_error = error
                    break
                if reason is None:
                    wall_deadline = started + invocation_timeout
                    deadline = wall_deadline
                    next_reason = "invocation-timeout"
                    if budgets.idle_started is not None:
                        idle_deadline = (
                            now + finalization_timeout - budgets.idle_elapsed(now)
                        )
                        if idle_deadline < deadline:
                            deadline = idle_deadline
                            next_reason = "post-test-timeout"
                    if interrupted:
                        reason = "cancelled"
                        deadline = now + flush_seconds
                    elif error is not None:
                        reason = "invalid-result-stream"
                        deadline = now + flush_seconds
                    elif now >= deadline - flush_seconds:
                        reason = next_reason
                    if reason is not None:
                        print(f"IOS_RUNNER: attempt={attempt} {reason}; interrupting "
                              "xcodebuild to flush the result bundle (failure remains "
                              "nonzero)", flush=True)
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
            signal_process_group(signal.SIGKILL)
        for sig, handler in previous_handlers.items():
            signal.signal(sig, handler)

    finished = time.monotonic()
    elapsed = finished - process_started
    reported = sum(test["reported_seconds"] for test in events.tests)
    status = native_exit if native_exit >= 0 else 128 - native_exit
    if reason == "cancelled":
        status = 128 + interrupted
    elif reason in ("invocation-timeout", "post-test-timeout"):
        status = 124
    elif error is not None or (status == 0 and (
        not events.tests or events.active
        or events.invocation_started != 1 or events.invocation_finished != 1
        or events.action_started != 1 or events.action_finished != 1
    )):
        status = 70
        reason = "incomplete-result-stream"
    elif status == 0 and any(
        test["status"] in ("Failure", "Failed") for test in events.tests
    ):
        status = 65
        reason = "failed-test-in-result-stream"

    summary = {
        "attempt": attempt,
        "exit_code": status,
        "xcodebuild_exit_code": native_exit,
        "termination_reason": reason,
        "stream_error": error,
        "invocation_seconds": elapsed,
        "reported_test_seconds": reported,
        "non_test_overhead_seconds": max(0, elapsed - reported),
        "post_last_test_seconds": (finished - events.last_finished
                                   if events.last_finished is not None else None),
        "invocation_budget_seconds": invocation_timeout,
        "finalization_budget_seconds": finalization_timeout,
        "flush_seconds_included": flush_seconds,
        "tests": events.tests,
    }
    timing_path.write_text(json.dumps(summary, indent=2) + "\n")
    print(f"IOS_RUNNER: attempt={attempt}; test-reported={reported:.3f}s; "
          f"non-test overhead={summary['non_test_overhead_seconds']:.3f}s; "
          f"post-last-test={summary['post_last_test_seconds']}; exit={status}; "
          f"timing={timing_path}", flush=True)
    return status, native_exit, events, summary, reason


def run(arguments, invocation_timeout=1440, finalization_timeout=120,
        flush_seconds=10, poll_seconds=0.1, executable="xcodebuild",
        retry_failed_tests=False):
    """Run once, optionally selecting only proven failed test IDs for one retry."""
    bundle = validate_arguments(arguments)
    for budget in (invocation_timeout, finalization_timeout):
        if not math.isfinite(budget) or budget <= flush_seconds:
            raise ValueError(f"Budgets must be finite and greater than {flush_seconds}s")
    bundle.parent.mkdir(parents=True, exist_ok=True)
    retry_bundle = bundle.with_name(f"{bundle.stem}-retry{bundle.suffix}")
    retry_stream = retry_bundle.with_suffix(".events.jsonl")
    retry_timing = retry_bundle.with_suffix(".timing.json")
    stream_path = bundle.with_suffix(".events.jsonl")
    timing_path = bundle.with_suffix(".timing.json")
    if retry_failed_tests and any(
        path.exists() for path in (retry_bundle, retry_stream, retry_timing)
    ):
        raise ValueError(f"Retry artifact already exists for {bundle}")
    command = [
        executable, *arguments,
        "-collect-test-diagnostics", "never",
        "-resultStreamPath", str(stream_path),
    ]
    for option, value in (
        ("-test-timeouts-enabled", "YES"), ("-parallel-testing-enabled", "NO"),
    ):
        if option not in arguments:
            command.extend((option, value))
    if "-disable-concurrent-destination-testing" not in arguments:
        command.append("-disable-concurrent-destination-testing")
    print(
        f"IOS_RUNNER: verbose simulator collection=never; maximum attempts="
        f"{2 if retry_failed_tests else 1}; shared post-test/idle budget="
        f"{finalization_timeout}s; shared invocation budget={invocation_timeout}s "
        f"(each includes {flush_seconds}s interrupt/flush)", flush=True,
    )
    started = time.monotonic()
    budgets = SharedBudgets(started)
    stream_path = bundle.with_suffix(".events.jsonl")
    attempt_one = run_invocation(
        command, stream_path, invocation_timeout, finalization_timeout,
        flush_seconds, poll_seconds, started, budgets, 1,
    )
    attempts = [attempt_one]
    first_status, first_native_exit, first_events, _, reason = attempt_one
    selected = retry_selection(arguments, first_events, first_native_exit) \
        if retry_failed_tests and reason is None else []
    if selected:
        retry_arguments = list(arguments)
        retry_arguments = [argument for argument in retry_arguments
                           if not argument.startswith("-only-testing:")]
        retry_arguments.extend(selected)
        bundle_index = retry_arguments.index("-resultBundlePath") + 1
        retry_arguments[bundle_index] = str(retry_bundle)
        retry_command = [
            executable, *retry_arguments,
            "-collect-test-diagnostics", "never",
            "-resultStreamPath", str(retry_stream),
        ]
        for option, value in (
            ("-test-timeouts-enabled", "YES"), ("-parallel-testing-enabled", "NO"),
        ):
            if option not in retry_arguments:
                retry_command.extend((option, value))
        if "-disable-concurrent-destination-testing" not in retry_arguments:
            retry_command.append("-disable-concurrent-destination-testing")
        attempts.append(run_invocation(
            retry_command, retry_stream, invocation_timeout, finalization_timeout,
            flush_seconds, poll_seconds, started, budgets, 2,
        ))
        reason = attempts[1][4]
    elif retry_failed_tests and first_status != 0:
        print(
            "IOS_RUNNER: retry not launched; first-attempt failure was not "
            "proven to be a complete, attributable test failure",
            flush=True,
        )

    all_tests = []
    for attempt_number, attempt in enumerate(attempts, 1):
        for test in attempt[2].tests:
            all_tests.append({**test, "attempt": attempt_number})
    grouped, flaky, failed, repetition_errors = summarize_attempts(
        all_tests, retry_failed_tests,
    )
    final_status = attempts[-1][0]
    if attempts[0][0] != 0 and not selected:
        final_status = attempts[0][0]
    if selected:
        retry_events = attempts[1][2]
        retry_ids = [test["identifier"] for test in retry_events.tests]
        expected_ids = [
            argument.split(":", 1)[1].split("/", 1)[1] + "()"
            for argument in selected
        ]
        # XCTest's result stream omits the test target from its identifiers.
        expected_ids = [
            "/".join(identifier.split("/")[-2:])
            for identifier in expected_ids
        ]
        if attempts[1][0] not in (0, 65):
            final_status = attempts[1][0]
        elif sorted(retry_ids) != sorted(expected_ids):
            repetition_errors.append(
                "Retry result identifiers did not exactly match selected failures"
            )
            final_status = 70
        elif retry_events.unclassified_issues or set(
            retry_events.failure_issues
        ) != {
            test["identifier"] for test in retry_events.tests
            if test["status"] in ("Failure", "Failed")
        }:
            repetition_errors.append(
                "Retry issue events did not map exactly to its failed tests"
            )
            final_status = 70
        elif attempts[1][0] not in (0, 65):
            final_status = attempts[1][0]
        elif attempts[1][0] == 0 and any(
            test["status"] != "Success" for test in retry_events.tests
        ):
            repetition_errors.append(
                "Retry exit was successful but selected tests did not all pass"
            )
            final_status = 70
        elif attempts[1][0] == 65 and not any(
            test["status"] in ("Failure", "Failed") for test in retry_events.tests
        ):
            repetition_errors.append(
                "Retry failed natively without a failed selected test"
            )
            final_status = 65
    if repetition_errors:
        if final_status not in (124, 130, 143):
            final_status = 70
        if reason is None and final_status == 70:
            reason = "unexpected-test-repetition"

    elapsed = time.monotonic() - started
    reported = sum(test["reported_seconds"] for test in all_tests)
    error = "; ".join(
        attempt[2].stream_error for attempt in attempts if attempt[2].stream_error
    ) or None
    summary = {
        "exit_code": final_status,
        "xcodebuild_exit_codes": [attempt[1] for attempt in attempts],
        "termination_reason": reason,
        "stream_error": error,
        "invocation_seconds": elapsed,
        "reported_test_seconds": reported,
        "non_test_overhead_seconds": max(0, elapsed - reported),
        "post_last_test_seconds": (
            time.monotonic() - attempts[-1][2].last_finished
            if attempts[-1][2].last_finished is not None else None
        ),
        "finalization_budget_seconds": finalization_timeout,
        "invocation_budget_seconds": invocation_timeout,
        "flush_seconds_included": flush_seconds,
        "verbose_simulator_diagnostics": "never",
        "max_attempts_per_test": 2 if retry_failed_tests else 1,
        "retry_selectors": selected,
        "attempts_by_test": grouped,
        "flaky_tests": flaky,
        "failed_tests": failed,
        "repetition_errors": repetition_errors,
        "attempt_results": [attempt[3] for attempt in attempts],
        "tests": all_tests,
    }
    timing_path.write_text(json.dumps(summary, indent=2) + "\n")
    print(f"IOS_RUNNER: test-reported={reported:.3f}s; "
          f"non-test overhead={summary['non_test_overhead_seconds']:.3f}s; "
          f"post-last-test={summary['post_last_test_seconds']}; exit={final_status}; "
          f"timing={timing_path}", flush=True)
    for identifier in flaky:
        # JSON escaping keeps test identifiers from injecting log annotations.
        message = f"Passed on retry (flaky): {json.dumps(identifier, ensure_ascii=True)}"
        print(f"IOS_RUNNER: {message}", flush=True)
        if os.environ.get("GITHUB_ACTIONS") == "true":
            escaped = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            print(f"::warning::{escaped}", flush=True)
    if retry_failed_tests and os.environ.get("GITHUB_STEP_SUMMARY"):
        # Derive labels from the fixed result-bundle name, never test-controlled Markdown.
        with Path(os.environ["GITHUB_STEP_SUMMARY"]).open("a") as step_summary:
            step_summary.write(
                f"\n### XCUI attempt results ({bundle.stem})\n\n"
                f"- Maximum attempts per test: 2 (one failed-test retry).\n"
                f"- Test executions recorded: {len(all_tests)}.\n"
                f"- Passed on retry (flaky): {len(flaky)}.\n"
                f"- Tests still failing: {len(failed)}.\n"
                f"- Runner exit code: {final_status}.\n\n"
                "Each invocation is retained in its own xcresult/event stream; "
                "the timing artifact groups every attempt.\n"
            )
    return final_status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--invocation-timeout", type=float, default=1440)
    parser.add_argument("--finalization-timeout", type=float, default=120)
    parser.add_argument("--retry-failed-tests", action="store_true",
                        help="Retry only failed tests once in a fresh process; retain every attempt")
    parser.add_argument("xcodebuild_arguments", nargs=argparse.REMAINDER,
                        help="-- test[-without-building] and existing xcodebuild arguments")
    options = parser.parse_args()
    arguments = options.xcodebuild_arguments
    if arguments[:1] == ["--"]:
        arguments = arguments[1:]
    try:
        return run(arguments, options.invocation_timeout, options.finalization_timeout,
                   retry_failed_tests=options.retry_failed_tests)
    except (ValueError, OSError) as exc:
        print(f"IOS_RUNNER: {exc}", file=sys.stderr)
        return 70


if __name__ == "__main__":
    sys.exit(main())
