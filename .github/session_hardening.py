from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

storage = r'''use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[cfg(windows)]
fn is_reparse(meta: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    meta.file_attributes() & 0x0000_0400 != 0
}

#[cfg(not(windows))]
fn is_reparse(_: &fs::Metadata) -> bool {
    false
}

fn ensure_normal_directory(path: &Path, label: &str) -> Result<(), String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;
    if !meta.is_dir() || meta.file_type().is_symlink() || is_reparse(&meta) {
        return Err(format!(
            "{label} no es una carpeta normal o es un enlace/junction/reparse point: {}",
            path.display()
        ));
    }
    Ok(())
}

fn ensure_regular_file(path: &Path, label: &str) -> Result<(), String> {
    let meta = fs::symlink_metadata(path)
        .map_err(|e| format!("No se pudo inspeccionar {label} {}: {e}", path.display()))?;
    if !meta.is_file() || meta.file_type().is_symlink() || is_reparse(&meta) {
        return Err(format!(
            "{label} no es un archivo regular seguro: {}",
            path.display()
        ));
    }
    Ok(())
}

fn unique_sibling(path: &Path, suffix: &str) -> PathBuf {
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let name = path
        .file_name()
        .map(|name| name.to_string_lossy())
        .unwrap_or_else(|| "repartocopier".into());
    path.with_file_name(format!(".{name}.{}.{}.{}", std::process::id(), stamp, suffix))
}

pub fn atomic_write(path: &Path, data: &[u8], label: &str) -> Result<(), String> {
    let parent = path
        .parent()
        .ok_or_else(|| format!("Ruta inválida para {label}: {}", path.display()))?;
    ensure_normal_directory(parent, "La carpeta contenedora")?;

    if path.exists() {
        ensure_regular_file(path, label)?;
    }

    let tmp = unique_sibling(path, "tmp");
    let backup = unique_sibling(path, "bak");
    let result = (|| {
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&tmp)
            .map_err(|e| format!("No se pudo crear el temporal de {label} {}: {e}", tmp.display()))?;
        file.write_all(data)
            .map_err(|e| format!("No se pudo escribir {label}: {e}"))?;
        file.sync_all()
            .map_err(|e| format!("No se pudo sincronizar {label}: {e}"))?;
        drop(file);

        let had_old = path.exists();
        if had_old {
            ensure_regular_file(path, label)?;
            fs::rename(path, &backup).map_err(|e| {
                format!("No se pudo preparar el reemplazo de {label} {}: {e}", path.display())
            })?;
        }

        match fs::rename(&tmp, path) {
            Ok(()) => {
                if had_old {
                    fs::remove_file(&backup).map_err(|e| {
                        format!("{label} se guardó, pero no se pudo limpiar el backup {}: {e}", backup.display())
                    })?;
                }
                Ok(())
            }
            Err(commit_err) => {
                if had_old {
                    match fs::rename(&backup, path) {
                        Ok(()) => Err(format!(
                            "No se pudo reemplazar {label}: {commit_err}; se restauró la versión anterior"
                        )),
                        Err(restore_err) => Err(format!(
                            "CRÍTICO: no se pudo reemplazar {label}: {commit_err}; tampoco restaurar {}: {restore_err}",
                            backup.display()
                        )),
                    }
                } else {
                    Err(format!("No se pudo finalizar {label}: {commit_err}"))
                }
            }
        }
    })();

    if tmp.exists() {
        let _ = fs::remove_file(&tmp);
    }
    if backup.exists() && path.exists() {
        let _ = fs::remove_file(&backup);
    }
    result
}

pub fn read_regular_file(path: &Path, max_bytes: u64, label: &str) -> Result<Vec<u8>, String> {
    ensure_regular_file(path, label)?;
    let meta = fs::metadata(path)
        .map_err(|e| format!("No se pudo leer metadata de {label} {}: {e}", path.display()))?;
    if meta.len() > max_bytes {
        return Err(format!("{label} es demasiado grande (máximo {max_bytes} bytes)."));
    }
    let mut file = File::open(path)
        .map_err(|e| format!("No se pudo abrir {label} {}: {e}", path.display()))?;
    let mut data = Vec::with_capacity(meta.len() as usize);
    file.read_to_end(&mut data)
        .map_err(|e| format!("No se pudo leer {label}: {e}"))?;
    Ok(data)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_root(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("repartocopier-{name}-{stamp}"))
    }

    #[test]
    fn atomic_write_replaces_existing_file_without_leftovers() {
        let root = temp_root("atomic");
        fs::create_dir_all(&root).unwrap();
        let path = root.join("settings.conf");
        fs::write(&path, b"old").unwrap();
        atomic_write(&path, b"new", "prueba").unwrap();
        assert_eq!(fs::read(&path).unwrap(), b"new");
        let entries: Vec<_> = fs::read_dir(&root).unwrap().collect();
        assert_eq!(entries.len(), 1);
        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn atomic_write_rejects_directory_as_target() {
        let root = temp_root("reject-dir");
        fs::create_dir_all(root.join("target")).unwrap();
        let err = atomic_write(&root.join("target"), b"x", "prueba").unwrap_err();
        assert!(err.contains("archivo regular"));
        let _ = fs::remove_dir_all(root);
    }
}
'''

