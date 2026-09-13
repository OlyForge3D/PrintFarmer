#!/usr/bin/env python3
"""Structural regression guard, not a sandbox or an enrollment authorizer."""

import argparse
import copy
import importlib.util
import json
import os
from pathlib import Path
import posixpath
import re
import shutil
import subprocess
import sys
import unittest
import uuid

from ruamel.yaml import YAML
from ruamel.yaml.nodes import MappingNode, SequenceNode


ROOT = Path(__file__).resolve().parents[1]
TEMPLATES = ROOT / "scripts" / "docker" / "compose-templates"
OBSOLETE_GUIDANCE = re.compile(
    r"\bhost[\s_-]+network(?:ing)?|host\.docker\.internal|NET_ADMIN|NET_RAW|"
    r"privileged\s*:\s*(?:true|yes)|--privileged|privileged\s+mode|"
    r"\b(?:docker_)?network_mode[\"']?\s*[:=]\s*[\"']?host\b|"
    r"\bbridge(?:\s*(?:-|→|->|=>)\s*|\s+)(?:to[\s-]+)?host\b|"
    r"NGINX_FRONTEND_CONFIG|DOCKER_HOST_NETWORK|"
    r"--net(?:work)?(?:=|\s+)host",
    re.IGNORECASE,
)


def assert_current_guidance(text, path):
    match = OBSOLETE_GUIDANCE.search(text)
    if match:
        line = text.count("\n", 0, match.start()) + 1
        raise AssertionError(f"{path}:{line}: obsolete topology guidance: {match.group()}")


def normalize_powershell_error(text):
    text = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", text)
    # PowerShell's ConciseView prefixes wrapped diagnostic lines with a gutter.
    text = re.sub(r"(?m)^[ \t]*\|[ \t]*", "", text)
    return " ".join(text.split())


def supported_guidance_paths():
    paths = set((ROOT / "docs").rglob("*.md"))
    paths.update(path for path in (ROOT / "scripts" / "docker").rglob("*")
                 if path.suffix in {".sh", ".ps1", ".py", ".md", ".conf", ".yml", ".yaml"}
                 or path.name.startswith("Dockerfile"))
    paths.update((ROOT / "scripts").glob("deploy-*"))
    paths.update((ROOT / "scripts").glob("fix-*.sh"))
    paths.add(ROOT / "scripts" / "start-all-local-with-workers.sh")
    paths.update(path for path in (ROOT / "deploy" / "nginx").rglob("*")
                 if path.suffix in {".conf", ".md", ".yml", ".yaml"})
    paths.add(ROOT / "README.md")
    return paths


def load_compose(path):
    text = path.read_text(encoding="utf-8-sig")
    if path.name == "docker-compose.yml" and path.parent == TEMPLATES:
        text = (TEMPLATES / "docker-compose.common.yml").read_text() + "\n" + text
    yaml = YAML(typ="safe")

    def compose_tag(constructor, node):
        if isinstance(node, SequenceNode):
            return constructor.construct_sequence(node, deep=True)
        if isinstance(node, MappingNode):
            return constructor.construct_mapping(node, deep=True)
        return constructor.construct_scalar(node)

    # Inspect the payload even when Compose will replace/reset the original.
    for tag in ("!reset", "!override"):
        yaml.constructor.add_constructor(tag, compose_tag)
    return yaml.load(text) or {}


