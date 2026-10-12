"""Script mutations for cs#531: each applied to a scratch copy, Test-ParallelInstance.ps1 run, then restored."""
import re
import subprocess
import sys

ROOT = 'F:/8005-scratch/cs531-mut'
PSM = 'scripts/parallel/ParallelInstance.psm1'
INST = 'scripts/parallel/Install-ParallelInstanceLocal.ps1'
EVT = 'src/ControlServer.Host/Runtime/EffectiveConfigurationEvent.cs'
PROG = 'src/ControlServer.Host/Program.cs'
DEF = 'scripts/parallel/instance-factory01-v2.json'

MUTATIONS = [
    ('M1', 'whitelist: mapStationCatalog not a known top-level section', PSM,
     "'taskTypeStations', 'mapStationCatalog')", "'taskTypeStations')"),
    ('M2', 'both-or-neither check removed', PSM,
     "if (-not $hasSync -or -not $hasMax) {", "if ($false) {"),
    ('M3', 'format: TimeSpan''s own parser (accepts "30", days, fractions)', PSM,
     "if ($Value -isnot [string] -or $Value -notmatch '^\\d{2}:\\d{2}:\\d{2}$') { return $null }\n    [timespan] $span = [timespan]::Zero\n    if (-not [timespan]::TryParseExact($Value, 'hh\\:mm\\:ss', [cultureinfo]::InvariantCulture, [ref] $span)) { return $null }",
     "if ($Value -isnot [string]) { return $null }\n    [timespan] $span = [timespan]::Zero\n    if (-not [timespan]::TryParse($Value, [cultureinfo]::InvariantCulture, [ref] $span)) { return $null }"),
    ('M4', 'positive period check removed', PSM,
     "if ($catalogSpans.ContainsKey('approvedSyncPeriod') -and $catalogSpans['approvedSyncPeriod'] -le [timespan]::Zero) {",
     "if ($false) {"),
    ('M5', 'maximum equal to the period accepted (-le -> -lt)', PSM,
     "$catalogSpans['approvedMaxUnconfirmed'] -le $catalogSpans['approvedSyncPeriod']) {",
     "$catalogSpans['approvedMaxUnconfirmed'] -lt $catalogSpans['approvedSyncPeriod']) {"),
    ('M6', 'approvalRecord check removed', PSM,
     "if (-not $catalog.Contains('approvalRecord') -or $catalog['approvalRecord'] -isnot [string] -or",
     "if ($false -and"),
    ('M7', 'overlay: section written only when approved (no nulls to clear an earlier value)', PSM,
     "        MapStationCatalog = [ordered]@{\n            approvedSyncPeriod = $catalogApproval['approvedSyncPeriod']\n            approvedMaxUnconfirmed = $catalogApproval['approvedMaxUnconfirmed']\n        }",
     "        MapStationCatalog = [ordered]@{}\n"),
    ('M8', 'overlay: the raw definition section (approvalRecord leaks, values not normalised)', PSM,
     "    $catalogApproval = Get-ParallelMapStationCatalogApproval -Definition $Definition\n",
     "    $catalogApproval = Get-ParallelMapStationCatalogApproval -Definition $Definition\n    if ($Definition.Contains('mapStationCatalog')) { $catalogApproval['approvedSyncPeriod'] = $Definition['mapStationCatalog']['approvalRecord'] }\n"),
    ('M9', 'read-back: catalog comparison skipped', PSM,
     "    if ($null -ne $catalogBound) {\n        $catalogExpected", "    if ($false) {\n        $catalogExpected"),
    ('M10', 'read-back: a null bound value matches anything', PSM,
     "$same = ($null -eq $expected) ? ($null -eq $actual) : ($actual -is [string] -and $actual -ceq $expected)",
     "$same = ($null -eq $actual) -or (($null -eq $expected) ? $true : ($actual -is [string] -and $actual -ceq $expected))"),
    ('M11', 'read-back: the event field is never read', PSM,
     "MapStationCatalog = ($event.ContainsKey('MapStationCatalog') -and", "MapStationCatalog = ($false -and"),
    ('M12', 'read-back line: no warning when none is bound', PSM,
     "$warning = ($null -eq $bound.ApprovedSyncPeriod -and $null -eq $bound.ApprovedMaxUnconfirmed) ?",
     "$warning = $false ?"),
    ('M13', 'installer: the warning is not raised', INST,
     "        if ($catalog.Warning) { Write-Warning $catalog.Warning }\n", ""),
    ('M14', 'Host: mapStationCatalog dropped from the event template', EVT,
     " mapStationCatalog={MapStationCatalog}\";", "\";"),
    ('M14b', 'Host: the event placeholder renamed (compiles; the module no longer finds the template it reads)', EVT,
     " mapStationCatalog={MapStationCatalog}\";", " catalog={MapStationCatalog}\";"),
    ('M15', 'Program.cs logs a new MapStationCatalogOptions instead of the bound one', PROG,
     "EffectiveConfigurationEvent.Log(app.Logger, effective, catalog, ", "EffectiveConfigurationEvent.Log(app.Logger, effective, new MapStationCatalogOptions(), "),
    ('M16', 'shipped definition: a different maximum', DEF,
     '"approvedMaxUnconfirmed": "00:05:00"', '"approvedMaxUnconfirmed": "00:10:00"'),
]


