from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

p1 = Path('src/app/part1.rs')
p4 = Path('src/app/part4.rs')
p5 = Path('src/app/part5.rs')

part1 = p1.read_text(encoding='utf-8')
part4 = p4.read_text(encoding='utf-8')
part5 = p5.read_text(encoding='utf-8')

part1 = replace_one(
    part1,
    '''    fn accent(_light: bool) -> Color32 {\n        let rgb = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);\n        Color32::from_rgb(\n            ((rgb >> 16) & 0xFF) as u8,\n            ((rgb >> 8) & 0xFF) as u8,\n            (rgb & 0xFF) as u8,\n        )\n    }\n''',
    '''    fn accent(_light: bool) -> Color32 {\n        let rgb = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);\n        Color32::from_rgb(\n            ((rgb >> 16) & 0xFF) as u8,\n            ((rgb >> 8) & 0xFF) as u8,\n            (rgb & 0xFF) as u8,\n        )\n    }\n\n    fn on_accent(light: bool) -> Color32 {\n        let accent = Self::accent(light);\n        let luminance = (u32::from(accent.r()) * 299\n            + u32::from(accent.g()) * 587\n            + u32::from(accent.b()) * 114)\n            / 1000;\n        if luminance >= 150 {\n            Color32::from_rgb(18, 18, 18)\n        } else {\n            Color32::WHITE\n        }\n    }\n''',
    'accent contrast helper',
)

part1 = replace_one(
    part1,
    '''        style.spacing.item_spacing = egui::vec2(SPACING_SM, 5.0);\n        style.spacing.button_padding = egui::vec2(12.0, 6.0);\n''',
    '''        style.spacing.item_spacing = egui::vec2(SPACING_SM, 6.0);\n        style.spacing.button_padding = egui::vec2(12.0, 7.0);\n''',
    'global spacing',
)
part1 = replace_one(
    part1,
    '''        .inner_margin(egui::Margin::symmetric(10.0, 8.0))\n''',
    '''        .inner_margin(egui::Margin::symmetric(12.0, 9.0))\n''',
    'card padding',
)

insert_after = '''fn format_duration(secs: f64) -> String {\n    if !secs.is_finite() || secs <= 0.0 {\n        return "—".to_owned();\n    }\n    let total = secs.round() as u64;\n    let hours = total / 3600;\n    let minutes = (total % 3600) / 60;\n    let seconds = total % 60;\n    if hours > 0 {\n        format!("{hours:02}:{minutes:02}:{seconds:02}")\n    } else {\n        format!("{minutes:02}:{seconds:02}")\n    }\n}\n'''
addition = insert_after + '''\nfn progress_fraction(written: u64, total: u64, phase: DestPhase) -> f32 {\n    if total == 0 {\n        return if phase == DestPhase::Done { 1.0_f32 } else { 0.0_f32 };\n    }\n    (written as f64 / total as f64).clamp(0.0, 1.0) as f32\n}\n'''
part1 = replace_one(part1, insert_after, addition, 'progress helper')

part4 = replace_one(
    part4,
    '''            if path_error_count > 0 {\n                ui.colored_label(\n                    Theme::warning(self.use_light_theme),\n                    format!("⚠ {path_error_count} problema(s) de ruta"),\n                )\n                .on_hover_text(self.path_errors.join("\\n"));\n            }\n''',
    '''            if let Some(first_error) = self.path_errors.first() {\n                let suffix = if path_error_count > 1 {\n                    format!(" · +{} más", path_error_count - 1)\n                } else {\n                    String::new()\n                };\n                ui.colored_label(\n                    Theme::warning(self.use_light_theme),\n                    RichText::new(format!("⚠ {first_error}{suffix}")).size(11.5),\n                )\n                .on_hover_text(self.path_errors.join("\\n"));\n            }\n''',
    'visible path error',
)

part4 = replace_one(
    part4,
    '''                            if ui\n                                .add_sized([84.0, 28.0], egui::Button::new("Cancelar"))\n                                .clicked()\n                            {\n                                job.request_cancel();\n                            }\n                            let pause_label = if paused { "Continuar" } else { "Pausar" };\n                            if ui\n                                .add_sized([84.0, 28.0], egui::Button::new(pause_label))\n                                .clicked()\n                            {\n                                job.set_paused(!paused);\n                            }\n''',
    '''                            let cancel = egui::Button::new(\n                                RichText::new("Cancelar")\n                                    .strong()\n                                    .color(Theme::error(self.use_light_theme)),\n                            )\n                            .fill(Theme::card(self.use_light_theme))\n                            .stroke(egui::Stroke::new(\n                                1.0_f32,\n                                Theme::border(self.use_light_theme),\n                            ));\n                            if ui.add_sized([88.0, 30.0], cancel).clicked() {\n                                job.request_cancel();\n                            }\n                            let pause_label = if paused { "Continuar" } else { "Pausar" };\n                            let pause_button = egui::Button::new(\n                                RichText::new(pause_label)\n                                    .strong()\n                                    .color(Theme::accent(self.use_light_theme)),\n                            )\n                            .fill(Theme::selected(self.use_light_theme))\n                            .stroke(egui::Stroke::new(\n                                1.0_f32,\n                                Theme::accent(self.use_light_theme),\n                            ));\n                            if ui.add_sized([92.0, 30.0], pause_button).clicked() {\n                                job.set_paused(!paused);\n                            }\n''',
    'run controls hierarchy',
)

