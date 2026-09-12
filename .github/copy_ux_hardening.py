from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

copy_plan = r'''use std::path::Path;

pub const MAX_DESTINATIONS: usize = 256;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CopyPlan {
    pub source: String,
    pub dests: Vec<String>,
    pub skip_same: bool,
    pub keep_going: bool,
}

fn normalized_path(path: &str) -> String {
    let trimmed = path.trim();
    let without_extended = if let Some(rest) = trimmed.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{rest}")
    } else if let Some(rest) = trimmed.strip_prefix(r"\\?\") {
        rest.to_owned()
    } else {
        trimmed.to_owned()
    };
    let normalized = without_extended.replace('/', r"\");
    let without_trailing = normalized.trim_end_matches('\\');
    if without_trailing.is_empty() {
        normalized
    } else if without_trailing.len() == 2 && without_trailing.ends_with(':') {
        format!("{without_trailing}\\")
    } else {
        without_trailing.to_owned()
    }
}

#[cfg(windows)]
fn ordinal_eq_ignore_case(a: &str, b: &str) -> bool {
    #[link(name = "kernel32")]
    extern "system" {
        #[link_name = "CompareStringOrdinal"]
        fn compare_string_ordinal(
            string1: *const u16,
            count1: i32,
            string2: *const u16,
            count2: i32,
            ignore_case: i32,
        ) -> i32;
    }
    const CSTR_EQUAL: i32 = 2;
    let a: Vec<u16> = a.encode_utf16().collect();
    let b: Vec<u16> = b.encode_utf16().collect();
    unsafe {
        compare_string_ordinal(
            a.as_ptr(),
            a.len() as i32,
            b.as_ptr(),
            b.len() as i32,
            1,
        ) == CSTR_EQUAL
    }
}

pub fn same_path(a: &str, b: &str) -> bool {
    let a = normalized_path(a);
    let b = normalized_path(b);
    #[cfg(windows)]
    {
        ordinal_eq_ignore_case(&a, &b)
    }
    #[cfg(not(windows))]
    {
        a == b
    }
}

pub fn append_unique_destinations(
    source: &str,
    existing: &mut Vec<String>,
    selected: impl IntoIterator<Item = String>,
) -> usize {
    let mut added = 0usize;
    for path in selected {
        let path = path.trim().to_owned();
        if path.is_empty() || same_path(&path, source) {
            continue;
        }
        if existing.iter().any(|current| same_path(current, &path)) {
            continue;
        }
        existing.push(path);
        added += 1;
    }
    added
}

impl CopyPlan {
    pub fn new(
        source: impl Into<String>,
        dests: Vec<String>,
        skip_same: bool,
        keep_going: bool,
    ) -> Result<Self, String> {
        let source = source.into().trim().to_owned();
        if source.is_empty() {
            return Err("Selecciona una carpeta de origen.".to_owned());
        }
        if dests.is_empty() {
            return Err("Agrega al menos un destino.".to_owned());
        }
        if dests.len() > MAX_DESTINATIONS {
            return Err(format!("La copia supera el máximo de {MAX_DESTINATIONS} destinos."));
        }

        let mut clean = Vec::with_capacity(dests.len());
        for dest in dests {
            let dest = dest.trim().to_owned();
            if dest.is_empty() {
                return Err("La copia contiene un destino vacío.".to_owned());
            }
            if same_path(&source, &dest) {
                return Err("El origen no puede ser también un destino.".to_owned());
            }
            if clean.iter().any(|current: &String| same_path(current, &dest)) {
                return Err("La copia contiene destinos duplicados.".to_owned());
            }
            clean.push(dest);
        }

        Ok(Self {
            source,
            dests: clean,
            skip_same,
            keep_going,
        })
    }
}

pub fn source_name(path: &str) -> Option<String> {
    Path::new(path.trim())
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| !name.is_empty())
        .map(str::to_owned)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plan_trims_and_rejects_invalid_destinations() {
        let plan = CopyPlan::new(
            " C:/Origen/ ",
            vec![" D:/Uno/ ".to_owned(), "E:/Dos".to_owned()],
            true,
            false,
        )
        .unwrap();
        assert_eq!(plan.source, "C:/Origen/");
        assert_eq!(plan.dests, vec!["D:/Uno/", "E:/Dos"]);
        assert!(CopyPlan::new("C:/Origen", vec!["C:/Origen/".to_owned()], true, true).is_err());
        assert!(CopyPlan::new(
            "C:/Origen",
            vec!["D:/Uno".to_owned(), "D:/Uno/".to_owned()],
            true,
            true,
        )
        .is_err());
    }

    #[test]
    fn extended_and_normal_paths_compare_equal() {
        assert!(same_path(r"\\?\C:\Datos\", r"C:/Datos"));
        assert!(same_path(r"\\?\UNC\Servidor\Share\", r"\\Servidor\Share"));
    }

    #[cfg(windows)]
    #[test]
    fn windows_path_comparison_handles_unicode_case() {
        assert!(same_path(r"C:\MÚSICA\Niño", r"c:\música\niño\"));
    }
}
'''
Path('src/copy_plan.rs').write_text(copy_plan, encoding='utf-8')

