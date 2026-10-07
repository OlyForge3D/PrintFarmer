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
THIRD_COMMIT = "e" * 40
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
        self.up_configs = []
        self.containers = {}
        self.volumes = set()
        self.images = {}
        self.pull_failures = set()
        self.unhealthy_after_up = set()
        self.tracked = None
        self.platform = "linux/x86_64"
        self.volume_label_map = {}
        self.volume_files = {}
        self.fail_copy = False
        self.copy_error = None
        self.copy_runs = []

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
            self.up_configs.append(config)
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
            if args[2] not in self.volumes:
                return 1, "no such volume"
            return 0, json.dumps([{"Name": args[2], "Labels": self.volume_label_map.get(args[2])}])
        if args[:2] == ["volume", "create"]:
            name, labels, rest = args[-1], {}, args[2:-1]
            assert name not in self.volumes and len(rest) % 2 == 0, args
            for flag, pair in zip(rest[::2], rest[1::2]):
                assert flag == "--label", args
                key, _, value = pair.partition("=")
                labels[key] = value
            self.volumes.add(name)
            self.volume_label_map[name] = labels
            return 0, name
        if args[0] == "run":
            return self.profile_copy(args)
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

    def profile_copy(self, args):
        """Emulate the controller's one-shot copy helper (`docker run ... -c <script>`)."""
        self.copy_runs.append(list(args))
        mounts = {}
        for index, arg in enumerate(args):
            if arg == "-v":
                name, _, target = args[index + 1].partition(":")
                mounts[target] = name
        source, dest, script = mounts["/from:ro"], mounts["/to"], args[-1]
        assert source in self.volumes and dest in self.volumes, args
        files = self.volume_files.setdefault(dest, {})
        if "find /to -mindepth 1 -delete" in script:
            files.clear()
        if files:
            return 1, "target not empty"
        if self.copy_error:
            return self.copy_error
        for index, (path, content) in enumerate(sorted(self.volume_files.get(source, {}).items())):
            if self.fail_copy and index == 1:
                return 1, "copy interrupted"
            files[path] = content
        return 0, ""


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
        self.history = [OLD_COMMIT, NEW_COMMIT, THIRD_COMMIT]  # linear development history, oldest first
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
        match = re.search(r"/compare/([0-9a-f]{40})\.\.\.([0-9a-f]{40})$", url)
        if match and set(match.groups()) <= set(self.history):
            base, head = (self.history.index(c) for c in match.groups())
            return {"status": "ahead" if head > base else "behind" if head < base else "identical"}
        raise reg.MigrationError("not found: " + url)  # unknown lineage stays unknown

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

    def test_git_ls_files_failure_fails_closed_without_writing(self):
        self.docker.tracked = None
        code, err = self.main("migrate", "--deployed-commit", OLD_COMMIT)
        self.assertEqual(code, 2)
        self.assertIn("git ls-files", err)
        self.assertFalse(os.path.exists(self.p(".printfarmer")))

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
        # The worker identity digest is the release index digest, overriding the stale local-build .env value.
        worker_env = self.docker.up_configs[-1]["services"]["orcaslicer-worker"]["environment"]
        self.assertEqual(worker_env["ORCASLICER_CONTAINER_DIGEST"],
                         data["images"]["orcaslicer-worker"]["reference"].partition("@")[2])
        self.assertNotIn("9" * 64, worker_env["ORCASLICER_CONTAINER_DIGEST"])

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
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2")
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

    def test_health_failure_names_rollback_only_when_a_previous_release_exists(self):
        self.publish("1.2.0")
        self.docker.unhealthy_after_up.add("api")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--health-timeout", "20")
        self.assertEqual(code, 2)
        self.assertNotIn("or roll back", err)
        self.assertIn("no previous release to roll back to", err)
        self.assertIn("scripts/deploy-docker.sh", err)
        self.docker.unhealthy_after_up.clear()
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed",
                                   "--resume-interrupted")[0], 0)
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2")
        self.assertEqual(self.main("update", "--version", "1.3.0", "--backup-confirmed")[0], 0)
        self.publish("1.4.0", commit="d" * 40, char="4")
        self.fetched["https://api.github.com/repos/%s/compare/%s...%s"
                     % (reg.GITHUB_REPOSITORY, THIRD_COMMIT, "d" * 40)] = {"status": "ahead"}
        self.docker.unhealthy_after_up.add("api")
        code, err = self.main("update", "--version", "1.4.0", "--backup-confirmed", "--health-timeout", "20")
        self.assertEqual(code, 2)
        self.assertIn("--resume-interrupted or roll back", err)

    def test_pending_history_is_capped_at_one_level(self):
        self.publish("1.2.0")
        self.docker.unhealthy_after_up.add("api")
        for extra in ([], ["--resume-interrupted"], ["--resume-interrupted"]):
            code, _ = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--health-timeout", "20",
                                *extra)
            self.assertEqual(code, 2)
        pending = load_json(self.p(".printfarmer/pending.json"))
        self.assertEqual(pending["previousPending"]["phase"], "failed-health")
        self.assertNotIn("previousPending", pending["previousPending"])

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
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2")
        self.assertEqual(self.main("update", "--version", "1.3.0", "--backup-confirmed")[0], 0)
        code, err = self.main("rollback", "--backup-confirmed", "--allow-unsafe-downgrade")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.release_state()["current"]["version"], "1.2.0")
        self.assertIn("Data is NOT restored", self.output.getvalue())
        self.assertIn("compare: behind", self.output.getvalue())
        otel = self.docker.containers["otel-collector"]["Mounts"][0]["Source"]
        self.assertEqual(otel, self.p(".printfarmer/files/scripts/docker/configs/otel.yaml"))

    def test_rollback_requires_explicit_data_risk_acknowledgement(self):
        self.publish("1.2.0")
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2")
        self.assertEqual(self.main("update", "--version", "1.3.0", "--backup-confirmed")[0], 0)
        ups = len(self.compose_commands("up"))
        code, err = self.main("rollback", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("compare: behind", err)
        self.assertIn("rollback --backup-confirmed --allow-unsafe-downgrade", err)
        self.assertEqual(len(self.compose_commands("up")), ups)
        self.assertEqual(self.release_state()["current"]["version"], "1.3.0")
        self.assertFalse(os.path.exists(self.p(".printfarmer/pending.json")))

    def test_unknown_lineage_is_refused(self):
        self.publish("1.2.0", commit="f" * 40)  # GitHub cannot relate this commit
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("compare: unknown", err)
        self.assertEqual(self.compose_commands("up"), [])

    def test_existing_lock_reports_holder_and_is_not_taken_over(self):
        self.publish("1.2.0")
        self.write_text(".printfarmer/lock", "%d deadbeef 2026-01-01T00:00:00+00:00" % os.getpid())
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("PID %d" % os.getpid(), err)
        self.assertIn("is still running" if os.name == "posix" else "liveness could not be determined", err)
        self.assertTrue(os.path.exists(self.p(".printfarmer/lock")))
        self.assertEqual(self.compose_commands("up"), [])

    def test_lock_cleanup_failure_does_not_mask_primary_error(self):
        data = self.publish("1.2.0")
        self.docker.pull_failures.add(data["images"]["api"]["reference"])
        real_remove = reg.os.remove

        def failing_remove(path):
            if path.endswith("lock"):
                raise PermissionError("denied")
            return real_remove(path)

        reg.os.remove = failing_remove
        try:
            code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        finally:
            reg.os.remove = real_remove
        self.assertEqual(code, 2)
        self.assertIn("manifest unknown", err)
        self.assertIn("could not remove lock", self.output.getvalue())

    def test_lock_replaced_by_another_run_is_left_alone(self):
        self.publish("1.2.0")
        controller = self.controller()
        original = controller.wait_healthy

        def replace_lock(*args):
            self.write_text(".printfarmer/lock", "999999 other-run 2026-01-01T00:00:00+00:00")
            return original(*args)

        controller.wait_healthy = replace_lock
        self.assertEqual(controller.update("1.2.0"), 0)
        self.assertEqual(read_text(self.p(".printfarmer/lock")).split()[1], "other-run")
        self.assertIn("belongs to another run", self.output.getvalue())

    def test_malformed_pending_state_fails_with_actionable_error(self):
        self.publish("1.2.0")
        self.write(".printfarmer/pending.json", {"action": "update", "version": "1.2.0", "images": {}})
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--resume-interrupted")
        self.assertEqual(code, 2)
        self.assertIn("malformed (invalid: phase)", err)
        self.assertIn("move it aside", err)
        self.assertEqual(self.compose_commands("up"), [])
        self.assertTrue(os.path.exists(self.p(".printfarmer/pending.json")))
        self.write_text(".printfarmer/pending.json", "{not json")
        code, err = self.main("status")
        self.assertEqual(code, 2)
        self.assertIn("unreadable", err)

    def test_interrupt_during_health_leaves_recoverable_pending(self):
        self.publish("1.2.0")
        controller = self.controller()

        def interrupt(*args):
            raise KeyboardInterrupt

        controller.wait_healthy = interrupt
        with self.assertRaises(KeyboardInterrupt):
            controller.update("1.2.0")
        self.assertEqual(load_json(self.p(".printfarmer/pending.json"))["phase"], "failed-health")
        self.assertFalse(os.path.exists(self.p(".printfarmer/release.json")))
        self.assertFalse(os.path.exists(self.p(".printfarmer/lock")))
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--resume-interrupted")
        self.assertEqual(code, 0, err)
        self.assertFalse(os.path.exists(self.p(".printfarmer/pending.json")))

    def test_status_lists_and_verifies_preserved_host_paths(self):
        self.output.seek(0)
        self.output.truncate(0)
        self.assertEqual(self.main("status")[0], 0)
        out = self.output.getvalue()
        self.assertIn("ok       ./.volumes/data", out)
        self.assertIn("ok       ./deploy/nginx/nginx.conf", out)
        self.assertIn("ok       ./.env", out)
        self.assertNotIn(".printfarmer/files", out)
        self.assertNotIn("s3cr3t-value", out)
        shutil.rmtree(self.p("deploy"))
        self.output.seek(0)
        self.output.truncate(0)
        self.assertEqual(self.main("status")[0], 1)
        self.assertIn("MISSING  ./deploy/nginx/nginx.conf", self.output.getvalue())

    def test_rollback_to_git_checkout_is_refused(self):
        self.publish("1.2.0")
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        code, err = self.main("rollback", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("no previous release", err)


OLD_VOLUME = "printfarmer-custom-profiles-2.4.2"
NEW_VOLUME = "printfarmer-custom-profiles-2.5.0"
ADOPT = ("--adopt-orcaslicer-version", "2.5.0", "--accept-profile-compatibility-risk")


class OrcaProfileAdoptionTests(RegistryTestCase):
    def setUp(self):
        super().setUp()
        self.migrate()
        self.profiles = {"filament/pla.json": "pla", "machine/x1c.json": "x1c", "process/fine.json": "fine"}
        self.docker.volume_files[OLD_VOLUME] = dict(self.profiles)
        self.compose_labels = {"com.docker.compose.project": "printfarmer",
                               "com.docker.compose.volume": "orcaslicer-custom-profiles"}
        self.docker.volume_label_map[OLD_VOLUME] = dict(self.compose_labels, **{
            "com.docker.compose.config-hash": "abc", "com.docker.compose.version": "5.4.0"})

    def release_state(self):
        return load_json(self.p(".printfarmer/release.json"))

    def worker_volume(self):
        return self.docker.containers["orcaslicer-worker"]["Mounts"][0]["Name"]

    def test_explicit_adoption_copies_profiles_into_new_volume(self):
        data = self.publish("1.2.0", orca="2.5.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT)
        self.assertEqual(code, 0, err)
        self.assertEqual(self.worker_volume(), NEW_VOLUME)
        self.assertEqual(self.docker.up_configs[-1]["volumes"]["orcaslicer-custom-profiles"]["name"], NEW_VOLUME)
        self.assertEqual(self.docker.volume_files[NEW_VOLUME], self.profiles)
        self.assertEqual(self.docker.volume_files[OLD_VOLUME], self.profiles)
        # Same Compose project/volume identity as the replaced volume; config-hash/version are not copied.
        self.assertEqual(self.docker.volume_label_map[NEW_VOLUME],
                         dict(self.compose_labels, **{reg.PROFILE_COPY_LABEL: OLD_VOLUME}))
        self.assertTrue(NEW_VOLUME.startswith("printfarmer-custom-profiles-"))
        self.assertEqual(self.release_state()["current"]["orcaslicerVersion"], "2.5.0")
        record = load_json(self.p(".printfarmer/orca-volumes.json"))["copies"]
        self.assertEqual([(c["source"], c["target"]) for c in record], [(OLD_VOLUME, NEW_VOLUME)])
        # The helper is the verified worker image by digest, offline, with the old volume read-only and no env.
        (run,) = self.docker.copy_runs
        self.assertEqual(run[run.index("--network") + 1], "none")
        self.assertIn(data["images"]["orcaslicer-worker"]["reference"], run)
        self.assertIn("%s:/from:ro" % OLD_VOLUME, run)
        self.assertFalse({"-e", "--env", "--env-file"} & set(run))
        calls = self.docker.calls
        copy_index = next(i for i, c in enumerate(calls) if c[1] == "run")
        up_index = next(i for i, c in enumerate(calls) if c[:2] == ["docker", "compose"] and "up" in c)
        self.assertLess(copy_index, up_index)
        # Later updates stay on the adopted volume without repeating the flags, despite the stale .env value.
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2", orca="2.5.0")
        code, err = self.main("update", "--version", "1.3.0", "--backup-confirmed")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.worker_volume(), NEW_VOLUME)
        self.assertEqual(len(self.docker.copy_runs), 1)

    def test_adoption_requires_explicit_risk_acknowledgement(self):
        self.publish("1.2.0", orca="2.5.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT[:2])
        self.assertEqual(code, 2)
        self.assertIn("--accept-profile-compatibility-risk", err)
        self.assertNotIn(NEW_VOLUME, self.docker.volumes)
        self.assertEqual(self.compose_commands("up"), [])
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--adopt-orcaslicer-version",
                              "2.4.2", "--accept-profile-compatibility-risk")
        self.assertEqual(code, 2)
        self.assertIn("already the deployment's profile version", err)

    def test_mismatch_without_adoption_names_the_flags(self):
        self.publish("1.2.0", orca="2.5.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 2)
        self.assertIn("--adopt-orcaslicer-version 2.5.0", err)
        self.assertNotIn(NEW_VOLUME, self.docker.volumes)

    def test_plan_shows_adoption_without_side_effects(self):
        self.publish("1.2.0", orca="2.5.0")
        before = self.snapshot()
        code, err = self.main("plan", "--version", "1.2.0", *ADOPT)
        self.assertEqual(code, 0, err)
        self.assertIn("copy %s -> %s" % (OLD_VOLUME, NEW_VOLUME), self.output.getvalue())
        self.assertEqual(self.snapshot(), before)
        self.assertNotIn(NEW_VOLUME, self.docker.volumes)
        self.assertEqual(self.docker.copy_runs, [])

    def test_copy_failure_starts_nothing_and_retry_recopies(self):
        self.publish("1.2.0", orca="2.5.0")
        self.docker.fail_copy = True
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT)
        self.assertEqual(code, 2)
        self.assertEqual(self.compose_commands("up"), [])
        self.assertEqual(self.worker_volume(), OLD_VOLUME)
        self.assertEqual(self.docker.volume_files[OLD_VOLUME], self.profiles)
        self.assertEqual(load_json(self.p(".printfarmer/pending.json"))["phase"], "failed-copying")
        self.assertFalse(os.path.exists(self.p(".printfarmer/orca-volumes.json")))
        self.assertFalse(os.path.exists(self.p(".printfarmer/release.json")))
        self.docker.fail_copy = False
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT)
        self.assertEqual(code, 2)
        self.assertIn("--resume-interrupted", err)
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--resume-interrupted", *ADOPT)
        self.assertEqual(code, 0, err)
        self.assertIn("find /to -mindepth 1 -delete", self.docker.copy_runs[-1][-1])
        self.assertEqual(self.docker.volume_files[NEW_VOLUME], self.profiles)
        self.assertEqual(self.worker_volume(), NEW_VOLUME)

    def test_single_verification_message_and_copy_message(self):
        self.publish("1.2.0", orca="2.5.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT)
        self.assertEqual(code, 0, err)
        out = self.output.getvalue()
        self.assertEqual(out.count("All images present and verified"), 1)
        verified, copied = out.index("All images present and verified"), out.index("Profiles copied into")
        self.assertLess(verified, copied)
        self.assertIn("Profiles copied into %s; no service has been stopped or restarted yet." % NEW_VOLUME, out)

    def test_copy_script_checks_tools_before_writing(self):
        for recopy in (False, True):
            script = reg.profile_copy_script(recopy)
            first_write = script.index("cp -a /from/. /to/")
            for tool in reg.PROFILE_COPY_TOOLS:
                self.assertLess(script.index(tool), first_write)
            self.assertLess(script.index("--no-dereference /dev/null"), first_write)
            self.assertLess(script.index("-maxdepth 0 -printf"), first_write)
            self.assertIn("diff -r --no-dereference /from /to", script)
            self.assertIn("%P|%u:%g|%m|%y|%l", script)
            if recopy:
                self.assertLess(script.index("-maxdepth 0 -printf"), script.index("find /to -mindepth 1 -delete"))
            else:
                self.assertNotIn("-delete", script)

    def test_missing_copy_tool_fails_with_actionable_reason(self):
        self.publish("1.2.0", orca="2.5.0")
        self.docker.copy_error = (3, "profile copy: worker image lacks diff; nothing was copied")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT)
        self.assertEqual(code, 2)
        self.assertIn("worker image lacks diff; nothing was copied", err)
        self.assertIn("%s was mounted read-only and is unchanged" % OLD_VOLUME, err)
        self.assertIn("--resume-interrupted", err)
        self.assertEqual(self.compose_commands("up"), [])
        self.assertEqual(self.worker_volume(), OLD_VOLUME)
        self.assertEqual(load_json(self.p(".printfarmer/pending.json"))["phase"], "failed-copying")

    def test_resume_onto_other_volume_after_started_adoption_needs_ack(self):
        self.publish("1.2.0")
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2")
        self.assertEqual(self.main("update", "--version", "1.3.0", "--backup-confirmed")[0], 0)
        self.publish("1.4.0", commit="d" * 40, char="4", orca="2.5.0")
        self.fetched["https://api.github.com/repos/%s/compare/%s...%s"
                     % (reg.GITHUB_REPOSITORY, THIRD_COMMIT, "d" * 40)] = {"status": "ahead"}
        self.docker.unhealthy_after_up.add("api")
        code, err = self.main("update", "--version", "1.4.0", "--backup-confirmed", "--health-timeout", "20", *ADOPT)
        self.assertEqual(code, 2)
        self.assertEqual(self.worker_volume(), NEW_VOLUME)
        self.assertEqual(self.release_state()["current"]["orcaslicerVersion"], "2.4.2")
        self.assertEqual(load_json(self.p(".printfarmer/pending.json"))["workerVolumes"], [NEW_VOLUME])
        self.output.truncate(0)
        self.output.seek(0)
        self.assertEqual(self.main("status")[0], 1)
        self.assertIn("worker may be mounted on %s" % NEW_VOLUME, self.output.getvalue())
        self.docker.unhealthy_after_up.clear()
        self.docker.volume_files[NEW_VOLUME]["filament/petg.json"] = "petg"
        ups = len(self.compose_commands("up"))
        for command in (["update", "--version", "1.4.0"], ["rollback", "--allow-unsafe-downgrade"]):
            code, err = self.main(*command, "--backup-confirmed", "--resume-interrupted")
            self.assertEqual(code, 2)
            self.assertIn("--accept-profile-volume-revert", err)
            self.assertIn(NEW_VOLUME, err)
        self.assertEqual(len(self.compose_commands("up")), ups)
        self.assertEqual(self.worker_volume(), NEW_VOLUME)
        code, err = self.main("rollback", "--allow-unsafe-downgrade", "--backup-confirmed", "--resume-interrupted",
                              "--accept-profile-volume-revert")
        self.assertEqual(code, 0, err)
        self.assertIn("no longer mounted", self.output.getvalue())
        self.assertEqual(self.worker_volume(), OLD_VOLUME)
        self.assertEqual(self.docker.volume_files[NEW_VOLUME]["filament/petg.json"], "petg")
        self.assertFalse(os.path.exists(self.p(".printfarmer/pending.json")))

    def test_resume_with_same_adoption_stays_on_new_volume(self):
        self.publish("1.2.0", orca="2.5.0")
        self.docker.unhealthy_after_up.add("api")
        code, _ = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--health-timeout", "20", *ADOPT)
        self.assertEqual(code, 2)
        self.docker.unhealthy_after_up.clear()
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", "--resume-interrupted", *ADOPT)
        self.assertEqual(code, 0, err)
        self.assertEqual(self.worker_volume(), NEW_VOLUME)
        self.assertEqual(len(self.docker.copy_runs), 1)

    def test_foreign_existing_target_volume_is_refused(self):
        self.publish("1.2.0", orca="2.5.0")
        self.docker.volumes.add(NEW_VOLUME)
        self.docker.volume_files[NEW_VOLUME] = {"other.json": "keep"}
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed", *ADOPT)
        self.assertEqual(code, 2)
        self.assertIn("was not created by a profile adoption", err)
        self.assertEqual(self.docker.copy_runs, [])
        self.assertEqual(self.compose_commands("up"), [])
        self.assertEqual(self.docker.volume_files[NEW_VOLUME], {"other.json": "keep"})

    def test_rollback_returns_to_old_volume_and_keeps_both(self):
        self.publish("1.2.0")
        self.assertEqual(self.main("update", "--version", "1.2.0", "--backup-confirmed")[0], 0)
        self.publish("1.3.0", commit=THIRD_COMMIT, char="2", orca="2.5.0")
        self.assertEqual(self.main("update", "--version", "1.3.0", "--backup-confirmed", *ADOPT)[0], 0)
        self.docker.volume_files[NEW_VOLUME]["filament/petg.json"] = "petg"
        code, err = self.main("rollback", "--backup-confirmed", "--allow-unsafe-downgrade")
        self.assertEqual(code, 0, err)
        self.assertEqual(self.worker_volume(), OLD_VOLUME)
        self.assertEqual(self.release_state()["current"]["orcaslicerVersion"], "2.4.2")
        self.assertTrue({OLD_VOLUME, NEW_VOLUME} <= self.docker.volumes)
        self.assertEqual(self.docker.volume_files[OLD_VOLUME], self.profiles)
        self.assertIn("filament/petg.json", self.docker.volume_files[NEW_VOLUME])
        self.assertIn("Profile changes made since the adoption stay in the newer volume", self.output.getvalue())

    def test_unapproved_profile_volume_switch_is_still_rejected(self):
        controller = self.controller()
        deployment = controller.deployment()
        self.docker.volumes.add(NEW_VOLUME)
        refs = {c: "%s%s@%s" % (reg.IMAGE_PREFIX, c, digest("3")) for c in set(deployment["services"].values())}
        resolved, _ = controller.resolved_config(deployment, refs, "2.5.0")
        containers = controller.project_containers(deployment["project"])
        with self.assertRaises(reg.MigrationError) as raised:
            controller.check_storage(deployment["project"], resolved, containers, controller.vendored_abs(deployment))
        self.assertIn("would switch from volume %s to %s" % (OLD_VOLUME, NEW_VOLUME), str(raised.exception))
        controller.check_storage(deployment["project"], resolved, containers, controller.vendored_abs(deployment),
                                 allowed_transitions={(OLD_VOLUME, NEW_VOLUME)})


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
        self.up_configs.append(config)
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


REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", ".."))
GENERATOR = os.path.join(REPO_ROOT, "scripts", "docker", "compose-generator.sh")
PF_SERVICES = {"api", "frontend", "slicer-host", "printer-discovery", "orcaslicer-worker"}
# Git-tracked bind sources in a generated deployment; everything else it binds is generated state.
GENERATED_TRACKED = ["deploy/nginx/nginx-proxy-split.conf", "scripts/docker/configs/otel-collector-config.yaml"]
GENERATED_ENV = {
    "DB_PASSWORD": "s3cr3t-value", "SA_PASSWORD": "s3cr3t-value", "ConnectionStrings__Default": "s3cr3t-value",
    "Jwt__Key": "s3cr3t-value", "GRAFANA_ADMIN_PASSWORD": "s3cr3t-value",
    "WebAuthn__RelyingPartyId": "farm.example", "WebAuthn__Origin": "https://farm.example",
    "ORCASLICER_VERSION": "2.4.2", "ORCASLICER_CONTAINER_DIGEST": "sha256:" + "9" * 64,
}


def find_bash():
    if os.name == "nt":
        git_bash = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), "Git", "bin", "bash.exe")
        return git_bash if os.path.exists(git_bash) else None
    return shutil.which("bash")