session = r'''use crate::storage;
use std::path::{Path, PathBuf};

const MAGIC: &str = "RepartoCopierSession/1";
const MAX_SESSION_BYTES: u64 = 1024 * 1024;
const MAX_DESTINATIONS: usize = 256;

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CopySession {
    pub source: String,
    pub dests: Vec<String>,
    pub skip_same: bool,
    pub keep_going: bool,
}

fn encode_hex(value: &str) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let bytes = value.as_bytes();
    let mut out = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        out.push(HEX[(byte >> 4) as usize] as char);
        out.push(HEX[(byte & 0x0f) as usize] as char);
    }
    out
}

fn decode_nibble(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

fn decode_hex(value: &str, field: &str) -> Result<String, String> {
    let bytes = value.as_bytes();
    if bytes.len() % 2 != 0 {
        return Err(format!("Campo {field} tiene hexadecimal inválido."));
    }
    let mut out = Vec::with_capacity(bytes.len() / 2);
    for pair in bytes.chunks_exact(2) {
        let hi = decode_nibble(pair[0]).ok_or_else(|| format!("Campo {field} tiene hexadecimal inválido."))?;
        let lo = decode_nibble(pair[1]).ok_or_else(|| format!("Campo {field} tiene hexadecimal inválido."))?;
        out.push((hi << 4) | lo);
    }
    String::from_utf8(out).map_err(|_| format!("Campo {field} no contiene UTF-8 válido."))
}

fn parse_bool(value: &str, field: &str) -> Result<bool, String> {
    match value {
        "0" => Ok(false),
        "1" => Ok(true),
        _ => Err(format!("Campo {field} inválido; se esperaba 0 o 1.")),
    }
}

fn validate(session: &CopySession) -> Result<(), String> {
    if session.source.trim().is_empty() {
        return Err("La sesión no contiene una carpeta de origen.".to_owned());
    }
    if session.dests.is_empty() {
        return Err("La sesión no contiene destinos.".to_owned());
    }
    if session.dests.len() > MAX_DESTINATIONS {
        return Err(format!("La sesión supera el máximo de {MAX_DESTINATIONS} destinos."));
    }
    if session.dests.iter().any(|dest| dest.trim().is_empty()) {
        return Err("La sesión contiene un destino vacío.".to_owned());
    }
    Ok(())
}

fn render(session: &CopySession) -> Result<String, String> {
    validate(session)?;
    let mut out = String::new();
    out.push_str(MAGIC);
    out.push('\n');
    out.push_str("source=");
    out.push_str(&encode_hex(session.source.trim()));
    out.push('\n');
    out.push_str(if session.skip_same { "skip_same=1\n" } else { "skip_same=0\n" });
    out.push_str(if session.keep_going { "keep_going=1\n" } else { "keep_going=0\n" });
    for dest in &session.dests {
        out.push_str("dest=");
        out.push_str(&encode_hex(dest.trim()));
        out.push('\n');
    }
    Ok(out)
}

fn parse(text: &str) -> Result<CopySession, String> {
    let mut lines = text.lines();
    if lines.next() != Some(MAGIC) {
        return Err("Formato o versión de sesión no compatible.".to_owned());
    }

    let mut source = None;
    let mut skip_same = None;
    let mut keep_going = None;
    let mut dests = Vec::new();
    for line in lines {
        if line.is_empty() {
            continue;
        }
        let (key, value) = line
            .split_once('=')
            .ok_or_else(|| "La sesión contiene una línea inválida.".to_owned())?;
        match key {
            "source" => {
                if source.is_some() {
                    return Err("La sesión contiene más de un origen.".to_owned());
                }
                source = Some(decode_hex(value, "source")?);
            }
            "skip_same" => {
                if skip_same.is_some() {
                    return Err("La sesión duplica skip_same.".to_owned());
                }
                skip_same = Some(parse_bool(value, "skip_same")?);
            }
            "keep_going" => {
                if keep_going.is_some() {
                    return Err("La sesión duplica keep_going.".to_owned());
                }
                keep_going = Some(parse_bool(value, "keep_going")?);
            }
            "dest" => {
                if dests.len() >= MAX_DESTINATIONS {
                    return Err(format!("La sesión supera el máximo de {MAX_DESTINATIONS} destinos."));
                }
                dests.push(decode_hex(value, "dest")?);
            }
            _ => return Err(format!("Campo de sesión desconocido: {key}.")),
        }
    }

    let session = CopySession {
        source: source.ok_or_else(|| "La sesión no contiene source.".to_owned())?,
        dests,
        skip_same: skip_same.ok_or_else(|| "La sesión no contiene skip_same.".to_owned())?,
        keep_going: keep_going.ok_or_else(|| "La sesión no contiene keep_going.".to_owned())?,
    };
    validate(&session)?;
    Ok(session)
}

pub fn with_default_extension(path: PathBuf) -> PathBuf {
    if path.extension().is_some() {
        path
    } else {
        path.with_extension("repartocopy")
    }
}

pub fn save(path: &Path, session: &CopySession) -> Result<(), String> {
    let text = render(session)?;
    storage::atomic_write(path, text.as_bytes(), "la sesión")
}

pub fn load(path: &Path) -> Result<CopySession, String> {
    let bytes = storage::read_regular_file(path, MAX_SESSION_BYTES, "la sesión")?;
    let text = std::str::from_utf8(&bytes)
        .map_err(|_| "La sesión no contiene UTF-8 válido.".to_owned())?;
    parse(text)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn sample() -> CopySession {
        CopySession {
            source: r"C:\Música\Niño\日本語".to_owned(),
            dests: vec![r"D:\Copias".to_owned(), r"\\servidor\Datos compartidos".to_owned()],
            skip_same: true,
            keep_going: false,
        }
    }

    #[test]
    fn unicode_and_unc_roundtrip() {
        let original = sample();
        let text = render(&original).unwrap();
        assert_eq!(parse(&text).unwrap(), original);
        assert!(!text.contains("Música"));
    }

    #[test]
    fn parser_rejects_bad_version_duplicate_fields_and_bad_hex() {
        assert!(parse("RepartoCopierSession/9\n").is_err());
        let valid = render(&sample()).unwrap();
        let duplicated = valid.replacen("skip_same=1\n", "skip_same=1\nskip_same=1\n", 1);
        assert!(parse(&duplicated).unwrap_err().contains("duplica"));
        let bad = valid.replacen("source=", "source=z", 1);
        assert!(parse(&bad).unwrap_err().contains("hexadecimal"));
    }

    #[test]
    fn file_roundtrip_and_default_extension() {
        let stamp = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let root = std::env::temp_dir().join(format!("repartocopier-session-{stamp}"));
        fs::create_dir_all(&root).unwrap();
        let path = root.join("trabajo.repartocopy");
        let original = sample();
        save(&path, &original).unwrap();
        assert_eq!(load(&path).unwrap(), original);
        assert_eq!(with_default_extension(root.join("trabajo")).extension().unwrap(), "repartocopy");
        let _ = fs::remove_dir_all(root);
    }
}
'''

