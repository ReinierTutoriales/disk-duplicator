from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

main_p = Path('src/main.rs')
p3 = Path('src/app/part3.rs')
p4 = Path('src/app/part4.rs')
p5 = Path('src/app/part5.rs')

main = main_p.read_text(encoding='utf-8')
part3 = p3.read_text(encoding='utf-8')
part4 = p4.read_text(encoding='utf-8')
part5 = p5.read_text(encoding='utf-8')

main = replace_one(
    main,
    'use app::CopierApp;\nuse eframe::egui;\nuse std::sync::Arc;\n',
    'use app::CopierApp;\nuse eframe::egui;\nuse std::ffi::OsString;\nuse std::path::PathBuf;\nuse std::sync::Arc;\n',
    'main imports',
)

needle = '''fn app_icon() -> Option<Arc<egui::IconData>> {\n    let bytes = include_bytes!(concat!(env!("OUT_DIR"), "/RepartoCopier-runtime.png"));\n    eframe::icon_data::from_png_bytes(bytes).ok().map(Arc::new)\n}\n'''
addition = needle + '''\nfn launch_source_from_args<I>(args: I) -> Option<PathBuf>\nwhere\n    I: IntoIterator<Item = OsString>,\n{\n    let mut args = args.into_iter();\n    let _exe = args.next();\n    let first = args.next()?;\n\n    if first == "--source" {\n        let source = args.next()?;\n        return args.next().is_none().then(|| PathBuf::from(source));\n    }\n\n    if first.to_string_lossy().starts_with('-') {\n        return None;\n    }\n\n    args.next().is_none().then(|| PathBuf::from(first))\n}\n'''
main = replace_one(main, needle, addition, 'launch source parser')

main = replace_one(
    main,
    '''fn main() -> eframe::Result<()> {\n    let mut viewport = egui::ViewportBuilder::default()\n''',
    '''fn main() -> eframe::Result<()> {\n    let launch_source = launch_source_from_args(std::env::args_os());\n    let mut viewport = egui::ViewportBuilder::default()\n''',
    'parse args before UI',
)

main = replace_one(
    main,
    '''        Box::new(|_cc| Ok(Box::new(CopierApp::new()))),\n    )\n}\n''',
    '''        Box::new(move |_cc| Ok(Box::new(CopierApp::new_with_source(launch_source)))),\n    )\n}\n\n#[cfg(test)]\nmod tests {\n    use super::launch_source_from_args;\n    use std::ffi::OsString;\n    use std::path::PathBuf;\n\n    fn args(values: &[&str]) -> Vec<OsString> {\n        values.iter().map(OsString::from).collect()\n    }\n\n    #[test]\n    fn launch_source_accepts_explicit_and_positional_paths() {\n        assert_eq!(\n            launch_source_from_args(args(&["RepartoCopier.exe", "--source", r"C:\\Música\\Niño"])),\n            Some(PathBuf::from(r"C:\\Música\\Niño"))\n        );\n        assert_eq!(\n            launch_source_from_args(args(&["RepartoCopier.exe", r"\\servidor\\Datos compartidos"])),\n            Some(PathBuf::from(r"\\servidor\\Datos compartidos"))\n        );\n    }\n\n    #[test]\n    fn launch_source_rejects_ambiguous_or_unknown_arguments() {\n        assert_eq!(launch_source_from_args(args(&["RepartoCopier.exe"])), None);\n        assert_eq!(\n            launch_source_from_args(args(&["RepartoCopier.exe", "--unknown", r"C:\\Origen"])),\n            None\n        );\n        assert_eq!(\n            launch_source_from_args(args(&["RepartoCopier.exe", r"C:\\Uno", r"D:\\Dos"])),\n            None\n        );\n    }\n}\n''',
    'launch source tests',
)

old_new = '''    pub fn new() -> Self {\n        refresh_system_accent();\n        let settings = load_settings();\n        let use_light_theme = resolve_theme(settings.theme);\n        Self {\n            source: String::new(),\n'''
new_new = '''    pub fn new_with_source(launch_source: Option<PathBuf>) -> Self {\n        refresh_system_accent();\n        let settings = load_settings();\n        let use_light_theme = resolve_theme(settings.theme);\n        let source = launch_source\n            .map(|path| path.to_string_lossy().into_owned())\n            .unwrap_or_default();\n        let status = if source.is_empty() {\n            "Listo para copiar una carpeta a múltiples destinos".to_owned()\n        } else {\n            "Origen precargado · agrega uno o más destinos".to_owned()\n        };\n        Self {\n            source,\n'''
part3 = replace_one(part3, old_new, new_new, 'source-aware constructor')
part3 = replace_one(
    part3,
    '            status: "Listo para copiar una carpeta a múltiples destinos".to_owned(),\n',
    '            status,\n',
    'constructor status',
)

update_needle = '''        let path_error_count = self.path_errors.len();\n        let start_disabled =\n            start_disabled_reason(&self.source, self.dests.len(), path_error_count);\n        let ready_to_start = start_disabled.is_none();\n\n'''
update_add = update_needle + '''        let escape_pressed = ctx.input(|input| input.key_pressed(egui::Key::Escape));\n        if escape_pressed {\n            self.show_settings = false;\n            self.show_credits = false;\n        }\n\n        if !busy {\n            let open_source = ctx.input_mut(|input| input.consume_shortcut(&egui::KeyboardShortcut::new(egui::Modifiers::CTRL, egui::Key::O)));\n            if open_source {\n                if let Some(path) = self.pick_source_dir() {\n                    self.source = path;\n                }\n            }\n\n            let add_destinations = ctx.input_mut(|input| input.consume_shortcut(&egui::KeyboardShortcut::new(egui::Modifiers::CTRL, egui::Key::D)));\n            if add_destinations {\n                if let Some(paths) = self.pick_destination_dirs() {\n                    let added = self.add_destinations(paths);\n                    if added > 0 {\n                        self.status = count_label(added as u64, "destino agregado", "destinos agregados");\n                    }\n                }\n            }\n        }\n\n'''
part4 = replace_one(part4, update_needle, update_add, 'safe keyboard shortcuts')

part4 = part4.replace(
    '.on_hover_text("Seleccionar carpeta de origen")',
    '.on_hover_text("Seleccionar carpeta de origen · Ctrl+O")',
)
part4 = part4.replace(
    '"Selecciona uno o varios destinos. Usa Ctrl o Shift para selección múltiple.",',
    '"Selecciona uno o varios destinos · Ctrl+D. Usa Ctrl o Shift para selección múltiple.",',
)

part5 = part5.replace('CopierApp::new()', 'CopierApp::new_with_source(None)')

main_p.write_text(main, encoding='utf-8')
p3.write_text(part3, encoding='utf-8')
p4.write_text(part4, encoding='utf-8')
p5.write_text(part5, encoding='utf-8')