@unittest.skipUnless(compose_available() and find_bash(), "docker compose CLI or bash not available")
class RealGeneratorTests(RegistryTestCase):
    """Migrates compose files rendered by the real compose-generator.sh, not hand-written fixtures."""

    def render(self, provider):
        import subprocess
        for name in ("docker-compose.yml", "docker-compose.override.yml"):
            os.remove(self.p(name))
        result = subprocess.run([find_bash(), GENERATOR.replace("\\", "/"), "--output-dir",
                                 self.root.replace("\\", "/"), "--db-provider", provider, "--include-spoolman",
                                 "--include-discovery", "--enable-orca-worker", "yes"],
                                cwd=REPO_ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        if result.returncode != 0 and ("envsubst" in result.stdout or "ruamel" in result.stdout):
            self.skipTest("compose-generator prerequisites unavailable: " + result.stdout[-300:])
        self.assertEqual(result.returncode, 0, result.stdout[-2000:])
        for rel in GENERATED_TRACKED:
            os.makedirs(os.path.dirname(self.p(rel)), exist_ok=True)
            shutil.copyfile(os.path.join(REPO_ROOT, *rel.split("/")), self.p(rel))
        self.write_text(".env", "".join("%s=%s\n" % item for item in GENERATED_ENV.items()))
        self.write_text(".deploy-config", "DB_PROVIDER=%s\nENABLE_ORCA_WORKER=yes\n" % provider)
        self.docker = RealComposeDocker(self.root)
        self.docker.tracked = list(GENERATED_TRACKED)
        compose_file = self.p("docker-compose.yml")
        code, text = self.docker.real_config(["-p", PROJECT, "--project-directory", self.root, "--env-file",
                                              self.p(".env"), "-f", compose_file, "config", "--format", "json"], {})
        self.assertEqual(code, 0, text)
        config = json.loads(text)
        self.assertTrue(PF_SERVICES <= set(config["services"]), sorted(config["services"]))
        self.docker.volumes = {v["name"] for v in (config.get("volumes") or {}).values()}
        for service in config["services"]:
            self.docker.create(service, "printfarmer-" + service, config, [compose_file], self.root)
        for container in self.docker.containers.values():
            for mount in container["Mounts"]:
                if mount["Type"] == "bind" and not os.path.exists(mount["Source"]):
                    os.makedirs(mount["Source"])  # a live host already has its data directories
        return config

    def check_provider(self, provider):
        original = self.render(provider)
        before = {name: c["Mounts"] for name, c in self.docker.containers.items()}
        self.migrate()
        release_text = read_text(self.p("docker-compose.release.yml"))
        self.assertNotIn("s3cr3t-value", release_text)
        release = json.loads(release_text)
        self.assertEqual(set(release["services"]), set(original["services"]))
        for name, svc in release["services"].items():
            self.assertNotIn("build", svc, name)
            if name in PF_SERVICES:
                self.assertTrue(svc["image"].startswith("${%s:?" % reg.image_var(name)), (name, svc["image"]))

        data = self.publish("1.2.0")
        code, err = self.main("update", "--version", "1.2.0", "--backup-confirmed")
        self.assertEqual(code, 0, err)
        calls = self.docker.calls
        up_index = next(i for i, c in enumerate(calls) if c[:2] == ["docker", "compose"] and "up" in c)
        pulls = [i for i, c in enumerate(calls) if c[1] == "pull"]
        self.assertEqual(len(pulls), len(PF_SERVICES))
        self.assertTrue(all(i < up_index for i in pulls))
        self.assertIn("--no-build", calls[up_index])
        for name in PF_SERVICES:
            self.assertEqual(self.docker.containers[name]["Config"]["Image"], data["images"][name]["reference"])
        for name in set(original["services"]) - PF_SERVICES:
            self.assertEqual(self.docker.containers[name]["Config"]["Image"], original["services"][name]["image"])
        vendored = {self.p(rel): self.p(".printfarmer", "files", *rel.split("/")) for rel in GENERATED_TRACKED}
        expected = {name: [dict(m, Source=vendored.get(m.get("Source"), m.get("Source"))) if m["Type"] == "bind"
                           else m for m in mounts] for name, mounts in before.items()}
        self.assertEqual({name: c["Mounts"] for name, c in self.docker.containers.items()}, expected)
        for source in vendored.values():
            self.assertTrue(os.path.isfile(source), source)
        worker_env = self.docker.up_configs[-1]["services"]["orcaslicer-worker"]["environment"]
        self.assertEqual(worker_env["Worker__ContainerDigest"],
                         data["images"]["orcaslicer-worker"]["reference"].partition("@")[2])

    def test_generated_postgres_deployment(self):
        self.check_provider("postgres")

    def test_generated_sqlserver_deployment(self):
        self.check_provider("sqlserver")


if __name__ == "__main__":
    unittest.main()
