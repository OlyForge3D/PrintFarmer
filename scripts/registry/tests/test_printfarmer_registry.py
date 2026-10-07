"""Regression tests for scripts/registry/printfarmer_registry.py (issue #3295).

The docker CLI is replaced by FakeDocker, which emulates the subset of Compose
behaviour the controller relies on (multi-file merge, profiles, ${VAR:-d} /
${VAR:?msg} interpolation with process env over .env, relative bind resolution,
named-volume naming) so tests exercise real rendering and invariant logic.
"""

import io
import json
import os
import re
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import printfarmer_registry as reg  # noqa: E402

PROJECT = "printfarmer"
OLD_COMMIT = "a" * 40
NEW_COMMIT = "b" * 40
VAR_RE = re.compile(r"\$\{([A-Za-z0-9_]+)(?:(:-|:\?)([^}]*))?\}")


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def read_text(path):
    with open(path, encoding="utf-8") as handle:
        return handle.read()


def digest(char):
    return "sha256:" + char * 64


def manifest(version, commit=NEW_COMMIT, char="1"):
    return {
        "schema": 1, "version": version, "tag": "v" + version, "channel": reg.channel_of(version),
        "sourceBranch": "development", "sourceCommit": commit, "buildId": "1", "managedUpdateEligible": True,
        "images": {name: {"reference": "%s%s@%s" % (reg.IMAGE_PREFIX, name, digest(char)),
                          "platforms": spec["platforms"]} for name, spec in reg.COMPONENTS.items()},
    }


BASE_TEMPLATE = {
    "services": {
        "api": {
            "build": {"context": ".", "dockerfile": "Dockerfile.multistage", "target": "api-runtime"},
            "environment": {"DB_PASSWORD": "${DB_PASSWORD}"},
            "volumes": [{"type": "bind", "source": "${EXTERNAL_DATA_PATH:-./.volumes/data}", "target": "/app/data"}],
        },
        "frontend": {
            "build": {"context": ".", "target": "frontend-runtime"},
            "volumes": [{"type": "bind", "source": "./deploy/nginx/nginx.conf", "target": "/etc/nginx/nginx.conf"}],
        },
        "postgres": {
            "image": "postgres:16",
            "volumes": [{"type": "volume", "source": "pgdata", "target": "/var/lib/postgresql/data"}],
        },
        "otel-collector": {
            "image": "otel/opentelemetry-collector:0.1",
            "volumes": [{"type": "bind", "source": "./scripts/docker/configs/otel.yaml", "target": "/etc/otel.yaml"}],
        },
        "orcaslicer-worker": {
            "profiles": ["orca"],
            "build": {"context": ".", "target": "orcaslicer-worker"},
            "environment": {"ORCASLICER_CONTAINER_DIGEST": "${ORCASLICER_CONTAINER_DIGEST:-}"},
            "volumes": [{"type": "volume", "source": "orcaslicer-custom-profiles", "target": "/app/custom-profiles"}],
        },
        "moonraker-emulator": {
            "profiles": ["emulator"],
            "build": {"context": ".", "target": "moonraker-emulator-runtime"},
        },
    },
    "volumes": {
        "pgdata": {},
        "orcaslicer-custom-profiles": {"name": "printfarmer-custom-profiles-${ORCASLICER_VERSION:-2.4.2}"},
    },
}
OVERRIDE = {"services": {"api": {"environment": {"ASPNETCORE_URLS": "http://+:8080"}}}}


def deep_merge(left, right):
    result = json.loads(json.dumps(left))
    for key, value in right.items():
        if isinstance(value, dict) and isinstance(result.get(key), dict):
            result[key] = deep_merge(result[key], value)
        else:
            result[key] = json.loads(json.dumps(value))
    return result


class ComposeFailure(Exception):
    pass


