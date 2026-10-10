import sys, shutil, subprocess, os
# usage: mutate.py <file> <old> <new> <filter> <label>
f, old, new, flt, label = sys.argv[1:6]
s = open(f, encoding='utf-8', newline='').read()
n = s.count(old)
print(f'[{label}] occurrences of mutation target: {n}')
if n != 1:
    sys.exit(2)
bak = f + '.cs357bak'
shutil.copyfile(f, bak)
try:
    open(f, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
    b = subprocess.run(['dotnet', 'build', 'tests/ControlServer.Tests', '-c', 'Release', '--no-incremental'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    errs = [l for l in b.stdout.splitlines() if 'Error(s)' in l or ' error ' in l]
    print(f'[{label}] build: ' + ' | '.join(errs[-3:]))
    if not any(' 0 Error(s)' in l for l in errs):
        print(f'[{label}] BUILD FAILED, tests not run'); sys.exit(3)
    t = subprocess.run(['dotnet', 'test', 'tests/ControlServer.Tests', '-c', 'Release', '--no-build', '--filter', flt], capture_output=True, text=True, encoding='utf-8', errors='replace')
    for l in t.stdout.splitlines():
        if l.strip().startswith('Failed ControlServer') or 'Passed!' in l or 'Failed!' in l:
            print(f'[{label}] ' + l.strip())
finally:
    shutil.copyfile(bak, f)
    os.remove(bak)
    print(f'[{label}] restored')
