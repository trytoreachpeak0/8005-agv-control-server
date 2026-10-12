"""C# test-side mutations for cs#531: mutate the scratch copy, run the targeted VSTest class, restore."""
import re
import subprocess
import sys

ROOT = 'F:/8005-scratch/cs531-mut'
HEAVY = 'C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1'

MUTATIONS = [
    ('C1', 'Host logs the approved values as total seconds instead of hh:mm:ss',
     'src/ControlServer.Host/Runtime/EffectiveConfigurationEvent.cs',
     '["ApprovedSyncPeriod"] = catalog.ApprovedSyncPeriod?.ToString("c", CultureInfo.InvariantCulture),',
     '["ApprovedSyncPeriod"] = catalog.ApprovedSyncPeriod?.TotalSeconds.ToString(CultureInfo.InvariantCulture),'),
    ('C2', 'overlay writes MapStationCatalog only when approved (same as M7), seen through .NET binding',
     'scripts/parallel/ParallelInstance.psm1',
     "        MapStationCatalog = [ordered]@{\n            approvedSyncPeriod = $catalogApproval['approvedSyncPeriod']\n            approvedMaxUnconfirmed = $catalogApproval['approvedMaxUnconfirmed']\n        }",
     "        MapStationCatalog = [ordered]@{}\n"),
]


def run(mid, desc, rel, old, new):
    path = f'{ROOT}/{rel}'
    raw = open(path, 'rb').read()
    text = raw.decode('utf-8-sig')
    bom = raw[:3] == b'\xef\xbb\xbf'
    crlf = '\r\n' in text
    norm = text.replace('\r\n', '\n')
    if norm.count(old) != 1:
        print(f'{mid}\tNOT-APPLIED\tcount={norm.count(old)}\t{desc}', flush=True)
        return
    mutated = norm.replace(old, new)
    if crlf:
        mutated = mutated.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + mutated.encode('utf-8'))
    try:
        if rel.endswith('.psm1'):
            cmd = ("$e = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile('" + path +
                   "', [ref] $null, [ref] $e); $e.Count")
            parse = subprocess.run(['pwsh', '-NoProfile', '-Command', cmd], capture_output=True, text=True).stdout.strip()
            if parse != '0':
                print(f'{mid}\tINVALID-MUTANT\tparse errors {parse}\t{desc}', flush=True)
                return
        proc = subprocess.run(['pwsh', '-File', HEAVY, '-Ticket', 'cs#531', 'dotnet', 'test', f'{ROOT}/tests/ControlServer.Tests',
                               '--filter', 'FullyQualifiedName~ParallelInstanceEffectiveConfigurationTests'], cwd=ROOT,
                              capture_output=True, text=True, encoding='utf-8', errors='replace')
        out = proc.stdout + proc.stderr
        if re.search(r'error [A-Z]+\d+', out) or 'Build FAILED' in out or not re.search(r'(Passed!|Failed!)\s+- Failed:', out):
            print(f'{mid}\tINVALID-MUTANT\tbuild failed\t{desc}', flush=True)
            return
        summary = re.findall(r'(Passed!|Failed!)\s+- Failed:\s+(\d+), Passed:\s+(\d+)', out)
        failed = sorted(set(re.findall(r'^\s+Failed (ControlServer\.Tests\.\S+)', out, re.M)))
        verdict = 'KILLED' if failed else 'SURVIVED'
        print(f'{mid}\t{verdict}\t{summary[-1] if summary else out[-300:]}\t{desc}', flush=True)
        for f in failed:
            print(f'    {f}', flush=True)
    finally:
        open(path, 'wb').write(raw)


if __name__ == '__main__':
    for m in MUTATIONS:
        if len(sys.argv) > 1 and m[0] not in sys.argv[1:]:
            continue
        run(*m)
