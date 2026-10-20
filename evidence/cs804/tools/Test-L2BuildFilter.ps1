#Requires -Version 7
# The l2.yml build step's filter lines (control-server#804), run as written, against a repository checkout.
param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Out)
Set-Location -LiteralPath $Repository
$l2Projects = @([regex]::Matches((Get-Content -Raw -LiteralPath ./ControlServer.sln), '"([^"]+\.csproj)"') |
    ForEach-Object { $_.Groups[1].Value } | Where-Object { -not $_.StartsWith('tests\') })
if (@($l2Projects | Where-Object { $_ -ceq 'src\ControlServer.Host\ControlServer.Host.csproj' }).Count -ne 1) { throw "no host: $($l2Projects -join ', ')" }
$l2Filter = Join-Path $Out 'l2-build.slnf'
@{ solution = @{ path = (Resolve-Path ./ControlServer.sln).Path; projects = $l2Projects } } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $l2Filter -Encoding utf8NoBOM
"Building $($l2Projects.Count) projects: $($l2Projects -join ', ')"
$clock = [Diagnostics.Stopwatch]::StartNew()
& dotnet build $l2Filter -c Release --nologo -bl:(Join-Path $Out 'l2-build.binlog') | Set-Content -LiteralPath (Join-Path $Out 'l2-build.log')
"dotnet build exit $LASTEXITCODE in $([Math]::Round($clock.Elapsed.TotalSeconds))s"
"log lines naming ControlServer.Tests: $(@(Select-String -LiteralPath (Join-Path $Out 'l2-build.log') -Pattern 'ControlServer\.Tests').Count)"
