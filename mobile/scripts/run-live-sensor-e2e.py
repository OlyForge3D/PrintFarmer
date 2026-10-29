#!/usr/bin/env python3
"""Run production-mode iOS sensor E2E against isolated local backends."""

import argparse
import json
import os
import secrets
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


REPO = Path(__file__).resolve().parents[2]
MOBILE = REPO / "mobile"
SRC = REPO / "src"
# CI runners install the same approved Xcode under a different bundle name;
# main() still rejects anything other than Xcode 26.6 build 17F113.
DEVELOPER_DIR = Path(os.environ.get(
    "PRINTFARM_SENSOR_E2E_DEVELOPER_DIR",
    "/Applications/Xcode-26.6.0.app/Contents/Developer",
))
API_URL = os.environ.get("PRINTFARM_SENSOR_E2E_API_URL", "http://localhost:5245")
MOONRAKER_URL = os.environ.get("PRINTFARM_SENSOR_E2E_MOONRAKER_URL", "http://127.0.0.1:7125")
DEFAULT_PROOF = "backend-ui"
PROOFS = {
    "backend-ui": {
        "test_plan": "PrintFarmerLiveSensorE2E",
        "test_name": (
            "PrintFarmerUITests/PrinterSensorsLiveBackendUITests/"
            "testLiveSensorsSignalRControlsAndReconnectAgainstBothBackends"
        ),
        "expected_test_id": (
            "PrinterSensorsLiveBackendUITests/"
            "testLiveSensorsSignalRControlsAndReconnectAgainstBothBackends()"
        ),
    },
    "retained-state": {
        "test_plan": "PrintFarmerLiveSensorStateE2E",
        "test_name": (
            "PrintFarmerTests/PrinterSensorLiveStateTests/"
            "testProductionPrinterStateReceivesHumidityAndAvailabilityAcrossSignalRReconnect"
        ),
        "expected_test_id": (
            "PrinterSensorLiveStateTests/"
            "testProductionPrinterStateReceivesHumidityAndAvailabilityAcrossSignalRReconnect()"
        ),
    },
}


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--proof",
        choices=PROOFS,
        default=DEFAULT_PROOF,
        help="Live proof to run (default: backend-ui; retained-state is opt-in).",
    )
    return parser.parse_args(argv)


def verify_named_test_succeeded(timing_path, expected_test_id):
    """Fail unless the bounded runner saw exactly the named test, once, as Success.

    run-tests.py rejects zero-test and failed runs but not skipped ones, and a
    skipped test would leave every live field readback unexecuted.
    """
    try:
        tests = json.loads(Path(timing_path).read_text(encoding="utf-8"))["tests"]
        observed = [(test["identifier"], test["status"]) for test in tests]
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise RuntimeError(f"No usable runner test results at {timing_path}: {error}") from error
    if observed != [(expected_test_id, "Success")]:
        raise RuntimeError(
            f"Expected exactly {expected_test_id} with status Success; observed {observed}"
        )
    print(
        f"LIVE_SENSOR_E2E: verified {expected_test_id} executed once with Success",
        flush=True,
    )


def build_test_command(proof_name, simulator_id, result_bundle):
    proof = PROOFS[proof_name]
    return [
        sys.executable, "scripts/run-tests.py", "--",
        "test", "-project", "PrintFarmer.xcodeproj", "-scheme", "PrintFarmer",
        "-testPlan", proof["test_plan"],
        "-destination", f"platform=iOS Simulator,id={simulator_id}",
        "-only-testing:" + proof["test_name"],
        "-parallel-testing-enabled", "NO",
        "-resultBundlePath", str(result_bundle),
    ]


def ensure_port_free(port):
    with socket.socket() as sock:
        sock.settimeout(0.5)
        if sock.connect_ex(("127.0.0.1", port)) == 0:
            raise RuntimeError(
                f"127.0.0.1:{port} is occupied; refusing to use or stop an existing service."
            )


