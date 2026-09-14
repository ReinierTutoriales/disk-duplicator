from pathlib import Path
import re
from collections import Counter

ROOT = Path('.')
prod_files = list((ROOT/'dotnet'/'RepartoCopier.Core').glob('*.cs')) + list((ROOT/'dotnet'/'RepartoCopier.WinUI').glob('*.cs'))
xaml_files = list((ROOT/'dotnet'/'RepartoCopier.WinUI').glob('*.xaml'))
texts = {p: p.read_text(encoding='utf-8') for p in prod_files + xaml_files}
all_text = '\n'.join(texts.values())

# Conservative symbol scan: only private/internal/public declarations with PascalCase identifiers.
patterns = [
    re.compile(r'\b(?:private|internal|public)\s+(?:static\s+)?(?:async\s+)?(?:[\w<>,?.\[\]\s]+?)\s+([A-Z][A-Za-z0-9_]*)\s*\('),
    re.compile(r'\b(?:private|internal|public)\s+(?:sealed\s+)?(?:class|record|struct|enum)\s+([A-Z][A-Za-z0-9_]*)\b'),
]

decls = []
for path, text in texts.items():
    if path.suffix != '.cs':
        continue
    for pat in patterns:
        for m in pat.finditer(text):
            decls.append((m.group(1), path.as_posix(), text.count('\n', 0, m.start()) + 1))

counts = Counter()
for name, _, _ in decls:
    counts[name] = len(re.findall(rf'\b{re.escape(name)}\b', all_text))

print('=== SYMBOLS WITH <= 1 PRODUCTION/XAML REFERENCE ===')
seen = set()
for name, path, line in sorted(decls, key=lambda x: (x[1], x[2])):
    if name in seen:
        continue
    seen.add(name)
    if counts[name] <= 1:
        print(f'{name}\trefs={counts[name]}\t{path}:{line}')

print('=== TEST-ONLY PRODUCTION SYMBOL CANDIDATES ===')
test_text = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT/'dotnet'/'RepartoCopier.Core.Tests').glob('*.cs'))
for name, path, line in sorted(decls, key=lambda x: (x[1], x[2])):
    if counts[name] == 1 and re.search(rf'\b{re.escape(name)}\b', test_text):
        print(f'{name}\tproduction=definition-only\ttests=yes\t{path}:{line}')

print('=== STALE ROADMAP/DOC MARKERS ===')
for path in [ROOT/'README.md', ROOT/'CHANGELOG.md', ROOT/'TESTING.md', ROOT/'docs'/'FANOUT-PERFORMANCE-ROADMAP.md']:
    if not path.exists():
        continue
    text = path.read_text(encoding='utf-8')
    for marker in ['16 MiB', 'Verificación BLAKE3 física', 'verificación BLAKE3', 'direct/unbuffered source fast path', 'VerifyCpuWait', 'verificación física habilitada']:
        if marker.lower() in text.lower():
            print(f'{path.as_posix()}: contains stale marker: {marker}')
