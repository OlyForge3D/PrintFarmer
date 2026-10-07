#!/usr/bin/env python3
"""Build-free PrintFarmer deployment controller (issue #3295).

Converts an existing git-checkout deployment created by scripts/deploy-docker.sh
into a deployment that runs and updates exclusively from published GHCR release
images, then performs release updates without a source checkout or image build.

Design rules (see docs/DEPLOYMENT_REGISTRY_MIGRATION.md):
  * The existing Compose project stays the authority: the release compose file is
    derived from the project's own ordered -f set, project name, and profiles.
  * Python 3 stdlib + docker CLI only. No secrets are read or printed: .env is only
    handed to `docker compose --env-file`, resolved config stays in memory, and
    differences are reported as field paths, never values.
  * Every image is validated and pulled before anything is started or stopped.
    Storage identity (project, named volumes, bind sources) must match the running
    containers. `down`, volume removal, and credential rotation are never used.
  * A failed or interrupted transaction never overwrites the last-good release.
"""

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

CONTROLLER_VERSION = "1"
GITHUB_REPOSITORY = "OlyForge3D/PrintFarmer"
IMAGE_PREFIX = "ghcr.io/olyforge3d/printfarmer-"
STATE_DIR = ".printfarmer"
RELEASE_COMPOSE = "docker-compose.release.yml"
ENV_FILE = ".env"

# Mirrors scripts/ci/release-policy.mjs `components`.
COMPONENTS = {
    "api": {"target": "api-runtime", "platforms": ["linux/amd64", "linux/arm64"]},
    "frontend": {"target": "frontend-runtime", "platforms": ["linux/amd64", "linux/arm64"]},
    "slicer-host": {"target": "slicer-host-runtime", "platforms": ["linux/amd64", "linux/arm64"]},
    "printer-discovery": {"target": "printer-discovery-runtime", "platforms": ["linux/amd64", "linux/arm64"]},
    "orcaslicer-worker": {"target": "orcaslicer-worker", "platforms": ["linux/amd64"]},
    "monolith": {"target": "monolith-runtime", "platforms": ["linux/amd64", "linux/arm64"]},
}
TARGET_TO_COMPONENT = {spec["target"]: name for name, spec in COMPONENTS.items()}
UNPUBLISHED_TARGETS = {
    "moonraker-emulator-runtime": "the Moonraker emulator is a development/validation service with no published "
                                  "release image; remove it from the deployment before migrating",
}
WORKER_PROFILE_MOUNT = "/app/custom-profiles"
WORKER_VOLUME_PREFIX = "printfarmer-custom-profiles-"
PREVIOUS_WORKER_SERVICE = "orcaslicer-worker-previous"
PENDING_ACTIONS = {"update", "rollback"}
PENDING_PHASES = {"pulling", "starting", "health", "failed-starting", "failed-health"}
PENDING_HELP = (". Nothing was changed. Check `docker compose ps` to see which images are running, keep a copy of "
                "%s for diagnosis, move it aside, then rerun the intended update or rollback with "
                "--resume-interrupted.")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(-insider\.\d+)?$")
COMMIT_RE = re.compile(r"^[0-9a-f]{40}$")
DIGEST_RE = re.compile(r"^sha256:[0-9a-f]{64}$")
IGNORED_SERVICE_KEYS = {"build", "image", "pull_policy"}


class MigrationError(Exception):
    """Fail-closed condition. Messages never contain secret values."""


def image_var(component):
    return "PRINTFARMER_%s_IMAGE" % component.upper().replace("-", "_")


def channel_of(version):
    return "insider" if "-insider." in version else "stable"


def utc_now():
    return datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat()


def write_json_atomic(path, data):
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    tmp = "%s.tmp-%d" % (path, os.getpid())
    with open(tmp, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=2, sort_keys=True)
        handle.write("\n")
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(tmp, path)


def read_json(path, default=None):
    if not os.path.exists(path):
        return default
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def sha256_file(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


class Runner:
    """Executes external commands. Tests substitute a fake."""

    def run(self, args, env=None, check=True):
        result = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                env=env, check=False)
        if check and result.returncode != 0:
            raise MigrationError("command failed (%s): %s" % (" ".join(args[:4]), result.stderr.strip()[-400:]))
        return result.returncode, result.stdout


def http_get_json(url):
    request = urllib.request.Request(url, headers={"Accept": "application/vnd.github+json",
                                                   "User-Agent": "printfarmer-registry/" + CONTROLLER_VERSION})
    token = os.environ.get("GITHUB_TOKEN")
    if token and url.startswith("https://api.github.com/"):
        request.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, ValueError) as error:
        raise MigrationError("could not fetch %s: %s" % (url, error))


def json_paths_differ(old, new, path="", out=None):
    """Return the paths at which two JSON values differ. Values are never returned."""
    out = [] if out is None else out
    if isinstance(old, dict) and isinstance(new, dict):
        for key in sorted(set(old) | set(new)):
            child = "%s.%s" % (path, key) if path else str(key)
            if key not in old or key not in new:
                out.append(child)
            else:
                json_paths_differ(old[key], new[key], child, out)
    elif isinstance(old, list) and isinstance(new, list) and len(old) == len(new):
        for index, (left, right) in enumerate(zip(old, new)):
            json_paths_differ(left, right, "%s[%d]" % (path, index), out)
    elif old != new:
        out.append(path or "<root>")
    return out