def loopback_port(name, url):
    try:
        parsed = urlsplit(url)
        port = parsed.port
    except ValueError as error:
        raise RuntimeError(f"{name} must be a loopback HTTP URL with an explicit port.") from error
    if (
        parsed.scheme != "http"
        or parsed.hostname not in {"localhost", "127.0.0.1"}
        or port is None
        or port == 0
        or parsed.path not in ("", "/")
        or parsed.query
        or parsed.fragment
        or parsed.username
        or parsed.password
    ):
        raise RuntimeError(f"{name} must be a loopback HTTP URL with an explicit port.")
    return port


def http_json(url, method="GET", payload=None, timeout=5):
    body = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(
        url,
        data=body,
        method=method,
        headers={"Content-Type": "application/json"} if body is not None else {},
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.status, response.read()


def wait_http(url, process, log_path, timeout=90):
    deadline = time.monotonic() + timeout
    last_error = None
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise RuntimeError(
                f"Service exited with code {process.returncode}; see {log_path}"
            )
        try:
            status, body = http_json(url, timeout=2)
            if 200 <= status < 300:
                return body
        except (OSError, urllib.error.URLError) as error:
            last_error = error
        time.sleep(0.25)
    raise RuntimeError(f"Timed out waiting for {url}; last error: {last_error}; see {log_path}")


def create_initial_admin(username, password, bootstrap_secret):
    status, body = http_json(
        f"{API_URL}/api/setup/initial-admin",
        method="POST",
        payload={
            "username": username,
            "email": "sensor-e2e@localhost",
            "password": password,
            "firstName": "Sensor",
            "lastName": "E2E",
            "bootstrapSecret": bootstrap_secret,
        },
    )
    if not 200 <= status < 300:
        raise RuntimeError(f"Initial-admin setup returned HTTP {status}.")
    result = json.loads(body)
    if result.get("success") is not True or not result.get("token"):
        raise RuntimeError("Initial-admin setup did not return an authenticated test user.")
    return result["token"]


def seed_unknown_printer_model(database_path, api_log_path):
    """Seed only the disposable DB after the known SQLite nozzle-catalog seed failure.

    Startup currently aborts component and printer-model YAML seeding when nozzle
    insertion fails with a SQLite foreign-key violation in SeedNozzlesAsync. This
    check makes the test prerequisite explicit and prevents a different seed failure
    from being silently hidden by the E2E fixture.
    """
    with sqlite3.connect(database_path) as database:
        manufacturer = database.execute(
            'SELECT "Id" FROM "Manufacturers" WHERE "Name" = ?',
            ("Unknown",),
        ).fetchone()
        if manufacturer is None:
            raise RuntimeError("The isolated API database did not seed the Unknown manufacturer.")

        exists = database.execute(
            'SELECT 1 FROM "PrinterModels" WHERE "ManufacturerId" = ? AND "Name" = ?',
            (manufacturer[0], "Unknown Model"),
        ).fetchone()
        if exists:
            print("Unknown Model catalog prerequisite already exists.", flush=True)
            return

        api_log = api_log_path.read_text(encoding="utf-8", errors="replace")
        expected_seed_failure = (
            "[SeedData] Error seeding component models",
            "SQLite Error 19: 'FOREIGN KEY constraint failed'",
            "DataSeedService.SeedNozzlesAsync",
        )
        missing_evidence = [marker for marker in expected_seed_failure if marker not in api_log]
        if missing_evidence:
            raise RuntimeError(
                "The Unknown Model row is missing, and the API log does not show the known "
                f"SQLite nozzle seed failure; missing evidence: {missing_evidence}"
            )

        database.execute(
            """
            INSERT INTO "PrinterModels"
                ("Id", "Name", "ManufacturerId", "DefaultAutoDispatchState",
                 "IsActive", "UpdatedAt", "NameLowered")
            VALUES (?, ?, ?, 0, 1, ?, ?)
            """,
            (
                "34190000-0000-0000-0000-000000000001",
                "Unknown Model",
                manufacturer[0],
                time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "unknown model",
            ),
        )
        database.commit()
        print(
            "Inserted the Unknown Model reference row into the disposable SQLite database "
            "after confirming the known SQLite foreign-key failure in SeedNozzlesAsync. "
            "This E2E run does not validate catalog seeding.",
            flush=True,
        )


def wait_for_printer_names(token, expected, process, log_path):
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"API exited with code {process.returncode}; see {log_path}")
        try:
            request = urllib.request.Request(
                f"{API_URL}/api/printers",
                headers={"Authorization": f"Bearer {token}"},
            )
            with urllib.request.urlopen(request, timeout=3) as response:
                printers = json.loads(response.read())
            names = {printer.get("name") for printer in printers}
            if expected.issubset(names):
                return printers
        except urllib.error.HTTPError as error:
            raise RuntimeError(
                f"Authenticated printer-list request returned HTTP {error.code}; see {log_path}"
            ) from error
        except (KeyError, OSError, TypeError, urllib.error.URLError, json.JSONDecodeError):
            pass
        time.sleep(0.5)
    raise RuntimeError(
        f"API did not expose expected local printers {sorted(expected)}; see {log_path}"
    )


