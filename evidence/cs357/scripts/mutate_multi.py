import sys, shutil, subprocess, os
# usage: mutate_multi.py <file> <filter> <label> <old1> <new1> [<old2> <new2> ...]
f, flt, label = sys.argv[1:4]
pairs = list(zip(sys.argv[4::2], sys.argv[5::2]))
s = open(f, encoding='utf-8', newline='').read()
for old, _ in pairs:
    n = s.count(old)
    print(f'[{label}] target occurrences: {n}')
    if n != 1:
        sys.exit(2)
bak = f + '.cs357bak'
shutil.copyfile(f, bak)
try:
    m = s
    for old, new in pairs:
        m = m.replace(old, new)
    open(f, 'w', encoding='utf-8', newline='').write(m)
    b = subprocess.run(['dotnet', 'build', 'tests/ControlServer.Tests', '-c', 'Release', '--no-incremental'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    errs = [l for l in b.stdout.splitlines() if 'Error(s)' in l]
    print(f'[{label}] build: ' + ' | '.join(errs[-1:]))
    if not any(' 0 Error(s)' in l for l in errs):
        print(f'[{label}] BUILD FAILED, tests not run'); sys.exit(3)
    t = subprocess.run(['dotnet', 'test', 'tests/ControlServer.Tests', '-c', 'Release', '--no-build', '--filter', flt, '--logger', 'console;verbosity=detailed'], capture_output=True, text=True, encoding='utf-8', errors='replace')
    import re
    lines = t.stdout.splitlines()
    for i, l in enumerate(lines):
        st = l.strip()
        if st.startswith('Failed ControlServer') or 'Passed!' in l or 'Failed!' in l:
            print(f'[{label}] ' + st)
            if st.startswith('Failed ControlServer'):
                for k in lines[i+1:i+40]:
                    m = re.search(r'([A-Za-z0-9_.]+Tests[A-Za-z0-9_.]*\.cs):line (\d+)', k)
                    if m:
                        print(f'[{label}]    first test-frame: {m.group(1)}:{m.group(2)}'); break
    print(f'[{label}] dotnet test exit code {t.returncode}')
finally:
    shutil.copyfile(bak, f)
    os.remove(bak)
    print(f'[{label}] restored')