class Controller:
    def __init__(self, project_dir, runner=None, fetch=None, out=None, sleep=time.sleep, clock=time.monotonic):
        self.project_dir = os.path.abspath(project_dir)
        self.runner = runner or Runner()
        self.fetch = fetch or http_get_json
        self.out = out or sys.stdout
        self.sleep = sleep
        self.clock = clock
        self.state_dir = os.path.join(self.project_dir, STATE_DIR)

    # ----------------------------------------------------------------- helpers
    def say(self, message=""):
        self.out.write(message + "\n")

    def path(self, *parts):
        return os.path.normpath(os.path.join(self.project_dir, *parts))

    def docker(self, *args, env=None, check=True):
        return self.runner.run(["docker"] + list(args), env=env, check=check)

    def compose_base(self, project, files, profiles):
        args = ["compose", "-p", project, "--project-directory", self.project_dir]
        if os.path.exists(self.path(ENV_FILE)):
            args += ["--env-file", self.path(ENV_FILE)]
        for name in files:
            args += ["-f", name if os.path.isabs(name) else self.path(name)]
        for profile in profiles:
            args += ["--profile", profile]
        return args

    def compose(self, project, files, profiles, *args, env=None, check=True):
        return self.docker(*(self.compose_base(project, files, profiles) + list(args)), env=env, check=check)

    def compose_config(self, project, files, profiles, env=None, raw=False, unresolved_paths=False):
        extra = ["config", "--format", "json"]
        if raw:
            extra += ["--no-interpolate", "--no-path-resolution"]
        elif unresolved_paths:
            extra += ["--no-path-resolution"]
        _, text = self.compose(project, files, profiles, *extra, env=env)
        return json.loads(text)

    def project_containers(self, project):
        _, text = self.docker("ps", "-a", "--filter", "label=com.docker.compose.project=%s" % project,
                              "--format", "{{.ID}}")
        ids = [line.strip() for line in text.splitlines() if line.strip()]
        if not ids:
            return []
        _, text = self.docker("inspect", *ids)
        return [c for c in json.loads(text)
                if c.get("Config", {}).get("Labels", {}).get("com.docker.compose.oneoff", "False") != "True"]

    def host_platform(self):
        _, text = self.docker("info", "--format", "{{.OSType}}/{{.Architecture}}")
        os_type, _, arch = text.strip().partition("/")
        arch = {"x86_64": "amd64", "aarch64": "arm64"}.get(arch, arch)
        return "%s/%s" % (os_type or "linux", arch)

    def deployment(self):
        data = read_json(os.path.join(self.state_dir, "deployment.json"))
        if not data:
            raise MigrationError("no %s/deployment.json; run `migrate` from the existing checkout first" % STATE_DIR)
        return data

    # ------------------------------------------------------------ discovery
    def discover_project(self, project=None):
        _, text = self.docker("ps", "-a", "--filter",
                              "label=com.docker.compose.project.working_dir=%s" % self.project_dir,
                              "--format", "{{.Label \"com.docker.compose.project\"}}")
        found = sorted({line.strip() for line in text.splitlines() if line.strip()})
        if project:
            if project not in found:
                raise MigrationError("project %r has no containers created from %s (found: %s)"
                                     % (project, self.project_dir, ", ".join(found) or "none"))
            return project
        if len(found) != 1:
            raise MigrationError("expected exactly one Compose project created from %s, found %d; pass --project"
                                 % (self.project_dir, len(found)))
        return found[0]

    def original_compose_files(self, containers):
        sets = {c["Config"]["Labels"].get("com.docker.compose.project.config_files", "") for c in containers}
        dirs = {c["Config"]["Labels"].get("com.docker.compose.project.working_dir", "") for c in containers}
        if len(sets) != 1 or not next(iter(sets)):
            raise MigrationError("running containers were created from different compose file sets; redeploy once "
                                 "with scripts/deploy-docker.sh so the project is consistent, then migrate")
        if dirs != {self.project_dir}:
            raise MigrationError("containers were created from a different project directory; run migrate in place "
                                 "from the directory that owns the deployment")
        files = next(iter(sets)).split(",")
        for name in files:
            if not os.path.exists(name if os.path.isabs(name) else self.path(name)):
                raise MigrationError("compose file recorded by the running project is missing: %s" % name)
        return files

    # ------------------------------------------------------------ rendering
    def classify_service(self, name, service):
        build = service.get("build")
        target = build.get("target") if isinstance(build, dict) else None
        if target in UNPUBLISHED_TARGETS:
            raise MigrationError("service %s: %s" % (name, UNPUBLISHED_TARGETS[target]))
        version_arg = str(((build.get("args") or {}) if isinstance(build, dict) else {}).get("ORCASLICER_VERSION", ""))
        if target == "orcaslicer-worker" and (name == PREVIOUS_WORKER_SERVICE
                                              or version_arg.startswith("${ORCASLICER_VERSION_PREVIOUS")):
            raise MigrationError(
                "service %s is the optional previous-version OrcaSlicer worker; releases publish only the current "
                "worker, so it cannot run build-free. Set ENABLE_ORCA_WORKER_PREVIOUS=no in .deploy-config, run "
                "./scripts/deploy-docker.sh to remove it, then rerun migrate (its printfarmer-custom-profiles-"
                "<previous> volume is kept)" % name)
        if target:
            if target not in TARGET_TO_COMPONENT:
                raise MigrationError("service %s builds unknown target %r with no published release image"
                                     % (name, target))
            return TARGET_TO_COMPONENT[target]
        if build:
            raise MigrationError("service %s has a build stanza without a published target" % name)
        image = str(service.get("image", ""))
        for component in COMPONENTS:
            if re.search(r"printfarmer-%s(?![a-z-])" % re.escape(component), image):
                return component
        return None

    def selected_template(self, project, files):
        _, text = self.compose(project, files, [], "config", "--profiles")
        all_profiles = sorted({line.strip() for line in text.splitlines() if line.strip()})
        template = self.compose_config(project, files, all_profiles, raw=True)
        self.fix_mount_types(template, self.compose_config(project, files, all_profiles, unresolved_paths=True))
        return template, all_profiles

    @staticmethod
    def fix_mount_types(template, interpolated):
        """Without interpolation Compose parses `${VAR:-./path}:/target` as a named volume.

        Take each mount's type from an interpolated render (kept in memory only) while keeping the
        uninterpolated source, so the release file still follows .env.
        """
        for name, service in template.get("services", {}).items():
            raw = service.get("volumes") or []
            resolved = (interpolated.get("services", {}).get(name) or {}).get("volumes") or []
            if len(raw) != len(resolved):
                raise MigrationError("service %s: could not align volume definitions" % name)
            for left, right in zip(raw, resolved):
                if not (isinstance(left, dict) and isinstance(right, dict)):
                    continue
                if "$" not in str(left.get("target", "")) and left.get("target") != right.get("target"):
                    raise MigrationError("service %s: could not align volume definitions" % name)
                if left.get("type") != right.get("type"):
                    for key in ("volume", "bind", "tmpfs"):
                        left.pop(key, None)
                    left["type"] = right.get("type")
                    if right.get("type") in right:
                        left[right["type"]] = right[right["type"]]

    def render_release(self, template, active_profiles, tracked_files):
        template = json.loads(json.dumps(template))
        services, components, vendored = {}, {}, {}
        for name, service in sorted(template.get("services", {}).items()):
            profiles = service.get("profiles") or []
            if profiles and not set(profiles) & set(active_profiles):
                continue
            component = self.classify_service(name, service)
            if component:
                components[name] = component
                service.pop("build", None)
                service["image"] = "${%s:?Run .printfarmer/bin/printfarmer-registry update}" % image_var(component)
                service["pull_policy"] = "never"
            for volume in service.get("volumes", []) or []:
                if isinstance(volume, dict) and volume.get("type") == "bind":
                    volume["source"] = self.vendor_path(volume.get("source", ""), tracked_files, vendored)
            for entry in service.get("env_file", []) if isinstance(service.get("env_file"), list) else []:
                if isinstance(entry, dict) and "path" in entry:
                    entry["path"] = self.vendor_path(entry["path"], tracked_files, vendored)
            services[name] = service
        for section in ("configs", "secrets"):
            for item in (template.get(section) or {}).values():
                if isinstance(item, dict) and "file" in item:
                    item["file"] = self.vendor_path(item["file"], tracked_files, vendored)
        release = {key: value for key, value in template.items() if not key.startswith("x-")}
        release["services"] = services
        release["x-printfarmer"] = {"generatedBy": "printfarmer-registry migrate", "schema": 1,
                                    "note": "Do not run docker compose up directly; use "
                                            ".printfarmer/bin/printfarmer-registry update."}
        return release, components, vendored

    def vendor_path(self, source, tracked_files, vendored):
        if not source or "$" in source or os.path.isabs(source):
            return source
        rel = os.path.normpath(source).replace("\\", "/")
        if rel.startswith("../") or rel == STATE_DIR or rel.startswith(STATE_DIR + "/"):
            return source
        if rel in tracked_files or any(name.startswith(rel + "/") for name in tracked_files):
            vendored[rel] = "./%s/files/%s" % (STATE_DIR, rel)
            return vendored[rel]
        return source

    def tracked_files(self):
        try:
            code, text = self.runner.run(["git", "-C", self.project_dir, "ls-files", "-z"], check=False)
        except OSError as error:
            code, text = None, str(error)
        if code != 0:
            raise MigrationError("`git ls-files` failed in %s (exit %s). migrate must run inside the git checkout "
                                 "that deployed PrintFarmer, with git installed, so tracked bind-mounted files can be "
                                 "vendored before the checkout is retired. Nothing was changed." % (self.project_dir, code))
        return {name for name in text.split("\0") if name}

    def host_paths(self, resolved):
        """Non-vendored host paths the deployment depends on (paths only, never values)."""
        state_root = self.path(STATE_DIR)
        paths = {self.path(ENV_FILE), self.path(RELEASE_COMPOSE), state_root}
        if os.path.exists(self.path(".deploy-config")):
            paths.add(self.path(".deploy-config"))
        for service in (resolved.get("services") or {}).values():
            for volume in service.get("volumes", []) or []:
                if isinstance(volume, dict) and volume.get("type") == "bind" and volume.get("source"):
                    source = os.path.normpath(volume["source"])
                    if source != state_root and not source.startswith(state_root + os.sep):
                        paths.add(source)
        return sorted(paths)

    def display_path(self, path):
        rel = os.path.relpath(path, self.project_dir)
        return path if rel.startswith("..") else "./" + rel.replace("\\", "/")

    # ------------------------------------------------------- invariant checks
    @staticmethod
    def normalized(config, vendored_abs):
        config = json.loads(json.dumps(config))
        for key in [k for k in config if k.startswith("x-")]:
            config.pop(key)
        for service in config.get("services", {}).values():
            for key in IGNORED_SERVICE_KEYS:
                service.pop(key, None)
            for volume in service.get("volumes", []) or []:
                if isinstance(volume, dict) and volume.get("type") == "bind":
                    volume["source"] = vendored_abs.get(volume.get("source"), volume.get("source"))
        return config

    @staticmethod
    def expected_mounts(resolved, service):
        mounts = {}
        for volume in resolved["services"][service].get("volumes", []) or []:
            if not isinstance(volume, dict):
                continue
            kind, target, source = volume.get("type"), volume.get("target"), volume.get("source")
            if kind == "volume" and source:
                declared = (resolved.get("volumes") or {}).get(source) or {}
                mounts[target] = ("volume", declared.get("name") or source)
            elif kind == "bind":
                mounts[target] = ("bind", source)
        return mounts

    def check_storage(self, project, resolved, containers, vendored_abs, allow_new_services=False):
        """Fail before mutation if storage identity would change or is missing."""
        problems = []
        if resolved.get("name") != project:
            problems.append("project name would change")
        by_service = {}
        for container in containers:
            by_service.setdefault(container["Config"]["Labels"].get("com.docker.compose.service"),
                                  []).append(container)
        for service in sorted(resolved.get("services", {})):
            expected = self.expected_mounts(resolved, service)
            if service not in by_service and not allow_new_services:
                problems.append("service %s has no existing container (Compose would create it fresh)" % service)
            for kind, ident in expected.values():
                if kind == "volume":
                    code, _ = self.docker("volume", "inspect", ident, check=False)
                    if code != 0:
                        problems.append("service %s: named volume %s is missing" % (service, ident))
                elif not (os.path.exists(ident) or os.path.exists(vendored_abs.get(ident, ""))):
                    problems.append("service %s: bind source %s is missing" % (service, ident))
            for container in by_service.get(service, []):
                actual = {m.get("Destination"): m for m in container.get("Mounts", [])}
                for target, (kind, ident) in sorted(expected.items()):
                    mount = actual.get(target)
                    if mount is None:
                        problems.append("service %s: running container has no mount at %s" % (service, target))
                    elif kind == "volume":
                        if mount.get("Type") != "volume" or mount.get("Name") != ident:
                            problems.append("service %s: %s would switch from volume %s to %s"
                                            % (service, target, mount.get("Name") or mount.get("Source"), ident))
                    elif mount.get("Type") != "bind" or mount.get("Source") not in {ident, vendored_abs.get(ident)}:
                        problems.append("service %s: %s bind source would change" % (service, target))
        if problems:
            raise MigrationError("storage identity check failed (nothing was changed):\n  - "
                                 + "\n  - ".join(problems))

    def worker_orca_version(self, resolved, components):
        versions = set()
        for service, component in components.items():
            if component != "orcaslicer-worker" or service not in resolved.get("services", {}):
                continue
            mount = self.expected_mounts(resolved, service).get(WORKER_PROFILE_MOUNT)
            if not mount or mount[0] != "volume" or not mount[1].startswith(WORKER_VOLUME_PREFIX):
                raise MigrationError("service %s: cannot determine the OrcaSlicer version that owns its "
                                     "custom-profiles volume" % service)
            versions.add(mount[1][len(WORKER_VOLUME_PREFIX):])
        if len(versions) > 1:
            raise MigrationError("OrcaSlicer workers use different custom-profiles volume versions")
        return next(iter(versions), None)

    # -------------------------------------------------------------- migrate
    def migrate(self, project=None, deployed_commit=None, dry_run=False, allow_new_services=False):
        if os.path.exists(os.path.join(self.state_dir, "deployment.json")):
            raise MigrationError("this deployment is already migrated; use `status`, `plan`, or `update`")
        project = self.discover_project(project)
        containers = self.project_containers(project)
        if not containers:
            raise MigrationError("project %s has no containers; deploy it with scripts/deploy-docker.sh first"
                                 % project)
        files = self.original_compose_files(containers)
        template, all_profiles = self.selected_template(project, files)
        running = {c["Config"]["Labels"].get("com.docker.compose.service") for c in containers}
        active = sorted({p for name, svc in template.get("services", {}).items() if name in running
                         for p in (svc.get("profiles") or [])} & set(all_profiles))
        release, components, vendored = self.render_release(template, active, self.tracked_files())
        if not components:
            raise MigrationError("no PrintFarmer services were found in project %s" % project)
        commit = self.resolve_deployed_commit(deployed_commit, containers)

        old = self.compose_config(project, files, active)
        placeholder = {image_var(c): "%s%s@sha256:%s" % (IMAGE_PREFIX, c, "0" * 64) for c in set(components.values())}
        vendored_abs = {self.path(v[2:]): self.path(k) for k, v in vendored.items()}
        scratch = tempfile.mkdtemp(prefix="printfarmer-registry-")
        try:
            candidate = os.path.join(scratch, RELEASE_COMPOSE)
            write_json_atomic(candidate, release)
            new = self.compose_config(project, [candidate], active, env=dict(os.environ, **placeholder))
        finally:
            shutil.rmtree(scratch, ignore_errors=True)
        diff = json_paths_differ(self.normalized(old, {}), self.normalized(new, vendored_abs))
        if diff:
            raise MigrationError("release compose would change deployment configuration (paths only):\n  - "
                                 + "\n  - ".join(diff[:50]))
        self.check_storage(project, new, containers, vendored_abs, allow_new_services)
        orca_version = self.worker_orca_version(new, components)

        self.say("Migration plan for project %s (%s)" % (project, self.project_dir))
        self.say("  original compose files: %s" % ", ".join(os.path.relpath(f, self.project_dir) for f in files))
        self.say("  active profiles: %s" % (", ".join(active) or "(none)"))
        self.say("  deployed source commit: %s" % commit)
        for service, component in sorted(components.items()):
            self.say("  service %-28s -> %s%s (digest pinned per release)" % (service, IMAGE_PREFIX, component))
        for rel in sorted(vendored):
            self.say("  vendor tracked file %s -> %s" % (rel, vendored[rel]))
        if orca_version:
            self.say("  OrcaSlicer worker volume version: %s (releases must match)" % orca_version)
        self.say("  storage identity: unchanged (project, named volumes, bind sources verified)")
        self.say("  keep these host paths when retiring the checkout (derived from the resolved configuration):")
        for path in self.host_paths(new):
            self.say("    %s" % self.display_path(path))
        if dry_run:
            self.say("Dry run: nothing written, no containers touched.")
            return 0

        for rel, dest in vendored.items():
            src, dst = self.path(rel), self.path(dest[2:])
            if os.path.isdir(src):
                shutil.copytree(src, dst, dirs_exist_ok=True)
            else:
                os.makedirs(os.path.dirname(dst), exist_ok=True)
                shutil.copy2(src, dst)
        bin_dir = os.path.join(self.state_dir, "bin")
        os.makedirs(bin_dir, exist_ok=True)
        controller = os.path.join(bin_dir, "printfarmer-registry")
        if os.path.abspath(__file__) != os.path.abspath(controller):
            shutil.copy2(os.path.abspath(__file__), controller)
        os.chmod(controller, 0o755)
        write_json_atomic(self.path(RELEASE_COMPOSE), release)
        write_json_atomic(os.path.join(self.state_dir, "deployment.json"), {
            "schema": 1, "controllerVersion": CONTROLLER_VERSION, "project": project,
            "composeFiles": [RELEASE_COMPOSE], "profiles": active, "services": components,
            "vendored": vendored, "orcaslicerVersion": orca_version, "controllerSha256": sha256_file(controller),
            "migratedFrom": {"composeFiles": [os.path.relpath(f, self.project_dir) for f in files],
                             "deployedCommit": commit, "migratedAt": utc_now()},
        })
        self.say("Migrated. No containers were changed. Next: back up, then run")
        self.say("  .printfarmer/bin/printfarmer-registry plan --version <X.Y.Z>")
        return 0

    def resolve_deployed_commit(self, requested, containers):
        labels = {c["Config"]["Labels"].get("org.opencontainers.image.revision", "") for c in containers}
        labelled = {value for value in labels if COMMIT_RE.match(value)}
        if requested == "HEAD":
            _, dirty = self.runner.run(["git", "-C", self.project_dir, "status", "--porcelain",
                                        "--untracked-files=no"])
            if dirty.strip():
                raise MigrationError("checkout has tracked modifications; the running code may not match HEAD. "
                                     "Pass --deployed-commit <full sha> explicitly")
            _, head = self.runner.run(["git", "-C", self.project_dir, "rev-parse", "HEAD"])
            requested = head.strip()
        if requested:
            if not COMMIT_RE.match(requested):
                raise MigrationError("--deployed-commit must be a full 40-character lowercase commit SHA or HEAD")
            if labelled and labelled != {requested}:
                raise MigrationError("--deployed-commit disagrees with the running images' revision labels")
            return requested
        if len(labelled) == 1:
            return next(iter(labelled))
        raise MigrationError("cannot determine which commit is running (local builds carry no revision label). "
                             "Pass --deployed-commit <sha>, or --deployed-commit HEAD if it was deployed from HEAD")

    # ------------------------------------------------------------- releases
    def load_release(self, version, manifest_file=None):
        if not VERSION_RE.match(version or ""):
            raise MigrationError("version must be X.Y.Z or X.Y.Z-insider.N (no leading v)")
        if manifest_file:
            data, source = read_json(manifest_file), os.path.abspath(manifest_file)
        else:
            source = "https://github.com/%s/releases/download/v%s/container-images.json" % (GITHUB_REPOSITORY, version)
            data = self.fetch(source)
        if not isinstance(data, dict) or data.get("schema") != 1:
            raise MigrationError("container-images.json has an unsupported schema")
        if data.get("version") != version or data.get("tag") != "v" + version:
            raise MigrationError("container-images.json is for a different release")
        if data.get("channel") != channel_of(version):
            raise MigrationError("container-images.json channel does not match the version")
        if not COMMIT_RE.match(str(data.get("sourceCommit", ""))):
            raise MigrationError("container-images.json has an invalid sourceCommit")
        images = data.get("images")
        if not isinstance(images, dict) or set(images) != set(COMPONENTS):
            raise MigrationError("container-images.json must list exactly the published components")
        refs = {}
        for name, spec in COMPONENTS.items():
            entry = images[name] if isinstance(images[name], dict) else {}
            repo, _, digest = str(entry.get("reference", "")).partition("@")
            if repo != IMAGE_PREFIX + name or not DIGEST_RE.match(digest):
                raise MigrationError("container-images.json has an invalid reference for %s" % name)
            if sorted(entry.get("platforms") or []) != sorted(spec["platforms"]):
                raise MigrationError("container-images.json platforms differ from the release policy for %s" % name)
            refs[name] = entry["reference"]
        return {"kind": "release", "version": version, "tag": data["tag"], "channel": data["channel"],
                "sourceCommit": data["sourceCommit"], "source": source, "images": refs}

    def check_lineage(self, deployment, state, release, allow_unsafe, action="update"):
        current = (state or {}).get("current")
        base = current["sourceCommit"] if current else deployment["migratedFrom"]["deployedCommit"]
        head = release["sourceCommit"]
        if base == head:
            status = "identical"
        else:
            try:
                status = str(self.fetch("https://api.github.com/repos/%s/compare/%s...%s"
                                        % (GITHUB_REPOSITORY, base, head)).get("status", "unknown"))
            except MigrationError:
                status = "unknown"
        if status in ("ahead", "identical"):
            return status
        if action == "rollback":
            message = ("rollback moves the source from %s back to %s (%s; GitHub compare: %s). Database migrations "
                       "are forward-only and rollback restores images only, never data."
                       % (base[:12], head[:12], release["version"], status))
            remedy = (" Restore the database and data-volume backup taken before the current release was applied, "
                      "then rerun `rollback --backup-confirmed --allow-unsafe-downgrade` to acknowledge the risk.")
        else:
            message = ("target %s (%s) is not a verified descendant of the deployed commit %s (GitHub compare: %s). "
                       "Database migrations are forward-only; moving to a non-descendant can break or corrupt data."
                       % (release["version"], head[:12], base[:12], status))
            remedy = (" Lineage is verified online through the GitHub compare API (set GITHUB_TOKEN if rate-limited). "
                      "Restore a backup instead, or pass --allow-unsafe-downgrade after verifying schema "
                      "compatibility yourself.")
        if not allow_unsafe:
            raise MigrationError(message + remedy)
        self.say("WARNING: " + message + " Proceeding because --allow-unsafe-downgrade was given.")
        return status

    def resolved_config(self, deployment, refs):
        env = dict(os.environ)
        for component, reference in refs.items():
            env[image_var(component)] = reference
        if "orcaslicer-worker" in refs:
            # Process env overrides the .env value deploy-docker.sh resolved from the local build.
            env["ORCASLICER_CONTAINER_DIGEST"] = refs["orcaslicer-worker"].partition("@")[2]
        config = self.compose_config(deployment["project"], deployment["composeFiles"], deployment["profiles"],
                                     env=env)
        return config, env

    def vendored_abs(self, deployment):
        return {self.path(v[2:]): self.path(k) for k, v in (deployment.get("vendored") or {}).items()}

    def preflight(self, deployment, release, allow_insider, allow_unsafe, state, action="update"):
        if release["channel"] == "insider" and not allow_insider:
            raise MigrationError("%s is an insider release; pass --allow-insider to opt in explicitly"
                                 % release["version"])
        host = self.host_platform()
        needed = sorted(set(deployment["services"].values()))
        for component in needed:
            if host not in COMPONENTS[component]["platforms"]:
                raise MigrationError("%s is not published for %s (published: %s)"
                                     % (component, host, ", ".join(COMPONENTS[component]["platforms"])))
        lineage = self.check_lineage(deployment, state, release, allow_unsafe, action)
        return host, needed, lineage

    def plan(self, version, manifest_file=None, allow_insider=False, allow_unsafe=False):
        deployment = self.deployment()
        state = read_json(os.path.join(self.state_dir, "release.json"), {})
        release = self.load_release(version, manifest_file)
        _, needed, lineage = self.preflight(deployment, release, allow_insider, allow_unsafe, state)
        containers = self.project_containers(deployment["project"])
        resolved, _ = self.resolved_config(deployment, {c: release["images"][c] for c in needed})
        self.check_storage(deployment["project"], resolved, containers, self.vendored_abs(deployment))
        current = {c["Config"]["Labels"].get("com.docker.compose.service"): c["Config"].get("Image")
                   for c in containers}
        self.say("Project %s in %s" % (deployment["project"], self.project_dir))
        self.say("Target release %s (%s, source %s, from %s)" % (release["version"], release["channel"],
                                                                  release["sourceCommit"], release["source"]))
        for service, component in sorted(deployment["services"].items()):
            self.say("  %s\n      old: %s\n      new: %s" % (service, current.get(service, "(not created)"),
                                                            release["images"][component]))
        self.say("Source lineage: %s. Storage identity: unchanged." % lineage)
        for name, volume in sorted((resolved.get("volumes") or {}).items()):
            self.say("  volume %-30s %s" % (name, volume.get("name", name)))
        self.say("Plan only: nothing pulled, written, or restarted.")
        return 0

    # --------------------------------------------------------------- apply
    @staticmethod
    def pid_alive(pid):
        if os.name != "posix":
            return None  # on Windows os.kill(pid, 0) terminates the process instead of probing it
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        except (OSError, ValueError, OverflowError):
            return None
        return True

    def acquire_lock(self):
        os.makedirs(self.state_dir, exist_ok=True)
        path = os.path.join(self.state_dir, "lock")
        token = "%d %s %s" % (os.getpid(), os.urandom(8).hex(), utc_now())
        try:
            fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            try:
                with open(path, encoding="utf-8") as handle:
                    holder = handle.read().split()
            except OSError:
                holder = []
            pid = int(holder[0]) if holder and holder[0].isdigit() else None
            alive = self.pid_alive(pid) if pid else None
            liveness = {True: "is still running", False: "is no longer running",
                        None: "liveness could not be determined"}[alive]
            raise MigrationError("another printfarmer-registry run holds %s (PID %s, started %s; that process %s). "
                                 "Locks are never taken over automatically. If no other run is active on this host, "
                                 "run `status`, then delete the lock file and retry."
                                 % (path, pid if pid else "unknown", holder[2] if len(holder) > 2 else "unknown",
                                    liveness))
        try:
            os.write(fd, token.encode())
        finally:
            os.close(fd)
        return path, token

    def release_lock(self, lock):
        path, token = lock
        try:
            with open(path, encoding="utf-8") as handle:
                owner = handle.read()
            if owner != token:
                self.say("WARNING: %s now belongs to another run; leaving it in place." % path)
                return
            os.remove(path)
        except OSError as error:
            self.say("WARNING: could not remove lock %s (%s); delete it once no run is active." % (path, error))

    def load_pending(self, path):
        try:
            pending = read_json(path)
        except (OSError, ValueError) as error:
            raise MigrationError("%s is unreadable (%s)" % (path, error.__class__.__name__) + PENDING_HELP % path)
        if pending is None:
            return None
        problems = []
        if not isinstance(pending, dict):
            problems.append("not an object")
        else:
            if pending.get("action") not in PENDING_ACTIONS:
                problems.append("action")
            if pending.get("phase") not in PENDING_PHASES:
                problems.append("phase")
            if not VERSION_RE.match(str(pending.get("version", ""))):
                problems.append("version")
            if not isinstance(pending.get("images"), dict):
                problems.append("images")
        if problems:
            raise MigrationError("%s is malformed (invalid: %s)" % (path, ", ".join(problems)) + PENDING_HELP % path)
        return pending

    def apply(self, release, action, allow_insider=False, allow_unsafe=False, health_timeout=600,
              resume_interrupted=False):
        deployment = self.deployment()
        pending_path = os.path.join(self.state_dir, "pending.json")
        state_path = os.path.join(self.state_dir, "release.json")
        lock = self.acquire_lock()
        try:
            pending = self.load_pending(pending_path)
            if pending and not resume_interrupted:
                raise MigrationError("a previous %s to %s stopped in phase %r; inspect `status`, then rerun with "
                                     "--resume-interrupted" % (pending["action"], pending["version"],
                                                               pending["phase"]))
            state = read_json(state_path, {})
            host, needed, _ = self.preflight(deployment, release, allow_insider, allow_unsafe, state, action)
            containers = self.project_containers(deployment["project"])
            refs = {c: release["images"][c] for c in needed}
            transaction = {"action": action, "version": release["version"], "phase": "pulling",
                           "startedAt": utc_now(), "images": refs, "previousPending": pending}
            write_json_atomic(pending_path, transaction)

            for component, reference in sorted(refs.items()):
                self.say("Pulling %s" % reference)
                self.docker("pull", "--platform", host, reference)
                self.verify_image(component, reference, release, host, deployment)
            resolved, env = self.resolved_config(deployment, refs)
            self.check_storage(deployment["project"], resolved, containers, self.vendored_abs(deployment))
            for name, service in sorted(resolved.get("services", {}).items()):
                image = service.get("image", "")
                if name not in deployment["services"] and image:
                    code, _ = self.docker("image", "inspect", image, check=False)
                    if code != 0:
                        self.say("Pulling dependency image %s" % image)
                        self.docker("pull", image)
            self.say("All images present and verified; nothing has been stopped or restarted yet.")

            transaction["phase"] = "starting"
            write_json_atomic(pending_path, transaction)
            self.compose(deployment["project"], deployment["composeFiles"], deployment["profiles"],
                         "up", "-d", "--no-build", "--pull", "never", env=env)
            transaction["phase"] = "health"
            write_json_atomic(pending_path, transaction)
            self.wait_healthy(deployment, resolved, health_timeout)

            previous = state.get("current") or {"kind": "git-checkout",
                                                 "sourceCommit": deployment["migratedFrom"]["deployedCommit"]}
            write_json_atomic(state_path, {"schema": 1, "current": dict(release, appliedAt=utc_now()),
                                           "previous": previous})
            os.remove(pending_path)
            self.say("PrintFarmer %s (%s) is running and healthy." % (release["version"], release["channel"]))
            return 0
        except BaseException as primary:
            try:
                self.record_failure(pending_path)
            except Exception as error:  # never mask the primary failure
                self.say("WARNING: could not record the failed transaction (%s); run `status`." % error)
            if isinstance(primary, KeyboardInterrupt):
                self.say("Interrupted. The transaction state was recorded; run `status`, then rerun with "
                         "--resume-interrupted or roll back.")
            raise
        finally:
            self.release_lock(lock)

    def record_failure(self, pending_path):
        try:
            current = read_json(pending_path)
        except (OSError, ValueError):
            return
        if not isinstance(current, dict) or current.get("phase") not in PENDING_PHASES:
            return
        if current["phase"] == "pulling":
            if current.get("previousPending"):
                write_json_atomic(pending_path, current["previousPending"])
            else:
                os.remove(pending_path)  # nothing was started; the deployment is untouched
        elif not current["phase"].startswith("failed-"):
            current["phase"] = "failed-" + current["phase"]
            write_json_atomic(pending_path, current)

    def verify_image(self, component, reference, release, host, deployment):
        _, text = self.docker("image", "inspect", reference)
        image = json.loads(text)[0]
        labels = (image.get("Config") or {}).get("Labels") or {}
        problems = []
        if "%s/%s" % (image.get("Os"), image.get("Architecture")) != host:
            problems.append("platform %s/%s" % (image.get("Os"), image.get("Architecture")))
        if labels.get("org.opencontainers.image.version") != release["version"]:
            problems.append("version label")
        if labels.get("org.opencontainers.image.revision") != release["sourceCommit"]:
            problems.append("revision label")
        if labels.get("org.printfarmer.release-channel") != release["channel"]:
            problems.append("channel label")
        if component == "orcaslicer-worker" and labels.get("orcaslicer.version") != deployment.get("orcaslicerVersion"):
            expected = deployment.get("orcaslicerVersion")
            problems.append("OrcaSlicer %s does not match the deployment's custom-profiles volume version %s; choose "
                            "a release built with OrcaSlicer %s" % (labels.get("orcaslicer.version"), expected, expected))
        if problems:
            raise MigrationError("%s image does not match the release: %s" % (component, "; ".join(problems)))

    def wait_healthy(self, deployment, resolved, timeout):
        deadline = self.clock() + timeout
        expected = set(resolved.get("services", {}))
        while True:
            problems = []
            by_service = {}
            for container in self.project_containers(deployment["project"]):
                by_service.setdefault(container["Config"]["Labels"].get("com.docker.compose.service"),
                                      []).append(container)
            for service in sorted(expected):
                if not by_service.get(service):
                    problems.append("%s: no container" % service)
                for container in by_service.get(service, []):
                    state = container.get("State") or {}
                    health = (state.get("Health") or {}).get("Status")
                    restart = ((container.get("HostConfig") or {}).get("RestartPolicy") or {}).get("Name") or "no"
                    if state.get("Status") == "exited" and state.get("ExitCode") == 0 and restart == "no":
                        continue  # completed one-shot service
                    if state.get("Status") != "running" or state.get("Restarting"):
                        problems.append("%s: %s" % (service, state.get("Status")))
                    elif health not in (None, "healthy"):
                        problems.append("%s: %s" % (service, health))
            if not problems:
                return
            if self.clock() >= deadline:
                raise MigrationError("services not healthy after %ss: %s. New images may already be running; the "
                                     "last-good release record was not changed. Inspect `docker compose logs`, then "
                                     "retry with --resume-interrupted or roll back." % (timeout, ", ".join(problems)))
            self.sleep(5)

    def update(self, version, manifest_file=None, **options):
        return self.apply(self.load_release(version, manifest_file), "update", **options)

    def rollback(self, **options):
        state = read_json(os.path.join(self.state_dir, "release.json"), {})
        previous = state.get("previous")
        if not previous or previous.get("kind") != "release":
            raise MigrationError("no previous release to roll back to. Returning to a source build requires a "
                                 "checkout and scripts/deploy-docker.sh; restore data from backup if migrations ran")
        self.say("Rolling back images to %s. Data is NOT restored; restore your backup first if the newer release "
                 "ran database migrations (rollback also requires --allow-unsafe-downgrade)." % previous["version"])
        return self.apply(previous, "rollback", **options)

    def status(self):
        deployment = self.deployment()
        state = read_json(os.path.join(self.state_dir, "release.json"), {})
        self.say("Project: %s  profiles: %s" % (deployment["project"], ", ".join(deployment["profiles"]) or "-"))
        current = state.get("current")
        if current:
            self.say("Current release: %s (%s) source %s" % (current["version"], current["channel"],
                                                              current["sourceCommit"]))
        else:
            self.say("Current: migrated git-checkout deployment at %s (no release applied yet)"
                     % deployment["migratedFrom"]["deployedCommit"])
        previous = state.get("previous")
        if previous:
            self.say("Previous: %s" % (previous.get("version") or "git checkout " + previous["sourceCommit"]))
        code = 0
        refs = dict(current["images"]) if current else {}
        for component in deployment["services"].values():
            refs.setdefault(component, "%s%s@sha256:%s" % (IMAGE_PREFIX, component, "0" * 64))
        resolved, _ = self.resolved_config(deployment, refs)
        self.say("Host paths this deployment depends on:")
        for path in self.host_paths(resolved):
            present = os.path.exists(path)
            self.say("  %-8s %s" % ("ok" if present else "MISSING", self.display_path(path)))
            if not present:
                code = 1
        pending = self.load_pending(os.path.join(self.state_dir, "pending.json"))
        if pending:
            self.say("UNFINISHED: %s to %s in phase %s (started %s)" % (pending["action"], pending["version"],
                                                                       pending["phase"], pending.get("startedAt")))
            code = 1
        return code