Path('src/storage.rs').write_text(storage, encoding='utf-8')
Path('src/session.rs').write_text(session, encoding='utf-8')

main_p = Path('src/main.rs')
config_p = Path('src/config.rs')
p1_p = Path('src/app/part1.rs')
p3_p = Path('src/app/part3.rs')
p4_p = Path('src/app/part4.rs')
p5_p = Path('src/app/part5.rs')
build_p = Path('.github/workflows/build.yml')

main = main_p.read_text(encoding='utf-8')
config = config_p.read_text(encoding='utf-8')
p1 = p1_p.read_text(encoding='utf-8')
p3 = p3_p.read_text(encoding='utf-8')
p4 = p4_p.read_text(encoding='utf-8')
p5 = p5_p.read_text(encoding='utf-8')
build = build_p.read_text(encoding='utf-8')

main = replace_one(main, 'mod preflight;\n', 'mod preflight;\nmod session;\nmod storage;\n', 'main modules')

old_save = '''pub fn save_settings(settings: AppSettings) -> Result<(), String> {\n    let path = settings_path().ok_or_else(|| {\n        "Windows no proporcionó una carpeta APPDATA para guardar los ajustes.".to_owned()\n    })?;\n    let parent = path\n        .parent()\n        .ok_or_else(|| "Ruta de configuración inválida.".to_owned())?;\n    fs::create_dir_all(parent)\n        .map_err(|e| format!("No se pudo crear la carpeta de ajustes: {e}"))?;\n\n    let tmp = path.with_extension("conf.tmp");\n    fs::write(&tmp, render_settings(settings))\n        .map_err(|e| format!("No se pudieron guardar los ajustes: {e}"))?;\n\n    match fs::rename(&tmp, &path) {\n        Ok(()) => Ok(()),\n        Err(rename_err) if path.exists() => {\n            fs::remove_file(&path).map_err(|remove_err| {\n                format!(\n                    "No se pudo reemplazar la configuración ({rename_err}); tampoco se pudo retirar la anterior ({remove_err})."\n                )\n            })?;\n            fs::rename(&tmp, &path)\n                .map_err(|e| format!("No se pudo finalizar el guardado de ajustes: {e}"))\n        }\n        Err(e) => Err(format!("No se pudo finalizar el guardado de ajustes: {e}")),\n    }\n}\n'''
new_save = '''pub fn save_settings(settings: AppSettings) -> Result<(), String> {\n    let path = settings_path().ok_or_else(|| {\n        "Windows no proporcionó una carpeta APPDATA para guardar los ajustes.".to_owned()\n    })?;\n    let parent = path\n        .parent()\n        .ok_or_else(|| "Ruta de configuración inválida.".to_owned())?;\n    fs::create_dir_all(parent)\n        .map_err(|e| format!("No se pudo crear la carpeta de ajustes: {e}"))?;\n    crate::storage::atomic_write(\n        &path,\n        render_settings(settings).as_bytes(),\n        "la configuración",\n    )\n}\n'''
config = replace_one(config, old_save, new_save, 'safe settings persistence')