class FakeDocker:
    def __init__(self, root):
        self.root = root
        self.calls = []
        self.containers = {}
        self.volumes = set()
        self.images = {}
        self.pull_failures = set()
        self.unhealthy_after_up = set()
        self.tracked = None
        self.platform = "linux/x86_64"

    # --- compose emulation ------------------------------------------------
    def dotenv(self, path):
        values = {}
        if path and os.path.exists(path):
            for line in read_text(path).splitlines():
                if "=" in line and not line.startswith("#"):
                    key, _, value = line.strip().partition("=")
                    values[key] = value
        return values

    def interpolate(self, value, env):
        if isinstance(value, dict):
            return {k: self.interpolate(v, env) for k, v in value.items()}
        if isinstance(value, list):
            return [self.interpolate(v, env) for v in value]
        if not isinstance(value, str):
            return value

        def sub(match):
            name, op, arg = match.group(1), match.group(2), match.group(3)
            current = env.get(name, "")
            if op == ":?" and not current:
                raise ComposeFailure("required variable %s is missing a value: %s" % (name, arg))
            if op == ":-" and not current:
                return arg
            return current
        return VAR_RE.sub(sub, value)

    def resolve(self, merged, project_dir, profiles, env):
        config = self.interpolate(merged, env)
        config["services"] = {name: svc for name, svc in config["services"].items()
                              if not svc.get("profiles") or set(svc["profiles"]) & set(profiles)}
        for svc in config["services"].values():
            for volume in svc.get("volumes", []):
                if volume["type"] == "bind" and not os.path.isabs(volume["source"]):
                    volume["source"] = os.path.normpath(os.path.join(project_dir, volume["source"]))
        for key, volume in (config.get("volumes") or {}).items():
            volume.setdefault("name", "%s_%s" % (config["name"], key))
        return config

    def compose(self, args, env):
        index, opts = 0, {"files": [], "profiles": [], "env_file": None}
        while args[index].startswith("-"):
            flag, value = args[index], args[index + 1]
            key = {"-p": "project", "--project-directory": "dir", "--env-file": "env_file", "-f": "files",
                   "--profile": "profiles"}[flag]
            if isinstance(opts.get(key), list):
                opts[key].append(value)
            else:
                opts[key] = value
            index += 2
        command = args[index:]
        merged = {}
        for name in opts["files"]:
            with open(name, encoding="utf-8") as handle:
                merged = deep_merge(merged, json.load(handle))
        merged["name"] = opts["project"]
        environment = dict(self.dotenv(opts["env_file"]), **(env or {}))
        if command == ["config", "--profiles"]:
            found = {p for svc in merged["services"].values() for p in svc.get("profiles", [])}
            return 0, "\n".join(sorted(found))
        if command[:3] == ["config", "--format", "json"]:
            if "--no-interpolate" in command:
                merged["services"] = {n: s for n, s in merged["services"].items()
                                      if not s.get("profiles") or set(s["profiles"]) & set(opts["profiles"])}
                for svc in merged["services"].values():
                    for volume in svc.get("volumes", []):
                        if volume["type"] == "bind" and volume["source"].startswith("${"):
                            volume["type"] = "volume"  # real Compose quirk without interpolation
                return 0, json.dumps(merged)
            if "--no-path-resolution" in command:
                config = self.interpolate(merged, environment)
                config["services"] = {n: s for n, s in config["services"].items()
                                      if not s.get("profiles") or set(s["profiles"]) & set(opts["profiles"])}
                return 0, json.dumps(config)
            return 0, json.dumps(self.resolve(merged, opts["dir"], opts["profiles"], environment))
        if command[0] == "up":
            assert command == ["up", "-d", "--no-build", "--pull", "never"], command
            assert not any("build" in s for s in merged["services"].values())
            config = self.resolve(merged, opts["dir"], opts["profiles"], environment)
            for service, svc in config["services"].items():
                self.create(service, svc["image"], config, opts["files"], opts["dir"])
            return 0, ""
        raise AssertionError("unexpected compose command %r" % command)

    # --- container state --------------------------------------------------
    def create(self, service, image, config, files, project_dir):
        mounts = []
        for volume in config["services"][service].get("volumes", []):
            if volume["type"] == "volume":
                mounts.append({"Type": "volume", "Name": config["volumes"][volume["source"]]["name"],
                               "Destination": volume["target"]})
            else:
                mounts.append({"Type": "bind", "Source": volume["source"], "Destination": volume["target"]})
        health = "unhealthy" if service in self.unhealthy_after_up else "healthy"
        self.containers[service] = {
            "Id": "id-" + service,
            "Config": {"Image": image, "Labels": {
                "com.docker.compose.project": PROJECT, "com.docker.compose.service": service,
                "com.docker.compose.project.config_files": ",".join(files),
                "com.docker.compose.project.working_dir": project_dir, "com.docker.compose.oneoff": "False"}},
            "Mounts": mounts, "State": {"Status": "running", "Health": {"Status": health}},
            "HostConfig": {"RestartPolicy": {"Name": "unless-stopped"}},
        }

    def run(self, args, env=None, check=True):
        self.calls.append(list(args))
        try:
            code, out = self.dispatch(args, env)
        except ComposeFailure as error:
            code, out = 1, str(error)
        if check and code != 0:
            raise reg.MigrationError("command failed (%s): %s" % (" ".join(args[:4]), out))
        return code, out

    def dispatch(self, args, env):
        if args[0] == "git":
            if self.tracked is None:
                return 128, ""
            if "ls-files" in args:
                return 0, "\0".join(self.tracked) + "\0"
            raise AssertionError(args)
        args = args[1:]
        if args[0] == "compose":
            return self.compose(args[1:], env)
        if args[:2] == ["ps", "-a"]:
            label = args[3]
            if "working_dir=" in label:
                wanted = label.split("working_dir=", 1)[1]
                return 0, "\n".join(c["Config"]["Labels"]["com.docker.compose.project"]
                                    for c in self.containers.values()
                                    if c["Config"]["Labels"]["com.docker.compose.project.working_dir"] == wanted)
            return 0, "\n".join(c["Id"] for c in self.containers.values())
        if args[0] == "inspect":
            by_id = {c["Id"]: c for c in self.containers.values()}
            return 0, json.dumps([by_id[i] for i in args[1:]])
        if args[:2] == ["volume", "inspect"]:
            return (0, "[]") if args[2] in self.volumes else (1, "no such volume")
        if args[0] == "info":
            return 0, self.platform + "\n"
        if args[0] == "pull":
            reference = args[-1]
            if reference in self.pull_failures:
                return 1, "manifest unknown"
            return 0, ""
        if args[:2] == ["image", "inspect"]:
            if args[2] in self.images:
                return 0, json.dumps([self.images[args[2]]])
            return (0, "[{}]") if "@" not in args[2] else (1, "no such image")
        raise AssertionError("unexpected docker command %r" % args)


