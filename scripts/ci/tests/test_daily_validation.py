import copy
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("daily", Path(__file__).parents[1] / "daily-validation.py")
daily = importlib.util.module_from_spec(spec)
spec.loader.exec_module(daily)


def manifest():
    commit = "a" * 40
    tag = f"sha-{commit}-run-123-attempt-1"
    return {
        "schemaVersion": 1, "repository": daily.REPO, "branch": "development",
        "commit": commit, "workflowRun": f"https://github.com/{daily.REPO}/actions/runs/123", "tag": tag,
        "images": {service: {
            "name": f"ghcr.io/olyforge3d/printfarmer-{service}", "tag": tag,
            "digest": "sha256:" + "b" * 64,
            "reference": f"ghcr.io/olyforge3d/printfarmer-{service}@sha256:" + "b" * 64
        } for service in daily.SERVICES},
    }


class ManifestTests(unittest.TestCase):
    def test_valid_manifest(self):
        self.assertEqual(daily.validate_manifest(manifest(), "123")["commit"], "a" * 40)

    def test_wrong_run_missing_extra_mixed_mutable_and_malformed(self):
        changes = [
            lambda m: m.update(commit="main"),
            lambda m: m.update(workflowRun="https://github.com/other/repo/actions/runs/123"),
            lambda m: m["images"].pop("api"),
            lambda m: m["images"].update(extra=m["images"]["api"]),
            lambda m: m["images"]["api"].update(reference="ghcr.io/olyforge3d/printfarmer-api:latest"),
            lambda m: m["images"]["api"].update(digest="sha256:broken"),
            lambda m: m["images"]["api"].update(tag="another-commit"),
            lambda m: m["images"]["api"].update(name="ghcr.io/other/api"),
        ]
        for change in changes:
            with self.subTest(change=change):
                value = manifest()
                change(value)
                with self.assertRaises(daily.Blocked):
                    daily.validate_manifest(value, "123")


class PhaseTests(unittest.TestCase):
    def setUp(self):
        self.state = dict(validationId="unique", commit="a" * 40, manifestHash="manifest", harnessHash="harness")
        self.invocation = dict(id="invocation", phase="phase-a", status="finished",
                               startedAt="2026-09-11T14:00:00+00:00", finishedAt="2026-09-11T15:00:00+00:00")
        self.report = dict(self.state, invocationId="invocation", phase="phase-a",
                           startedAt="2026-09-11T14:01:00+00:00", finishedAt="2026-09-11T14:10:00+00:00",
                           status="passed", errors=[], tests=[{"category": "passed", "attempts": [{"status": "passed"}]}])

    def test_distinct_counts_and_flaky(self):
        self.report["tests"] += [
            {"category": "skipped", "attempts": [{"status": "skipped"}],
             "annotations": [{"type": "skip", "description": "unsupported"}]},
            {"category": "did-not-run", "attempts": []},
            {"category": "failed", "attempts": [{"status": "failed"}]},
            {"category": "passed", "outcome": "flaky", "attempts": [{"status": "failed"}, {"status": "passed"}]},
        ]
        self.assertEqual(daily.validate_phase(self.report, self.state, self.invocation, 1),
                         {"passed": 2, "failed": 1, "skipped": 1, "did-not-run": 1, "flaky": 1})

    def test_stale_august_evidence_missing_invocation_and_zero_tests_rejected(self):
        changes = [
            lambda r: r.update(startedAt="2026-08-14T14:00:00+00:00"),
            lambda r: r.update(invocationId="old"),
            lambda r: r.update(validationId="old"),
            lambda r: r.update(manifestHash="old"),
            lambda r: r.update(commit="old"),
            lambda r: r.update(harnessHash="old"),
            lambda r: r.update(tests=[]),
            lambda r: r["tests"][0].update(attempts=[]),
            lambda r: r.update(status="failed"),
            lambda r: r.update(errors=[{"message": "global setup failed"}]),
        ]
        for change in changes:
            with self.subTest(change=change):
                report = copy.deepcopy(self.report)
                change(report)
                with self.assertRaises(daily.Blocked):
                    daily.validate_phase(report, self.state, self.invocation, 0)

    def test_missing_execution_not_success(self):
        self.invocation["status"] = "running"
        with self.assertRaises(daily.Blocked):
            daily.validate_phase(self.report, self.state, self.invocation, 0)

    def test_no_explicit_reason_annotation_is_not_skip(self):
        self.report["tests"].append({"category": "skipped", "attempts": [{"status": "skipped"}]})
        with self.assertRaises(daily.Blocked):
            daily.validate_phase(self.report, self.state, self.invocation, 0)


