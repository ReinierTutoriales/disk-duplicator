from pathlib import Path

path = Path('tools/apply_extreme_verify.py')
text = path.read_text(encoding='utf-8')

old = '''engine = regex_once(
    engine,
    r"(\\n\\s*(?:private|internal) sealed class CurrentFile[^\\n]*\\n\\s*\\{)",
    r"\\1\\n        public List<VerificationBlock> VerificationBlocks { get; } = [];",
    "current-file verification blocks",
)
'''
new = '''current_marker = """        bool writeThrough)\n    {\n        public FileEntry Entry { get; } = entry;"""
current_replacement = """        bool writeThrough)\n    {\n        public List<VerificationBlock> VerificationBlocks { get; } = [];\n        public FileEntry Entry { get; } = entry;"""
engine = replace_once(
    engine,
    current_marker,
    current_replacement,
    "current-file verification blocks",
)
'''
if old not in text:
    raise RuntimeError('CurrentFile patch block not found in optimizer')
text = text.replace(old, new, 1)

old_capture = '''    "                            await WriteWithRetryAsync(worker, current, data.Block.Memory, job).ConfigureAwait(false);",
    "                            await WriteWithRetryAsync(worker, current, data.Block.Memory, job).ConfigureAwait(false);\\n                            current.VerificationBlocks.Add(new VerificationBlock(data.Block.Length, data.Block.VerificationCrc32));",
    "capture verification block",
'''
new_capture = '''    "                                    await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);",
    "                                    await WriteWithRetryAsync(worker, current, chunkData.Block.Memory, job).ConfigureAwait(false);\\n                                    current.VerificationBlocks.Add(new VerificationBlock(chunkData.Block.Length, chunkData.Block.VerificationCrc32));",
    "capture verification block",
'''
if old_capture not in text:
    raise RuntimeError('DataMessage capture patch block not found in optimizer')
text = text.replace(old_capture, new_capture, 1)

path.write_text(text, encoding='utf-8', newline='\n')
print('optimizer patch fixed')
