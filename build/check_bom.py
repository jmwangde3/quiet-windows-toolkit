"""列出仓库内脚本文件的 BOM 状态（不修改任何文件）。"""
import os
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(ROOT)
EXTS = ('.ps1', '.psm1')
SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules'}
BOM = b'\xef\xbb\xbf'

rows = []
for dirpath, dirnames, filenames in os.walk(ROOT):
    dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
    for name in sorted(filenames):
        if not name.lower().endswith(EXTS):
            continue
        path = os.path.join(dirpath, name)
        with open(path, 'rb') as f:
            head = f.read(3)
        rows.append((os.path.relpath(path, ROOT), head == BOM))

for rel, has in sorted(rows):
    print('%-52s %s' % (rel, 'BOM' if has else 'NO-BOM'))
print('total %d, missing %d' % (len(rows), sum(1 for _, h in rows if not h)))
sys.exit(0)
