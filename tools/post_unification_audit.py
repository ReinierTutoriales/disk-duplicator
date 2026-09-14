from pathlib import Path
import re

ROOT = Path('.')
prod = list((ROOT/'dotnet'/'RepartoCopier.Core').glob('*.cs')) + list((ROOT/'dotnet'/'RepartoCopier.WinUI').glob('*.cs'))
tests = list((ROOT/'dotnet'/'RepartoCopier.Core.Tests').glob('*.cs'))
prod_text = {p: p.read_text(encoding='utf-8') for p in prod}
test_text = '\n'.join(p.read_text(encoding='utf-8') for p in tests)

# Top-level-ish declared types with no references outside their defining production file.
type_pat = re.compile(r'(?m)^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|readonly\s+|partial\s+)*(?:class|record|struct|enum)\s+([A-Z][A-Za-z0-9_]*)\b')
print('=== TYPES WITH ZERO EXTERNAL PRODUCTION FILE REFERENCES ===')
for path, text in sorted(prod_text.items(), key=lambda kv: kv[0].as_posix()):
    for m in type_pat.finditer(text):
        name = m.group(1)
        outside = sum(len(re.findall(rf'\b{re.escape(name)}\b', other)) for p, other in prod_text.items() if p != path)
        in_tests = len(re.findall(rf'\b{re.escape(name)}\b', test_text))
        if outside == 0:
            line = text.count('\n', 0, m.start()) + 1
            print(f'{name}\toutside=0\ttestRefs={in_tests}\t{path.as_posix()}:{line}')

# Methods whose declaration is their only production occurrence.
method_pat = re.compile(r'(?m)^\s*(?:public|internal|private)\s+(?:static\s+)?(?:async\s+)?[\w<>,?.\[\]\s]+\s+([A-Z][A-Za-z0-9_]*)\s*\(')
print('=== DEFINITION-ONLY PRODUCTION METHODS ===')
seen = set()
all_prod = '\n'.join(prod_text.values())
for path, text in sorted(prod_text.items(), key=lambda kv: kv[0].as_posix()):
    for m in method_pat.finditer(text):
        name = m.group(1)
        if name in seen:
            continue
        seen.add(name)
        refs = len(re.findall(rf'\b{re.escape(name)}\b', all_prod))
        if refs == 1:
            line = text.count('\n', 0, m.start()) + 1
            test_refs = len(re.findall(rf'\b{re.escape(name)}\b', test_text))
            print(f'{name}\tprodRefs=1\ttestRefs={test_refs}\t{path.as_posix()}:{line}')

print('=== TEST HOOK / OBSOLETE MARKERS ===')
markers = re.compile(r'(?i)(for tests|fortests|test-only|kept for.*test|obsolete|deprecated|legacy.*helper|temporary|one-time)')
for path, text in sorted(prod_text.items(), key=lambda kv: kv[0].as_posix()):
    for idx, line in enumerate(text.splitlines(), 1):
        if markers.search(line):
            print(f'{path.as_posix()}:{idx}: {line.strip()}')

print('=== ROADMAP CONTRACT CHECK ===')
engine = (ROOT/'dotnet'/'RepartoCopier.Core'/'CopyEngine.cs').read_text(encoding='utf-8')
direct = (ROOT/'dotnet'/'RepartoCopier.Core'/'DirectIoSourceReader.cs').read_text(encoding='utf-8')
verify = (ROOT/'dotnet'/'RepartoCopier.Core'/'FastVerificationReader.cs').read_text(encoding='utf-8')
crc = (ROOT/'dotnet'/'RepartoCopier.Core'/'FastCrc32.cs').read_text(encoding='utf-8')
checks = {
    'block32MiB': 'private const int BlockSize = 32 * 1024 * 1024;' in engine,
    'prefetch8': 'SourcePrefetchPhysicalCapacity = 8' in engine,
    'hashPipeline4': 'SourceHashPipelineCapacity = 4' in engine,
    'sourceOverlapped': 'TryOpenOverlapped' in engine and 'FileFlagOverlapped' in direct,
    'verifyFastReader': 'FastVerificationReader.VerifyAsync' in engine,
    'verifyOverlapped': 'TryOpenOverlappedForVerification' in verify,
    'crcTableByteLoop': 'foreach (var value in data)' in crc,
    'bufferedWriterStillActive': 'ExplicitOffsetWriter' in engine,
    'deferredHolStillPresent': 'for (var deferredIndex = 0; deferredIndex < deferred.Count; deferredIndex++)' in engine,
}
for key, value in checks.items():
    print(f'{key}={value}')
if not all(checks.values()):
    raise RuntimeError('One or more current-roadmap contracts were not observed in code')