def default_project_dir():
    here = os.path.dirname(os.path.abspath(__file__))
    if os.path.basename(here) == "bin" and os.path.basename(os.path.dirname(here)) == STATE_DIR:
        return os.path.dirname(os.path.dirname(here))
    return os.getcwd()


def build_parser():
    parser = argparse.ArgumentParser(prog="printfarmer-registry",
                                     description="Build-free PrintFarmer deployment controller")
    parser.add_argument("--project-dir", default=None, help="deployment directory (default: owning directory)")
    sub = parser.add_subparsers(dest="command", required=True)

    migrate = sub.add_parser("migrate", help="convert the existing checkout deployment in place (no restarts)")
    migrate.add_argument("--project", help="Compose project name (default: discovered from running containers)")
    migrate.add_argument("--deployed-commit", help="full SHA of the running build, or HEAD")
    migrate.add_argument("--dry-run", action="store_true", help="print the plan without writing anything")
    migrate.add_argument("--allow-new-services", action="store_true",
                         help="permit configured services that have never been created")

    def release_options(command):
        command.add_argument("--allow-insider", action="store_true", help="explicitly opt in to insider releases")
        command.add_argument("--allow-unsafe-downgrade", action="store_true",
                             help="proceed when the target is not a verified descendant of the deployed commit")

    def apply_options(command):
        release_options(command)
        command.add_argument("--backup-confirmed", action="store_true",
                             help="required: confirms a current database and data-volume backup exists")
        command.add_argument("--health-timeout", type=int, default=600)
        command.add_argument("--resume-interrupted", action="store_true")

    plan = sub.add_parser("plan", help="dry run: show image changes and storage identity")
    plan.add_argument("--version", required=True)
    plan.add_argument("--manifest-file", help="local copy of the release's container-images.json (lineage is still "
                                              "verified online through the GitHub compare API)")
    release_options(plan)

    update = sub.add_parser("update", help="pull, verify, and switch to a release")
    update.add_argument("--version", required=True)
    update.add_argument("--manifest-file", help="local copy of the release's container-images.json (lineage is still "
                                                "verified online through the GitHub compare API)")
    apply_options(update)

    rollback = sub.add_parser("rollback", help="switch images back to the previous release (no data restore; "
                                               "requires --allow-unsafe-downgrade after restoring a backup)")
    apply_options(rollback)
    sub.add_parser("status", help="show current/previous release and unfinished transactions")
    return parser


