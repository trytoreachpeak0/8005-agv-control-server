"""Ticket 07 support: a minimal stand-in for the RIoT backend.

The cross-machine assertion of ticket 07 is about the ONBOARD<->CONTROLSERVER transport.
RIoT sits upstream of the ControlServer and is not part of either link under test, but the
vehicle-safety projection is fail-closed: with no reachable RIoT it can only answer UNKNOWN,
so the onboard session can never leave RecoveryRequired and the ticket's Ready assertion
cannot be exercised at all.

This server answers the exact Round-41 predicate the projection requires (a stopped, idle,
enabled, online vehicle with no non-final orders). Every request is logged with its path so
the SDK's real routes are discovered rather than guessed.
"""

import json
import sys
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG_PATH = sys.argv[2] if len(sys.argv) > 2 else "fake-riot.log"

SAFE_VEHICLE = {
    "vehicle": {
        "movementState": "MT_FINISHED",
        "controlState": "CONTROL_STATE_OK",
        "emergencyState": "OK",
        "breakSwitchState": "MOVABLE",
        "locationState": "LOCATION_STATE_RUNNING",
        "speed": 0,
    },
    "vehicleTaskInfo": {
        "key": None,  # filled in per request from the path
        "procState": "IDLE",
        "processingOrder": False,
        "enable": True,
        "integrationLevel": "ON_LINE",
    },
}

EMPTY_ORDER_PAGE = {
    "code": "0",
    "result": {"current": 1, "size": 100, "total": 0, "records": []},
}


def log(line):
    stamp = datetime.now(timezone.utc).isoformat()
    with open(LOG_PATH, "a", encoding="utf-8") as handle:
        handle.write(f"{stamp}\t{line}\n")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def _respond(self, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _handle(self, method):
        path = self.path
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode("utf-8") if length else ""
        log(f"{method} {path} body={body}")

        if "getVehicleInfo" in path:
            vehicle_key = path.rstrip("/").rsplit("/", 1)[-1]
            payload = json.loads(json.dumps(SAFE_VEHICLE))
            payload["vehicleTaskInfo"]["key"] = vehicle_key
            self._respond(payload)
            return

        # Everything else the projection touches is the non-final order listing; an empty,
        # fully covered page is what "no order is holding this vehicle" looks like.
        self._respond(EMPTY_ORDER_PAGE)

    def do_GET(self):
        self._handle("GET")

    def do_POST(self):
        self._handle("POST")

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 58888
    log(f"fake RIoT listening on 127.0.0.1:{port}")
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
