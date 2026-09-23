import io, shutil, subprocess, os
path = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs'
old, new = '            _ => ExitNotStoppedReason,\n', '            _ => null,\n'
src = io.open(path, encoding='utf-8', newline='').read()
assert src.count(old) == 1, src.count(old)
shutil.copyfile(path, path + '.bak')
try:
    io.open(path, 'w', encoding='utf-8', newline='').write(src.replace(old, new))
    b = subprocess.run(['dotnet','build','tests/ControlServer.Tests/ControlServer.Tests.csproj','-c','Release','--no-incremental'],capture_output=True,text=True,encoding='utf-8',errors='replace')
    print([l.strip() for l in b.stdout.splitlines() if 'Error(s)' in l])
    r = subprocess.run(['dotnet','test','tests/ControlServer.Tests/ControlServer.Tests.csproj','-c','Release','--no-build','--filter','FullyQualifiedName~StoppedRebuildExitTests'],capture_output=True,text=True,encoding='utf-8',errors='replace')
    lines = (r.stdout + r.stderr).splitlines()
    for i, l in enumerate(lines):
        if l.strip().startswith('Failed ControlServer'):
            print(l.strip()[:200])
            for m in lines[i+1:i+5]:
                if m.strip() and not m.strip().startswith('Error Message'):
                    print('    ' + m.strip()[:200]); break
        if 'Failed!' in l or 'Passed!' in l: print(l.strip())
finally:
    shutil.move(path + '.bak', path); os.utime(path, None)
print(subprocess.run(['git','status','--short'],capture_output=True,text=True).stdout or 'clean')
