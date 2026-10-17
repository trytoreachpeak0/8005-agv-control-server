$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### JourneyDemands cols"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('JourneyDemands')"
"### JourneyDemands of agv03 journey"; & $R -Sql "SELECT jd.*, ad.Sublot, ad.Status, json_extract(ad.LiveMesFieldsJson,'$.area') AS area, json_extract(ad.LiveMesFieldsJson,'$.package') AS pkg FROM JourneyDemands jd LEFT JOIN AcceptedDemands ad ON ad.DemandId = jd.DemandId WHERE jd.JourneyId='journey:dadde6f1-9e2b-4ef1-9141-ca7f7b5ef328'"
"### agv03 journey slots"; & $R -Sql "SELECT ExpectedBasketCount, TargetSlotsJson, FullSlotPositionsJson, LoadingPhaseState, LoadingClosedReason, Stage FROM JourneyRuntimes WHERE JourneyId='journey:dadde6f1-9e2b-4ef1-9141-ca7f7b5ef328'"
"### JourneyStops"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('JourneyStops')"
