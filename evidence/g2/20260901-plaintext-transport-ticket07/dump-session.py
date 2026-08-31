"""Read the ControlServer session projection. The server log only carries EF SQL, so the
session's readiness has to be read from the derived rows, not grepped out of the log."""

import json
import sqlite3
import sys

path = sys.argv[1]
connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
connection.row_factory = sqlite3.Row

names = [r[0] for r in connection.execute(
    "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
print("tables:", ", ".join(names))
print()

for table in ("OnboardSessions", "Sessions", "SessionRecoveries", "ProtocolInbox"):
    if table not in names:
        continue
    rows = list(connection.execute(f'SELECT * FROM "{table}"'))
    print(f"==== {table}: {len(rows)} row(s) ====")
    for row in rows[-6:]:
        data = {k: row[k] for k in row.keys()}
        if table == "ProtocolInbox":
            data = {k: data[k] for k in ("MessageId", "MessageType", "ReceivedAt")}
        print(json.dumps(data, ensure_ascii=False, indent=2, default=str))
    print()

if "ProtocolInbox" in names:
    print("==== ProtocolInbox message types ====")
    for row in connection.execute(
            'SELECT "MessageType", COUNT(*) AS n FROM "ProtocolInbox" GROUP BY "MessageType"'):
        print(f"{row['MessageType']}: {row['n']}")