def create_isolated_simulator(source_udid, devices_path, runtimes_path, run_dir):
    devices = json.loads(devices_path.read_text(encoding="utf-8"))["devices"]
    source_matches = [
        (runtime_id, device)
        for runtime_id, runtime_devices in devices.items()
        for device in runtime_devices
        if device.get("udid") == source_udid
    ]
    if len(source_matches) != 1:
        raise RuntimeError("The shared resolver destination was not present exactly once.")

    runtime_id, source = source_matches[0]
    runtimes = json.loads(runtimes_path.read_text(encoding="utf-8"))["runtimes"]
    runtime = next((item for item in runtimes if item.get("identifier") == runtime_id), None)
    if (
        runtime is None
        or runtime.get("version") != "26.5"
        or runtime.get("buildversion") != "23F77"
        or not runtime.get("isAvailable")
    ):
        raise RuntimeError("The resolved simulator is not on the approved iOS 26.5 (23F77) runtime.")

    name = f"Sensor E2E {run_dir.name}"
    command = [
        "xcrun", "simctl", "create", name,
        source["deviceTypeIdentifier"], runtime_id,
    ]
    print(f"$ {' '.join(command)}", flush=True)
    result = subprocess.run(command, env=os.environ.copy(), capture_output=True, text=True)
    creation_log = run_dir / "isolated-simulator-create.log"
    creation_log.write_text(
        f"$ {' '.join(command)}\n{result.stdout}{result.stderr}",
        encoding="utf-8",
    )
    if result.returncode:
        raise RuntimeError(
            f"Could not create the isolated simulator; see {creation_log}"
        )

    simulator_udid = result.stdout.strip()
    if len(simulator_udid) != 36:
        raise RuntimeError(f"simctl returned an invalid simulator UDID; see {creation_log}")
    identity = {
        "name": name,
        "udid": simulator_udid,
        "deviceTypeIdentifier": source["deviceTypeIdentifier"],
        "runtime": runtime_id,
        "runtimeVersion": runtime["version"],
        "runtimeBuild": runtime["buildversion"],
        "resolvedTemplate": {
            "name": source["name"],
            "udid": source_udid,
        },
        "dataIsolation": "new simulator created without cloning existing app or account data",
    }
    (run_dir / "isolated-simulator.json").write_text(
        json.dumps(identity, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        f"Isolated simulator UDID: {simulator_udid} "
        f"({source['deviceTypeIdentifier']}, iOS {runtime['version']} "
        f"build {runtime['buildversion']})",
        flush=True,
    )
    return simulator_udid


def delete_isolated_simulator(simulator_udid, run_dir):
    log_path = run_dir / "isolated-simulator-cleanup.log"
    with log_path.open("w", encoding="utf-8") as log:
        for action in ("shutdown", "delete"):
            command = ["xcrun", "simctl", action, simulator_udid]
            result = subprocess.run(command, env=os.environ.copy(), capture_output=True, text=True)
            log.write(f"$ {' '.join(command)}\n{result.stdout}{result.stderr}")
            if action == "delete" and result.returncode:
                raise RuntimeError(
                    f"Could not delete the isolated simulator {simulator_udid}; see {log_path}"
                )


def create_moonraker_printer(token, database_path, moonraker_port):
    with sqlite3.connect(database_path) as database:
        manufacturer = database.execute(
            'SELECT "Id" FROM "Manufacturers" WHERE "Name" = ?',
            ("Unknown",),
        ).fetchone()
        model = database.execute(
            'SELECT "Id" FROM "PrinterModels" WHERE "ManufacturerId" = ? AND "Name" = ?',
            (manufacturer[0], "Unknown Model"),
        ).fetchone() if manufacturer else None
    if manufacturer is None or model is None:
        raise RuntimeError("The isolated API database is missing its Unknown catalog references.")

    payload = {
        "name": "Moonraker Sensor E2E",
        "serverUrl": MOONRAKER_URL,
        "backend": "Moonraker",
        "backendPort": moonraker_port,
        "frontendPort": moonraker_port,
        "manufacturerId": manufacturer[0],
        "modelId": model[0],
        "manufacturer": "Unknown",
        "model": "Unknown Model",
        "isEnabled": True,
        "isReachable": True,
        "hasHeatedBed": True,
        "maxBuildVolumeX": 200,
        "maxBuildVolumeY": 200,
        "maxBuildVolumeZ": 200,
    }
    request = urllib.request.Request(
        f"{API_URL}/api/printers",
        data=json.dumps(payload).encode(),
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
        },
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        if not 200 <= response.status < 300:
            raise RuntimeError(f"Moonraker test-printer creation returned HTTP {response.status}.")
        printer = json.loads(response.read())
    if printer.get("name") != payload["name"] or not printer.get("id"):
        raise RuntimeError("The API did not create the isolated Moonraker test printer.")
    return printer


def run_logged(command, cwd, env, log_path, display_command=None):
    shown_command = display_command or command
    print(f"$ {' '.join(str(part) for part in shown_command)}", flush=True)
    with log_path.open("w", encoding="utf-8") as log:
        completed = subprocess.run(
            [str(part) for part in command],
            cwd=cwd,
            env=env,
            stdout=log,
            stderr=subprocess.STDOUT,
        )
    lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
    for line in lines[-45:]:
        print(line, flush=True)
    if completed.returncode:
        raise subprocess.CalledProcessError(completed.returncode, command)


class LocalBackend:
    def __init__(self, command, cwd, env, log_path):
        self.command = command
        self.cwd = cwd
        self.env = env
        self.log_path = log_path
        self.log = log_path.open("a", encoding="utf-8")
        self.process = None

    def start(self):
        self.process = subprocess.Popen(
            self.command,
            cwd=self.cwd,
            env=self.env,
            stdout=self.log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )

    def stop(self):
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=5)
        self.process = None

    def close(self):
        self.stop()
        self.log.close()