p1 = replace_one(
    p1,
    'use crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};\n',
    'use crate::engine::{format_bps, start_job, CopyOpts, DestPhase, JobState};\nuse crate::session::{self, CopySession};\n',
    'session import',
)

constructor_end = '''    fn starting(&self) -> bool {\n'''
session_methods = r'''    fn session_snapshot(&self) -> Result<CopySession, String> {
        let source = self.source.trim();
        if source.is_empty() {
            return Err("Selecciona una carpeta de origen antes de guardar la sesión.".to_owned());
        }
        if self.dests.is_empty() {
            return Err("Agrega al menos un destino antes de guardar la sesión.".to_owned());
        }

        let mut dests = Vec::with_capacity(self.dests.len());
        for dest in &self.dests {
            let dest = dest.trim();
            if dest.is_empty() {
                return Err("No se puede guardar una sesión con destinos vacíos.".to_owned());
            }
            if Self::same_path(source, dest) {
                return Err("El origen no puede ser también un destino.".to_owned());
            }
            if dests.iter().any(|existing: &String| Self::same_path(existing, dest)) {
                return Err("La sesión contiene destinos duplicados.".to_owned());
            }
            dests.push(dest.to_owned());
        }

        Ok(CopySession {
            source: source.to_owned(),
            dests,
            skip_same: self.skip_same,
            keep_going: self.keep_going,
        })
    }

    fn apply_session(&mut self, session: CopySession) -> Result<(), String> {
        if session.source.trim().is_empty() || session.dests.is_empty() {
            return Err("La sesión no contiene origen y destinos válidos.".to_owned());
        }
        let source = session.source.trim().to_owned();
        let mut dests = Vec::with_capacity(session.dests.len());
        for dest in session.dests {
            let dest = dest.trim().to_owned();
            if dest.is_empty() || Self::same_path(&source, &dest) {
                return Err("La sesión contiene un destino inválido o igual al origen.".to_owned());
            }
            if dests.iter().any(|existing: &String| Self::same_path(existing, &dest)) {
                return Err("La sesión contiene destinos duplicados.".to_owned());
            }
            dests.push(dest);
        }

        self.source = source;
        self.dests = dests;
        self.skip_same = session.skip_same;
        self.keep_going = session.keep_going;
        self.job = None;
        self.workers.clear();
        self.startup_rx = None;
        self.path_errors.clear();
        self.paths_key = u64::MAX;
        self.last_path_check = Instant::now() - PATH_CHECK_INTERVAL;
        self.error_flash_until = None;
        self.status = "Sesión cargada · al iniciar se validará lo completado y solo se copiará lo pendiente".to_owned();
        Ok(())
    }

    fn save_session_dialog(&mut self) {
        let session = match self.session_snapshot() {
            Ok(session) => session,
            Err(error) => {
                self.flash_error(error);
                return;
            }
        };
        let mut dialog = rfd::FileDialog::new()
            .set_title("Guardar sesión de RepartoCopier")
            .add_filter("Sesión de RepartoCopier", &["repartocopy"])
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
        match session::save(&path, &session) {
            Ok(()) => {
                self.error_flash_until = None;
                self.status = format!("Sesión guardada · {}", display_path(&path.to_string_lossy()));
            }
            Err(error) => self.flash_error(error),
        }
    }

    fn load_session_dialog(&mut self) {
        let mut dialog = rfd::FileDialog::new()
            .set_title("Cargar sesión de RepartoCopier")
            .add_filter("Sesión de RepartoCopier", &["repartocopy"]);
        if let Some(source) = Self::existing_dir(&self.source) {
            if let Some(parent) = source.parent() {
                dialog = dialog.set_directory(parent);
            }
        }
        let Some(path) = dialog.pick_file() else {
            return;
        };
        match session::load(&path).and_then(|session| self.apply_session(session)) {
            Ok(()) => {}
            Err(error) => self.flash_error(error),
        }
    }

'''
p3 = replace_one(p3, constructor_end, session_methods + constructor_end, 'session app methods')

