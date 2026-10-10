#Requires -Version 7
# Does a non-zero native exit early in a script survive to the caller's $LASTEXITCODE when a later
# native command in the same script exits 0? Mirrors Uninstall-ControlServerLocal.ps1: sc.exe
# delete (line 75) runs before netstat.exe (line 126), and the result file is written after both.
$ErrorActionPreference = 'Stop'
$dir = Join-Path ([IO.Path]::GetTempPath()) "cs262-1072-$PID"
New-Item -ItemType Directory -Path $dir | Out-Null
try {
    $fake = Join-Path $dir 'fake.ps1'
    Set-Content -LiteralPath $fake -Value @(
        '[CmdletBinding()] param()'
        "`$ErrorActionPreference = 'Stop'"
        '& cmd.exe /c exit 1072 | Out-Null'
        '"after sc.exe-like: $LASTEXITCODE"'
        "`$netstat = @(& `"`$env:SystemRoot\System32\netstat.exe`" -ano)"
        '"after netstat: $LASTEXITCODE"'
    )
    $global:LASTEXITCODE = 0
    $out = & $fake
    $out
    "caller sees LASTEXITCODE = $global:LASTEXITCODE"
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force
}
