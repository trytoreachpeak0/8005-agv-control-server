#Requires -Version 7
$ErrorActionPreference = 'Stop'
$file = Join-Path $PWD 'ParallelInstance.psm1'
$mutations = [ordered]@{
    'M1 generic work-type rule off' = @('} elseif (-not $exact) {', '} elseif ($false) {')
    'M2 WIRE_TO_GATE branch off' = @('if (@($listed | Where-Object { $_ -is [string] -and $_.Trim() -ieq $script:MvpWorkType }).Count -gt 0) {', 'if ($false) {')
    'M3 fakeMesIngest-absent rule off' = @("if (Test-KeyPresent -Node `$Definition -Key 'fakeMesIngest') {", 'if ($false) {')
    'M4 retire skips previous assert' = @('throw ("The previously installed definition was refused', '$null = ("The previously installed definition was refused')
    'M5 fake checks skipped' = @("if (`$mesSource -cne 'fake') {", 'if ($true) {')
    'M6 production baseUrl unchecked' = @('$baseUrl -cne $script:ProductionMesIngestBaseUrl', '$false')
    'M7 footprint keeps nameless task' = @('if ($null -ne $layout.TaskName) {', 'if ($true) {')
    'M8 source case-insensitive' = @('$script:MesIngestSources -ccontains $value', '$script:MesIngestSources -contains $value')
}
foreach ($name in $mutations.Keys) {
    $text = [IO.File]::ReadAllText($file)
    $from, $to = $mutations[$name]
    $n = ([regex]::Matches($text, [regex]::Escape($from))).Count
    if ($n -ne 1) { "$name : NOT APPLIED (matches=$n)"; continue }
    [IO.File]::WriteAllText($file, $text.Replace($from, $to), [Text.UTF8Encoding]::new($false))
    $out = & pwsh -NoProfile -File ./Test-ParallelInstance.ps1 2>&1
    $code = $LASTEXITCODE
    & git checkout -- ParallelInstance.psm1
    $summary = ($out | Select-Object -Last 1)
    $first = ($out | Where-Object { "$_" -match '^\s+FAIL' } | Select-Object -First 2) -join ' / '
    "$name : exit=$code $summary :: $first"
}
& git status --short
