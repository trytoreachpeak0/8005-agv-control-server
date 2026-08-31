#Requires -Version 7
# Ticket 07: build the two onboard peers needed for the cross-machine plaintext run.
# The onboard repository is read-only for agents, so every build happens in a throwaway
# clone under F:\w2g-ticket07 and nothing is written to C:\Users\szy\Desktop\8005-agv-onboard-hmi.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$stage = 'F:\w2g-ticket07'
$srcRoot = Join-Path $stage 'src'
$pubRoot = Join-Path $stage 'publish'
$logRoot = Join-Path $stage 'logs'
foreach ($d in @($srcRoot, $pubRoot, $logRoot)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$onboardSource = Join-Path $srcRoot 'onboard-hmi'
$upstream = 'C:\Users\szy\Desktop\8005-agv-onboard-hmi'

$plaintextCommit = '238b46eb2c9ae90584e4288a782176f66b7de942'
$tlsCommit = '31263b1'

if (-not (Test-Path -LiteralPath $onboardSource)) {
    & git -c core.autocrlf=false clone --no-hardlinks --no-checkout $upstream $onboardSource 2>&1 |
        Tee-Object -FilePath (Join-Path $logRoot 'clone-onboard.log')
    if ($LASTEXITCODE -ne 0) { throw 'clone failed' }
    & git -C $onboardSource config core.autocrlf false
}

function Publish-Onboard {
    param([string]$Commit, [string]$OutName)

    & git -C $onboardSource checkout --detach $Commit 2>&1 |
        Tee-Object -FilePath (Join-Path $logRoot "checkout-$OutName.log")
    if ($LASTEXITCODE -ne 0) { throw "checkout $Commit failed" }
    $actual = (& git -C $onboardSource rev-parse HEAD).Trim()
    $status = @(& git -C $onboardSource status --porcelain)
    if ($status.Count -ne 0) { throw "worktree not clean at $Commit" }
    "checked out $OutName at $actual"

    $out = Join-Path $pubRoot $OutName
    if (Test-Path -LiteralPath $out) { Remove-Item -Recurse -Force -LiteralPath $out }
    & dotnet publish (Join-Path $onboardSource 'src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj') `
        -c Release -r win-x64 --self-contained true -o $out 2>&1 |
        Tee-Object -FilePath (Join-Path $logRoot "publish-$OutName.log") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish $OutName failed" }
    $exe = Join-Path $out 'SQCD.Agv.Wpf.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "no exe produced for $OutName" }
    "published $OutName -> $out ($([math]::Round(((Get-ChildItem $out -Recurse | Measure-Object Length -Sum).Sum/1MB),1)) MB), HEAD=$actual"
}

Publish-Onboard -Commit $plaintextCommit -OutName 'onboard-plaintext-238b46e'
Publish-Onboard -Commit $tlsCommit -OutName 'onboard-tls-31263b1'

'--- upstream repository must be untouched ---'
"upstream status: " + (@(& git -C $upstream status --porcelain).Count) + " changed paths"
"upstream HEAD:   " + (& git -C $upstream rev-parse HEAD).Trim()
'DONE'
