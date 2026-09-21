#Requires -Version 7
param([string]$Repo)
Import-Module (Join-Path $Repo 'scripts/DesktopLock.psm1')
$h = Enter-DesktopLock -Reason 'cs#218 UIA probe: can ItemStatus on a DataTemplate Grid be read (10 s window)' -TimeoutSeconds 0
try {
  $p = Start-Process pwsh -ArgumentList '-NoProfile','-NonInteractive','-STA','-File',(Join-Path $PSScriptRoot 'show.ps1') -PassThru
  & pwsh -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'read.ps1') -ProcessId $p.Id
  Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
} finally { Exit-DesktopLock $h }
