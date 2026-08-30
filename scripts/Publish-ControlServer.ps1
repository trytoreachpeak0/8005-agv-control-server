[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
$project = Join-Path $root 'src\ControlServer.Host\ControlServer.Host.csproj'
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)

if (Test-Path -LiteralPath $resolvedOutput) {
    throw "OutputPath already exists; use a new empty destination: $resolvedOutput"
}

$status = & git -C $root status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the source repository.' }
if ($status) { throw 'The source repository must be clean before publishing.' }

$sourceCommit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the source commit.'
}

& $dotnet restore $project --locked-mode --runtime $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnet publish $project --configuration Release --runtime $RuntimeIdentifier `
    --self-contained true --no-restore --output $resolvedOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$files = Get-ChildItem -LiteralPath $resolvedOutput -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($resolvedOutput, $_.FullName).Replace('\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

$manifest = [ordered]@{
    schemaVersion = 1
    product = '8005 AGV ControlServer'
    sourceCommit = $sourceCommit
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    files = @($files)
}
$manifestPath = Join-Path $resolvedOutput 'deployment-manifest.json'
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

Write-Output "Published ControlServer package: $resolvedOutput"
Write-Output "Source commit: $sourceCommit"
Write-Output "Manifest SHA-256: $((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())"