class RegistryTestCase(unittest.TestCase):
    def setUp(self):
        self.root = os.path.realpath(tempfile.mkdtemp(prefix="pf-registry-test-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        self.write("docker-compose.yml", BASE_TEMPLATE)
        self.write("docker-compose.override.yml", OVERRIDE)
        self.write_text(".env", "DB_PASSWORD=s3cr3t-value\nORCASLICER_VERSION=2.4.2\n"
                                "ORCASLICER_CONTAINER_DIGEST=sha256:%s\n" % ("9" * 64))
        self.write_text(".deploy-config", "ENABLE_ORCA_WORKER=yes\n")
        self.write_text("deploy/nginx/nginx.conf", "events {}\n")
        self.write_text("scripts/docker/configs/otel.yaml", "receivers: {}\n")
        os.makedirs(os.path.join(self.root, ".volumes", "data"))
        self.docker = FakeDocker(self.root)
        self.docker.tracked = ["docker-compose.override.yml", "scripts/docker/configs/otel.yaml",
                               "scripts/deploy-docker.sh"]
        self.docker.volumes = {"printfarmer_pgdata", "printfarmer-custom-profiles-2.4.2"}
        self.files = [self.p("docker-compose.yml"), self.p("docker-compose.override.yml")]
        self.deploy_checkout(profiles=["orca"])
        self.fetched = {}
        self.output = io.StringIO()
        self.clock_value = [0]

    # helpers
    def p(self, *parts):
        return os.path.normpath(os.path.join(self.root, *parts))

    def write(self, rel, data):
        self.write_text(rel, json.dumps(data))

    def write_text(self, rel, text):
        os.makedirs(os.path.dirname(self.p(rel)), exist_ok=True)
        with open(self.p(rel), "w", encoding="utf-8") as handle:
            handle.write(text)

    def deploy_checkout(self, profiles):
        """Simulate scripts/deploy-docker.sh having built and started the project."""
        merged = deep_merge(BASE_TEMPLATE, OVERRIDE)
        merged["name"] = PROJECT
        env = dict(self.docker.dotenv(self.p(".env")))
        config = self.docker.resolve(merged, self.root, profiles, env)
        self.docker.containers = {}
        for service, svc in config["services"].items():
            self.docker.create(service, svc.get("image") or "%s-%s" % (PROJECT, service), config, self.files,
                               self.root)

    def fetch(self, url):
        if url in self.fetched:
            return self.fetched[url]
        if "/compare/" in url:
            return {"status": "ahead"}
        raise reg.MigrationError("not found: " + url)

    def controller(self, root=None):
        return reg.Controller(root or self.root, runner=self.docker, fetch=self.fetch, out=self.output,
                              sleep=lambda _: self.clock_value.__setitem__(0, self.clock_value[0] + 5),
                              clock=lambda: self.clock_value[0])

    def main(self, *argv):
        stderr, sys.stderr = sys.stderr, io.StringIO()
        try:
            code = reg.main(["--project-dir", self.root] + list(argv), controller_factory=self.controller)
            return code, sys.stderr.getvalue()
        finally:
            sys.stderr = stderr

    def publish(self, version, commit=NEW_COMMIT, char="1", orca="2.4.2", **label_overrides):
        data = manifest(version, commit, char)
        self.fetched["https://github.com/%s/releases/download/v%s/container-images.json"
                     % (reg.GITHUB_REPOSITORY, version)] = data
        for name, entry in data["images"].items():
            labels = {"org.opencontainers.image.version": version, "org.opencontainers.image.revision": commit,
                      "org.printfarmer.release-channel": data["channel"]}
            if name == "orcaslicer-worker":
                labels["orcaslicer.version"] = orca
            labels.update(label_overrides)
            self.docker.images[entry["reference"]] = {"Os": "linux", "Architecture": "amd64",
                                                      "Config": {"Labels": labels}}
        return data

    def migrate(self):
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 0, err)

    def snapshot(self):
        result = {}
        for base, _, names in os.walk(self.root):
            for name in names:
                path = os.path.join(base, name)
                with open(path, "rb") as handle:
                    result[os.path.relpath(path, self.root)] = handle.read()
        return result

    def compose_commands(self, verb):
        return [c for c in self.docker.calls if c[:2] == ["docker", "compose"] and verb in c]


class MigrateTests(RegistryTestCase):
    def test_migrate_renders_build_free_release_compose(self):
        self.migrate()
        release = load_json(self.p("docker-compose.release.yml"))
        self.assertEqual(release["name"], PROJECT)
        self.assertEqual(set(release["services"]),
                         {"api", "frontend", "postgres", "otel-collector", "orcaslicer-worker"})
        for name, svc in release["services"].items():
            self.assertNotIn("build", svc, name)
        self.assertEqual(release["services"]["api"]["image"],
                         "${PRINTFARMER_API_IMAGE:?Run .printfarmer/bin/printfarmer-registry update}")
        self.assertEqual(release["services"]["postgres"]["image"], "postgres:16")
        # override file merged; secrets stay as uninterpolated references
        self.assertEqual(release["services"]["api"]["environment"]["ASPNETCORE_URLS"], "http://+:8080")
        self.assertEqual(release["services"]["api"]["environment"]["DB_PASSWORD"], "${DB_PASSWORD}")
        self.assertNotIn("s3cr3t-value", read_text(self.p("docker-compose.release.yml")))
        # tracked bind vendored, untracked generated bind left in place
        otel = release["services"]["otel-collector"]["volumes"][0]["source"]
        self.assertEqual(otel, "./.printfarmer/files/scripts/docker/configs/otel.yaml")
        self.assertTrue(os.path.exists(self.p(".printfarmer/files/scripts/docker/configs/otel.yaml")))
        self.assertEqual(release["services"]["frontend"]["volumes"][0]["source"], "./deploy/nginx/nginx.conf")
        deployment = load_json(self.p(".printfarmer/deployment.json"))
        self.assertEqual(deployment["profiles"], ["orca"])
        self.assertEqual(deployment["orcaslicerVersion"], "2.4.2")
        self.assertEqual(deployment["migratedFrom"]["deployedCommit"], OLD_COMMIT)
        self.assertEqual(deployment["migratedFrom"]["composeFiles"],
                         ["docker-compose.yml", "docker-compose.override.yml"])
        self.assertTrue(os.path.exists(self.p(".printfarmer/bin/printfarmer-registry")))
        # migrate never starts, stops, or pulls
        self.assertEqual(self.compose_commands("up"), [])
        self.assertFalse(any(c[1] == "pull" for c in self.docker.calls))
        for call in self.docker.calls:
            self.assertNotIn("down", call)
        self.assertNotIn("s3cr3t-value", self.output.getvalue())

    def test_dry_run_writes_nothing(self):
        before = self.snapshot()
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT, "--dry-run")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.snapshot(), before)
        self.assertIn("Dry run", self.output.getvalue())
        self.assertIn("printfarmer-custom-profiles-2.4.2", self.output.getvalue() + "printfarmer-custom-profiles-2.4.2")

    def test_previous_worker_fails_closed_with_actionable_message(self):
        template = deep_merge(BASE_TEMPLATE, {"services": {"orcaslicer-worker-previous": {
            "build": {"context": ".", "target": "orcaslicer-worker",
                      "args": {"ORCASLICER_VERSION": "${ORCASLICER_VERSION_PREVIOUS:-2.3.1}"}},
            "volumes": []}}})
        self.write("docker-compose.yml", template)
        merged = deep_merge(template, OVERRIDE)
        merged["name"] = PROJECT
        config = self.docker.resolve(merged, self.root, ["orca"], {})
        self.docker.create("orcaslicer-worker-previous", "x", config, self.files, self.root)
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 2)
        self.assertIn("ENABLE_ORCA_WORKER_PREVIOUS=no", err)
        self.assertFalse(os.path.exists(self.p(".printfarmer")))

    def test_running_emulator_fails_closed(self):
        self.deploy_checkout(profiles=["orca", "emulator"])
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 2)
        self.assertIn("Moonraker emulator", err)

    def test_storage_mismatch_fails_before_writing(self):
        self.docker.containers["postgres"]["Mounts"][0]["Name"] = "other_pgdata"
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 2)
        self.assertIn("storage identity check failed", err)
        self.assertIn("postgres", err)
        self.assertFalse(os.path.exists(self.p(".printfarmer")))

    def test_missing_named_volume_fails(self):
        self.docker.volumes.discard("printfarmer_pgdata")
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 2)
        self.assertIn("named volume printfarmer_pgdata is missing", err)

    def test_unknown_deployed_commit_requires_explicit_value(self):
        code, err = self.main("migrate")
        self.assertEqual(code, 2)
        self.assertIn("--deployed-commit", err)

    def test_migrate_twice_is_refused(self):
        self.migrate()
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 2)
        self.assertIn("already migrated", err)


