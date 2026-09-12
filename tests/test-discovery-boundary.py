#!/usr/bin/env python3
"""Structural regression guard, not a sandbox or an enrollment authorizer."""

import argparse
import copy
import importlib.util
import json
from pathlib import Path
import re
import unittest

from ruamel.yaml import YAML
from ruamel.yaml.nodes import MappingNode, SequenceNode


ROOT = Path(__file__).resolve().parents[1]
TEMPLATES = ROOT / "scripts" / "docker" / "compose-templates"


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


def assert_boundary(compose):
    services = compose.get("services", {})
    if not isinstance(services, dict):
        raise AssertionError("services must be a mapping")
    for name, service in services.items():
        serialized = json.dumps(service).lower().replace("\\\\", "/")
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
            source = source.rstrip("/") or "/"
            if source in {"/", "/run", "/var/run", "/var/lib/docker", "/run/user"}:
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
                    inherited = load_compose(TEMPLATES / "docker-compose.discovery.yml")["services"]["printer-discovery"]
                    inherited.update(discovery)
                    config["services"]["printer-discovery"] = inherited
                assert_boundary(config)

    def test_merged_supported_database_and_discovery_configurations(self):
        spec = importlib.util.spec_from_file_location(
            "compose_merge", ROOT / "scripts" / "docker" / "compose-merge.py")
        merge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(merge)
        for provider in ("postgres", "sqlserver"):
            for discovery_enabled in (False, True):
                with self.subTest(provider=provider, discovery=discovery_enabled):
                    config = load_compose(TEMPLATES / "docker-compose.yml")
                    database = load_compose(ROOT / "scripts" / "docker" / "database-templates" / f"{provider}.yml")
                    config["services"]["database"] = database["database"]
                    if discovery_enabled:
                        config = merge.merge(config, load_compose(TEMPLATES / "docker-compose.discovery.yml"))
                        self.assertIn("printer-discovery", config["services"])
                    assert_boundary(config)

    def test_socket_and_proxy_abuse_cases_are_rejected(self):
        cases = (
            {"volumes": ["/var/run/docker.sock:/var/run/docker.sock:ro"]},
            {"volumes": [{"type": "bind", "source": "/run/docker.sock", "target": "/observer", "read_only": True}]},
            {"volumes": ["//./pipe/docker_engine://./pipe/docker_engine"]},
            {"volumes": ["/var/run:/host-run:ro"]},
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

    def test_discovery_privilege_regressions_are_rejected(self):
        baseline = load_compose(TEMPLATES / "docker-compose.discovery.yml")
        for key, value in (
            ("cap_add", ["NET_RAW"]), ("privileged", True), ("read_only", False),
            ("security_opt", []), ("volumes", ["arbitrary:/observe:ro"]),
            ("ports", ["5247:5247"]), ("network_mode", "host"),
            ("devices", ["/dev/mem"]), ("cap_drop", []),
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