main = Path('src/main.rs').read_text(encoding='utf-8')
main = replace_one(main, 'mod config;\n', 'mod config;\nmod copy_plan;\n', 'main copy_plan module')
Path('src/main.rs').write_text(main, encoding='utf-8')

session_path = Path('src/session.rs')
session = session_path.read_text(encoding='utf-8')
session = replace_one(session, 'use crate::storage;\n', 'use crate::copy_plan::{CopyPlan, MAX_DESTINATIONS};\nuse crate::storage;\n', 'session imports')
start = session.index('#[derive(Clone, Debug, PartialEq, Eq)]\npub struct CopySession')
end = session.index('\nfn encode_hex', start)
session = session[:start] + 'pub type CopySession = CopyPlan;\n' + session[end:]
validate_start = session.index('fn validate(session: &CopySession)')
validate_end = session.index('\nfn render', validate_start)
session = session[:validate_start] + session[validate_end+1:]
session = session.replace('    validate(session)?;\n', '')
old_construct = '''    let session = CopySession {
        source: source.ok_or_else(|| "La sesión no contiene source.".to_owned())?,
        dests,
        skip_same: skip_same.ok_or_else(|| "La sesión no contiene skip_same.".to_owned())?,
        keep_going: keep_going.ok_or_else(|| "La sesión no contiene keep_going.".to_owned())?,
    };
    validate(&session)?;
    Ok(session)
'''
new_construct = '''    CopyPlan::new(
        source.ok_or_else(|| "La copia guardada no contiene source.".to_owned())?,
        dests,
        skip_same.ok_or_else(|| "La copia guardada no contiene skip_same.".to_owned())?,
        keep_going.ok_or_else(|| "La copia guardada no contiene keep_going.".to_owned())?,
    )
'''
session = replace_one(session, old_construct, new_construct, 'session construction')
session = session.replace('Sesión', 'Copia').replace('sesión', 'copia guardada')
session_path.write_text(session, encoding='utf-8')

part1_path = Path('src/app/part1.rs')
part1 = part1_path.read_text(encoding='utf-8')
part1 = replace_one(
    part1,
    'use crate::config::{load_settings, save_settings, AppSettings, ThemePreference};\nuse crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};\nuse crate::session::{self, CopySession};\n',
    'use crate::config::{load_settings, save_settings, AppSettings, ThemePreference};\nuse crate::copy_plan::{append_unique_destinations, same_path, CopyPlan};\nuse crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};\nuse crate::session;\n',
    'app imports',
)
insert_after = 'type StartResult = Result<(Arc<JobState>, Vec<JoinHandle<()>>), String>;\n'
pending = '''\n#[derive(Clone, Debug)]
enum PendingDrop {
    Folder(PathBuf),
    File(PathBuf),
}\n'''
part1 = replace_one(part1, insert_after, insert_after + pending, 'pending drop enum')
part1_path.write_text(part1, encoding='utf-8')

part2_path = Path('src/app/part2.rs')
part2 = part2_path.read_text(encoding='utf-8')
part2 = replace_one(part2, '    show_settings: bool,\n', '    show_settings: bool,\n    pending_drop: Option<PendingDrop>,\n', 'pending drop field')
part2_path.write_text(part2, encoding='utf-8')

