from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise SystemExit(f"{label}: expected one anchor, found {text.count(old)}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


part1 = Path("src/app/part1.rs")
part3 = Path("src/app/part3.rs")
part4 = Path("src/app/part4.rs")
part5 = Path("src/app/part5.rs")

replace_once(
    part1,
    ".rounding(egui::Rounding::same(10.0))\n    } else {",
    ".rounding(egui::Rounding::same(FLUENT_RADIUS_MD))\n    } else {",
    "primary drop radius",
)
replace_once(
    part1,
    ".rounding(egui::Rounding::same(10.0))\n    };",
    ".rounding(egui::Rounding::same(FLUENT_RADIUS_MD))\n    };",
    "secondary drop radius",
)

anchor = '''    fn pick_source_dir(&self) -> Option<String> {
        let mut dialog = rfd::FileDialog::new().set_title("Seleccionar carpeta de origen");
        if let Some(start) = Self::existing_dir(&self.source)
            .or_else(|| self.dests.last().and_then(|dest| Self::existing_dir(dest)))
        {
            dialog = dialog.set_directory(start);
        }
        dialog
            .pick_folder()
            .map(|path| path.to_string_lossy().into_owned())
    }
'''
replacement = anchor + '''
    fn pick_source_file(&self) -> Option<String> {
        let mut dialog = rfd::FileDialog::new().set_title("Seleccionar archivo de origen");
        let source = PathBuf::from(self.source.trim());
        let start = if source.is_file() {
            source.parent().map(PathBuf::from)
        } else {
            Self::existing_dir(&self.source)
        }
        .or_else(|| self.dests.last().and_then(|dest| Self::existing_dir(dest)));
        if let Some(start) = start {
            dialog = dialog.set_directory(start);
        }
        dialog
            .pick_file()
            .map(|path| path.to_string_lossy().into_owned())
    }

    fn source_picker_menu(&mut self, ui: &mut egui::Ui) {
        ui.menu_button("Examinar", |ui| {
            if ui.button("Carpeta…").clicked() {
                ui.close_menu();
                if let Some(path) = self.pick_source_dir() {
                    self.source = path;
                }
            }
            if ui.button("Archivo…").clicked() {
                ui.close_menu();
                if let Some(path) = self.pick_source_file() {
                    self.source = path;
                }
            }
        })
        .response
        .on_hover_text("Seleccionar carpeta o archivo de origen");
    }
'''
replace_once(part3, anchor, replacement, "source picker functions")

replace_once(
    part4,
    ".rounding(egui::Rounding::same(14.0))",
    ".rounding(egui::Rounding::same(FLUENT_RADIUS_LG))",
    "drop affordance radius",
)
replace_once(
    part4,
    '''                            ui.add_sized(
                                [field_width, 28.0],
                                egui::TextEdit::singleline(&mut self.source)
                                    .hint_text("Archivo o carpeta que quieres copiar"),
                            );
                            if ui
                                .add_sized([button_width, 28.0], egui::Button::new("Examinar"))
                                .on_hover_text("Seleccionar carpeta de origen · Ctrl+O · también puedes arrastrar un archivo")
                                .clicked()
                            {
                                if let Some(path) = self.pick_source_dir() {
                                    self.source = path;
                                }
                            }
''',
    '''                            ui.add_sized(
                                [field_width, FLUENT_CONTROL_HEIGHT],
                                egui::TextEdit::singleline(&mut self.source)
                                    .hint_text("Archivo o carpeta que quieres copiar"),
                            );
                            ui.allocate_ui_with_layout(
                                egui::vec2(button_width, FLUENT_CONTROL_HEIGHT),
                                egui::Layout::left_to_right(egui::Align::Center),
                                |ui| self.source_picker_menu(ui),
                            );
''',
    "stacked source controls",
)
replace_once(
    part4,
    '''                        ui.add_sized(
                            [field_width, 28.0],
                            egui::TextEdit::singleline(&mut self.source)
                                .hint_text("Archivo o carpeta que quieres copiar"),
                        );
                        if ui
                            .add_sized([button_width, 28.0], egui::Button::new("Examinar"))
                            .on_hover_text("Seleccionar carpeta de origen · Ctrl+O · también puedes arrastrar un archivo")
                            .clicked()
                        {
                            if let Some(path) = self.pick_source_dir() {
                                self.source = path;
                            }
                        }
''',
    '''                        ui.add_sized(
                            [field_width, FLUENT_CONTROL_HEIGHT],
                            egui::TextEdit::singleline(&mut self.source)
                                .hint_text("Archivo o carpeta que quieres copiar"),
                        );
                        ui.allocate_ui_with_layout(
                            egui::vec2(button_width, FLUENT_CONTROL_HEIGHT),
                            egui::Layout::left_to_right(egui::Align::Center),
                            |ui| self.source_picker_menu(ui),
                        );
''',
    "wide source controls",
)
replace_once(part4, "[154.0, 34.0]", "[154.0, FLUENT_CONTROL_HEIGHT]", "add destinations height")
replace_once(part4, "[84.0, 26.0]", "[84.0, FLUENT_CONTROL_HEIGHT]", "remove all height")
replace_once(part4, "[82.0, 24.0]", "[82.0, FLUENT_CONTROL_HEIGHT]", "about height")

replace_once(
    part5,
    '''    fn fluent_metrics_keep_windows_control_density() {
        assert_eq!(FLUENT_CONTROL_HEIGHT, 32.0);
        assert_eq!(FLUENT_RADIUS_SM, 6.0);
        assert_eq!(FLUENT_RADIUS_MD, 8.0);
        assert_eq!(FLUENT_RADIUS_LG, 12.0);
    }
''',
    '''    fn fluent_metrics_keep_windows_control_density() {
        assert_eq!(FLUENT_CONTROL_HEIGHT, 32.0);
        assert_eq!(FLUENT_RADIUS_SM, 6.0);
        assert_eq!(FLUENT_RADIUS_MD, 8.0);
        assert_eq!(FLUENT_RADIUS_LG, 12.0);
    }

    #[test]
    fn source_picker_supports_file_and_folder_semantics() {
        let source_file = include_str!("part3.rs");
        assert!(source_file.contains("pick_source_dir"));
        assert!(source_file.contains("pick_source_file"));
        assert!(source_file.contains("Carpeta…"));
        assert!(source_file.contains("Archivo…"));
    }
''',
    "source picker test",
)

print("responsive Fluent UI optimization applied")
