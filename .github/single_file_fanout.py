from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

# main.rs: map destinations differently for folder and single-file sources.
path = Path('src/main.rs')
text = path.read_text(encoding='utf-8')
old = '''    pub fn start_job(
        source: PathBuf,
        dests: Vec<PathBuf>,
        opts: CopyOpts,
    ) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
        let folder_name = source.file_name().ok_or_else(|| {
            "El origen debe ser una carpeta con nombre; no se puede duplicar una raíz completa."
                .to_owned()
        })?;
        let effective_dests = dests
            .into_iter()
            .map(|base| base.join(folder_name))
            .collect();
        crate::preflight::start_job(source, effective_dests, opts)
    }
'''
new = '''    pub fn start_job(
        source: PathBuf,
        dests: Vec<PathBuf>,
        opts: CopyOpts,
    ) -> Result<(Arc<JobState>, Vec<JoinHandle<()>>), String> {
        let meta = std::fs::symlink_metadata(&source)
            .map_err(|e| format!("No se pudo inspeccionar el origen {}: {e}", source.display()))?;
        let source_name = source.file_name().ok_or_else(|| {
            "El origen debe tener un nombre; no se puede duplicar una raíz completa.".to_owned()
        })?;
        let effective_dests = if meta.is_dir() {
            dests
                .into_iter()
                .map(|base| base.join(source_name))
                .collect()
        } else if meta.is_file() {
            dests
        } else {
            return Err("El origen debe ser un archivo regular o una carpeta.".to_owned());
        };
        crate::preflight::start_job(source, effective_dests, opts)
    }
'''
text = replace_one(text, old, new, 'main start_job source type')
path.write_text(text, encoding='utf-8')

# preflight part1: carry whether the selection is a single file.
path = Path('src/preflight/part1.rs')
text = path.read_text(encoding='utf-8')
old = '''struct PreflightResult {
    source: PathBuf,
    dests: Vec<PathBuf>,
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
}
'''
new = '''struct PreflightResult {
    source: PathBuf,
    dests: Vec<PathBuf>,
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
    single_file: bool,
}
'''
text = replace_one(text, old, new, 'preflight result single file')
path.write_text(text, encoding='utf-8')

