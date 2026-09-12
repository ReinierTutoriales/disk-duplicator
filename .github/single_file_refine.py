from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

# Collapse supervisor source-related parameters into one cohesive value.
path = Path('src/preflight/part3.rs')
text = path.read_text(encoding='utf-8')
old_sig = '''fn supervise_job(
    source: PathBuf,
    dest_paths: Vec<PathBuf>,
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
    single_file: bool,
    state: Arc<JobState>,
    handles: Vec<JoinHandle<()>>,
    opts: CopyOpts,
) -> JoinHandle<()> {
'''
new_sig = '''struct SupervisorSource {
    root: PathBuf,
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
    single_file: bool,
}

fn supervise_job(
    source: SupervisorSource,
    dest_paths: Vec<PathBuf>,
    state: Arc<JobState>,
    handles: Vec<JoinHandle<()>>,
    opts: CopyOpts,
) -> JoinHandle<()> {
'''
text = replace_one(text, old_sig, new_sig, 'supervisor context signature')
text = replace_one(
    text,
    '        let source_problem = source_change(&source, &files, &dirs, single_file);\n',
    '        let source_problem = source_change(\n            &source.root,\n            &source.files,\n            &source.dirs,\n            source.single_file,\n        );\n',
    'supervisor source change context',
)
text = text.replace('final_source_hashes_for_job(&source, &files,', 'final_source_hashes_for_job(&source.root, &source.files,')
text = text.replace('            &source,\n            &dest_paths,\n            &files,\n            &dirs,', '            &source.root,\n            &dest_paths,\n            &source.files,\n            &source.dirs,')
old_call = '''    let supervisor = supervise_job(
        preflight.source,
        preflight.dests,
        preflight.files,
        preflight.dirs,
        preflight.single_file,
        Arc::clone(&state),
        handles,
        opts,
    );
'''
new_call = '''    let supervisor = supervise_job(
        SupervisorSource {
            root: preflight.source,
            files: preflight.files,
            dirs: preflight.dirs,
            single_file: preflight.single_file,
        },
        preflight.dests,
        Arc::clone(&state),
        handles,
        opts,
    );
'''
text = replace_one(text, old_call, new_call, 'supervisor construction')
path.write_text(text, encoding='utf-8')

# Keep the supervisor cancellation regression on the new compact interface.
path = Path('src/preflight/part4.rs')
text = path.read_text(encoding='utf-8')
old_test_call = '''        let supervisor = supervise_job(
            source,
            vec![dest],
            Arc::new(Vec::new()),
            Arc::new(Vec::new()),
            Arc::clone(&state),
            vec![worker],
            opts(false),
        );
'''
new_test_call = '''        let supervisor = supervise_job(
            SupervisorSource {
                root: source,
                files: Arc::new(Vec::new()),
                dirs: Arc::new(Vec::new()),
                single_file: false,
            },
            vec![dest],
            Arc::clone(&state),
            vec![worker],
            opts(false),
        );
'''
text = replace_one(text, old_test_call, new_test_call, 'supervisor regression call')
path.write_text(text, encoding='utf-8')

# Make all product wording match the new file-or-folder source capability.
path = Path('src/app/part3.rs')
text = path.read_text(encoding='utf-8')
text = text.replace('"Listo para copiar una carpeta a múltiples destinos"', '"Listo para copiar un archivo o carpeta a múltiples destinos"')
text = text.replace('"Selecciona una carpeta de origen."', '"Selecciona un archivo o carpeta de origen."')
path.write_text(text, encoding='utf-8')

path = Path('src/copy_plan.rs')
text = path.read_text(encoding='utf-8')
text = text.replace('"Selecciona una carpeta de origen."', '"Selecciona un archivo o carpeta de origen."')
path.write_text(text, encoding='utf-8')

path = Path('src/app/part5.rs')
text = path.read_text(encoding='utf-8')
text = text.replace('Some("Selecciona una carpeta de origen")', 'Some("Selecciona un archivo o carpeta de origen")')
path.write_text(text, encoding='utf-8')
