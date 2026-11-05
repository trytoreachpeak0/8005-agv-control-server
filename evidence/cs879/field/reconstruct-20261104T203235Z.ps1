#Requires -Version 7
# control-server#879: replays the 20:32:35 UTC round of journey 8f62304a with the mechanism the fix removes, and compares the
# result with the stop sequences backed up before the 11-05 repair (stops-before-repair.txt). Pure arithmetic, no database.
#
# S0: the sequences after the 20:31:54 plan (rev 8816): P1 done, P2 current, then P3, N6-5, N6-11, N4-1, N2-2, N2-4 open,
#     and the four stops removed earlier after them, newest first (each revision puts the stop it removes right after the open
#     ones and the older removed ones after that: N6-10 at 20:31:54, N2-5 at 20:31:08, N2-1 at 20:30:20, N1-10 at 20:28:12).
# A:  the onboard connection ends bb32 (N6-11): JourneyPlanRevision removes it, renumbers the open stops from the current one,
#     puts N6-11 after them and the earlier removed ones after that. Committed.
# P:  the dispatch round plans the 9974 append (N6-4 new, after P3) from the database as it is after A (cursor, no tracking).
# W:  the round's context had tracked every stop of the journey at S0 earlier in the round. ApplyResequencingAsync writes P's
#     sequence into those tracked instances; EF writes a column only when the new value differs from the tracked original (S0).
$order0 = 'P1','P2','P3','N6-5','N6-11','N4-1','N2-2','N2-4','N6-10','N2-5','N2-1','N1-10'
$S0 = [ordered]@{}; for ($i = 0; $i -lt $order0.Count; $i++) { $S0[$order0[$i]] = $i + 1 }
$removedBefore = 'N6-10','N2-5','N2-1','N1-10'
# A: done below current kept; open from current (2) in order; N6-11 (removed now) next; earlier removed after it.
$A = [ordered]@{ 'P1' = 1 }; $n = 2
foreach ($s in 'P2','P3','N6-5','N4-1','N2-2','N2-4') { $A[$s] = $n++ }
$A['N6-11'] = $n++; foreach ($s in $removedBefore) { $A[$s] = $n++ }
# P: completed prefix + open stops with N6-4 inserted after P3, then the trailing removed ones (EnRoutePlanOf + planner).
$P = [ordered]@{}; $n = 1
foreach ($s in 'P1','P2','P3','N6-4','N6-5','N4-1','N2-2','N2-4') { $P[$s] = $n++ }
foreach ($s in @('N6-11') + $removedBefore) { $P[$s] = $n++ }
# W: database after A, then P written through instances tracked at S0 (N6-4 is new and always inserted).
$db = [ordered]@{}; foreach ($k in $A.Keys) { $db[$k] = $A[$k] }
foreach ($k in $P.Keys) { if (-not $S0.Contains($k) -or $P[$k] -ne $S0[$k]) { $db[$k] = $P[$k] } }
$observed = [ordered]@{ 'P1'=1; 'P2'=2; 'P3'=3; 'N6-4'=4; 'N4-1'=5; 'N6-5'=5; 'N2-2'=6; 'N2-4'=7; 'N6-11'=9; 'N6-10'=10; 'N2-5'=11; 'N2-1'=12; 'N1-10'=13 }
"{0,-6} {1,3} {2,3} {3,3} {4,6} {5,8}" -f 'stop','S0','A','P','result','observed'
$all = $true
foreach ($k in $observed.Keys) {
  $ok = $db[$k] -eq $observed[$k]; $all = $all -and $ok
  "{0,-6} {1,3} {2,3} {3,3} {4,6} {5,8} {6}" -f $k, $S0[$k], $A[$k], $P[$k], $db[$k], $observed[$k], ($ok ? '' : '<-- differs')
}
"all 13 stops match the backup: $all"
