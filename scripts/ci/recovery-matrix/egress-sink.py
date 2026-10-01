#!/usr/bin/env python3
import json
import socket
import sys
import threading
from datetime import datetime, timezone


def dns_query_name(payload):
    try:
        if len(payload) < 13:
            return None
        index = 12
        labels = []
        while index < len(payload):
            length = payload[index]
            if length == 0:
                return ".".join(labels) or "."
            if length & 0xC0:
                return "<compressed>"
            index += 1
            labels.append(payload[index:index + length].decode("ascii", errors="replace"))
            index += length
    except Exception:
        return None
    return None


def record(path, protocol, destination, source, **extra):
    payload = {
        "at": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "destination": destination,
        "source": source,
        "protocol": protocol,
    }
    payload.update({key: value for key, value in extra.items() if value is not None})
    with open(path, "a", encoding="utf-8") as handle:
        handle.write(json.dumps(payload, separators=(",", ":")) + "\n")
        handle.flush()


def tcp_socket(port):
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", port))
    server.listen(128)
    return server


def udp_socket(port):
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("0.0.0.0", port))
    return server


def tcp_listener(path, server, port):
    while True:
        connection, address = server.accept()
        query = None
        if port == 53:
            try:
                payload = connection.recv(4096)
                query = dns_query_name(payload[2:] if len(payload) > 2 else payload)
            except OSError:
                query = None
        record(path, f"tcp/{port}", query or f"tcp-listener:{port}", f"{address[0]}:{address[1]}", query=query)
        try:
            connection.close()
        except OSError:
            pass


def udp_listener(path, server, port):
    while True:
        payload, address = server.recvfrom(4096)
        query = dns_query_name(payload)
        record(path, f"udp/{port}", query or f"udp-listener:{port}", f"{address[0]}:{address[1]}", query=query)


def main():
    if len(sys.argv) not in (2, 3):
        print("usage: egress-sink.py <attempts.ndjson> [ready-marker]", file=sys.stderr)
        return 2
    path = sys.argv[1]
    open(path, "a", encoding="utf-8").close()
    # Bind every listener before signalling readiness so a caller never probes a sink that would
    # refuse (and therefore not record) its first attempt.
    tcp_servers = [(tcp_socket(port), port) for port in (53, 80, 443)]
    udp_server = udp_socket(53)
    for server, port in tcp_servers:
        threading.Thread(target=tcp_listener, args=(path, server, port), daemon=True).start()
    threading.Thread(target=udp_listener, args=(path, udp_server, 53), daemon=True).start()
    if len(sys.argv) == 3:
        with open(sys.argv[2], "w", encoding="utf-8") as handle:
            handle.write("ready\n")
    threading.Event().wait()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
