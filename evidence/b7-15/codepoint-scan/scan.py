# Every file this ticket changed against the integration branch, scanned byte by byte for control characters
# other than TAB, LF and CR (and for U+0080..U+009F C1 controls after UTF-8 decoding). Prints the raw counts,
# not a verdict, so a broken scanner shows up as a count that cannot be right.
import subprocess, sys
files = subprocess.run(['git', 'diff', '--name-only', '--diff-filter=AM', 'origin/fp/v2-impl...HEAD'],
                       capture_output=True, text=True, check=True).stdout.split()
total = 0
for f in files:
    b = open(f, 'rb').read()
    c0 = [i for i, x in enumerate(b) if x < 32 and x not in (9, 10, 13)] + [i for i, x in enumerate(b) if x == 127]
    try:
        t = b.decode('utf-8')
        c1 = [i for i, ch in enumerate(t) if 0x80 <= ord(ch) <= 0x9f]
    except UnicodeDecodeError:
        c1 = ['not-utf8']
    total += len(c0) + len(c1)
    print(f"{len(b):8d} bytes  C0/DEL={len(c0):3d}  C1={len(c1):3d}  {f}")
print(f"files={len(files)} findings={total}")