ready_block = '''        let ready_to_start = start_disabled.is_none();\n\n'''
shortcut_block = ready_block + '''        let save_session_shortcut = ctx.input_mut(|input| {\n            input.consume_shortcut(&egui::KeyboardShortcut::new(\n                egui::Modifiers::CTRL,\n                egui::Key::S,\n            ))\n        });\n        if save_session_shortcut {\n            self.save_session_dialog();\n        }\n\n        let load_session_shortcut = !busy\n            && ctx.input_mut(|input| {\n                input.consume_shortcut(&egui::KeyboardShortcut::new(\n                    egui::Modifiers::CTRL | egui::Modifiers::SHIFT,\n                    egui::Key::O,\n                ))\n            });\n        if load_session_shortcut {\n            self.load_session_dialog();\n        }\n\n'''
p4 = replace_one(p4, ready_block, shortcut_block, 'session shortcuts')

header_old = '''                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {\n                    let settings = egui::Button::new(RichText::new("⚙").size(17.0)).frame(false);\n                    if ui.add(settings).on_hover_text("Ajustes").clicked() {\n                        self.show_settings = true;\n                    }\n                    if starting {\n                        ui.weak("Validando…");\n                    }\n                });\n'''
header_new = '''                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {\n                    let settings = egui::Button::new(RichText::new("⚙").size(17.0)).frame(false);\n                    if ui.add(settings).on_hover_text("Ajustes").clicked() {\n                        self.show_settings = true;\n                    }\n                    ui.menu_button("Sesión", |ui| {\n                        let can_save = !self.source.trim().is_empty() && !self.dests.is_empty();\n                        if ui\n                            .add_enabled(can_save, egui::Button::new("Guardar sesión…   Ctrl+S"))\n                            .on_hover_text("Guarda origen, destinos y opciones. El progreso seguro permanece en los journals de cada destino.")\n                            .clicked()\n                        {\n                            ui.close_menu();\n                            self.save_session_dialog();\n                        }\n                        if ui\n                            .add_enabled(!busy, egui::Button::new("Cargar sesión…   Ctrl+Shift+O"))\n                            .on_hover_text("Carga el trabajo. No inicia la copia automáticamente.")\n                            .clicked()\n                        {\n                            ui.close_menu();\n                            self.load_session_dialog();\n                        }\n                    });\n                    if starting {\n                        ui.weak("Validando…");\n                    }\n                });\n'''
p4 = replace_one(p4, header_old, header_new, 'session menu')

