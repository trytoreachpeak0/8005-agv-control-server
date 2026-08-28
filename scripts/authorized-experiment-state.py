#!/usr/bin/env python3
"""Extract and inspect a one-shot authorized experiment without disclosing identities."""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path


def rows(connection: sqlite3.Connection, sql: str, parameters: tuple = ()) -> list[sqlite3.Row]:
    return list(connection.execute(sql, parameters))


def count(connection: sqlite3.Connection, table: str) -> int:
    return int(connection.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0])


def identity_hash(*values: object) -> str:
    canonical = "|".join("" if value is None else str(value) for value in values)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def open_readonly(path: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    return connection


def extract(args: argparse.Namespace) -> None:
    shadow = open_readonly(args.shadow_db)
    production = open_readonly(args.production_db)
    try:
        intents = rows(
            shadow,
            """
            SELECT MovementLegId, DemandId, UpperId, Purpose, VehicleKey,
                   AgvLifecycleGeneration,
                   DispatchGeneration, Status, DispatchAuditVersion,
                   DispatchAuditSequence, OrderId, CreateAttemptId,
                   CreateAttemptCount, CreateDispatchArmedAt, LastCreateOutcome,
                   LastCreateOutcomeAt, LastCreateReceiptJson,
                   LastReconciliationOutcome, ExperimentalCreateAuthorizationId
            FROM OrderIntents
            """,
        )
        runtimes = rows(shadow, "SELECT DemandId, Stage FROM JourneyRuntimes")
        accepted = rows(shadow, "SELECT DemandId FROM AcceptedDemands")
        leases = rows(
            shadow,
            "SELECT DemandId, VehicleKey, ReleasedAt FROM VehicleDispatchLeases",
        )
        if (
            len(intents) != 1
            or len(runtimes) != 1
            or len(accepted) != 1
            or len(leases) != 1
            or count(shadow, "ExperimentalRiotCreateAuthorizations") != 0
            or count(shadow, "StationOperations") != 0
        ):
            raise SystemExit("Shadow run did not produce one closed pre-create journey state.")
        intent = intents[0]
        if intent["Purpose"] != "TO_PICKUP":
            raise SystemExit("The only shadow intent is not the pickup intent.")
        if (
            runtimes[0]["DemandId"] != intent["DemandId"]
            or accepted[0]["DemandId"] != intent["DemandId"]
            or leases[0]["DemandId"] != intent["DemandId"]
            or leases[0]["VehicleKey"] != intent["VehicleKey"]
            or leases[0]["ReleasedAt"] is not None
        ):
            raise SystemExit("Shadow demand, runtime, intent, and active lease identities differ.")
        if intent["Status"] != "RESULT_UNKNOWN":
            raise SystemExit("Shadow pickup intent did not fail closed as RESULT_UNKNOWN.")
        if intent["DispatchAuditVersion"] != 1:
            raise SystemExit("Shadow pickup intent is not on dispatch audit version 1.")
        if (
            intent["CreateAttemptCount"] != 0
            or intent["CreateAttemptId"] is not None
            or intent["OrderId"] is not None
            or intent["CreateDispatchArmedAt"] is not None
            or intent["LastCreateOutcome"] is not None
            or intent["LastCreateOutcomeAt"] is not None
            or intent["LastCreateReceiptJson"] is not None
        ):
            raise SystemExit("Shadow pass reached a Create attempt; no permit may be derived.")
        if intent["ExperimentalCreateAuthorizationId"] is not None:
            raise SystemExit("Shadow pass unexpectedly used an experimental authorization.")

        audit = rows(
            shadow,
            """
            SELECT MovementLegId, DemandId, UpperId, DispatchGeneration, Sequence,
                   AttemptId, AttemptNumber, Phase, Outcome, ReceiptOperation,
                   OccurredAt, RequestSemanticSha256, ReceiptClassification,
                   ReceiptObservedAt, HttpStatusCode, BusinessCode,
                   ResultPresent, ReturnedOrderId, FailureCategory,
                   ExperimentalAuthorizationId, EligibilityBasis
            FROM RiotDispatchAuditEvents ORDER BY Sequence
            """,
        )
        expected_reconciliation = (
            "PreCreateReconciliationUnknown"
            if len(audit) == 1
            else "PostCreateReconciliationUnknown"
        )
        exact_absent = len(audit) >= 1 and all(
            row["MovementLegId"] == intent["MovementLegId"]
            and row["DemandId"] == intent["DemandId"]
            and row["UpperId"] == intent["UpperId"]
            and row["DispatchGeneration"] == intent["DispatchGeneration"]
            and row["Sequence"] == index
            and row["AttemptId"] is None
            and row["AttemptNumber"] is None
            and row["OccurredAt"] is not None
            and row["RequestSemanticSha256"] is None
            and row["Phase"]
            == (
                "PRE_CREATE_RECONCILIATION"
                if index == 1
                else "POST_CREATE_RECONCILIATION"
            )
            and row["Outcome"] == "UNKNOWN"
            and row["ReceiptOperation"] == "RECONCILE"
            and row["ReceiptClassification"] == "AbsentAtObservation"
            and row["ReceiptObservedAt"] is not None
            and row["HttpStatusCode"] is None
            and row["BusinessCode"] is None
            and row["ResultPresent"] == 0
            and row["ReturnedOrderId"] is None
            and row["FailureCategory"] is None
            and row["ExperimentalAuthorizationId"] is None
            and row["EligibilityBasis"] is None
            for index, row in enumerate(audit, start=1)
        )
        if (
            not exact_absent
            or intent["DispatchAuditSequence"] != len(audit)
            or intent["LastReconciliationOutcome"] != expected_reconciliation
        ):
            raise SystemExit(
                "Shadow pass did not retain one PRE and only exact read-only POST absent audits."
            )

        overlap = production.execute(
            """
            SELECT
              EXISTS(SELECT 1 FROM AcceptedDemands WHERE DemandId = ?) OR
              EXISTS(SELECT 1 FROM OrderIntents WHERE DemandId = ? OR UpperId = ? OR MovementLegId = ?)
            """,
            (
                intent["DemandId"],
                intent["DemandId"],
                intent["UpperId"],
                intent["MovementLegId"],
            ),
        ).fetchone()[0]
        if overlap:
            raise SystemExit("Shadow selection overlaps an existing production demand or intent.")

        selection_hash = identity_hash(
            intent["DemandId"],
            intent["UpperId"],
            intent["MovementLegId"],
            intent["AgvLifecycleGeneration"],
            intent["DispatchGeneration"],
        )
        private = {
            "schemaVersion": 1,
            "demandId": intent["DemandId"],
            "upperId": intent["UpperId"],
            "movementLegId": intent["MovementLegId"],
            "agvLifecycleGeneration": intent["AgvLifecycleGeneration"],
            "dispatchGeneration": intent["DispatchGeneration"],
            "selectionIdentitySha256": selection_hash,
        }
        sanitized = {
            "schemaVersion": 1,
            "result": "PASS",
            "observedAt": datetime.now(timezone.utc).isoformat(),
            "shadowRuntimeCount": len(runtimes),
            "shadowPickupIntentCount": len(intents),
            "shadowAcceptedDemandCount": len(accepted),
            "shadowActiveLeaseCount": len(leases),
            "shadowAuditEventCount": len(audit),
            "shadowPostCreateReconciliationCount": len(audit) - 1,
            "shadowCreateAttemptCount": 0,
            "exactAbsentAtObservation": True,
            "productionIdentityOverlap": False,
            "productionAcceptedDemandCount": count(production, "AcceptedDemands"),
            "productionActiveLeaseCount": int(
                production.execute(
                    "SELECT COUNT(*) FROM VehicleDispatchLeases WHERE ReleasedAt IS NULL"
                ).fetchone()[0]
            ),
            "selectionIdentitySha256": selection_hash,
            "rawIdentityIncluded": False,
        }
        write_json(args.private_out, private)
        write_json(args.sanitized_out, sanitized)
    finally:
        shadow.close()
        production.close()


def inspect(args: argparse.Namespace) -> None:
    permit = json.loads(args.permit.read_text(encoding="utf-8"))
    connection = open_readonly(args.database)
    try:
        intents = rows(connection, "SELECT * FROM OrderIntents WHERE Purpose = 'TO_PICKUP'")
        runtimes = rows(connection, "SELECT * FROM JourneyRuntimes")
        permits = rows(connection, "SELECT * FROM ExperimentalRiotCreateAuthorizations")
        audits = rows(connection, "SELECT * FROM RiotDispatchAuditEvents ORDER BY Sequence")
        matching = [
            row
            for row in intents
            if row["DemandId"] == permit["demandId"]
            and row["UpperId"] == permit["upperId"]
            and row["MovementLegId"] == permit["movementLegId"]
            and row["AgvLifecycleGeneration"] == permit["agvLifecycleGeneration"]
            and row["DispatchGeneration"] == permit["dispatchGeneration"]
        ]
        intent = matching[0] if len(matching) == 1 else None
        runtime = runtimes[0] if len(runtimes) == 1 else None
        permit_row = permits[0] if len(permits) == 1 else None
        result = {
            "schemaVersion": 1,
            "observedAt": datetime.now(timezone.utc).isoformat(),
            "runtimeCount": len(runtimes),
            "pickupIntentCount": len(intents),
            "permitCount": len(permits),
            "identityMatchesPermit": intent is not None,
            "runtimeStage": None if runtime is None else runtime["Stage"],
            "runtimeBlockReason": None if runtime is None else runtime["BlockReasonCode"],
            "intentStatus": None if intent is None else intent["Status"],
            "orderConfirmed": bool(intent is not None and intent["OrderId"]),
            "createAttemptCount": 0 if intent is None else (intent["CreateAttemptCount"] or 0),
            "permitPersisted": bool(
                permit_row is not None
                and permit_row["AuthorizationId"] == permit["authorizationId"]
            ),
            "permitConsumed": bool(permit_row is not None and permit_row["ConsumedAt"]),
            "preCount": sum(row["Phase"] == "PRE_CREATE_RECONCILIATION" for row in audits),
            "armCount": sum(row["Phase"] == "CREATE_DISPATCH" for row in audits),
            "startCount": sum(row["Phase"] == "CREATE_REQUEST" for row in audits),
            "responseCount": sum(row["Phase"] == "CREATE_RESPONSE" for row in audits),
            "auditEventCount": len(audits),
            "operationCount": count(connection, "StationOperations"),
            "rawIdentityIncluded": False,
        }
        write_json(args.out, result)
    finally:
        connection.close()


def grouped(connection: sqlite3.Connection, sql: str) -> list[dict[str, object]]:
    return [dict(row) for row in connection.execute(sql)]


def diagnose(args: argparse.Namespace) -> None:
    connection = open_readonly(args.database)
    try:
        result = {
            "schemaVersion": 1,
            "observedAt": datetime.now(timezone.utc).isoformat(),
            "counts": {
                table: count(connection, table)
                for table in (
                    "AcceptedDemands",
                    "JourneyBacklog",
                    "JourneyRuntimes",
                    "OrderIntents",
                    "RiotDispatchAuditEvents",
                    "SessionRecoveries",
                    "VehicleDispatchLeases",
                    "StationOperations",
                    "ProtocolInbox",
                    "ExperimentalRiotCreateAuthorizations",
                )
            },
            "sessions": grouped(
                connection,
                """
                SELECT SessionGeneration AS sessionGeneration,
                       Readiness AS readiness,
                       ReasonCode AS reasonCode,
                       DepartureSafe AS departureSafe,
                       COUNT(*) AS count
                FROM SessionRecoveries
                GROUP BY SessionGeneration, Readiness, ReasonCode, DepartureSafe
                ORDER BY SessionGeneration, Readiness, ReasonCode
                """,
            ),
            "backlog": grouped(
                connection,
                """
                SELECT ReasonCode AS reasonCode, COUNT(*) AS count
                FROM JourneyBacklog
                GROUP BY ReasonCode
                ORDER BY ReasonCode
                """,
            ),
            "runtimes": grouped(
                connection,
                """
                SELECT Stage AS stage, BlockReasonCode AS blockReasonCode,
                       COUNT(*) AS count
                FROM JourneyRuntimes
                GROUP BY Stage, BlockReasonCode
                ORDER BY Stage, BlockReasonCode
                """,
            ),
            "intents": grouped(
                connection,
                """
                SELECT Purpose AS purpose, Status AS status,
                       LastCreateOutcome AS lastCreateOutcome,
                       LastReconciliationOutcome AS lastReconciliationOutcome,
                       SUM(COALESCE(CreateAttemptCount, 0)) AS createAttemptCount,
                       COUNT(*) AS count
                FROM OrderIntents
                GROUP BY Purpose, Status, LastCreateOutcome,
                         LastReconciliationOutcome
                ORDER BY Purpose, Status
                """,
            ),
            "audits": grouped(
                connection,
                """
                SELECT Phase AS phase, Outcome AS outcome,
                       ReceiptOperation AS receiptOperation,
                       ReceiptClassification AS receiptClassification,
                       FailureCategory AS failureCategory,
                       COUNT(*) AS count
                FROM RiotDispatchAuditEvents
                GROUP BY Phase, Outcome, ReceiptOperation,
                         ReceiptClassification, FailureCategory
                ORDER BY Phase, Outcome, ReceiptOperation,
                         ReceiptClassification, FailureCategory
                """,
            ),
            "rawIdentityIncluded": False,
        }
        if args.out is None:
            print(json.dumps(result, ensure_ascii=False, indent=2))
        else:
            write_json(args.out, result)
    finally:
        connection.close()


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(required=True)

    extract_parser = subparsers.add_parser("extract")
    extract_parser.add_argument("--shadow-db", type=Path, required=True)
    extract_parser.add_argument("--production-db", type=Path, required=True)
    extract_parser.add_argument("--private-out", type=Path, required=True)
    extract_parser.add_argument("--sanitized-out", type=Path, required=True)
    extract_parser.set_defaults(function=extract)

    inspect_parser = subparsers.add_parser("inspect")
    inspect_parser.add_argument("--database", type=Path, required=True)
    inspect_parser.add_argument("--permit", type=Path, required=True)
    inspect_parser.add_argument("--out", type=Path, required=True)
    inspect_parser.set_defaults(function=inspect)

    diagnose_parser = subparsers.add_parser("diagnose")
    diagnose_parser.add_argument("--database", type=Path, required=True)
    diagnose_parser.add_argument("--out", type=Path)
    diagnose_parser.set_defaults(function=diagnose)

    arguments = parser.parse_args()
    arguments.function(arguments)


if __name__ == "__main__":
    main()
