#Requires -Version 7
# First trip (C11 approved 2026-10-14 20:2x): import the two-area table, poll AcceptedDemands read-only every 2 s until both
# chosen sublots are accepted (max 300 s), then import the header-only table to stop intake. Reads back at the end.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$dir = 'D:\zhengyushao\control-server-v2-ops\prod-20261014'
$want = 'Q26096572-7', 'Q26097573-2'
& "$dir\import-area.ps1" -Name area-first-trip-N2-4-N4-1.csv -ExpectedSha256 ED5234A349E53CFD0B7A1A1E5026BC06563D067C24620DECDF629B3C4550E536 -Commit
$deadline = (Get-Date).AddSeconds(300)
do {
  Start-Sleep -Seconds 2
  $rows = @(& "$dir\ro-sql.ps1" -Sql 'SELECT Sublot, WorkType, Status, AcceptedAt FROM AcceptedDemands')
  $have = @($want | Where-Object { $s = $_; $rows | Where-Object { $_ -match "Sublot=$([regex]::Escape($s)) " } })
} until ($have.Count -eq $want.Count -or (Get-Date) -gt $deadline)
"### accepted at $(Get-Date -Format o) ($($have.Count)/$($want.Count))"; $rows
& "$dir\import-area.ps1" -Name area-header-only.csv -ExpectedSha256 4181C5F2173592D2AEB559ECD63D9846103F6061FFA0188835050DA8CF6810D0 -Commit
"### accepted after stop $(Get-Date -Format o)"; & "$dir\ro-sql.ps1" -Sql 'SELECT Sublot, WorkType, Status, AcceptedAt FROM AcceptedDemands'
