#Requires -Version 7
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "cs262-links-$PID"
function New-Target($name) { $t = Join-Path $root $name; New-Item -ItemType Directory $t -Force | Out-Null; Set-Content (Join-Path $t 'precious.txt') 'x'; $t }
function Report($label, $target, $link) {
  "{0,-58} target file survives={1}  link gone={2}" -f $label, (Test-Path (Join-Path $target 'precious.txt')), (-not (Test-Path -LiteralPath $link))
}
New-Item -ItemType Directory $root -Force | Out-Null
try {
  # 1. a junction INSIDE a V2 directory, removed with Remove-Item -Recurse -Force on the parent
  $t1 = New-Target 'mvp1'; $v1 = Join-Path $root 'ControlServer.V2'; New-Item -ItemType Directory $v1 | Out-Null
  $l1 = Join-Path $v1 'inner'; New-Item -ItemType Junction -Path $l1 -Target $t1 | Out-Null
  Remove-Item -LiteralPath $v1 -Recurse -Force
  Report '1 junction inside, Remove-Item -Recurse on parent' $t1 $v1
  # 2. the V2 directory itself is a junction
  $t2 = New-Target 'mvp2'; $l2 = Join-Path $root 'ControlServer.V2.previous'
  New-Item -ItemType Junction -Path $l2 -Target $t2 | Out-Null
  Remove-Item -LiteralPath $l2 -Recurse -Force
  Report '2 target itself a junction, Remove-Item -Recurse' $t2 $l2
  # 3. nested two levels deep
  $t3 = New-Target 'mvp3'; $v3 = Join-Path $root 'control-server-v2-staging\a\b'; New-Item -ItemType Directory $v3 -Force | Out-Null
  New-Item -ItemType Junction -Path (Join-Path $v3 'deep') -Target $t3 | Out-Null
  Remove-Item -LiteralPath (Join-Path $root 'control-server-v2-staging') -Recurse -Force
  Report '3 junction two levels down' $t3 (Join-Path $root 'control-server-v2-staging')
  # 4. directory symbolic link inside (needs privilege or developer mode)
  $t4 = New-Target 'mvp4'; $v4 = Join-Path $root 'ControlServer.V2.FakeMesIngest'; New-Item -ItemType Directory $v4 | Out-Null
  try {
    New-Item -ItemType SymbolicLink -Path (Join-Path $v4 'sym') -Target $t4 | Out-Null
    Remove-Item -LiteralPath $v4 -Recurse -Force
    Report '4 directory symlink inside' $t4 $v4
  } catch { "4 directory symlink inside: could not create ($($_.Exception.Message))" }
  "attributes of a junction: " + (Get-Item -LiteralPath (New-Item -ItemType Junction -Path (Join-Path $root 'probe') -Target $t1).FullName -Force).Attributes
  "pwsh $($PSVersionTable.PSVersion)"
} finally {
  # Links first, by removing only the link object, then everything else.
  Get-ChildItem -LiteralPath $root -Recurse -Force -Attributes ReparsePoint -ErrorAction SilentlyContinue | ForEach-Object { [IO.Directory]::Delete($_.FullName) }
  [IO.Directory]::Delete($root, $true)
}
