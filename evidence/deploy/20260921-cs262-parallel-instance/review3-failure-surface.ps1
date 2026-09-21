#Requires -Version 7
# control-server#262 S1 re-review, M1: how does each way a product script can fail show up to
# the caller? Measured before choosing how Invoke-ParallelProductUninstaller decides success.
# Each fake is an advanced script, like Uninstall-ControlServerLocal.ps1; the caller runs with
# ErrorActionPreference Stop, like the uninstaller.
$ErrorActionPreference = 'Stop'
$dir = Join-Path ([IO.Path]::GetTempPath()) "cs262-failure-surface-$PID"
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$fakes = [ordered]@{
    'silenced errors only (what a normal uninstall does)' = "[CmdletBinding()] param()`n`$ErrorActionPreference='Stop'`n`$null = Get-Service -Name 'no-such-service-cs262' -ErrorAction SilentlyContinue`ntry { throw 'handled' } catch { }`n'done'"
    'Write-Error under Continue'                          = "[CmdletBinding()] param()`n`$ErrorActionPreference='Continue'`nWrite-Error 'boom'`n'after'"
    'exit 1'                                              = "[CmdletBinding()] param()`nexit 1"
    'native command exits 3, last statement'              = "[CmdletBinding()] param()`n& cmd.exe /c exit 3"
    'native command exits 3, then more output'            = "[CmdletBinding()] param()`n& cmd.exe /c exit 3`n'more'"
    'throw'                                               = "[CmdletBinding()] param()`nthrow 'refused'"
}
try {
    '{0,-52} {1,-8} {2,-6} {3,-12} {4,-8} {5}' -f 'fake', 'threw', '$?', 'LASTEXITCODE', 'errvar', 'output'
    $i = 0
    foreach ($name in $fakes.Keys) {
        $path = Join-Path $dir "fake$i.ps1"; $i++
        Set-Content -LiteralPath $path -Value $fakes[$name]
        $global:LASTEXITCODE = 0; $ev = $null; $threw = '-'; $ok = '-'; $out = @()
        try { $out = @(& $path -ErrorVariable ev 2>$null); $ok = $? } catch { $threw = 'yes' }
        '{0,-52} {1,-8} {2,-6} {3,-12} {4,-8} {5}' -f $name, $threw, $ok, $LASTEXITCODE, @($ev).Count, ($out -join ',')
    }
    ''
    "pwsh $($PSVersionTable.PSVersion)"
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force
}