# preflight part2: add source planning and adapt change detection.
path = Path('src/preflight/part2.rs')
text = path.read_text(encoding='utf-8')
anchor = '''fn run_preflight(
    source: &Path,
    dests: &[PathBuf],
    opts: CopyOpts,
) -> Result<PreflightPlan, String> {
'''
helper = '''struct SourcePlan {
    engine_root: PathBuf,
    overlap_path: PathBuf,
    files: Vec<PlannedFile>,
    dirs: Vec<PathBuf>,
    single_file: bool,
}

fn plan_source(source: &Path) -> Result<SourcePlan, String> {
    let meta = fs::symlink_metadata(source)
        .map_err(|e| format!("No se pudo inspeccionar el origen {}: {e}", source.display()))?;
    if meta.file_type().is_symlink() || is_reparse_point(&meta) {
        return Err(format!(
            "No se permite usar un enlace simbólico, junction o reparse point como origen: {}.",
            source.display()
        ));
    }

    let canonical = canonical_existing(source, "origen")?;
    if meta.is_dir() {
        let (files, dirs) = scan_source(&canonical)?;
        return Ok(SourcePlan {
            engine_root: canonical.clone(),
            overlap_path: canonical,
            files,
            dirs,
            single_file: false,
        });
    }

    if !meta.is_file() {
        return Err("El origen debe ser un archivo regular o una carpeta.".to_owned());
    }

    let file_name = canonical
        .file_name()
        .ok_or_else(|| "El archivo de origen no tiene un nombre válido.".to_owned())?
        .to_os_string();
    let parent = canonical
        .parent()
        .ok_or_else(|| "El archivo de origen no tiene una carpeta contenedora válida.".to_owned())?
        .to_path_buf();
    let file = File::open(&canonical)
        .map_err(|e| format!("No se puede leer {}: {e}", canonical.display()))?;
    let file_meta = file
        .metadata()
        .map_err(|e| format!("metadata {}: {e}", canonical.display()))?;
    if !file_meta.is_file() {
        return Err("El origen dejó de ser un archivo regular durante el análisis.".to_owned());
    }

    Ok(SourcePlan {
        engine_root: parent,
        overlap_path: canonical,
        files: vec![PlannedFile {
            rel: PathBuf::from(file_name),
            size: file_meta.len(),
            mtime_ns: metadata_mtime_ns(&file_meta),
        }],
        dirs: Vec::new(),
        single_file: true,
    })
}

'''
text = replace_one(text, anchor, helper + anchor, 'source plan helper')
old_body = '''    if !source.is_dir() {
        return Err("El origen debe ser una carpeta.".into());
    }
    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }

    reject_reparse_root(source, "origen")?;
    let canonical_source = canonical_existing(source, "origen")?;
    let canonical_dests = validate_destinations(&canonical_source, dests)?;
    let (files, dirs) = scan_source(&canonical_source)?;
    let files = Arc::new(files);
    let dirs = Arc::new(dirs);
'''
new_body = '''    if dests.is_empty() {
        return Err("Agrega al menos un destino.".into());
    }

    let source_plan = plan_source(source)?;
    let canonical_dests = validate_destinations(&source_plan.overlap_path, dests)?;
    let canonical_source = source_plan.engine_root;
    let files = Arc::new(source_plan.files);
    let dirs = Arc::new(source_plan.dirs);
    let single_file = source_plan.single_file;
'''
text = replace_one(text, old_body, new_body, 'run_preflight source setup')
old_result = '''        PreflightResult {
            source: canonical_source,
            dests: canonical_dests,
            files,
            dirs,
        },
'''
new_result = '''        PreflightResult {
            source: canonical_source,
            dests: canonical_dests,
            files,
            dirs,
            single_file,
        },
'''
text = replace_one(text, old_result, new_result, 'preflight result construction')
old_sig = '''fn source_change(source: &Path, files: &[PlannedFile], dirs: &[PathBuf]) -> Option<String> {
'''
new_sig = '''fn source_change(
    source: &Path,
    files: &[PlannedFile],
    dirs: &[PathBuf],
    single_file: bool,
) -> Option<String> {
'''
text = replace_one(text, old_sig, new_sig, 'source change signature')
old_scan_tail = '''    match scan_source(source) {
        Ok((now_files, now_dirs)) => {
            if now_files.len() != files.len() || now_dirs != dirs {
                return Some("La estructura del origen cambió durante la copia.".into());
            }
        }
        Err(e) => return Some(e),
    }
    None
}
'''
new_scan_tail = '''    if single_file {
        return None;
    }

    match scan_source(source) {
        Ok((now_files, now_dirs)) => {
            if now_files.len() != files.len() || now_dirs != dirs {
                return Some("La estructura del origen cambió durante la copia.".into());
            }
        }
        Err(e) => return Some(e),
    }
    None
}
'''
text = replace_one(text, old_scan_tail, new_scan_tail, 'single file source change tail')
path.write_text(text, encoding='utf-8')

# preflight part3: propagate single_file into the supervisor.
path = Path('src/preflight/part3.rs')
text = path.read_text(encoding='utf-8')
old_sig = '''fn supervise_job(
    source: PathBuf,
    dest_paths: Vec<PathBuf>,
    files: Arc<Vec<PlannedFile>>,
    dirs: Arc<Vec<PathBuf>>,
    state: Arc<JobState>,
    handles: Vec<JoinHandle<()>>,
    opts: CopyOpts,
) -> JoinHandle<()> {
'''
new_sig = '''fn supervise_job(
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
text = replace_one(text, old_sig, new_sig, 'supervisor signature')
text = replace_one(
    text,
    '        let source_problem = source_change(&source, &files, &dirs);\n',
    '        let source_problem = source_change(&source, &files, &dirs, single_file);\n',
    'supervisor source change call',
)
old_call = '''        preflight.files,
        preflight.dirs,
        Arc::clone(&state),
'''
new_call = '''        preflight.files,
        preflight.dirs,
        preflight.single_file,
        Arc::clone(&state),
'''
text = replace_one(text, old_call, new_call, 'supervisor single_file call')
path.write_text(text, encoding='utf-8')

# UI: accept files as valid sources and make dropped file actionable.
path = Path('src/app/part3.rs')
text = path.read_text(encoding='utf-8')
old_validate = '''            if !path.exists() {
                errors.push("El origen no existe.".to_owned());
            } else if !path.is_dir() {
                errors.push("El origen no es una carpeta.".to_owned());
            }