class BackendControl:
    def __init__(self, api, api_health_log):
        self.api = api
        self.api_health_log = api_health_log
        self.lock = threading.Lock()

    def mutate_moonraker(self, mutation):
        fields = mutation.get("fields")
        if fields is None:
            fields = {"temperature": mutation["temperature"]}
        status, _ = http_json(
            f"{MOONRAKER_URL}/__emulator/printer/sensor",
            method="POST",
            payload={
                "objectName": mutation["objectName"],
                "fields": fields,
            },
        )
        return 200 <= status < 300

    def set_moonraker_scenario(self, scenario):
        status, _ = http_json(
            f"{MOONRAKER_URL}/__emulator/printer/scenario",
            method="POST",
            payload={"scenario": scenario},
        )
        return 200 <= status < 300

    def stop_api(self):
        with self.lock:
            self.api.stop()

    def start_api(self):
        with self.lock:
            self.api.start()
            wait_http(f"{API_URL}/healthz", self.api.process, self.api.log_path, timeout=90)

    def restart_api(self, mutation=None):
        with self.lock:
            self.api.stop()
            mutated = self.mutate_moonraker(mutation) if mutation else False
            self.api.start()
            wait_http(f"{API_URL}/healthz", self.api.process, self.api.log_path, timeout=90)
            return mutated


