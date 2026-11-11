"""给仓库内的脚本文件补 UTF-8 BOM（zh-CN + PS 5.1 必需），已带 BOM 的跳过。"""
import io
import os
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(ROOT)
EXTS = ('.ps1', '.psm1')
SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules'}

BOM = b'\xef\xbb\xbf'
changed = []
for dirpath, dirnames, filenames in os.walk(ROOT):
    dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
    for name in filenames:
        if not name.lower().endswith(EXTS):
            continue
        path = os.path.join(dirpath, name)
        with open(path, 'rb') as f:
            data = f.read()
        if data.startswith(BOM):
            continue
        with open(path, 'wb') as f:
            f.write(BOM + data)
        changed.append(os.path.relpath(path, ROOT))

print('added BOM: %d' % len(changed))
for c in changed:
    print('  ' + c)
sys.exit(0)