def merge_compose(base, overlay):
    spec = importlib.util.spec_from_file_location(
        "compose_merge", ROOT / "scripts" / "docker" / "compose-merge.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.merge(base, overlay)


def assert_boundary(compose):
    services = compose.get("services", {})
    if not isinstance(services, dict):
        raise AssertionError("services must be a mapping")
    for name, service in services.items():
        serialized = json.dumps(service).lower().replace("\\\\", "/")
        if "host.docker.internal" in serialized:
            raise AssertionError(f"{name}: deployment services must use bridge service DNS")
        if any(token in serialized for token in (
            "docker.sock", "docker_engine", "containerd.sock", "podman.sock",
            "docker-socket-proxy", "socket-proxy", "docker-api-proxy",
        )):
            raise AssertionError(f"{name}: container control transport is forbidden")
        environment = service.get("environment") or {}
        keys = environment.keys() if isinstance(environment, dict) else (
            entry.split("=", 1)[0] for entry in environment
        )
        if any(key.upper() in {"DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS_VERIFY",
                               "DOCKER_CERT_PATH", "CONTAINER_HOST"} for key in keys):
            raise AssertionError(f"{name}: container control environment is forbidden")
        for volume in service.get("volumes") or []:
            source = volume.get("source", "") if isinstance(volume, dict) else volume.split(":", 1)[0]
            source = posixpath.normpath(source.replace("\\", "/"))
            if source.startswith("/"):
                source = "/" + source.lstrip("/")
            if source == "/" or any(
                source == parent or source.startswith(parent + "/")
                for parent in ("/run", "/var/run", "/var/lib/docker", "/var/lib/containerd",
                               "/var/lib/containers")
            ):
                raise AssertionError(f"{name}: host control directory is forbidden")
    discovery = services.get("printer-discovery")
    if discovery is None:
        return
    if discovery.get("volumes") or discovery.get("devices") or discovery.get("cap_add"):
        raise AssertionError("discovery requires no host mounts, devices or added capabilities")
    if discovery.get("privileged") or discovery.get("network_mode") or discovery.get("pid"):
        raise AssertionError("discovery must retain namespace isolation")
    if discovery.get("read_only") is not True or discovery.get("cap_drop") != ["ALL"]:
        raise AssertionError("discovery must use a read-only root and drop all capabilities")
    if "no-new-privileges:true" not in discovery.get("security_opt", []):
        raise AssertionError("discovery must forbid privilege escalation")
    if discovery.get("ports"):
        raise AssertionError("discovery must not publish a host listener")
    if not discovery.get("tmpfs") or not discovery.get("deploy", {}).get("resources", {}).get("limits"):
        raise AssertionError("discovery must retain bounded scratch and resource limits")
    if not discovery.get("networks") or not discovery.get("healthcheck"):
        raise AssertionError("discovery must retain service networking and health checks")
    environment = discovery.get("environment") or {}
    if isinstance(environment, list):
        environment = dict(entry.split("=", 1) for entry in environment if "=" in entry)
    if environment.get("Discovery__ApiBaseUrl") not in {
        "http://api:5245", "${DISCOVERY__API_BASE_URL:-http://api:5245}",
    }:
        raise AssertionError("discovery must use the canonical http://api:5245 API URL")


class DiscoveryBoundaryTests(unittest.TestCase):
    def test_every_canonical_template(self):
        paths = sorted(TEMPLATES.glob("*.yml"))
        self.assertTrue(paths)
        for path in paths:
            with self.subTest(template=path.name):
                config = load_compose(path)
                discovery = config.get("services", {}).get("printer-discovery")
                if discovery is not None and path.name != "docker-compose.discovery.yml":
                    # Overlays inherit hardening, but any explicit override must
                    # still satisfy the boundary rather than escaping inspection.
                    config = merge_compose(
                        load_compose(TEMPLATES / "docker-compose.discovery.yml"), config)
                assert_boundary(config)

    def test_merged_supported_database_and_discovery_configurations(self):
        for provider in ("postgres", "sqlserver"):
            for discovery_enabled in (False, True):
                with self.subTest(provider=provider, discovery=discovery_enabled):
                    config = load_compose(TEMPLATES / "docker-compose.yml")
                    database = load_compose(ROOT / "scripts" / "docker" / "database-templates" / f"{provider}.yml")
                    config["services"]["database"] = database["database"]
                    if discovery_enabled:
                        config = merge_compose(config, load_compose(TEMPLATES / "docker-compose.discovery.yml"))
                        self.assertIn("printer-discovery", config["services"])
                    assert_boundary(config)

    def test_socket_and_proxy_abuse_cases_are_rejected(self):
        cases = (
            {"volumes": ["/var/run/docker.sock:/var/run/docker.sock:ro"]},
            {"volumes": [{"type": "bind", "source": "/run/docker.sock", "target": "/observer", "read_only": True}]},
            {"volumes": ["//./pipe/docker_engine://./pipe/docker_engine"]},
            {"volumes": ["/var/run:/host-run:ro"]},
            {"volumes": ["/run/user/1000:/observer:ro"]},
            {"volumes": ["//run/user/1000:/observer:ro"]},
            {"volumes": ["/var/lib/docker/containers:/observer:ro"]},
            {"volumes": ["/var/lib/containerd/io.containerd.runtime.v2.task:/observer:ro"]},
            {"volumes": [{"type": "bind", "source": "/var/run/./containerd/", "target": "/observer"}]},
            {"volumes": ["/var/lib/containers/storage:/observer:ro"]},
            {"volumes": ["/:/host:ro"]},
            {"environment": ["DOCKER_HOST=tcp://control:2375"]},
            {"environment": {"DOCKER_HOST": "tcp://control:2376"}},
            {"environment": {"CONTAINER_HOST": "unix:///run/podman.sock"}},
            {"image": "example/docker-socket-proxy:1"},
        )
        for service in ("api", "frontend", "checker", "printer-discovery"):
            for case in cases:
                with self.subTest(service=service, case=case):
                    with self.assertRaises(AssertionError):
                        assert_boundary({"services": {service: case}})

    def test_similarly_named_non_control_paths_are_allowed(self):
        for source in ("/runtime/data", "/var/runs", "/var/lib/docker-backups", "app-data"):
            with self.subTest(source=source):
                assert_boundary({"services": {"api": {"volumes": [f"{source}:/data"]}}})

    def test_discovery_privilege_regressions_are_rejected(self):
        baseline = load_compose(TEMPLATES / "docker-compose.discovery.yml")
        for key, value in (
            ("cap_add", ["NET_RAW"]), ("privileged", True), ("read_only", False),
            ("security_opt", []), ("volumes", ["arbitrary:/observe:ro"]),
            ("ports", ["5247:5247"]), ("network_mode", "host"),
            ("devices", ["/dev/mem"]), ("cap_drop", []),
            ("environment", {"Discovery__ApiBaseUrl": "http://incorrect-api:5245"}),
        ):
            with self.subTest(key=key):
                config = copy.deepcopy(baseline)
                config["services"]["printer-discovery"][key] = value
                with self.assertRaises(AssertionError):
                    assert_boundary(config)

    def test_discovery_has_no_docker_client_dependency(self):
        for directory in ("discovery", "printer-discovery"):
            for path in (ROOT / "src" / directory).rglob("*"):
                if path.suffix not in {".cs", ".csproj"} or {"bin", "obj"} & set(path.parts):
                    continue
                text = path.read_text(encoding="utf-8-sig")
                self.assertIsNone(re.search(
                    r"Docker\.DotNet|docker\.sock|docker_engine|SocketType\.Raw|Process\.Start", text),
                    str(path.relative_to(ROOT)))

    def test_supported_guides_and_deployment_scripts(self):
        # Historical devnotes/archived files are not supported operating guides.
        for path in sorted(supported_guidance_paths()):
            with self.subTest(path=str(path.relative_to(ROOT))):
                text = path.read_text(encoding="utf-8-sig")
                if path == ROOT / "scripts" / "start-all-local-with-workers.sh":
                    exception = '    -e Worker__StorageEndpoint="http://host.docker.internal:5245" \\'
                    self.assertEqual(1, text.count(exception))
                    self.assertIn("Local development only:", text)
                    text = text.replace(exception, "")
                assert_current_guidance(text, path.relative_to(ROOT))

    def test_guidance_scan_covers_build_and_proxy_configuration(self):
        paths = supported_guidance_paths()
        for relative in ("scripts/docker/dockerfiles/Dockerfile.frontend",
                         "scripts/docker/compose-templates/docker-compose.yml",
                         "scripts/deploy-docker.ps1", "deploy/nginx/conf.d/frontend-app.conf"):
            self.assertIn(ROOT / relative, paths)
        self.assertFalse((ROOT / "deploy/nginx/conf.d/frontend-app-host-network.conf").exists())

    def test_guidance_guard_rejects_old_recommendations(self):
        for text in (
            "Use host networking", 'network_mode: "host"', "NET_ADMIN", "NET_RAW",
            "privileged: true", "docker run --privileged", "Use privileged mode",
            "http://host.docker.internal:5245", "docker run --network=host",
            "[Supported guide](HOST_NETWORK_DEPLOYMENT.md)",
            "NETWORK_MODE=host", 'NETWORK_MODE="host"', "NETWORK_MODE='HOST'",
            '"NETWORK_MODE": "host"', "$env:NETWORK_MODE = 'host'",
            "DOCKER_NETWORK_MODE: host", "bridge → host", "bridge-to-host",
            "bridge to host", "bridge -> host", "bridge => host",
            "ARG NGINX_FRONTEND_CONFIG=old.conf", "DOCKER_HOST_NETWORK=false",
        ):
            with self.subTest(text=text):
                with self.assertRaisesRegex(AssertionError, "fixture.md:2:"):
                    assert_current_guidance("Heading\n" + text, "fixture.md")

    def test_repair_uses_generator_and_recreates_without_template_edits(self):
        repair = (ROOT / "scripts/docker/fix-discovery-heartbeat.sh").read_text()
        self.assertIn('bash "$REPO_ROOT/scripts/deploy-docker.sh"', repair)
        self.assertIn('--config-file "$CONFIG_FILE"', repair)
        self.assertIn('--env-file "$ENV_FILE"', repair)
        self.assertIn('--output-dir "$DEPLOYMENT_DIR"', repair)
        self.assertIn("--force-recreate --wait --wait-timeout 120", repair)
        self.assertIn('bash "$SCRIPT_DIR/verify-discovery-service.sh"', repair)
        self.assertNotIn("compose-templates", repair)
        self.assertNotIn("docker rm", repair)
        self.assertNotIn("docker stop", repair)
        simple = (ROOT / "scripts/docker/fix-discovery-simple.sh").read_text()
        self.assertIn('exec bash "$SCRIPT_DIR/fix-discovery-heartbeat.sh" "$@"', simple)


class DiscoveryDiagnosticTests(unittest.TestCase):
    """Execute the real shell verifier; no daemon, environment secrets or deployment mutations."""

    @classmethod
    def setUpClass(cls):
        git_bash = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git/bin/bash.exe"
        cls.bash = os.environ.get("TEST_BASH") or (
            str(git_bash) if os.name == "nt" and git_bash.is_file() else shutil.which("bash"))
        if not cls.bash:
            raise RuntimeError("Bash is required for discovery diagnostic regression tests")
        cls.workspace = ROOT / "artifacts" / "discovery-tests" / uuid.uuid4().hex
        cls.workspace.mkdir(parents=True)
        # A fake environment file is checked for existence only, never loaded.
        (cls.workspace / "runtime.conf").write_text("", encoding="utf-8")
        (cls.workspace / "saved.conf").write_text("", encoding="utf-8")
        (cls.workspace / "docker-compose.yml").write_text("services: {}\n", encoding="utf-8")
        cls.stub = cls.workspace / "docker-stub.sh"
        cls.stub.write_text(r"""
docker() {
    printf '%s\n' "$*" >> "$CALL_LOG"
    case "$1" in
      compose)
        shift
        while [[ "$1" == --env-file || "$1" == -f ]]; do shift 2; done
        case "$1" in
          ps)
            [[ "$SCENARIO" != missing ]] || return 0
            printf '%s-id\n' "$3"
            ;;
          exec)
            local service="$3" url="${@: -1}"
            [[ "$*" == *"--fail --silent --show-error --connect-timeout 5 --max-time 15"* ]] || return 98
            case "$SCENARIO:$service:$url" in
              own-health:printer-discovery:http://localhost:5247/* | \
              api-health:printer-discovery:http://api:5245/* | \
              reverse-health:api:http://printer-discovery:5247/*) return 22 ;;
            esac
            ;;
          up)
            [[ "$SCENARIO" != recreate-failure ]] || return 1
            ;;
          *) return 99 ;;
        esac
        ;;
      inspect)
        case "$3" in
          *State.Running*)
            [[ "$SCENARIO" != stopped ]] && echo true || echo false ;;
          *HostConfig.Privileged*)
            case "$SCENARIO" in
              isolation) echo 'true|true|0|0|||printfarmer-network|app|[ALL]|[no-new-privileges:true]' ;;
              socket) echo 'false|true|0|0|host-mount||printfarmer-network|app|[ALL]|[no-new-privileges:true]' ;;
              root) echo 'false|true|0|0|||printfarmer-network|0:1000|[ALL]|[no-new-privileges:true]' ;;
              escalation) echo 'false|true|0|0|||printfarmer-network|app|[ALL]|[no-new-privileges:false]' ;;
              *) echo 'false|true|0|0|||printfarmer-network|app|[ALL]|[no-new-privileges:true]' ;;
            esac
            ;;
          *NetworkSettings.Networks*)
            if [[ "$SCENARIO" == disconnected && "$4" == api-id ]]; then
              echo another-network
            else
              echo printfarmer-network
            fi
            ;;
          *) return 99 ;;
        esac
        ;;
      network)
        [[ "$SCENARIO" != wrong-driver ]] && echo bridge || echo macvlan ;;
      *) return 99 ;;
    esac
}
export -f docker
bash() {
    case "$1" in
      */scripts/deploy-docker.sh)
        printf 'regenerate %s\n' "$*" >> "$CALL_LOG"
        [[ "$SCENARIO" != regenerate-failure ]]
        ;;
      *) command bash "$@" ;;
    esac
}
export -f bash
""", encoding="utf-8")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.workspace)

    def run_script(self, scenario, script="scripts/docker/verify-discovery-service.sh"):
        log = self.workspace / f"{scenario}.log"
        environment = dict(os.environ, SCENARIO=scenario, CALL_LOG=log.as_posix())
        command = 'source "$1"; bash "$2" "$3" "$4" "$5"'
        result = subprocess.run(
            [self.bash, "-c", command, "discovery-test", self.stub.as_posix(),
             (ROOT / script).as_posix(), self.workspace.as_posix(),
             (self.workspace / "runtime.conf").as_posix(),
             (self.workspace / "saved.conf").as_posix()],
            cwd=ROOT, env=environment, capture_output=True, text=True, timeout=30,
            encoding="utf-8", errors="replace",
        )
        return result, log.read_text() if log.exists() else ""

    def test_bridge_probe_success_does_not_claim_heartbeat_success(self):
        result, calls = self.run_script("healthy")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("bridge HTTP paths verified", result.stdout)
        self.assertIn("HTTP health alone does not prove", result.stdout)
        for target in ("http://localhost:5247/api/discovery/health",
                       "http://api:5245/healthz",
                       "http://printer-discovery:5247/api/discovery/health"):
            self.assertIn(target, calls)
        self.assertNotIn(".Config.Env", calls)
        self.assertNotIn(" logs ", calls)

    def test_all_failures_exit_nonzero_without_success_message(self):
        for scenario in ("missing", "stopped", "isolation", "socket", "root",
                         "escalation", "disconnected", "wrong-driver",
                         "own-health", "api-health", "reverse-health"):
            with self.subTest(scenario=scenario):
                result, _ = self.run_script(scenario)
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertIn("Check selected deployment", result.stdout)
                self.assertNotIn("bridge HTTP paths verified", result.stdout)

    def test_repair_regenerates_then_recreates_then_verifies(self):
        result, calls = self.run_script("repair", "scripts/docker/fix-discovery-simple.sh")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertLess(calls.index("regenerate "), calls.index(" up "))
        self.assertLess(calls.index(" up "), calls.index(" exec "))
        self.assertIn("--include-discovery", calls)
        self.assertIn("--force-recreate --wait --wait-timeout 120 printer-discovery", calls)
        self.assertIn("bridge HTTP paths verified", result.stdout)
        self.assertNotIn(" rm ", calls)
        self.assertNotIn(" stop ", calls)

    def test_repair_failures_stop_before_later_operations(self):
        for scenario in ("regenerate-failure", "recreate-failure"):
            with self.subTest(scenario=scenario):
                result, calls = self.run_script(scenario, "scripts/docker/fix-discovery-heartbeat.sh")
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertNotIn(" exec ", calls)
                self.assertNotIn("bridge HTTP paths verified", result.stdout)
                if scenario == "regenerate-failure":
                    self.assertIn("regeneration failed", result.stdout)
                    self.assertNotIn(" up ", calls)
                else:
                    self.assertIn("did not become healthy", result.stdout)

    def test_generator_port_adjustment_preserves_bridge_dns(self):
        generator = (ROOT / "scripts/docker/compose-generator.sh").read_text(encoding="utf-8")
        snippet = re.search(
            r'''"\$PYTHON_CMD" - "\$compose_file" "\$\{HTTPS_PORT-\}" <<'PY'\n(.*?)\nPY''',
            generator, re.DOTALL,
        )
        self.assertIsNotNone(snippet, "Cannot locate the generator port-adjustment block")
        for https_port in ("0", "443"):
            with self.subTest(https_port=https_port):
                compose = self.workspace / f"ports-{https_port}.yml"
                compose.write_text(
                    "services:\n"
                    "  frontend:\n    ports:\n      - '8080:80'\n"
                    "    networks:\n      - printfarmer-network\n"
                    "  nginx-proxy:\n    ports:\n      - '8080:80'\n      - '443:443'\n"
                    "    networks:\n      - printfarmer-network\n",
                    encoding="utf-8",
                )
                result = subprocess.run(
                    [sys.executable, "-", str(compose), https_port],
                    input=snippet.group(1), capture_output=True, text=True, timeout=15,
                )
                self.assertEqual(0, result.returncode, result.stderr)
                config = load_compose(compose)
                self.assertNotIn("ports", config["services"]["frontend"])
                proxy = config["services"]["nginx-proxy"]
                self.assertEqual(["printfarmer-network"], proxy["networks"])
                self.assertNotIn("extra_hosts", proxy)
                self.assertEqual(["8080:80"] + ([] if https_port == "0" else ["443:443"]),
                                 proxy["ports"])

    def run_deploy_configuration(self, mode, include, saved_mode="bridge", inherited_mode="bridge"):
        """Run real parser/configuration functions; stop at the generation boundary."""
        deploy = (ROOT / "scripts/deploy-docker.sh").read_text(encoding="utf-8")
        names = ("main", "redeploy_existing", "load_previous_config",
                 "validate_deployment_network", "apply_discovery_override",
                 "configure_networking", "configure_additional")
        functions = []
        for name in names:
            match = re.search(rf"^{name}\(\) \{{\n.*?^\}}", deploy, re.MULTILINE | re.DOTALL)
            self.assertIsNotNone(match, name)
            functions.append(match.group())
        parser = deploy.split("# Parse leftover CLI args", 1)[1].split("# Apply --native-arch", 1)[0]
        saved = self.workspace / "entry-point.conf"
        saved.write_text(
            f"NETWORK_MODE='{saved_mode}'\nINCLUDE_DISCOVERY=false\nENABLE_DISCOVERY=false\n"
            "ENVIRONMENT=Production\nARCHITECTURE=microservices\nDB_PROVIDER=postgres\n"
            "COMPOSE_FILE=unused.yml\nINCLUDE_MONITORING=false\nINCLUDE_TELEMETRY=false\n"
            "INCLUDE_SECURITY=false\nINCLUDE_REGISTRY=false\n", encoding="utf-8")
        driver = "\n".join(functions) + r"""
set -euo pipefail
REPO_ROOT="$1"; CONFIG_FILE="$2"; NETWORK_MODE="$3"; shift 3
BLUE=; NC=; SHOW_HELP=false; TEAR_DOWN=false; VALIDATE_STORAGE_ONLY=false
PREPARE_OFFLINE=false; DEPLOY_OFFLINE=false; PULL_IMAGES=false; SAVE_IMAGES=false
LOAD_IMAGES=false; CACHE_ORCASLICER=false; LOAD_CACHED_ORCASLICER=false
NON_INTERACTIVE=false; _ARGS_KEEP=()
print_info() { :; }
print_success() { :; }
print_header() { :; }
print_error() { echo "$*" >&2; }
capture_config_overrides() { :; }
restore_config_overrides() { :; }
enforce_supported_orcaslicer_release() { :; }
migrate_legacy_db_credentials() { :; }
normalize_worker_configuration() { :; }
resolve_deployment_shared_keys() { :; }
save_deployment_config() { echo "SAVE:$INCLUDE_DISCOVERY:$ENABLE_DISCOVERY:$NETWORK_MODE"; }
generate_env_file() { :; }
generate_react_env_production() { :; }
prepare_pgadmin_setup() { :; }
detect_environment() { :; }
choose_architecture() { :; }
configure_database() { :; }
adjust_connection_strings_for_network_mode() { :; }
configure_slicing() { :; }
configure_external_storage() { :; }
validate_configuration() { :; }
generate_deployment_config() { echo "GENERATE:$5"; exit 0; }
""" + "\n# Parse leftover CLI args" + parser + "\nmain\n"
        args = ["--non-interactive"] + ([mode] if mode else []) + (["--include-discovery"] if include else [])
        return subprocess.run(
            [self.bash, "-s", "--", ROOT.as_posix(), saved.as_posix(),
             inherited_mode, *args], input=driver, cwd=self.workspace, capture_output=True, text=True,
            encoding="utf-8", errors="replace", timeout=20,
        )

    def test_discovery_cli_overrides_saved_disable_in_every_deploy_flow(self):
        for mode in ("", "--regenerate-config", "--redeploy"):
            for include in (False, True):
                with self.subTest(mode=mode, include=include):
                    result = self.run_deploy_configuration(mode, include)
                    self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    expected = str(include).lower()
                    self.assertIn(f"SAVE:{expected}:{expected}:bridge", result.stdout)
                    self.assertIn(f"GENERATE:{expected}", result.stdout)

    def test_invalid_network_mode_stops_every_deploy_flow_before_writes(self):
        for mode in ("", "--regenerate-config", "--redeploy"):
            for saved_mode, inherited_mode in (("host", "bridge"), ("HOST", "bridge"),
                                                ("bridge", "host"), ("invalid", "bridge")):
                with self.subTest(mode=mode, saved=saved_mode, inherited=inherited_mode):
                    result = self.run_deploy_configuration(mode, True, saved_mode, inherited_mode)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("Only bridge networking is supported", result.stderr)
                    self.assertNotIn("SAVE:", result.stdout)
                    self.assertNotIn("GENERATE:", result.stdout)

    def test_generator_entry_point_rejects_stale_mode_and_accepts_bridge(self):
        for provider in ("postgres", "sqlserver"):
            for mode in ("bridge", "host"):
                with self.subTest(provider=provider, mode=mode):
                    result = subprocess.run(
                        [self.bash, (ROOT / "scripts/docker/compose-generator.sh").as_posix(),
                         "--db-provider", provider, "--include-discovery", "--dry-run",
                         "--output-dir", self.workspace.as_posix()],
                        cwd=ROOT, env=dict(os.environ, NETWORK_MODE=mode),
                        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30,
                    )
                    if mode == "bridge":
                        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                    else:
                        self.assertNotEqual(0, result.returncode)
                        self.assertIn("Only bridge networking is supported", result.stderr)

    def test_powershell_entry_point_fails_fast(self):
        for message in ("does not support it", "Only bridge networking is supported"):
            words = message.split()
            for split in range(1, len(words)):
                with self.subTest(message=message, wrap_after=split):
                    wrapped = (
                        f"\x1b[36;1m     | \x1b[31;1m{' '.join(words[:split])}\x1b[0m\r\n"
                        f"\x1b[36;1m     | \x1b[31;1m{' '.join(words[split:])}\x1b[0m\n"
                    )
                    self.assertEqual(message, normalize_powershell_error(wrapped))
            self.assertNotIn(message, normalize_powershell_error(" ".join(words[:-1])))

        pwsh = shutil.which("pwsh")
        if not pwsh:
            raise RuntimeError("PowerShell is required for deployment entry-point regression tests")
        script = ROOT / "scripts/deploy-docker.ps1"
        for args, mode, message in ((["-IncludeDiscovery"], "bridge", "does not support it"),
                                    ([], "host", "Only bridge networking is supported")):
            with self.subTest(args=args, mode=mode):
                result = subprocess.run(
                    [pwsh, "-NoProfile", "-File", str(script), *args],
                    cwd=self.workspace, env=dict(os.environ, NETWORK_MODE=mode),
                    capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20,
                )
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, normalize_powershell_error(result.stderr), result.stderr)

        # Execute the actual config loader without running deployment/image operations.
        text = script.read_text(encoding="utf-8")
        loader = re.search(r"^function Load-DeploymentConfig \{.*?^\}", text,
                           re.MULTILINE | re.DOTALL)
        self.assertIsNotNone(loader)
        saved = self.workspace / "powershell.conf"
        for value in ('host', '"HOST"', 'bridge'):
            with self.subTest(saved=value):
                saved.write_text(f"NETWORK_MODE={value}\n", encoding="utf-8")
                command = (
                    "$ErrorActionPreference='Stop'; function Write-Info {};"
                    "function Set-SupportedOrcaSlicerConfig {};\n" + loader.group()
                    + "\n$config = Load-DeploymentConfig -ConfigPath $env:TEST_CONFIG;"
                    "Write-Output $config['NETWORK_MODE']"
                )
                result = subprocess.run(
                    [pwsh, "-NoProfile", "-Command", command], cwd=self.workspace,
                    env=dict(os.environ, TEST_CONFIG=str(saved)), capture_output=True,
                    text=True, encoding="utf-8", errors="replace", timeout=20,
                )
                if value == 'bridge':
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertEqual("bridge", result.stdout.strip())
                else:
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("Only bridge networking is supported",
                                  normalize_powershell_error(result.stderr), result.stderr)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compose", type=Path, help="Also inspect an actual generator output")
    args = parser.parse_args()
    if args.compose:
        config = load_compose(args.compose)
        if "printer-discovery" not in config.get("services", {}):
            raise AssertionError("generated discovery service is missing")
        assert_boundary(config)
        print("Generated discovery configuration boundary: PASS")
    else:
        unittest.main(argv=[__file__], verbosity=2)
