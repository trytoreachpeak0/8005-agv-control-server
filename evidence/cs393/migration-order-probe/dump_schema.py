"""Dump a SQLite database's schema and EF migration history as canonical JSON.

Used by Invoke-MigrationOrderProbe.ps1 (control-server#393). Reads only; opens the file read-only.
Usage: python -I dump_schema.py <db path> <out json>
"""
import json
import sqlite3
import sys

db, out = sys.argv[1], sys.argv[2]
con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
cur = con.cursor()

objects = cur.execute(
    "SELECT type, name, tbl_name, sql FROM sqlite_master "
    "WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name").fetchall()
tables = [name for (kind, name, _, _) in objects if kind == "table"]

schema = {"objects": [list(o) for o in objects], "tables": {}}
for table in tables:
    quoted = '"' + table.replace('"', '""') + '"'
    columns = cur.execute(f"PRAGMA table_xinfo({quoted})").fetchall()
    indexes = cur.execute(f"PRAGMA index_list({quoted})").fetchall()
    index_detail = {}
    for index in indexes:
        index_name = index[1]
        quoted_index = '"' + index_name.replace('"', '""') + '"'
        index_detail[index_name] = {
            "list": list(index[2:]),
            "columns": [list(c) for c in cur.execute(f"PRAGMA index_xinfo({quoted_index})").fetchall()],
        }
    foreign_keys = cur.execute(f"PRAGMA foreign_key_list({quoted})").fetchall()
    schema["tables"][table] = {
        "columns": [list(c) for c in columns],
        "indexes": index_detail,
        "foreignKeys": [list(f) for f in foreign_keys],
    }

history = []
applied_order = []
if "__EFMigrationsHistory" in tables:
    history = [list(r) for r in cur.execute(
        'SELECT MigrationId, ProductVersion FROM "__EFMigrationsHistory" ORDER BY MigrationId').fetchall()]
    # Insertion order: the order the migrations were actually applied in. The history table has no
    # timestamp, so rowid is the only witness of it.
    applied_order = [r[0] for r in cur.execute(
        'SELECT MigrationId FROM "__EFMigrationsHistory" ORDER BY rowid').fetchall()]

con.close()
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump({"schema": schema, "migrationHistory": history, "appliedOrder": applied_order},
              f, ensure_ascii=False, indent=1, sort_keys=True)
