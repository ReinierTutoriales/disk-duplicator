from pathlib import Path
import re

p = Path('src/app/part4.rs')
t = p.read_text(encoding='utf-8')
pattern = re.compile(
    r'''let speed = if paused\s*\|\| matches!\(\s*progress\.phase,\s*DestPhase::Done\s*\| DestPhase::Failed\s*\| DestPhase::Cancelled\s*\)\s*\{\s*0\.0\s*\} else \{\s*shown_bps\(\s*progress\.bps_recent,\s*progress\.last_tick,\s*\)\s*\};''',
    re.MULTILINE,
)
replacement = '''let terminal = matches!(
                                                        progress.phase,
                                                        DestPhase::Done
                                                            | DestPhase::Failed
                                                            | DestPhase::Cancelled
                                                    );
                                                    let speed = visible_bps(
                                                        progress.bps_recent,
                                                        progress.last_tick,
                                                        terminal,
                                                        paused,
                                                    );'''
t, count = pattern.subn(replacement, t, count=1)
if count != 1:
    raise RuntimeError(f'per-destination speed centralization: expected 1 match, got {count}')
p.write_text(t, encoding='utf-8')