def syntax_check(rel):
    path = f'{ROOT}/{rel}'
    if rel.endswith(('.ps1', '.psm1')):
        cmd = ("$e = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile('" + path +
               "', [ref] $null, [ref] $e); $e.Count")
        out = subprocess.run(['pwsh', '-NoProfile', '-Command', cmd], capture_output=True, text=True).stdout.strip()
        return 'ok' if out == '0' else f'parse errors: {out}'
    if rel.endswith('.json'):
        import json
        json.load(open(path, encoding='utf-8'))
        return 'ok'
    if rel.endswith('.cs'):
        proc = subprocess.run(['dotnet', 'build', f'{ROOT}/src/ControlServer.Host/ControlServer.Host.csproj', '-c', 'Debug',
                               '-v', 'q', '-nologo', '-clp:ErrorsOnly'], cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
        return 'ok' if proc.returncode == 0 else 'build failed: ' + (proc.stdout + proc.stderr)[-400:]
    return 'unknown file type'


def run_one(mid, desc, rel, old, new):
    path = f'{ROOT}/{rel}'
    raw = open(path, 'rb').read()
    text = raw.decode('utf-8')
    crlf = '\r\n' in text
    norm = text.replace('\r\n', '\n')
    if norm.count(old) != 1:
        return mid, desc, 'NOT-APPLIED', f'count={norm.count(old)}', []
    mutated = norm.replace(old, new)
    if crlf:
        mutated = mutated.replace('\n', '\r\n')
    open(path, 'wb').write(mutated.encode('utf-8'))
    try:
        check = syntax_check(rel)
        if check != 'ok':
            return mid, desc, 'INVALID-MUTANT', check, []
        proc = subprocess.run(['pwsh', '-NoProfile', '-File', f'{ROOT}/scripts/parallel/Test-ParallelInstance.ps1'],
                              capture_output=True, text=True, encoding='utf-8', errors='replace')
        out = re.sub(r'\x1b\[[0-9;]*m', '', proc.stdout + proc.stderr)
        summary = re.findall(r'(\d+) passed, (\d+) failed', out)
        fails = [l.strip()[6:] for l in out.splitlines() if l.strip().startswith('FAIL ')]
        return mid, desc, 'KILLED' if fails else 'SURVIVED', (summary[-1] if summary else out[-300:]), fails
    finally:
        open(path, 'wb').write(raw)


if __name__ == '__main__':
    wanted = sys.argv[1:]
    for m in MUTATIONS:
        if wanted and m[0] not in wanted:
            continue
        mid, desc, verdict, summary, fails = run_one(*m)
        print(f'{mid}\t{verdict}\t{summary}\t{desc}', flush=True)
        for f in fails:
            print(f'    {f}', flush=True)
