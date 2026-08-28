#!/usr/bin/env python3
"""Local RIoT proxy that forwards reads and blocks every mutation."""

from __future__ import annotations

import argparse
import hmac
import http.client
import http.server
import json
import os
import threading
import urllib.parse


FORWARDED_READ_HEADERS = {"accept", "user-agent"}
HOP_BY_HOP = {
    "connection",
    "keep-alive",
    "proxy-authenticate",
    "proxy-authorization",
    "te",
    "trailers",
    "transfer-encoding",
    "upgrade",
}
ORDER_PATH_PREFIX = "/api/order/v1/orderRecord/detailByUpperId/"
VEHICLE_PATH = "/api/task/vehicles/getVehicleInfoByDeviceKey"


class State:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.forwarded_reads = 0
        self.blocked_reads = 0
        self.blocked_mutations = 0
        self.last_read_status: int | None = None

    def snapshot(self) -> dict[str, object]:
        with self.lock:
            return {
                "schemaVersion": 1,
                "forwardedReadCount": self.forwarded_reads,
                "blockedReadCount": self.blocked_reads,
                "blockedMutationCount": self.blocked_mutations,
                "forwardedMutationCount": 0,
                "lastReadStatus": self.last_read_status,
            }


class Proxy(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    state: State
    upstream_host: str
    upstream_port: int
    map_id: int
    vehicle_key: str
    dispatch_generation: int
    client_token: str
    riot_api_key: str

    def log_message(self, _format: str, *_args: object) -> None:
        pass

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/_proxy/status":
            self._json(200, self.state.snapshot())
            return
        if not authorized_client(self.headers, self.client_token) or not allowed_read(
            self.command,
            self.path,
            self.map_id,
            self.vehicle_key,
            self.dispatch_generation,
        ):
            with self.state.lock:
                self.state.blocked_reads += 1
            self.close_connection = True
            self._json(403, {"code": "READ_ONLY_PROXY_READ_REJECTED"})
            return
        self._forward_read(include_body=True)

    def do_HEAD(self) -> None:  # noqa: N802
        with self.state.lock:
            self.state.blocked_reads += 1
        self.close_connection = True
        self._json(405, {"code": "READ_ONLY_PROXY_HEAD_REJECTED"})

    def do_POST(self) -> None:  # noqa: N802
        self._block_mutation()

    def do_PUT(self) -> None:  # noqa: N802
        self._block_mutation()

    def do_PATCH(self) -> None:  # noqa: N802
        self._block_mutation()

    def do_DELETE(self) -> None:  # noqa: N802
        self._block_mutation()

    def _block_mutation(self) -> None:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0
        if 0 < length <= 1_048_576:
            self.rfile.read(length)
        with self.state.lock:
            self.state.blocked_mutations += 1
        self.close_connection = True
        self._json(
            503,
            {
                "code": "READ_ONLY_PROXY_MUTATION_BLOCKED",
                "message": "Shadow pass does not forward mutation requests.",
            },
        )

    def _forward_read(self, include_body: bool) -> None:
        headers = upstream_headers(self.headers, self.riot_api_key)
        connection = http.client.HTTPConnection(
            self.upstream_host, self.upstream_port, timeout=15
        )
        try:
            connection.request(self.command, self.path, headers=headers)
            response = connection.getresponse()
            body = response.read()
            with self.state.lock:
                self.state.forwarded_reads += 1
                self.state.last_read_status = response.status

            self.send_response(response.status, response.reason)
            for name, value in response.getheaders():
                lower = name.lower()
                if lower not in HOP_BY_HOP and lower != "content-length":
                    self.send_header(name, value)
            self.send_header("Content-Length", str(len(body) if include_body else 0))
            self.end_headers()
            if include_body and body:
                self.wfile.write(body)
        except Exception:
            self._json(502, {"code": "READ_ONLY_PROXY_UPSTREAM_FAILURE"})
        finally:
            connection.close()

    def _json(self, status: int, value: dict[str, object]) -> None:
        body = json.dumps(value, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        if self.close_connection:
            self.send_header("Connection", "close")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)


def allowed_read(
    method: str,
    target: str,
    map_id: int,
    vehicle_key: str,
    dispatch_generation: int,
) -> bool:
    if method != "GET":
        return False
    parsed = urllib.parse.urlsplit(target)
    if parsed.scheme or parsed.netloc or parsed.fragment:
        return False
    if parsed.path == f"/api/imap/v1/mapInfo/stations/{map_id}":
        return not parsed.query
    if parsed.path == VEHICLE_PATH:
        try:
            query = urllib.parse.parse_qs(
                parsed.query, keep_blank_values=True, strict_parsing=True
            )
        except ValueError:
            return False
        return query == {"key": [vehicle_key]}
    if parsed.path.startswith(ORDER_PATH_PREFIX) and not parsed.query:
        encoded = parsed.path[len(ORDER_PATH_PREFIX) :]
        upper_id = urllib.parse.unquote(encoded)
        return (
            1 <= len(upper_id) <= 256
            and "/" not in upper_id
            and "\\" not in upper_id
            and upper_id.startswith("W2G-")
            and upper_id.endswith(f"-PICKUP-{dispatch_generation}")
            and urllib.parse.quote(upper_id, safe="-._~") == encoded
        )
    return False


def upstream_headers(incoming: object, riot_api_key: str) -> dict[str, str]:
    headers = {
        name: value
        for name, value in incoming.items()  # type: ignore[attr-defined]
        if name.lower() in FORWARDED_READ_HEADERS
    }
    headers["Authorization"] = f"Bearer {riot_api_key}"
    return headers


def authorized_client(incoming: object, client_token: str) -> bool:
    supplied = incoming.get("Authorization", "")  # type: ignore[attr-defined]
    return hmac.compare_digest(supplied, f"Bearer {client_token}")


def self_test() -> None:
    map_id = 25
    vehicle_key = "VEHICLE-KEY"
    assert allowed_read("GET", "/api/imap/v1/mapInfo/stations/25", map_id, vehicle_key, 1)
    assert allowed_read(
        "GET",
        "/api/task/vehicles/getVehicleInfoByDeviceKey?key=VEHICLE-KEY",
        map_id,
        vehicle_key,
        1,
    )
    assert allowed_read(
        "GET",
        "/api/order/v1/orderRecord/detailByUpperId/W2G-D-1-PICKUP-1",
        map_id,
        vehicle_key,
        1,
    )
    for method, target in (
        ("HEAD", "/api/imap/v1/mapInfo/stations/25"),
        ("GET", "/api/imap/v1/mapInfo/stations/26"),
        ("GET", "/api/imap/v1/mapInfo/stations/25?override=true"),
        ("GET", "/api/task/vehicles/getVehicleInfoByDeviceKey?key=OTHER"),
        ("GET", "/api/order/v1/orderRecord/detailByUpperId/OTHER"),
        ("GET", "/api/order/v1/orderRecord/detailByUpperId/W2G-X-PICKUP-1%2Fbad"),
        ("GET", "http://upstream/api/imap/v1/mapInfo/stations/25"),
    ):
        assert not allowed_read(method, target, map_id, vehicle_key, 1)
    headers = upstream_headers(
        {
            "Authorization": "Bearer attacker",
            "X-HTTP-Method-Override": "POST",
            "Accept": "application/json",
        },
        "real-key",
    )
    assert headers == {
        "Accept": "application/json",
        "Authorization": "Bearer real-key",
    }
    assert authorized_client({"Authorization": "Bearer dummy"}, "dummy")
    assert not authorized_client({"Authorization": "Bearer attacker"}, "dummy")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--listen-host", default="127.0.0.1")
    parser.add_argument("--listen-port", type=int)
    parser.add_argument("--upstream")
    parser.add_argument("--map-id", type=int)
    parser.add_argument("--vehicle-key")
    parser.add_argument("--dispatch-generation", type=int)
    args = parser.parse_args()
    if args.self_test:
        self_test()
        print('{"result":"PASS","credentialsIncluded":false}')
        return
    if (
        args.listen_port is None
        or args.upstream is None
        or args.map_id is None
        or not args.vehicle_key
        or args.dispatch_generation is None
        or args.dispatch_generation <= 0
    ):
        parser.error(
            "listen-port, upstream, map-id, vehicle-key, and positive dispatch-generation are required"
        )

    upstream = urllib.parse.urlsplit(args.upstream)
    if upstream.scheme != "http" or not upstream.hostname:
        raise SystemExit("Only an absolute HTTP upstream is supported.")
    riot_api_key = os.environ.get("AUTHORIZED_EXPERIMENT_RIOT_CALL_API_KEY", "")
    client_token = os.environ.get("AUTHORIZED_EXPERIMENT_PROXY_CLIENT_TOKEN", "")
    if not riot_api_key.strip():
        raise SystemExit("Proxy credential environment variable is missing.")
    if not client_token.strip():
        raise SystemExit("Proxy client-token environment variable is missing.")

    Proxy.state = State()
    Proxy.upstream_host = upstream.hostname
    Proxy.upstream_port = upstream.port or 80
    Proxy.map_id = args.map_id
    Proxy.vehicle_key = args.vehicle_key
    Proxy.dispatch_generation = args.dispatch_generation
    Proxy.client_token = client_token
    Proxy.riot_api_key = riot_api_key
    server = http.server.ThreadingHTTPServer(
        (args.listen_host, args.listen_port), Proxy
    )
    server.serve_forever()


if __name__ == "__main__":
    main()