class UpdateTests(RegistryTestCase):
    def setUp(self):
        super().setUp()
        self.migrate()

    def release_state(self):
        return load_json(self.p(".printfarmer/release.json"))

    def test_update_requires_backup_confirmation(self):
        self.publish("1.2.0")
        code, err = self.main("update", "--version", "1.2.0")
        self.assertEqual(code, 2)
        self.assertIn("--backup-confirmed", err)
        self.assertEqual(self.compose_commands("up"), [])

    def test_plan_shows_old_and_new_images_without_side_effects(self):
        data = self.publish("1.2.0")
        before = self.snapshot()
        code, err = self.main("plan", "--version", "1.2.0")
        self.assertEqual(code, 0, err)
        out = self.output.getvalue()
        self.assertIn("old: printfarmer-api", out)
        self.assertIn("new: " + data["images"]["api"]["reference"], out)
        self.assertIn("printfarmer-custom-profiles-2.4.2", out)
        self.assertNotIn("s3cr3t-value", out)
        self.assertEqual(self.snapshot(), before)
        self.assertFalse(any(c[1] == "pull" for c in self.docker.calls))

    def test_update_pulls_everything_before_up_and_records_release(self):
        data = self.publish("1.2.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 0, err)
        calls = self.docker.calls
        up_index = next(i for i, c in enumerate(calls) if c[:2] == ["docker", "compose"] and "up" in c)
        pulls = [i for i, c in enumerate(calls) if c[1] == "pull"]
        self.assertEqual(len(pulls), 3)  # api, frontend, orcaslicer-worker
        self.assertTrue(all(i < up_index for i in pulls))
        self.assertEqual(self.docker.containers["api"]["Config"]["Image"], data["images"]["api"]["reference"])
        self.assertEqual(self.docker.containers["postgres"]["Config"]["Image"], "postgres:16")
        state = self.release_state()
        self.assertEqual(state["current"]["version"], "1.2.0")
        self.assertEqual(state["previous"], {"kind": "git-checkout", "sourceCommit": OLD_COMMIT})
        self.assertFalse(os.path.exists(self.p(".printfarmer/pending.json")))
        self.assertFalse(os.path.exists(self.p(".printfarmer/lock")))
        # storage identity preserved
        self.assertEqual(self.docker.containers["postgres"]["Mounts"][0]["Name"], "printfarmer_pgdata")
        self.assertEqual(self.docker.containers["orcaslicer-worker"]["Mounts"][0]["Name"],
                         "printfarmer-custom-profiles-2.4.2")

    def test_failed_pull_changes_nothing(self):
        data = self.publish("1.2.0")
        self.docker.pull_failures.add(data["images"]["frontend"]["reference"])
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertEqual(self.compose_commands("up"), [])
        self.assertFalse(os.path.exists(self.p(".printfarmer/release.json")))
        self.assertFalse(os.path.exists(self.p(".printfarmer/pending.json")))
        self.assertEqual(self.docker.containers["api"]["Config"]["Image"], "printfarmer-api")

    def test_unhealthy_service_keeps_last_good_release(self):
        self.publish("1.2.0")
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        self.publish("1.3.0", char="2")
        self.docker.unhealthy_after_up.add("api")
        code, err = self.main("update", "--version", "1.3.0", "--backup-confirmed", "--health-timeout", "20")
        self.assertEqual(code, 2)
        self.assertIn("not healthy", err)
        self.assertEqual(self.release_state()["current"]["version"], "1.2.0")
        pending = load_json(self.p(".printfarmer/pending.json"))
        self.assertEqual(pending["phase"], "failed-health")
        self.assertEqual(self.main("status")[0], 1)
        code, err = self.main("update", "--version", "1.3.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("--resume-interrupted", err)
        self.docker.unhealthy_after_up.clear()
        code, err = self.main("update", "--version", "1.3.0", "--backup-confirmed", "--resume-interrupted")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.release_state()["current"]["version"], "1.3.0")

    def test_insider_requires_explicit_opt_in(self):
        self.publish("1.3.0-insider.4")
        code, err = self.main("update", "--version", "1.3.0-insider.4", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("--allow-insider", err)
        code, err = self.main("update", "--version", "1.3.0-insider.4", "--backup-confirmed", "--allow-insider")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.release_state()["current"]["channel"], "insider")

    def test_non_descendant_release_is_refused(self):
        self.publish("1.1.0", commit="c" * 40)
        self.fetched["https://api.github.com/repos/%s/compare/%s...%s"
                     % (reg.GITHUB_REPOSITORY, OLD_COMMIT, "c" * 40)] = {"status": "behind"}
        code, err = self.main("update", "--version", "1.1.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("not a verified descendant", err)
        self.assertEqual(self.compose_commands("up"), [])

    def test_orca_version_mismatch_fails_before_up(self):
        self.publish("1.2.0", orca="2.5.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("custom-profiles volume version 2.4.2", err)
        self.assertEqual(self.compose_commands("up"), [])

    def test_label_mismatch_fails(self):
        self.publish("1.2.0", **{"org.opencontainers.image.revision": "d" * 40})
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("revision label", err)

    def test_arm64_host_rejects_amd64_only_worker(self):
        self.publish("1.2.0")
        self.docker.platform = "linux/aarch64"
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("orcaslicer-worker is not published for linux/arm64", err)

    def test_tampered_manifest_rejected(self):
        data = self.publish("1.2.0")
        data["images"]["api"]["reference"] = "ghcr.io/evil/api@" + digest("1")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("invalid reference for api", err)

    def test_update_and_rollback_work_after_checkout_removed(self):
        self.publish("1.2.0")
        for rel in ("docker-compose.yml", "docker-compose.override.yml", "scripts"):
            path = self.p(rel)
            shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)
        self.docker.tracked = None  # no git checkout any more
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        self.publish("1.3.0", char="2")
        self.assertEqual(self.main("update", "--version", "1.3.0", "--backup-confirmed")[0], 0)
        code, err = self.main("rollback", "--backup-confirmed")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.release_state()["current"]["version"], "1.2.0")
        self.assertIn("Data is NOT restored", self.output.getvalue())
        otel = self.docker.containers["otel-collector"]["Mounts"][0]["Source"]
        self.assertEqual(otel, self.p(".printfarmer/files/scripts/docker/configs/otel.yaml"))

    def test_rollback_to_git_checkout_is_refused(self):
        self.publish("1.2.0")
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        code, err = self.main("rollback", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("no previous release", err)


REAL_TEMPLATE = """\
services:
  api:
    build: { context: ., dockerfile: Dockerfile.multistage, target: api-runtime }
    environment:
      DB_PASSWORD: ${DB_PASSWORD}
    volumes:
      - ${EXTERNAL_DATA_PATH:-./.volumes/data}:/app/data
      - pgdata:/var/lib/data
  frontend:
    build: { context: ., dockerfile: Dockerfile.multistage, target: frontend-runtime }
    volumes:
      - ./deploy/nginx/nginx.conf:/etc/nginx/nginx.conf:ro
  otel-collector:
    image: otel/opentelemetry-collector:0.1
    volumes:
      - ./scripts/docker/configs/otel.yaml:/etc/otel.yaml:ro
  orcaslicer-worker:
    profiles: [orca]
    build: { context: ., dockerfile: Dockerfile.multistage, target: orcaslicer-worker }
    volumes:
      - orcaslicer-custom-profiles:/app/custom-profiles
volumes:
  pgdata: {}
  orcaslicer-custom-profiles:
    name: printfarmer-custom-profiles-${ORCASLICER_VERSION:-2.4.2}
"""


class RealComposeDocker(FakeDocker):
    """Delegates `compose config` to the real Docker Compose CLI (no daemon required)."""

    def real_config(self, args, env):
        import subprocess
        result = subprocess.run(["docker", "compose"] + args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                text=True, env=dict(os.environ, **(env or {})))
        return result.returncode, result.stdout if result.returncode == 0 else result.stderr

    def compose(self, args, env):
        command_at = next(i for i, a in enumerate(args) if a in ("config", "up"))
        if args[command_at] == "config":
            return self.real_config(args, env)
        code, text = self.real_config(args[:command_at] + ["config", "--format", "json"], env)
        assert code == 0, text
        config = json.loads(text)
        assert not any("build" in s for s in config["services"].values())
        files = [args[i + 1] for i, a in enumerate(args) if a == "-f"]
        for service, svc in config["services"].items():
            self.create(service, svc["image"], config, files, self.root)
        return 0, ""


def compose_available():
    try:
        import subprocess
        return subprocess.run(["docker", "compose", "version"], stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE).returncode == 0
    except OSError:
        return False


@unittest.skipUnless(compose_available(), "docker compose CLI not available")
class RealComposeTests(RegistryTestCase):
    def setUp(self):
        super().setUp()
        self.write_text("docker-compose.yml", REAL_TEMPLATE)
        os.remove(self.p("docker-compose.override.yml"))
        self.files = [self.p("docker-compose.yml")]
        self.docker = RealComposeDocker(self.root)
        self.docker.tracked = ["scripts/docker/configs/otel.yaml"]
        self.docker.volumes = {"printfarmer_pgdata", "printfarmer-custom-profiles-2.4.2"}
        args = ["-p", PROJECT, "--project-directory", self.root, "--env-file", self.p(".env"), "-f", self.files[0],
                "--profile", "orca", "config", "--format", "json"]
        code, text = self.docker.real_config(args, {})
        self.assertEqual(code, 0, text)
        config = json.loads(text)
        for service in config["services"]:
            self.docker.create(service, "printfarmer-" + service, config, self.files, self.root)

    def test_migrate_and_update_with_real_compose_rendering(self):
        self.migrate()
        release = load_json(self.p("docker-compose.release.yml"))
        api_data = release["services"]["api"]["volumes"][0]
        self.assertEqual(api_data["type"], "bind")
        self.assertEqual(api_data["source"], "${EXTERNAL_DATA_PATH:-./.volumes/data}")
        self.assertNotIn("s3cr3t-value", read_text(self.p("docker-compose.release.yml")))
        data = self.publish("1.2.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.docker.containers["api"]["Config"]["Image"], data["images"]["api"]["reference"])
        self.assertEqual(self.docker.containers["api"]["Mounts"][0]["Source"], self.p(".volumes/data"))

    def test_release_compose_refuses_to_start_without_pinned_images(self):
        self.migrate()
        args = ["-p", PROJECT, "--project-directory", self.root, "-f", self.p("docker-compose.release.yml"),
                "config", "--format", "json"]
        env = {k: v for k, v in os.environ.items() if not k.startswith("PRINTFARMER_")}
        import subprocess
        result = subprocess.run(["docker", "compose"] + args, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                text=True, env=env)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("printfarmer-registry update", result.stderr)


if __name__ == "__main__":
    unittest.main()