part4 = replace_one(
    part4,
    '''                        let button = egui::Button::new(RichText::new("Iniciar copia").strong());\n                        if ui\n                            .add_enabled(ready_to_start, button)\n                            .on_hover_text("Iniciar copia a todos los destinos")\n                            .clicked()\n''',
    '''                        let button = egui::Button::new(\n                            RichText::new("Iniciar copia")\n                                .strong()\n                                .color(Theme::on_accent(self.use_light_theme)),\n                        )\n                        .fill(Theme::accent(self.use_light_theme))\n                        .stroke(egui::Stroke::NONE)\n                        .min_size(egui::vec2(116.0, 30.0));\n                        if ui\n                            .add_enabled(ready_to_start, button)\n                            .on_hover_text("Iniciar copia a todos los destinos")\n                            .clicked()\n''',
    'primary action',
)

part4 = replace_one(
    part4,
    '''                        .map(|progress| {\n                            if progress.total == 0 {\n                                if progress.phase == DestPhase::Done {\n                                    1.0\n                                } else {\n                                    0.0\n                                }\n                            } else {\n                                progress.written as f64 / progress.total as f64\n                            }\n                        })\n''',
    '''                        .map(|progress| {\n                            f64::from(progress_fraction(\n                                progress.written,\n                                progress.total,\n                                progress.phase,\n                            ))\n                        })\n''',
    'overall progress normalization',
)

part4 = replace_one(
    part4,
    '''                                        let fraction = if progress.total == 0 {\n                                            if progress.phase == DestPhase::Done {\n                                                1.0\n                                            } else {\n                                                0.0\n                                            }\n                                        } else {\n                                            (progress.written as f32 / progress.total as f32)\n                                                .clamp(0.0, 1.0)\n                                        };\n''',
    '''                                        let fraction = progress_fraction(\n                                            progress.written,\n                                            progress.total,\n                                            progress.phase,\n                                        );\n''',
    'destination progress normalization',
)

part4 = replace_one(
    part4,
    '''                                    ui.weak(format!(\n                                        "{} · {eta}",\n                                        format_bytes(job.bytes_total.load(Ordering::Relaxed))\n                                    ));\n                                    ui.label(\n                                        RichText::new(format_bps(total_bps)).strong().size(11.5),\n                                    );\n''',
    '''                                    ui.weak(format!(\n                                        "{} · ETA {eta}",\n                                        format_bytes(job.bytes_total.load(Ordering::Relaxed))\n                                    ));\n                                    ui.label(\n                                        RichText::new(format!("Vel. {}", format_bps(total_bps)))\n                                            .strong()\n                                            .size(11.5),\n                                    );\n''',
    'general metrics labels',
)

part4 = replace_one(
    part4,
    '''                                                                    "{} ✓ · {} omit. · {} err.",\n''',
    '''                                                                    "{} hechos · {} omit. · {} err.",\n''',
    'destination metrics copy',
)

needle = '''    #[test]\n    fn destination_batch_skips_source_and_duplicates() {\n'''
new_tests = '''    #[test]\n    fn progress_fraction_is_bounded_and_terminal_zero_is_complete() {\n        assert_eq!(progress_fraction(0, 0, DestPhase::Done), 1.0_f32);\n        assert_eq!(progress_fraction(0, 0, DestPhase::Copying), 0.0_f32);\n        assert_eq!(progress_fraction(50, 100, DestPhase::Copying), 0.5_f32);\n        assert_eq!(progress_fraction(150, 100, DestPhase::Copying), 1.0_f32);\n    }\n\n    #[test]\n    fn accent_foreground_keeps_readable_contrast() {\n        let previous = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);\n        SYSTEM_ACCENT_RGB.store(0x00FF_FFFF, Ordering::Relaxed);\n        assert_eq!(Theme::on_accent(true), Color32::from_rgb(18, 18, 18));\n        SYSTEM_ACCENT_RGB.store(0x0000_0000, Ordering::Relaxed);\n        assert_eq!(Theme::on_accent(false), Color32::WHITE);\n        SYSTEM_ACCENT_RGB.store(previous, Ordering::Relaxed);\n    }\n\n    #[test]\n    fn compact_path_preserves_both_ends() {\n        let value = compact_path(r"C:\\very\\long\\folder\\tree\\important-file.bin", 24);\n        assert!(value.starts_with("C:"));\n        assert!(value.ends_with("file.bin"));\n        assert!(value.contains('…'));\n    }\n\n    #[test]\n    fn effective_destination_shows_selected_source_root() {\n        let shown = effective_destination_label(r"C:\\Input\\Package", r"D:\\Copies");\n        assert!(shown.ends_with(r"Copies\\Package"));\n    }\n\n''' + needle
part5 = replace_one(part5, needle, new_tests, 'ui regression tests')

p1.write_text(part1, encoding='utf-8')
p4.write_text(part4, encoding='utf-8')
p5.write_text(part5, encoding='utf-8')