part3_path = Path('src/app/part3.rs')
part3 = part3_path.read_text(encoding='utf-8')
part3 = replace_one(part3, '            show_settings: false,\n', '            show_settings: false,\n            pending_drop: None,\n', 'pending drop init')
start = part3.index('    fn session_snapshot(&self)')
end = part3.index('    fn starting(&self)', start)
replacement = r'''    fn copy_snapshot(&self) -> Result<CopyPlan, String> {
        CopyPlan::new(
            self.source.clone(),
            self.dests.clone(),
            self.skip_same,
            self.keep_going,
        )
    }

    fn apply_saved_copy(&mut self, copy: CopyPlan) {
        self.source = copy.source;
        self.dests = copy.dests;
        self.skip_same = copy.skip_same;
        self.keep_going = copy.keep_going;
        self.job = None;
        self.workers.clear();
        self.startup_rx = None;
        self.pending_drop = None;
        self.path_errors.clear();
        self.paths_key = u64::MAX;
        self.last_path_check = Instant::now() - PATH_CHECK_INTERVAL;
        self.error_flash_until = None;
        self.status = "Copia cargada · al iniciar se validará lo completado y solo se copiará lo pendiente".to_owned();
    }

    fn save_copy_dialog(&mut self) {
        let copy = match self.copy_snapshot() {
            Ok(copy) => copy,
            Err(error) => {
                self.flash_error(error);
                return;
            }
        };
        let mut dialog = rfd::FileDialog::new()
            .set_title("Salvar copia de RepartoCopier")
            .add_filter("Copia de RepartoCopier", &["repartocopy"])
            .set_file_name("copia.repartocopy");
        if let Some(source) = Self::existing_dir(&self.source) {
            if let Some(parent) = source.parent() {
                dialog = dialog.set_directory(parent);
            }
        }
        let Some(path) = dialog.save_file() else {
            return;
        };
        let path = session::with_default_extension(path);
        match session::save(&path, &copy) {
            Ok(()) => {
                self.error_flash_until = None;
                self.status = format!("Copia salvada · {}", display_path(&path.to_string_lossy()));
            }
            Err(error) => self.flash_error(error),
        }
    }

    fn load_copy_dialog(&mut self) {
        let mut dialog = rfd::FileDialog::new()
            .set_title("Cargar copia de RepartoCopier")
            .add_filter("Copia de RepartoCopier", &["repartocopy"]);
        if let Some(source) = Self::existing_dir(&self.source) {
            if let Some(parent) = source.parent() {
                dialog = dialog.set_directory(parent);
            }
        }
        let Some(path) = dialog.pick_file() else {
            return;
        };
        match session::load(&path) {
            Ok(copy) => self.apply_saved_copy(copy),
            Err(error) => self.flash_error(error),
        }
    }

'''
part3 = part3[:start] + replacement + part3[end:]
old_norm_start = part3.index('    fn normalized_path_key(path: &str)')
old_norm_end = part3.index('    fn existing_dir(path: &str)', old_norm_start)
part3 = part3[:old_norm_start] + part3[old_norm_end:]
old_add = '''    fn add_destinations(&mut self, selected: Vec<String>) -> usize {
        let mut added = 0usize;
        for path in selected {
            let path = path.trim().to_owned();
            if path.is_empty() || Self::same_path(&path, &self.source) {
                continue;
            }
            if self.dests.iter().any(|existing| Self::same_path(existing, &path)) {
                continue;
            }
            self.dests.push(path);
            added += 1;
        }
        added
    }
'''
new_add = '''    fn add_destinations(&mut self, selected: Vec<String>) -> usize {
        append_unique_destinations(&self.source, &mut self.dests, selected)
    }

    fn accept_drop(&mut self, paths: Vec<PathBuf>, busy: bool) {
        if paths.is_empty() {
            return;
        }
        if busy {
            self.flash_error("No se puede cambiar origen o destinos mientras una copia está activa.".to_owned());
            return;
        }
        if paths.len() != 1 {
            self.flash_error("Arrastra un solo archivo o carpeta para evitar una acción ambigua.".to_owned());
            return;
        }
        let path = paths.into_iter().next().expect("one dropped path");
        if path.is_dir() {
            self.pending_drop = Some(PendingDrop::Folder(path));
        } else if path.is_file() {
            self.pending_drop = Some(PendingDrop::File(path));
        } else {
            self.flash_error("El elemento arrastrado no es un archivo o carpeta accesible.".to_owned());
        }
    }

    fn draw_drop_prompt(&mut self, ctx: &egui::Context) {
        let Some(pending) = self.pending_drop.clone() else {
            return;
        };
        let mut close = false;
        let mut use_source = None;
        let mut add_destination = None;

        egui::Window::new(match pending {
            PendingDrop::Folder(_) => "Carpeta arrastrada",
            PendingDrop::File(_) => "Archivo arrastrado",
        })
        .collapsible(false)
        .resizable(false)
        .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
        .frame(window_frame(ctx, self.use_light_theme))
        .show(ctx, |ui| match &pending {
            PendingDrop::Folder(path) => {
                let shown = display_path(&path.to_string_lossy());
                ui.label(RichText::new(compact_path(&shown, 64)).strong());
                ui.label(RichText::new("¿Qué quieres hacer con esta carpeta?").color(Theme::muted(self.use_light_theme)));
                ui.add_space(SPACING_SM);
                if ui.button("Usar como origen y elegir destinos").clicked() {
                    use_source = Some(path.clone());
                }
                if !self.source.trim().is_empty()
                    && !same_path(&self.source, &path.to_string_lossy())
                    && ui.button("Agregar como destino").clicked()
                {
                    add_destination = Some(path.clone());
                }
                if ui.button("Cancelar").clicked() {
                    close = true;
                }
            }
            PendingDrop::File(path) => {
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
        });

        if close {
            self.pending_drop = None;
            return;
        }
        if let Some(path) = add_destination {
            let added = self.add_destinations(vec![path.to_string_lossy().into_owned()]);
            self.pending_drop = None;
            if added > 0 {
                self.status = "Destino agregado desde arrastrar y soltar".to_owned();
            }
            return;
        }
        if let Some(path) = use_source {
            self.source = path.to_string_lossy().into_owned();
            self.pending_drop = None;
            self.path_errors.clear();
            self.paths_key = u64::MAX;
            if let Some(paths) = self.pick_destination_dirs() {
                let added = self.add_destinations(paths);
                self.status = if added > 0 {
                    count_label(added as u64, "destino agregado", "destinos agregados")
                } else {
                    "Origen cargado · agrega uno o más destinos".to_owned()
                };
            } else {
                self.status = "Origen cargado · agrega uno o más destinos".to_owned();
            }
        }
    }
'''
part3 = replace_one(part3, old_add, new_add, 'add destinations and drop handling')
part3_path.write_text(part3, encoding='utf-8')