p5_insert = '''    #[cfg(windows)]\n    #[test]\n    fn destination_batch_rejects_equivalent_windows_paths() {\n'''
session_tests = r'''    #[test]
    fn session_snapshot_and_apply_preserve_copy_configuration() {
        let mut app = CopierApp::new_with_source(None);
        app.source = r"C:\Música\Proyecto".to_owned();
        app.dests = vec![r"D:\Copias".to_owned(), r"E:\Respaldo".to_owned()];
        app.skip_same = false;
        app.keep_going = false;
        let snapshot = app.session_snapshot().unwrap();

        let mut restored = CopierApp::new_with_source(None);
        restored.apply_session(snapshot).unwrap();
        assert_eq!(restored.source, r"C:\Música\Proyecto");
        assert_eq!(restored.dests.len(), 2);
        assert!(!restored.skip_same);
        assert!(!restored.keep_going);
        assert!(!restored.running_job());
        assert!(restored.startup_rx.is_none());
    }

    #[test]
    fn session_snapshot_rejects_source_as_destination_and_duplicates() {
        let mut app = CopierApp::new_with_source(None);
        app.source = r"C:\Origen".to_owned();
        app.dests = vec![r"C:\Origen".to_owned()];
        assert!(app.session_snapshot().is_err());

        app.dests = vec![r"D:\Copias".to_owned(), r"D:\Copias".to_owned()];
        assert!(app.session_snapshot().is_err());
    }

'''
p5 = replace_one(p5, p5_insert, session_tests + p5_insert, 'session app tests')

build = replace_one(
    build,
    '''  push:\n    branches: [ main ]\n''',
    '''  push:\n    branches: [ main ]\n    paths-ignore:\n      - '.github/*.py'\n      - '.github/workflows/*-audit.yml'\n''',
    'ci paths ignore',
)

main_p.write_text(main, encoding='utf-8')
config_p.write_text(config, encoding='utf-8')
p1_p.write_text(p1, encoding='utf-8')
p3_p.write_text(p3, encoding='utf-8')
p4_p.write_text(p4, encoding='utf-8')
p5_p.write_text(p5, encoding='utf-8')
build_p.write_text(build, encoding='utf-8')
