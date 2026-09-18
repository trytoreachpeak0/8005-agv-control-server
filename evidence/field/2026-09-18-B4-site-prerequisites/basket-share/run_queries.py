import sqlite3, sys, datetime, pathlib
db = pathlib.Path(sys.argv[1]).as_posix()
con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
def q(label, sql):
    print(f"== {label}\n{sql.strip()}")
    cur = con.execute(sql)
    print("\t".join(d[0] for d in cur.description))
    for r in cur.fetchall(): print("\t".join("" if v is None else str(v) for v in r))
    print()
print("executed_at", datetime.datetime.now().astimezone().isoformat(timespec="seconds"))
q("QC", "PRAGMA quick_check;")
q("Q0", "SELECT name, sql FROM sqlite_master WHERE type='table' AND name IN ('JourneyDemands','AcceptedDemands');")
q("QD", "SELECT COUNT(*) AS rows_, COUNT(DISTINCT DemandId) AS distinct_demands FROM JourneyDemands;")
q("QD2", "SELECT COUNT(*) AS demands_with_differing_counts FROM (SELECT DemandId FROM JourneyDemands GROUP BY DemandId HAVING COUNT(DISTINCT ExpectedBasketCount) > 1);")
q("Q1", """SELECT COUNT(*) AS journeyed_demands, MIN(a.AcceptedAt) AS window_start, MAX(a.AcceptedAt) AS window_end
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId) AS j
JOIN AcceptedDemands AS a ON a.DemandId = j.DemandId;""")
q("Q2", """SELECT json_extract(a.LiveMesFieldsJson, '$.Area') AS area, COUNT(*) AS total,
       SUM(CASE WHEN j.ExpectedBasketCount BETWEEN 5 AND 8 THEN 1 ELSE 0 END) AS basket_5_to_8,
       ROUND(100.0 * SUM(CASE WHEN j.ExpectedBasketCount BETWEEN 5 AND 8 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_5_to_8
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId) AS j
JOIN AcceptedDemands AS a ON a.DemandId = j.DemandId
GROUP BY area ORDER BY area;""")
q("Q2T", """SELECT COUNT(*) AS total,
       SUM(CASE WHEN j.ExpectedBasketCount BETWEEN 5 AND 8 THEN 1 ELSE 0 END) AS basket_5_to_8,
       ROUND(100.0 * SUM(CASE WHEN j.ExpectedBasketCount BETWEEN 5 AND 8 THEN 1 ELSE 0 END) / COUNT(*), 1) AS pct_5_to_8
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId) AS j
JOIN AcceptedDemands AS a ON a.DemandId = j.DemandId;""")
q("Q3", """SELECT ExpectedBasketCount AS baskets, COUNT(*) AS demands
FROM (SELECT DemandId, MAX(ExpectedBasketCount) AS ExpectedBasketCount FROM JourneyDemands GROUP BY DemandId)
GROUP BY ExpectedBasketCount ORDER BY ExpectedBasketCount;""")
q("Q4", """SELECT ReasonCode, COUNT(*) AS demands, MIN(FirstSeenAt) AS first_seen, MAX(LastSeenAt) AS last_seen
FROM JourneyBacklog
WHERE ReasonCode IN ('EXPECTED_BASKET_COUNT_OUT_OF_RANGE','SUBLOT_BOX_COUNT_UNAVAILABLE','SLOT_CAPACITY_TEMPORARILY_UNAVAILABLE')
GROUP BY ReasonCode;""")