part4_path = Path('src/app/part4.rs')
part4 = part4_path.read_text(encoding='utf-8')
part4 = part4.replace('save_session_shortcut', 'save_copy_shortcut')
part4 = part4.replace('load_session_shortcut', 'load_copy_shortcut')
part4 = part4.replace('self.save_session_dialog()', 'self.save_copy_dialog()')
part4 = part4.replace('self.load_session_dialog()', 'self.load_copy_dialog()')
part4 = part4.replace('ui.menu_button("Sesión"', 'ui.menu_button("Copia"')
part4 = part4.replace('"Guardar sesión…   Ctrl+S"', '"Salvar copia…   Ctrl+S"')
part4 = part4.replace('"Cargar sesión…   Ctrl+Shift+O"', '"Cargar copia…   Ctrl+Shift+O"')
part4 = part4.replace('"Guarda origen, destinos y opciones. El progreso seguro permanece en los journals de cada destino."', '"Salva origen, destinos y opciones. El progreso seguro permanece en cada destino."')
part4 = part4.replace('"Carga el trabajo. No inicia la copia automáticamente."', '"Carga origen, destinos y opciones. No inicia la copia automáticamente."')
anchor = '        let now = Instant::now();\n'
drop_code = '''        let dropped_paths: Vec<PathBuf> = ctx.input(|input| {
            input
                .raw
                .dropped_files
                .iter()
                .filter_map(|file| file.path.clone())
                .collect()
        });
        if !dropped_paths.is_empty() {
            self.accept_drop(dropped_paths, busy);
        }
        self.draw_drop_prompt(ctx);
\n'''
part4 = replace_one(part4, anchor, anchor + drop_code, 'drop event integration')
part4_path.write_text(part4, encoding='utf-8')

part5_path = Path('src/app/part5.rs')
part5 = part5_path.read_text(encoding='utf-8')
part5 = part5.replace('CopierApp::same_path', 'same_path')
part5 = part5.replace('session_snapshot_and_apply_preserve_copy_configuration', 'saved_copy_roundtrip_preserves_copy_configuration')
part5 = part5.replace('session_snapshot_rejects_source_as_destination_and_duplicates', 'copy_plan_rejects_source_as_destination_and_duplicates')
part5 = part5.replace('let snapshot = app.session_snapshot().unwrap();', 'let snapshot = app.copy_snapshot().unwrap();')
part5 = part5.replace('restored.apply_session(snapshot).unwrap();', 'restored.apply_saved_copy(snapshot);')
part5 = part5.replace('assert!(app.session_snapshot().is_err());', 'assert!(app.copy_snapshot().is_err());')
part5_path.write_text(part5, encoding='utf-8')