def main(argv=None, controller_factory=Controller):
    args = build_parser().parse_args(argv)
    controller = controller_factory(args.project_dir or default_project_dir())
    try:
        if args.command == "migrate":
            return controller.migrate(args.project, args.deployed_commit, args.dry_run, args.allow_new_services)
        if args.command == "plan":
            return controller.plan(args.version, args.manifest_file, args.allow_insider, args.allow_unsafe_downgrade)
        if args.command == "status":
            return controller.status()
        if not args.backup_confirmed:
            raise MigrationError("refusing to change images without --backup-confirmed. Back up the database and "
                                 "data volumes first (see docs/DEPLOYMENT_REGISTRY_MIGRATION.md)")
        options = {"allow_insider": args.allow_insider, "allow_unsafe": args.allow_unsafe_downgrade,
                   "health_timeout": args.health_timeout, "resume_interrupted": args.resume_interrupted}
        if args.command == "update":
            return controller.update(args.version, args.manifest_file, **options)
        return controller.rollback(**options)
    except MigrationError as error:
        sys.stderr.write("printfarmer-registry: %s\n" % error)
        return 2
    except KeyboardInterrupt:
        sys.stderr.write("printfarmer-registry: interrupted; run `status` before retrying\n")
        return 130


if __name__ == "__main__":
    sys.exit(main())