@unittest.skipUnless(sys.platform == "linux", "Native Linux lifecycle/permissions tests")
class LifecycleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root_patch = patch.object(daily, "ROOT", Path(self.temp.name) / "daily")
        self.root_patch.start()
        self.env_patch = patch.dict(os.environ, PF_DAILY_HARNESS_HASH="harness", PF_DAILY_HARNESS_REVISION="a" * 40)
        self.env_patch.start()
        self.run = daily.create_run()

    def tearDown(self):
        self.env_patch.stop()
        self.root_patch.stop()
        self.temp.cleanup()

    def test_concurrent_identity_ports_and_fresh_environment_reload(self):
        other = daily.create_run()
        for run in (self.run, other):
            run.state.update(source=str(Path(run.state["workspace"]) / "source"), manifest=manifest())
            versions = Path(run.state["source"]) / "scripts/docker/container-versions.conf"
            versions.parent.mkdir(parents=True)
            versions.write_text('export SDK_TAG="test-sdk"\n')
            run.allocate_environment()
        self.assertNotEqual(self.run.state["project"], other.state["project"])
        self.assertRegex(self.run.state["project"], r"^[a-z0-9-]+$")
        self.assertFalse(set(self.run.state["ports"].values()) & set(other.state["ports"].values()))
        # Reconstruct as the next independent command would, without exporting secrets.
        self.run.state.pop("manifest")
        self.run.save()
        reloaded = daily.Run(self.run.state["validationId"])
        self.assertEqual(reloaded.env["POSTGRES_PASSWORD"], self.run.env["POSTGRES_PASSWORD"])
        self.assertEqual(reloaded.env["BASE_URL"], self.run.env["BASE_URL"])
        self.assertEqual((self.run.directory / "runtime/secrets.json").stat().st_mode & 0o777, 0o600)

    def test_cleanup_failure_visible_and_bounded(self):
        with patch.object(self.run, "owned_resources", side_effect=daily.Blocked("foreign resource")):
            for _ in range(2):
                with self.assertRaisesRegex(daily.Blocked, "foreign resource"):
                    self.run.cleanup()
                self.assertEqual(self.run.state["cleanup"]["status"], "failed")
                self.assertTrue(Path(self.run.state["workspace"]).exists())
            with self.assertRaisesRegex(daily.Blocked, "exhausted"):
                self.run.cleanup()

    def test_cleanup_only_exact_runtime_preserves_evidence_and_other_run(self):
        other = daily.create_run()
        (self.run.directory / "phase-a.log").write_text("current")
        with patch.object(self.run, "owned_resources", return_value=[]):
            self.run.cleanup()
        self.assertFalse(Path(self.run.state["workspace"]).exists())
        self.assertTrue(Path(other.state["workspace"]).exists())
        self.assertEqual((self.run.directory / "phase-a.log").read_text(), "current")
        self.assertEqual(self.run.state["cleanup"]["status"], "complete")

    def test_run_lock_prevents_foreign_command(self):
        with daily.lock(self.run.directory / "command.lock"):
            with self.assertRaisesRegex(daily.Blocked, "Another command"):
                with daily.lock(self.run.directory / "command.lock"):
                    self.fail("Lock incorrectly acquired")

    def test_resume_exhausted_or_interrupted_step_does_not_run(self):
        calls = []
        self.run.step("prepare", lambda: calls.append(1))
        with self.assertRaisesRegex(daily.Blocked, "exhausted"):
            self.run.step("prepare", lambda: calls.append(2))
        self.run.state["steps"]["deploy"] = {"status": "running"}
        with self.assertRaisesRegex(daily.Blocked, "Interrupted"):
            self.run.step("deploy", lambda: calls.append(3))
        self.assertEqual(calls, [1])

    def test_health_failure_prevents_phase_invocation(self):
        self.run.state["steps"]["deploy"] = {"status": "complete"}
        with patch.object(self.run, "health", side_effect=daily.Blocked("unhealthy")):
            with self.assertRaisesRegex(daily.Blocked, "unhealthy"):
                self.run.phase("phase-a")
        self.assertEqual(self.run.state["phases"], {})
        self.assertIsNone(self.run.summary()["phases"]["phase-a"]["counts"])

    def test_exit_status_logging_and_bounded_timeout(self):
        log = self.run.directory / "child.log"
        code, output = daily.execute(["bash", "-lc", "printf 'literal $dollar\\n'; exit 19"],
                                    self.temp.name, os.environ, log=log, check=False)
        self.assertEqual(code, 19)
        self.assertEqual(output, log.read_text())
        code, _ = daily.execute(["bash", "-lc", "sleep 30"], self.temp.name,
                                os.environ, timeout=0.05, log=log, check=False)
        self.assertEqual(code, 124)

    def test_foreign_container_blocks_cleanup_before_down(self):
        container = {"Config": {"Labels": {daily.LABEL: "another-run"}}}
        calls = []
        def command(args, name, **kwargs):
            calls.append(args)
            return 0, json.dumps([container]) if "inspect" in args else "foreign-id"
        with patch.object(self.run, "command", side_effect=command):
            (self.run.directory / "inspect-private.log").touch()
            with self.assertRaisesRegex(daily.Blocked, "Foreign container"):
                self.run.cleanup()
        self.assertFalse(any("down" in call for call in calls))


class ConfigTests(unittest.TestCase):
    def test_overlay_isolates_network_and_volumes_without_changing_aliases(self):
        config = {
            "services": {service: {"networks": {"farm": {"aliases": ["discovery-voron"]}}}
                         for service in daily.TOPOLOGY},
            "networks": {"farm": {"name": "shared-network"}},
            "volumes": {"profiles": {"name": "shared-profiles"}},
        }
        state = {"project": "run-one", "validationId": "run-one"}
        overlay = daily.ownership_overlay(config, state)
        self.assertEqual(overlay["networks"]["farm"]["name"], "run-one-farm")
        self.assertEqual(overlay["volumes"]["profiles"]["name"], "run-one-profiles")
        self.assertNotIn("networks", overlay["services"]["moonraker-ready"])
        self.assertEqual(config["services"]["moonraker-ready"]["networks"]["farm"]["aliases"], ["discovery-voron"])
        config["networks"]["farm"]["external"] = True
        with self.assertRaises(daily.Blocked):
            daily.ownership_overlay(config, state)

    def test_secrets_redacted(self):
        env = {"POSTGRES_PASSWORD": "private-value", "Jwt__Key": "secret-key"}
        self.assertEqual(daily.redact("private-value secret-key Bearer abc.def.ghi", env),
                         "[REDACTED] [REDACTED] Bearer [REDACTED]")


if __name__ == "__main__":
    unittest.main()