class ControlHandler(BaseHTTPRequestHandler):
    control = None

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        try:
            payload = json.loads(self.rfile.read(length) or b"{}")
            if self.path == "/moonraker-sensor":
                updated = self.control.mutate_moonraker(payload)
                self.respond(200, {"updated": updated})
                return
            if self.path == "/moonraker-scenario":
                updated = self.control.set_moonraker_scenario(payload["scenario"])
                self.respond(200, {"updated": updated})
                return
            if self.path == "/stop-api":
                self.control.stop_api()
                self.respond(200, {"stopped": True})
                return
            if self.path == "/start-api":
                self.control.start_api()
                self.respond(200, {"started": True})
                return
            if self.path == "/restart-api":
                mutation = payload.get("moonrakerSensor")
                updated = self.control.restart_api(mutation)
                self.respond(200, {
                    "restarted": True,
                    "moonrakerSensorUpdated": updated,
                })
                return
            self.respond(404, {"error": "not found"})
        except Exception as error:  # surfaced to XCTest as an HTTP failure
            self.respond(500, {"error": type(error).__name__})

    def respond(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, _format, *_args):
        return


def main(argv=None):
    proof_name = parse_args(argv).proof
    if not DEVELOPER_DIR.is_dir():
        raise RuntimeError(f"Required Xcode installation not found: {DEVELOPER_DIR}")
    os.environ["DEVELOPER_DIR"] = str(DEVELOPER_DIR)
    api_port = loopback_port("PRINTFARM_SENSOR_E2E_API_URL", API_URL)
    moonraker_port = loopback_port("PRINTFARM_SENSOR_E2E_MOONRAKER_URL", MOONRAKER_URL)
    if api_port == moonraker_port:
        raise RuntimeError("The isolated API and Moonraker emulator must use distinct ports.")
    ensure_port_free(api_port)
    ensure_port_free(moonraker_port)

    (MOBILE / "build").mkdir(parents=True, exist_ok=True)
    run_dir = Path(tempfile.mkdtemp(prefix="sensor-e2e-3419.", dir=MOBILE / "build"))
    print(f"Evidence directory: {run_dir}", flush=True)
    env = os.environ.copy()
    env.update({
        "ASPNETCORE_ENVIRONMENT": "E2E",
        "ASPNETCORE_URLS": API_URL,
        "DB_PROVIDER": "sqlite",
        "ConnectionStrings__Default": f"Data Source={run_dir / 'farm.sqlite'}",
        "Logging__LogLevel__Default": "Information",
        "WorkerAuth__SharedKey": secrets.token_urlsafe(32),
        "PFARM__Setup__BootstrapSecret": secrets.token_urlsafe(32),
        "TestEmulator__Enabled": "true",
        "TestEmulator__Printers__0__Name": "Test Printer Alpha",
        "PRINTFARM_SENSOR_E2E_USERNAME": f"sensor-e2e-{secrets.token_hex(6)}",
        "PRINTFARM_SENSOR_E2E_PASSWORD": secrets.token_urlsafe(18),
        "PRINTFARM_SENSOR_E2E_API_URL": API_URL,
    })

    xcode_version = subprocess.run(
        ["xcodebuild", "-version"], cwd=MOBILE, env=env, check=True,
        capture_output=True, text=True,
    ).stdout
    (run_dir / "xcode.log").write_text(xcode_version, encoding="utf-8")
    if "Xcode 26.6" not in xcode_version or "17F113" not in xcode_version:
        raise RuntimeError(f"Unexpected Xcode version; see {run_dir / 'xcode.log'}")

    for filename, args in (
        ("runtimes.json", ["xcrun", "simctl", "list", "runtimes", "-j"]),
        ("devices.json", ["xcrun", "simctl", "list", "devices", "available", "-j"]),
    ):
        result = subprocess.run(args, cwd=MOBILE, env=env, check=True, capture_output=True)
        (run_dir / filename).write_bytes(result.stdout)

    resolver = subprocess.run(
        [str(REPO / "scripts/ci/resolve-ios-simulator.sh"), "--udid"],
        cwd=REPO, env=env, check=True, capture_output=True, text=True,
    )
    resolved_simulator_id = resolver.stdout.strip()
    (run_dir / "destination.log").write_text(
        resolver.stderr + f"UDID: {resolved_simulator_id}\n", encoding="utf-8"
    )
    print(resolver.stderr, end="", flush=True)
    print(f"Resolved simulator UDID: {resolved_simulator_id}", flush=True)

    api_project = SRC / "api"
    api_dll = api_project / "bin/Debug/net10.0/Farm.Web.Api.dll"
    emulator_project = SRC / "moonraker-emulator/Farm.Moonraker.Emulator"
    emulator_dll = emulator_project / "bin/Debug/net10.0/Farm.Moonraker.Emulator.dll"
    run_logged(
        ["dotnet", "build", "./api/Farm.Web.Api.csproj", "-c", "Debug", "--no-restore"],
        SRC, env, run_dir / "api-build.log",
    )
    run_logged(
        ["dotnet", "build", "./moonraker-emulator/Farm.Moonraker.Emulator/Farm.Moonraker.Emulator.csproj",
         "-c", "Debug", "--no-restore"],
        SRC, env, run_dir / "moonraker-build.log",
    )

    moonraker = LocalBackend(
        ["dotnet", str(emulator_dll), "--urls", MOONRAKER_URL, "--Emulator:EnableControlApi=true"],
        emulator_project, env, run_dir / "moonraker.log",
    )
    api = LocalBackend(["dotnet", str(api_dll)], api_project, env, run_dir / "api.log")
    server = None
    test_simulator_id = None
    config_path = MOBILE / "build/sensor-e2e-3419-config.json"
    owns_config = False
    try:
        test_simulator_id = create_isolated_simulator(
            resolved_simulator_id,
            run_dir / "devices.json",
            run_dir / "runtimes.json",
            run_dir,
        )
        moonraker.start()
        wait_http(f"{MOONRAKER_URL}/__emulator/printer", moonraker.process, moonraker.log_path)
        api.start()
        wait_http(f"{API_URL}/healthz", api.process, api.log_path)
        seed_unknown_printer_model(run_dir / "farm.sqlite", run_dir / "api.log")
        token = create_initial_admin(
            env["PRINTFARM_SENSOR_E2E_USERNAME"],
            env["PRINTFARM_SENSOR_E2E_PASSWORD"],
            env["PFARM__Setup__BootstrapSecret"],
        )
        wait_for_printer_names(
            token,
            {"Test Printer Alpha"},
            api.process,
            api.log_path,
        )
        create_moonraker_printer(token, run_dir / "farm.sqlite", moonraker_port)
        wait_for_printer_names(
            token,
            {"Test Printer Alpha", "Moonraker Sensor E2E"},
            api.process,
            api.log_path,
        )

        control = BackendControl(api, api.log_path)
        ControlHandler.control = control
        server = ThreadingHTTPServer(("127.0.0.1", 0), ControlHandler)
        env["PRINTFARM_SENSOR_E2E_CONTROL_URL"] = (
            f"http://127.0.0.1:{server.server_port}"
        )
        threading.Thread(target=server.serve_forever, daemon=True).start()

        config_fd = os.open(
            config_path,
            os.O_CREAT | os.O_EXCL | os.O_WRONLY,
            0o600,
        )
        owns_config = True
        with os.fdopen(config_fd, "w", encoding="utf-8") as configuration:
            json.dump({
                "username": env["PRINTFARM_SENSOR_E2E_USERNAME"],
                "password": env["PRINTFARM_SENSOR_E2E_PASSWORD"],
                "apiURL": env["PRINTFARM_SENSOR_E2E_API_URL"],
                "controlURL": env["PRINTFARM_SENSOR_E2E_CONTROL_URL"],
            }, configuration)

        result_bundle = run_dir / "SensorsLive.xcresult"
        command = build_test_command(proof_name, test_simulator_id, result_bundle)
        run_logged(command, MOBILE, env, run_dir / "test.log")
        verify_named_test_succeeded(
            result_bundle.with_suffix(".timing.json"),
            PROOFS[proof_name]["expected_test_id"],
        )
    finally:
        if server is not None:
            server.shutdown()
            server.server_close()
        api.close()
        moonraker.close()
        if owns_config:
            config_path.unlink(missing_ok=True)
        if test_simulator_id is not None:
            delete_isolated_simulator(test_simulator_id, run_dir)
        print(f"Logs and XCTest evidence retained in {run_dir}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"LIVE_SENSOR_E2E: {error}", file=sys.stderr, flush=True)
        sys.exit(1)