'''
new_validate = '''            if !path.exists() {
                errors.push("El origen no existe.".to_owned());
            } else if !path.is_dir() && !path.is_file() {
                errors.push("El origen no es un archivo regular ni una carpeta.".to_owned());
            }
'''
text = replace_one(text, old_validate, new_validate, 'UI source validation')
old_file_ui = '''            PendingDrop::File(path) => {
                let shown = display_path(&path.to_string_lossy());
                ui.label(RichText::new(compact_path(&shown, 64)).strong());
                ui.label(
                    RichText::new("FAN-OUT copia árboles de carpetas completos. Para no copiar contenido distinto al que esperas, un archivo suelto no se convierte automáticamente en origen.")
                        .color(Theme::muted(self.use_light_theme)),
                );
                ui.add_space(SPACING_SM);
                if let Some(parent) = path.parent() {
                    if ui.button("Usar la carpeta que contiene este archivo").clicked() {
                        use_source = Some(parent.to_path_buf());
                    }
                }
                if ui.button("Cancelar").clicked() {
                    close = true;
                }
            }
'''
new_file_ui = '''            PendingDrop::File(path) => {
                let shown = display_path(&path.to_string_lossy());
                ui.label(RichText::new(compact_path(&shown, 64)).strong());
                ui.label(
                    RichText::new("Puedes distribuir este archivo exacto por FAN-OUT o usar su carpeta contenedora como origen.")
                        .color(Theme::muted(self.use_light_theme)),
                );
                ui.add_space(SPACING_SM);
                if ui.button("Copiar este archivo y elegir destinos").clicked() {
                    use_source = Some(path.clone());
                }
                if let Some(parent) = path.parent() {
                    if ui.button("Usar la carpeta que contiene este archivo").clicked() {
                        use_source = Some(parent.to_path_buf());
                    }
                }
                if ui.button("Cancelar").clicked() {
                    close = true;
                }
            }
'''
text = replace_one(text, old_file_ui, new_file_ui, 'dropped file UX')
path.write_text(text, encoding='utf-8')

# UI copy wording: source may now be file or folder.
path = Path('src/app/part1.rs')
text = path.read_text(encoding='utf-8')
text = text.replace('Selecciona una carpeta de origen', 'Selecciona un archivo o carpeta de origen')
path.write_text(text, encoding='utf-8')

# Add regression tests to preflight.
path = Path('src/preflight/part4.rs')
text = path.read_text(encoding='utf-8')
insert = '''
    #[test]
    fn single_file_source_plan_contains_only_selected_file() {
        let root = temp_dir("single-file-source");
        let selected = root.join("selected.bin");
        fs::write(&selected, b"selected").unwrap();
        fs::write(root.join("sibling.bin"), b"sibling").unwrap();

        let plan = plan_source(&selected).unwrap();
        assert!(plan.single_file);
        assert_eq!(plan.engine_root, selected.canonicalize().unwrap().parent().unwrap());
        assert_eq!(plan.files.len(), 1);
        assert_eq!(plan.files[0].rel, PathBuf::from("selected.bin"));
        assert!(plan.dirs.is_empty());
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn single_file_preflight_targets_destination_root_without_copying_siblings() {
        let root = temp_dir("single-file-preflight");
        let selected = root.join("selected.bin");
        let dest = root.join("destination");
        fs::write(&selected, b"selected").unwrap();
        fs::write(root.join("sibling.bin"), b"sibling").unwrap();
        fs::create_dir_all(&dest).unwrap();

        let (plan, _) = run_preflight(&selected, std::slice::from_ref(&dest), opts(true)).unwrap();
        assert!(plan.single_file);
        assert_eq!(plan.files.len(), 1);
        assert_eq!(plan.files[0].rel, PathBuf::from("selected.bin"));
        assert_eq!(plan.dests.len(), 1);
        assert_eq!(plan.dests[0], dest.canonicalize().unwrap());
        let _ = fs::remove_dir_all(root);
    }
'''
marker = '\n    #[test]\n    fn scan_never_filters_regular_files_by_name() {'
text = replace_one(text, marker, insert + marker, 'single file preflight tests')
path.write_text(text, encoding='utf-8')
