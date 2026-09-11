#!/usr/bin/env python3
"""Run-owned daily validation. Invoked through daily-validation.ps1 on Windows."""

import argparse
import contextlib
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


REPO = "OlyForge3D/PrintFarmer"
SERVICES = ("api", "frontend", "slicer-host", "printer-discovery",
            "orcaslicer-worker", "moonraker-emulator")
INSTANCES = ("ready", "printing", "paused", "shutdown")
TOPOLOGY = ("database", "api", "frontend", "nginx-proxy", "slicer-host",
            "printer-discovery", "orcaslicer-worker",
            *(f"moonraker-{name}" for name in INSTANCES))
PORTS = ("API_PORT", "HTTP_PORT", "HTTPS_PORT", "SLICER_HOST_PORT", "POSTGRES_PORT",
         "MOONRAKER_EMULATOR_PORT", "MOONRAKER_EMULATOR_PRINTING_PORT",
         "MOONRAKER_EMULATOR_PAUSED_PORT", "MOONRAKER_EMULATOR_SHUTDOWN_PORT")
LABEL = "io.printfarmer.daily-validation"
PHASES = {
    "phase-a": ["npm", "run", "test:e2e:moonraker", "--", "--project=chromium"],
    "phase-b": ["npm", "run", "test:e2e:emulator", "--", "--project=chromium",
                "--workers=1", "--grep-invert", "Moonraker"],
}
ROOT = Path.home() / ".local/share/printfarmer-daily"


class Blocked(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise Blocked(message)


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def timestamp(value):
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00"))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(".tmp")
    with temporary.open("w", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.chmod(temporary, 0o600)
    temporary.replace(path)


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def private_directory(path):
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    require(path.resolve() == path and path.stat().st_uid == os.getuid(),
            f"Unsafe directory: {path}")
    require(path.stat().st_mode & 0o077 == 0, f"Directory must be private: {path}")


@contextlib.contextmanager
def lock(path):
    import fcntl
    with Path(path).open("a") as stream:
        try:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise Blocked("Another command owns this run/allocation lock; do not interfere") from error
        yield


def validate_manifest(manifest, run_id):
    require(manifest.get("schemaVersion") == 1 and manifest.get("repository") == REPO
            and manifest.get("branch") == "development", "Wrong manifest schema/repository/branch")
    commit = manifest.get("commit", "")
    require(re.fullmatch(r"[0-9a-f]{40}", commit), "Manifest requires a full commit SHA")
    require(manifest.get("workflowRun") == f"https://github.com/{REPO}/actions/runs/{run_id}",
            "Manifest belongs to another workflow run")
    tag = manifest.get("tag", "")
    require(re.fullmatch(f"sha-{commit}-run-{run_id}-attempt-[1-9][0-9]*", tag),
            "Manifest tag does not bind commit and run")
    require(set(manifest.get("images", {})) == set(SERVICES), "Manifest must contain exactly six services")
    for service, image in manifest["images"].items():
        name = f"ghcr.io/olyforge3d/printfarmer-{service}"
        require(image.get("name") == name and image.get("tag") == tag,
                f"Mixed image identity: {service}")
        require(re.fullmatch(r"sha256:[0-9a-f]{64}", image.get("digest", "")),
                f"Invalid digest: {service}")
        require(image.get("reference") == f'{name}@{image["digest"]}',
                f"Image reference is not digest-pinned: {service}")
    return manifest


def select_candidate(inventory, cutoff):
    eligible = []
    for item in inventory:
        require(item["headBranch"] == "development" and item["status"] == "completed"
                and item["conclusion"] == "success", "Wrong workflow inventory filters")
        require(re.fullmatch(r"[0-9a-f]{40}", item["headSha"]), "Invalid workflow commit")
        if timestamp(item["createdAt"]) <= timestamp(cutoff) and timestamp(item["updatedAt"]) <= timestamp(cutoff):
            eligible.append(item)
    require(eligible, "No successful image run observed at the selection cutoff")
    return max(eligible, key=lambda item: (timestamp(item["createdAt"]), int(item["databaseId"])))


def verify_selection(primary, verification, cutoff):
    candidate = select_candidate(primary, cutoff)
    observed = select_candidate(verification, cutoff)
    require((candidate["databaseId"], candidate["headSha"]) ==
            (observed["databaseId"], observed["headSha"]),
            "Image selection observations disagree at cutoff; blocked without reselection")
    return candidate


def redact(text, env):
    for key, value in sorted(env.items(), key=lambda pair: -len(pair[1])):
        if any(word in key.lower() for word in ("password", "key", "connectionstrings")):
            if value:
                text = text.replace(value, "[REDACTED]")
    text = re.sub(r"(?i)(bearer\s+)[\w.+/=-]+", r"\1[REDACTED]", text)
    text = re.sub(r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b", "[REDACTED]", text)
    return text


def execute(arguments, cwd, env, timeout=120, log=None, check=True):
    """Capture once; preserve real child status without a tee pipeline."""
    process = subprocess.Popen(arguments, cwd=cwd, env=env, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, start_new_session=True)
    try:
        output, errors = process.communicate(timeout=timeout)
        code = process.returncode
    except (subprocess.TimeoutExpired, KeyboardInterrupt):
        os.killpg(process.pid, signal.SIGTERM)
        try:
            output, errors = process.communicate(timeout=15)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            output, errors = process.communicate()
        code = 124
    text = output.decode("utf-8", errors="replace")
    diagnostic = text + errors.decode("utf-8", errors="replace")
    if log:
        Path(log).write_text(redact(diagnostic, env), encoding="utf-8")
        os.chmod(log, 0o600)
    if check and code:
        detail = redact(diagnostic, env)[-2500:]
        raise Blocked(f"{Path(arguments[0]).name} exited {code}: {detail}")
    return code, text


def base_environment():
    allowed = ("HOME", "USER", "LOGNAME", "PATH", "LANG", "LC_ALL", "SSH_AUTH_SOCK",
               "GH_TOKEN", "GITHUB_TOKEN", "XDG_CONFIG_HOME")
    env = {key: value for key, value in os.environ.items() if key in allowed}
    env.update(DOCKER_HOST="unix:///var/run/docker.sock", DOCKER_CONFIG=str(ROOT / "docker"),
               GIT_TERMINAL_PROMPT="0", GH_PROMPT_DISABLED="1", NO_COLOR="1")
    return env


def probe():
    env = base_environment()
    cwd = str(Path.home())
    versions = {}
    commands = {
        "dockerPath": ["bash", "-lc", "command -v docker"],
        "dockerPackage": ["dpkg-query", "-S", "/usr/bin/docker"],
        "dockerService": ["systemctl", "is-active", "docker"],
        "dockerVersion": ["/usr/bin/docker", "version", "--format", "{{json .}}"],
        "composeVersion": ["/usr/bin/docker", "compose", "version", "--short"],
        "nodeVersion": ["node", "--version"], "npmVersion": ["npm", "--version"],
        "githubVersion": ["gh", "--version"],
        "pythonVersion": ["/usr/bin/python3", "--version"],
        "filesystem": ["findmnt", "-T", str(ROOT), "-n", "-o", "FSTYPE"],
    }
    for name, command in commands.items():
        versions[name] = execute(command, cwd, env)[1].strip()
    require(versions["dockerPath"] == "/usr/bin/docker", "Reject non-native Docker CLI")
    require(re.match(r"docker(?:\.io|-ce(?:-cli)?):", versions["dockerPackage"]),
            "Docker CLI is not owned by an approved native package")
    require(Path("/usr/bin/docker").resolve().is_relative_to(Path("/usr/bin")),
            "Docker CLI resolves outside native /usr/bin")
    require(versions["filesystem"] == "ext4", "Run workspace must be native WSL ext4")
    require("docker-desktop" not in str(Path("/var/run/docker.sock").resolve()).lower(),
            "Docker Desktop socket is prohibited")
    version = json.loads(versions["dockerVersion"])
    require(version.get("Client") and version.get("Server"), "Native client and server required")
    require("desktop" not in json.dumps(version).lower(), "Docker Desktop daemon is prohibited")
    execute(["bash", "-lc", "command -v git jq openssl envsubst >/dev/null; "
             "python3 -c 'import ruamel.yaml'"], cwd, env)
    return versions


def ownership_overlay(config, state):
    project = state["project"]
    overlay = {"services": {}, "networks": {}, "volumes": {}}
    require(set(config["services"]) == set(TOPOLOGY), "Unexpected/incomplete generated topology")
    for name, service in config["services"].items():
        require(not service.get("container_name"), f"Fixed container name: {name}")
        require(not service.get("privileged") and not service.get("network_mode"),
                f"Unsafe isolation settings: {name}")
        overlay["services"][name] = {"labels": {LABEL: state["validationId"]}}
    for kind in ("networks", "volumes"):
        for name, resource in config.get(kind, {}).items():
            require(not resource.get("external"), f"External {kind} prohibited: {name}")
            overlay[kind][name] = {"name": f"{project}-{name}", "labels": {LABEL: state["validationId"]}}
    return overlay


def validate_config(config, state):
    require(set(config["services"]) == set(TOPOLOGY), "Incomplete topology")
    expected_images = {key: value["reference"] for key, value in state["manifest"]["images"].items()}
    for name, service in config["services"].items():
        image_key = "moonraker-emulator" if name.startswith("moonraker-") else name
        if image_key in expected_images:
            require(service.get("image") == expected_images[image_key], f"Wrong immutable image: {name}")
        require(service.get("labels", {}).get(LABEL) == state["validationId"], f"Missing owner: {name}")
        require(not service.get("build"), f"Local build is prohibited: {name}")
        for mount in service.get("volumes", []):
            if mount["type"] == "bind":
                source = Path(mount["source"])
                require(source.resolve().is_relative_to(Path(state["workspace"])),
                        f"Bind mount escapes owned runtime: {name}")
        for port in service.get("ports", []):
            require(port.get("host_ip") == "127.0.0.1"
                    and int(port["published"]) in state["ports"].values(),
                    f"Unexpected/non-loopback published port: {name}")
    for kind in ("networks", "volumes"):
        for name, resource in config.get(kind, {}).items():
            require(not resource.get("external")
                    and resource["name"] == f'{state["project"]}-{name}'
                    and resource.get("labels", {}).get(LABEL) == state["validationId"],
                    f"Foreign {kind}: {name}")


def validate_phase(report, state, invocation, code):
    for key in ("validationId", "commit", "manifestHash", "harnessHash"):
        require(report.get(key) == state[key], f"Phase evidence mismatched {key}")
    require(report.get("invocationId") == invocation["id"]
            and report.get("phase") == invocation["phase"], "Stale/missing phase invocation")
    require(invocation["status"] == "finished" and isinstance(code, int), "Phase did not finish")
    require(timestamp(invocation["startedAt"]) <= timestamp(report["startedAt"])
            <= timestamp(report["finishedAt"]) <= timestamp(invocation["finishedAt"]),
            "Phase timestamps do not belong to this invocation")
    require(report.get("tests"), "No tests collected/executed; counts cannot establish a pass")
    counts = dict(passed=0, failed=0, skipped=0, **{"did-not-run": 0, "flaky": 0})
    for test in report["tests"]:
        category = test.get("category")
        require(category in ("passed", "failed", "skipped", "did-not-run"), "Unknown test category")
        attempts = test.get("attempts", [])
        require(category == "did-not-run" or attempts, "Missing execution cannot become pass/skip")
        if category == "skipped":
            require(any(a["type"] in ("skip", "fixme") for a in test.get("annotations", [])),
                    "Unexplained skipped result is not an explicit skip")
        counts[category] += 1
        if test.get("outcome") == "flaky":
            counts["flaky"] += 1
    require(code != 0 or (report["status"] == "passed" and not report.get("errors")
                         and counts["failed"] == counts["did-not-run"] == 0),
            "Exit code/report disagreement; cannot claim pass")
    require(counts["passed"] + counts["failed"] > 0, "Phase did not execute meaningfully")
    return counts


class Run:
    def __init__(self, run_id):
        require(re.fullmatch(r"dv-[0-9]{8}t[0-9]{6}-[0-9a-f]{32}", run_id), "Invalid validation ID")
        self.directory = ROOT / "runs" / run_id
        require(self.directory.is_dir(), "Unknown run; never load historical global state")
        private_directory(self.directory)
        self.state = read_json(self.directory / "state.json")
        self.validate_identity(run_id)
        self.env = base_environment()
        env_path = self.directory / "runtime" / "secrets.json"
        if env_path.exists():
            require(not env_path.is_symlink() and env_path.stat().st_mode & 0o077 == 0,
                    "Secret env permissions are not restrictive")
            require(digest(env_path) == self.state["secretHash"], "Secret env changed")
            self.env.update(read_json(env_path))

    def validate_identity(self, run_id):
        state = self.state
        require(state.get("schemaVersion") == 1 and state.get("validationId") == run_id,
                "Stale/foreign state schema or ID")
        require(state.get("workspace") == str(self.directory / "runtime")
                and state.get("project") == run_id, "Run ownership mismatch")
        require(state["harnessHash"] == os.environ["PF_DAILY_HARNESS_HASH"],
                "Resume with the exact original harness bundle")
        require(state["harnessRevision"] == os.environ["PF_DAILY_HARNESS_REVISION"],
                "Resume with the exact original harness revision")
        if "manifest" in state:
            validate_manifest(state["manifest"], state["imageRun"])
            require(digest(self.directory / "image-set.json") == state["manifestHash"],
                    "Recorded manifest changed")

    def save(self):
        write_json(self.directory / "state.json", self.state)

    def command(self, args, name, timeout=120, check=True, cwd=None):
        return execute(args, cwd or self.state["workspace"], self.env, timeout,
                       self.directory / f"{name}.log", check)

    def compose(self, args, name="compose", timeout=120, check=True):
        files = self.state["composeFiles"]
        require(files and files[0] == str(Path(self.state["workspace"]) / "stack/docker-compose.yml"),
                "Missing/wrong ordered Compose files")
        command = ["/usr/bin/docker", "compose", "--env-file", self.state["envFile"],
                   "--project-name", self.state["project"]]
        for file in files:
            command += ["-f", file]
        return self.command(command + args, name, timeout, check)

    def verify_files(self):
        for path, expected in self.state.get("fileHashes", {}).items():
            require(digest(path) == expected, f"Run configuration/source changed: {path}")
        source = self.state["source"]
        require(self.command(["git", "rev-parse", "HEAD"], "source-head", cwd=source)[1].strip()
                == self.state["commit"], "Tested checkout moved")
        require(not self.command(["git", "status", "--porcelain", "--untracked-files=no"],
                                 "source-status", cwd=source)[1].strip(), "Tested source is modified")

    def step(self, name, action, limit=1):
        previous = self.state["steps"].get(name, {})
        require(previous.get("status") != "running", f"Interrupted {name}; cleanup, do not replay")
        require(previous.get("attempts", 0) < limit, f"{name} exhausted {limit} attempt(s)")
        record = {"status": "running", "startedAt": now(), "attempts": previous.get("attempts", 0) + 1}
        self.state["steps"][name] = record
        self.save()
        try:
            action()
            record["status"] = "complete"
        except (Blocked, OSError, ValueError, KeyError) as error:
            record.update(status="blocked", error=redact(str(error), self.env))
            raise
        finally:
            record["finishedAt"] = now()
            self.save()

    def prepare(self):
        self.state["environment"] = probe()
        self.save()
        cutoff = now()
        self.state["selection"] = {"cutoff": cutoff, "startedAt": now(),
                                   "ordering": "createdAt descending, databaseId descending"}
        self.save()
        run = self.command(["gh", "run", "list", "--repo", REPO, "--workflow",
                            "daily-development-images.yml", "--branch", "development",
                            "--status", "success", "--limit", "100", "--json",
                            "databaseId,headSha,headBranch,createdAt,updatedAt,status,conclusion"],
                           "select-image")[1]
        primary = json.loads(run)
        self.state["selection"]["primaryFinishedAt"] = now()
        query = urllib.parse.urlencode({"branch": "development", "status": "success", "per_page": 100})
        endpoint = f"repos/{REPO}/actions/workflows/daily-development-images.yml/runs?{query}"
        response = json.loads(self.command(["gh", "api", "-H", "Cache-Control: no-cache", endpoint],
                                           "verify-selection")[1])
        verification = [{
            "databaseId": item["id"], "headSha": item["head_sha"], "headBranch": item["head_branch"],
            "createdAt": item["created_at"], "updatedAt": item["updated_at"],
            "status": item["status"], "conclusion": item["conclusion"]
        } for item in response["workflow_runs"]]
        selected = verify_selection(primary, verification, cutoff)
        self.state["selection"].update(verifiedAt=now(), selected=selected,
                                      primaryHash=digest(self.directory / "select-image.log"),
                                      verificationHash=digest(self.directory / "verify-selection.log"))
        self.state["imageRun"] = str(selected["databaseId"])
        self.save()
        self.command(["gh", "run", "download", self.state["imageRun"], "--repo", REPO,
                      "--name", "daily-development-image-set", "--dir", str(self.directory / "release")],
                     "download-manifest", 180)
        manifest_path = self.directory / "release/image-set.json"
        manifest = validate_manifest(read_json(manifest_path), self.state["imageRun"])
        require(manifest["commit"] == selected["headSha"], "Manifest and selected workflow commit disagree")
        shutil.copyfile(manifest_path, self.directory / "image-set.json")
        self.state.update(manifest=manifest, manifestHash=digest(manifest_path), commit=manifest["commit"])
        self.save()
        runtime = Path(self.state["workspace"])
        source = runtime / "source"
        source.mkdir()
        self.state["source"] = str(source)
        self.save()
        self.command(["git", "init", "-q", str(source)], "source-init")
        self.command(["git", "config", "core.autocrlf", "input"], "source-config", cwd=source)
        self.command(["git", "fetch", "--depth", "1", f"https://github.com/{REPO}.git",
                      manifest["commit"]], "source-fetch", 300, cwd=source)
        self.command(["git", "checkout", "--detach", "FETCH_HEAD"], "source-checkout", cwd=source)
        for name in ("DAILY_DEVELOPMENT_IMAGES.md", "MOONRAKER_EMULATOR_VALIDATION.md"):
            shutil.copyfile(source / "docs" / name, self.directory / name)
        frontend = source / "src/Web/ReactApp"
        self.state["frontend"] = str(frontend)
        package = read_json(frontend / "package.json")
        require(package["scripts"]["test:e2e:moonraker"] ==
                "playwright test e2e/emulator/ --grep Moonraker --workers=1"
                and package["scripts"]["test:e2e:emulator"] == "playwright test e2e/emulator/",
                "Unsupported tested-commit suite commands; inspect authoritative docs")
        fixture = (frontend / "e2e/fixtures/emulator-setup.ts").read_text()
        require("E2E_ADMIN_USERNAME" in fixture and "E2E_ADMIN_PASSWORD" in fixture,
                "Tested commit does not support external admin fixture credentials")
        self.allocate_environment()
        self.generate()
        self.command(["npm", "ci", "--no-audit", "--no-fund"], "npm-ci", 600, cwd=frontend)
        self.command(["node_modules/.bin/playwright", "install", "chromium"],
                     "browser-install", 600, cwd=frontend)
        self.verify_files()

    def allocate_environment(self):
        runtime = Path(self.state["workspace"])
        with lock(ROOT / "allocation.lock"):
            used = set()
            for file in (ROOT / "runs").glob("*/state.json"):
                other = read_json(file)
                if other.get("cleanup", {}).get("status") != "complete":
                    used.update(other.get("ports", {}).values())
            sockets = []
            ports = {}
            try:
                for key in PORTS:
                    for _ in range(50):
                        candidate = socket.socket()
                        candidate.bind(("127.0.0.1", 0))
                        port = candidate.getsockname()[1]
                        if port not in used:
                            sockets.append(candidate)
                            ports[key] = port
                            used.add(port)
                            break
                        candidate.close()
                    require(key in ports, "Port allocation exhausted")
                self.state["ports"] = ports
                self.save()
            finally:
                for candidate in sockets:
                    candidate.close()
        version_script = (
            'set -euo pipefail; source "$1/scripts/docker/container-versions.conf"; '
            '''python3 -c 'import json,os; print(json.dumps({k:v for k,v in os.environ.items() '''
            '''if k.endswith(("_TAG", "_VERSION", "_SHA256"))}))' ''')
        env = json.loads(self.command(["bash", "-lc", version_script, "daily", self.state["source"]],
                                      "container-versions")[1])
        env.update({key: str(value) for key, value in ports.items()})
        env.update(POSTGRES_USER="printfarmer", POSTGRES_PASSWORD=secrets.token_hex(32),
                   Jwt__Key=secrets.token_hex(48), WORKER_SHARED_API_KEY=secrets.token_hex(32),
                   DISCOVERY_SHARED_API_KEY=secrets.token_hex(32), DB_PROVIDER="Postgres",
                   ENABLE_DISTRIBUTED_SLICING="true", ENABLE_ORCA_WORKER="yes",
                   ENABLE_ORCA_WORKER_PREVIOUS="no", ORCA_WORKER_COUNT="1",
                   COMPOSE_PROJECT_NAME=self.state["project"],
                   EXTERNAL_ORCA_WORKER_TEMP=str(runtime / "stack/.volumes/printfarmer-orcaslicer-temp"),
                   E2E_ADMIN_USERNAME="daily-admin", E2E_ADMIN_PASSWORD="Aa1!" + secrets.token_hex(24),
                   E2E_ADMIN_EMAIL="daily-admin@printfarmer.test",
                   BASE_URL=f'http://127.0.0.1:{ports["HTTP_PORT"]}',
                   API_BASE_URL=f'http://127.0.0.1:{ports["API_PORT"]}',
                   PLAYWRIGHT_BROWSERS_PATH=str(runtime / "browsers"),
                   DOCKER_CONFIG=str(runtime / "docker"),
                   PRINTFARMER_BUILD_CONTEXT=self.state["source"])
        env["ConnectionStrings__Default"] = ("Host=database;Port=5432;Database=printfarmer;"
                                            f'Username=printfarmer;Password={env["POSTGRES_PASSWORD"]}')
        for service, image in self.state["manifest"]["images"].items():
            env[f'PRINTFARMER_{service.upper().replace("-", "_")}_IMAGE'] = image["reference"]
        env["ORCASLICER_CONTAINER_DIGEST"] = self.state["manifest"]["images"]["orcaslicer-worker"]["digest"]
        for instance, key in zip(INSTANCES, PORTS[5:]):
            env[f"MOONRAKER_EMULATOR_URL_{instance.upper()}"] = f"http://127.0.0.1:{ports[key]}"
        write_json(runtime / "secrets.json", env)
        env_file = runtime / "compose.env"
        env_file.write_text("".join(f"{key}='{value}'\n" for key, value in env.items()))
        os.chmod(env_file, 0o600)
        self.state.update(envFile=str(env_file), secretHash=digest(runtime / "secrets.json"))
        self.env.update(env)
        self.save()

    def generate(self):
        runtime = Path(self.state["workspace"])
        source = Path(self.state["source"])
        stack = runtime / "stack"
        stack.mkdir()
        templates = source / "scripts/docker/compose-templates"
        self.state["composeFiles"] = [str(stack / "docker-compose.yml"),
                                     str(templates / "docker-compose.daily-registry.yml"),
                                     str(templates / "docker-compose.daily-validation.yml")]
        self.save()
        self.command(["bash", str(source / "scripts/docker/compose-generator.sh"),
                      "--architecture", "microservices", "--db-provider", "postgres",
                      "--enable-orca-worker", "yes", "--include-discovery", "--include-moonraker-emulator",
                      "--exclude-monitoring", "--exclude-telemetry", "--output-dir", str(stack)],
                     "generate", 180, cwd=source)
        shutil.copytree(source / "deploy/nginx", stack / "deploy/nginx")
        self.command(["bash", str(source / "scripts/generate-certs.sh"), str(stack / "deploy/nginx/certs")],
                     "certificates", cwd=source)
        self.command(["bash", "-lc", 'set -euo pipefail; source "$1/scripts/common-utils.sh"; '
                      'source "$1/scripts/docker-utils.sh"; prepare_orcaslicer_worker_temp_directories',
                      "daily", str(source)], "worker-temp", cwd=stack)
        config = json.loads(self.compose(["config", "--format", "json"], "config-private")[1])
        # Resolved config contains secrets: never retain its command log.
        (self.directory / "config-private.log").unlink()
        overlay = stack / "ownership.json"
        write_json(overlay, ownership_overlay(config, self.state))
        self.state["composeFiles"].append(str(overlay))
        self.save()
        final = json.loads(self.compose(["config", "--format", "json"], "config-private")[1])
        (self.directory / "config-private.log").unlink()
        validate_config(final, self.state)
        resources = {
            kind: {key: value["name"] for key, value in final.get(kind, {}).items()}
            for kind in ("networks", "volumes")
        }
        # Import the tested config; change only external hosting, output locations and add evidence.
        config_path = runtime / "external.config.mjs"
        config_path.write_text(
            f'import original from {json.dumps(str(Path(self.state["frontend"]) / "playwright.config.ts"))};\n'
            "export default { ...original, webServer: undefined,\n"
            f'  testDir: {json.dumps(str(Path(self.state["frontend"]) / "e2e"))},\n'
            "  outputDir: process.env.PF_DAILY_OUTPUT,\n"
            "  reporter: [...original.reporter, "
            f'[{json.dumps(str(Path(__file__).with_name("daily-validation-reporter.mjs")))}]],\n'
            "};\n")
        self.state["playwrightConfig"] = str(config_path)
        self.state["fileHashes"] = {path: digest(path) for path in
                                   [*self.state["composeFiles"], self.state["envFile"], str(config_path),
                                    str(Path(self.state["frontend"]) / "package-lock.json")]}
        self.state["resources"] = resources
        self.save()

    def owned_resources(self):
        project = self.state["project"]
        container_ids = self.command(["/usr/bin/docker", "ps", "-aq", "--filter",
                                      f"label=com.docker.compose.project={project}"], "owner-containers")[1].split()
        if container_ids:
            containers = json.loads(self.command(["/usr/bin/docker", "inspect", *container_ids],
                                                  "inspect-private")[1])
            (self.directory / "inspect-private.log").unlink()
        else:
            containers = []
        for container in containers:
            require(container["Config"]["Labels"].get(LABEL) == self.state["validationId"],
                    "Foreign container in project; refusing to act")
        for kind, command in (("volumes", "volume"), ("networks", "network")):
            for resource in self.state.get("resources", {}).get(kind, {}).values():
                names = self.command(["/usr/bin/docker", command, "ls", "--format", "{{.Name}}"],
                                     f"owner-{kind}")[1].split()
                if resource in names:
                    info = json.loads(self.command(["/usr/bin/docker", command, "inspect", resource],
                                                   f"inspect-{kind}")[1])[0]
                    require((info.get("Labels") or {}).get(LABEL) == self.state["validationId"],
                            f"Foreign {kind}; refusing to act")
        return containers

    def deploy(self):
        require(self.state["steps"].get("prepare", {}).get("status") == "complete", "Preparation incomplete")
        self.verify_files()
        self.owned_resources()
        self.compose(["pull"], "pull", 1200)
        # Ports are leased between our runs and rechecked immediately before Docker binds them.
        probes = []
        try:
            for port in self.state["ports"].values():
                sock = socket.socket()
                probes.append(sock)
                sock.bind(("127.0.0.1", port))
        except OSError as error:
            raise Blocked("Recorded port occupied; no reassignment/redeployment within this run") from error
        finally:
            for sock in probes:
                sock.close()
        self.compose(["up", "-d", "--no-build", "--pull", "never", "--scale", "orcaslicer-worker=1"],
                     "up", 300)
        self.health(wait=True)
        self.fixtures()

    def http(self, url, data=None, token=None, expected=200):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(url, json.dumps(data).encode() if data is not None else None,
                                         headers)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                require(response.status == expected, f"Unexpected HTTP status at {url}")
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            raise Blocked(f"HTTP {error.code} at {url}") from error
        except urllib.error.URLError as error:
            raise Blocked(f"Request failed at {url}: {error.reason}") from error

    def health(self, wait=False):
        self.verify_files()
        for attempt in range(60 if wait else 1):
            containers = self.owned_resources()
            services = [c["Config"]["Labels"].get("com.docker.compose.service") for c in containers]
            healthy = (sorted(services) == sorted(TOPOLOGY) and all(
                c["State"]["Status"] == "running" and c["State"].get("Health", {}).get("Status") == "healthy"
                for c in containers))
            if healthy:
                break
            if wait and attempt < 59:
                time.sleep(5)
        for container in containers:
            service = container["Config"]["Labels"].get("com.docker.compose.service", "")
            image_key = "moonraker-emulator" if service.startswith("moonraker-") else service
            if image_key in self.state["manifest"]["images"]:
                require(container["Config"]["Image"] == self.state["manifest"]["images"][image_key]["reference"],
                        f"Running image differs from selected manifest: {service}")
        self.state["health"] = {"checkedAt": now(), "healthy": healthy,
                                "containers": {c["Config"]["Labels"].get("com.docker.compose.service"): {
                                    "id": c["Id"], "image": c["Image"], "startedAt": c["State"]["StartedAt"],
                                    "status": c["State"]["Status"],
                                    "health": c["State"].get("Health", {}).get("Status")}
                                    for c in containers}}
        self.save()
        if not healthy:
            self.compose(["ps", "--all", "--format", "json"], "health-ps")
            self.compose(["logs", "--no-color", "--tail", "60"], "health-logs", check=False)
            raise Blocked("Required topology is absent/unhealthy; Playwright did not run")
        recorded = self.state.get("deployedContainers")
        identities = {c["Id"]: c["State"]["StartedAt"] for c in containers}
        require(not recorded or recorded == identities, "Resources restarted/replaced; prior phases invalid")
        self.state["deployedContainers"] = identities
        args = ["node", str(Path(self.state["source"]) / "scripts/ci/verify-acceptance-provenance.mjs"),
                "--expected-sha", self.state["commit"], "--base-url", self.env["BASE_URL"],
                "--evidence-dir", str(self.directory / "provenance")]
        for service, image in self.state["manifest"]["images"].items():
            args += ["--image-digest", f'{service}={image["reference"]}']
        self.command(args, "provenance", 120)
        self.save()

    def fixtures(self):
        api = self.env["API_BASE_URL"]
        if self.http(f"{api}/api/setup/status")["needsSetup"]:
            self.http(f"{api}/api/setup/initial-admin", {
                "username": self.env["E2E_ADMIN_USERNAME"], "password": self.env["E2E_ADMIN_PASSWORD"],
                "email": self.env["E2E_ADMIN_EMAIL"], "firstName": "Daily", "lastName": "Validation"})
        login = self.http(f"{api}/api/auth/login", {
            "usernameOrEmail": self.env["E2E_ADMIN_USERNAME"], "password": self.env["E2E_ADMIN_PASSWORD"]})
        require(login.get("token"), "Fixture login did not return an access token")
        token = login["token"]
        self.http(f"{api}/api/test/moonraker-emulator/reset", {}, token, 204)
        for name in INSTANCES:
            self.http(self.env[f"MOONRAKER_EMULATOR_URL_{name.upper()}"] + "/healthz")
        printers = self.http(f"{api}/api/printers", token=token)
        require(sum(p["backend"] == "Moonraker" for p in printers) >= 5
                and not any(p["backend"] == "TestEmulator" for p in printers),
                "Required real Moonraker fixtures absent")
        require(any(p["name"] == "Moonraker Offline" and p["isOnline"] is False for p in printers),
                "Offline Moonraker fixture is not ready")
        discovery = json.loads(self.compose(["exec", "-T", "printer-discovery", "curl",
                                            "--fail", "--silent", "--show-error", "-X", "POST",
                                            "http://localhost:5247/api/discovery/scan?autoRegister=false"],
                                           "discovery")[1])
        require(all(any(p["hostname"] == name and p["printerBackend"] == "moonraker" for p in discovery)
                    for name in ("Discovered Voron V2.4", "Discovered Prusa MK4S")),
                "Deterministic discovery fixtures missing")
        self.compose(["exec", "-T", "orcaslicer-worker", "sh", "-c",
                      "d=$(mktemp -d /app/temp/daily-probe.XXXXXX) && "
                      'printf ok > "$d/probe" && rm "$d/probe" && rmdir "$d"'], "worker-writable")
        self.state["fixtures"] = {"ready": True, "checkedAt": now()}
        self.save()

    def phase(self, name):
        require(self.state["steps"].get("deploy", {}).get("status") == "complete", "Deployment incomplete")
        require(name not in self.state["phases"], "Phase already invoked; never reuse/relabel old results")
        if name == "phase-b":
            self.phase_summary("phase-a")
        self.health()
        self.fixtures()
        directory = Path(self.state["workspace"]) / name
        directory.mkdir()
        invocation = {"id": uuid.uuid4().hex, "phase": name, "status": "running", "startedAt": now()}
        self.state["phases"][name] = invocation
        self.save()
        env = dict(self.env, PF_DAILY_ID=self.state["validationId"], PF_DAILY_INVOCATION=invocation["id"],
                   PF_DAILY_PHASE=name, PF_DAILY_COMMIT=self.state["commit"],
                   PF_DAILY_MANIFEST_HASH=self.state["manifestHash"], PF_DAILY_HARNESS_HASH=self.state["harnessHash"],
                   PF_DAILY_RESULT=str(directory / "result.json"), PF_DAILY_OUTPUT=str(directory / "test-results"),
                   PLAYWRIGHT_HTML_OUTPUT_DIR=str(directory / "html"), PLAYWRIGHT_HTML_OPEN="never")
        args = PHASES[name] + ["--config", self.state["playwrightConfig"]]
        invocation["command"] = args
        code, _ = execute(args, self.state["frontend"], env, 3600, self.directory / f"{name}.log", False)
        invocation.update(status="finished", exitCode=code, finishedAt=now())
        self.save()
        report = read_json(directory / "result.json")
        counts = validate_phase(report, self.state, invocation, code)
        target = self.directory / f"{name}.json"
        target.write_text(redact(json.dumps(report, indent=2), env))
        invocation.update(counts=counts, evidence=str(target), evidenceHash=digest(target))
        self.save()
        # Evidence is only accepted while the same healthy deployment still exists.
        self.health()
        invocation["postHealthVerified"] = True
        self.save()
        return counts

    def phase_summary(self, name):
        invocation = self.state["phases"].get(name)
        require(invocation and invocation.get("postHealthVerified"), f"No verified execution for {name}")
        require(digest(invocation["evidence"]) == invocation["evidenceHash"], "Phase evidence changed")
        return validate_phase(read_json(invocation["evidence"]), self.state, invocation, invocation["exitCode"])

    def cleanup(self):
        previous = self.state["cleanup"]
        if previous.get("status") == "complete":
            return
        require(previous.get("attempts", 0) < 2, "Cleanup exhausted two attempts; owner intervention required")
        self.state["cleanup"] = {"status": "running", "startedAt": now(),
                                 "attempts": previous.get("attempts", 0) + 1}
        self.save()
        try:
            containers = self.owned_resources()
            if self.state.get("resources"):
                for path, expected in self.state["fileHashes"].items():
                    require(digest(path) == expected, "Cleanup config changed; refusing unsafe teardown")
                self.compose(["down", "--volumes", "--remove-orphans", "--timeout", "20"], "down", 180)
                require(not self.owned_resources(), "Containers remain after teardown")
                for kind, command in (("volumes", "volume"), ("networks", "network")):
                    remaining = self.command(["/usr/bin/docker", command, "ls", "--format", "{{.Name}}"],
                                             f"remaining-{kind}")[1].split()
                    require(not set(remaining) & set(self.state["resources"][kind].values()),
                            f"{kind} remain after teardown")
            else:
                require(not containers, "Resources exist without complete cleanup configuration")
            runtime = Path(self.state["workspace"])
            # Remove only this exact registered runtime; never global leftovers.
            require(runtime == self.directory / "runtime" and runtime.resolve() == runtime,
                    "Unsafe cleanup path")
            if runtime.exists():
                self.preserve_artifacts()
                stack = runtime / "stack"
                root_owned = any(Path(parent, name).lstat().st_uid != os.getuid()
                                 for parent, directories, files in os.walk(stack)
                                 for name in [*directories, *files])
                if root_owned:
                    require(stack.is_dir() and stack.resolve() == stack,
                            "Root-owned cleanup is restricted to this run's exact stack directory")
                    helper = self.state["project"] + "-cleanup"
                    existing = self.command(["/usr/bin/docker", "ps", "-aq", "--filter", f"name=^/{helper}$"],
                                            "cleanup-helper-check")[1].strip()
                    require(not existing, "Cleanup helper name occupied; refuse to replace it")
                    self.state["cleanup"]["helper"] = helper
                    self.save()
                    self.command(["/usr/bin/docker", "run", "--rm", "--pull", "never", "--name", helper,
                                  "--label", f"{LABEL}={self.state['validationId']}",
                                  "--label", f"com.docker.compose.project={self.state['project']}",
                                  "--network", "none", "--user", "0:0", "--mount",
                                  f"type=bind,source={stack},target=/owned", "--entrypoint", "/bin/chown",
                                  self.state["manifest"]["images"]["api"]["reference"],
                                  "-R", "--no-dereference", f"{os.getuid()}:{os.getgid()}", "/owned"],
                                 "cleanup-permissions", 120)
                shutil.rmtree(runtime)
            self.state["cleanup"].update(status="complete", finishedAt=now())
        except (Blocked, OSError, ValueError) as error:
            self.state["cleanup"].update(status="failed", finishedAt=now(), error=redact(str(error), self.env))
            raise
        finally:
            self.save()

    def preserve_artifacts(self):
        # Browser traces can contain credentials. Keep them private, not auto-uploadable.
        evidence = self.directory / "private-browser-evidence"
        evidence.mkdir(exist_ok=True, mode=0o700)
        for name in PHASES:
            phase = Path(self.state["workspace"]) / name
            if phase.exists():
                shutil.copytree(phase, evidence / name, dirs_exist_ok=True)
        self.state["privateEvidence"] = str(evidence)
        self.save()

    def summary(self):
        result = {key: self.state.get(key) for key in (
            "validationId", "startedAt", "imageRun", "commit", "manifestHash", "harnessRevision",
            "harnessHash", "harnessDirty", "workspace", "composeFiles", "project", "ports",
            "environment", "health", "fixtures", "steps", "cleanup", "privateEvidence", "blocker", "selection")}
        result["evidenceDirectory"] = str(self.directory)
        result["phases"] = {}
        for name in PHASES:
            if name not in self.state["phases"]:
                result["phases"][name] = {"status": "not-invoked", "counts": None}
            else:
                try:
                    result["phases"][name] = {"status": "verified", "counts": self.phase_summary(name)}
                except (Blocked, OSError, KeyError, ValueError) as error:
                    result["phases"][name] = {"status": "invalid", "error": str(error), "counts": None}
        result["requiresAgentClassification"] = True
        write_json(self.directory / "summary.json", result)
        return result


def create_run():
    os.umask(0o077)
    private_directory(ROOT)
    private_directory(ROOT / "runs")
    run_id = "dv-" + datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dt%H%M%S-") + uuid.uuid4().hex
    directory = ROOT / "runs" / run_id
    directory.mkdir(mode=0o700)
    state = {"schemaVersion": 1, "validationId": run_id, "startedAt": now(), "project": run_id,
             "workspace": str(directory / "runtime"), "steps": {}, "phases": {},
             "harnessRevision": os.environ["PF_DAILY_HARNESS_REVISION"],
             "harnessHash": os.environ["PF_DAILY_HARNESS_HASH"],
             "harnessDirty": os.environ.get("PF_DAILY_HARNESS_DIRTY") == "true",
             "cleanup": {"status": "registered", "attempts": 0}}
    write_json(directory / "state.json", state)
    (directory / "runtime").mkdir()
    print(json.dumps({"validationId": run_id, "state": str(directory / "state.json")}), flush=True)
    return Run(run_id)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("probe", "init", "run", "deploy", *PHASES, "status", "read", "cleanup"))
    parser.add_argument("--run-id")
    parser.add_argument("--evidence")
    args = parser.parse_args()
    os.umask(0o077)
    private_directory(ROOT)
    if args.command == "probe":
        print(json.dumps(probe(), indent=2))
        return 0
    require(args.run_id or args.command in ("init", "run"), "This command requires --run-id")
    require(not args.run_id or args.command != "init", "init always creates a new identity")
    run = Run(args.run_id) if args.run_id else create_run()
    code = 0
    with lock(run.directory / "command.lock"):
        try:
            if args.command in ("init", "run") and not run.state["steps"].get("prepare"):
                run.step("prepare", run.prepare)
            if args.command in ("deploy", "run"):
                if run.state["steps"].get("deploy", {}).get("status") != "complete":
                    run.step("deploy", run.deploy)
                else:
                    run.health()
            if args.command in PHASES:
                run.phase(args.command)
            if args.command == "run":
                for name in PHASES:
                    if name in run.state["phases"]:
                        run.phase_summary(name)
                    else:
                        run.phase(name)
            if args.command == "cleanup":
                run.cleanup()
            if args.command == "status" and run.state["cleanup"]["status"] != "complete":
                run.health()
            if args.command == "read":
                require(args.evidence and re.fullmatch(r"[a-zA-Z0-9_.-]+\.(json|log|md)", args.evidence),
                        "Read accepts a single run-evidence filename, never arbitrary Linux paths")
                path = run.directory / args.evidence
                require(not path.is_symlink(), "Evidence symlink rejected")
                print(redact(path.read_text(), run.env))
        except (Blocked, OSError, ValueError, KeyError, KeyboardInterrupt) as error:
            code = 1
            run.state["blocker"] = {"command": args.command, "at": now(), "error": redact(str(error), run.env)}
            run.save()
            print(json.dumps({"blocked": run.state["blocker"]}), file=sys.stderr)
        finally:
            if args.command == "run" or (code and args.command not in ("read", "status", "cleanup")):
                try:
                    run.cleanup()
                except (Blocked, OSError, ValueError) as error:
                    code = 1
                    print(json.dumps({"cleanupFailed": redact(str(error), run.env)}), file=sys.stderr)
            if args.command != "read":
                print(json.dumps(run.summary(), indent=2))
        if args.command in ("run", *PHASES):
            code = code or next((p["exitCode"] for p in run.state["phases"].values()
                                if p.get("exitCode")), 0)
    return code


if __name__ == "__main__":
    def interrupt(_signal, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupt)
    try:
        sys.exit(main())
    except (Blocked, OSError, ValueError, KeyError) as error:
        print(json.dumps({"blocked": str(error)}), file=sys.stderr)
        sys.exit(1)
