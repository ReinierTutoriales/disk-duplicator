from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)


# Shared UI rules: terminal snapshots end the visual copy immediately, while
# engine_running may remain true briefly to finish supervisor cleanup.
p = Path('src/app/part1.rs')
t = p.read_text(encoding='utf-8')
anchor = '''fn shown_bps(bps: f64, last_tick: Instant) -> f64 {
    let idle = last_tick.elapsed().as_secs_f64();
    if idle <= SPEED_DECAY_GRACE_SECS {
        bps
    } else {
        bps * (-(idle - SPEED_DECAY_GRACE_SECS) / SPEED_DECAY_TAU_SECS).exp()
    }
}
'''
replacement = anchor + '''
fn ui_copy_active(engine_running: bool, all_terminal: bool) -> bool {
    engine_running && !all_terminal
}

fn visible_bps(bps: f64, last_tick: Instant, terminal: bool, paused: bool) -> f64 {
    if terminal || paused {
        0.0
    } else {
        shown_bps(bps, last_tick)
    }
}

fn drop_action_button(
    ui: &mut egui::Ui,
    label: &str,
    primary: bool,
    light: bool,
) -> egui::Response {
    let accent = Theme::accent(light);
    let button = if primary {
        egui::Button::new(RichText::new(label).strong().color(Theme::on_accent(light)))
            .fill(accent)
            .stroke(egui::Stroke::NONE)
    } else {
        egui::Button::new(RichText::new(label).strong().color(accent))
            .fill(Theme::selected(light))
            .stroke(egui::Stroke::new(1.0_f32, Theme::border(light)))
    };
    ui.add_sized([ui.available_width(), 40.0], button)
}
'''
t = replace_one(t, anchor, replacement, 'shared terminal and action helpers')
p.write_text(t, encoding='utf-8')


# Make the smart-drop prompt more deliberate and Material-like without adding
# another UI dependency.
p = Path('src/app/part3.rs')
t = p.read_text(encoding='utf-8')
t = replace_one(
    t,
    '''        .collapsible(false)\n        .resizable(false)\n        .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)\n''',
    '''        .collapsible(false)\n        .resizable(false)\n        .default_width(430.0)\n        .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)\n''',
    'drop dialog width',
)
t = replace_one(
    t,
    '''                if ui.button("Usar como origen y elegir destinos").clicked() {\n                    use_source = Some(path.clone());\n                }\n''',
    '''                if drop_action_button(\n                    ui,\n                    "Usar como origen y elegir destinos",\n                    true,\n                    self.use_light_theme,\n                )\n                .clicked()\n                {\n                    use_source = Some(path.clone());\n                }\n''',
    'folder primary drop action',
)
t = replace_one(
    t,
    '''                    && ui.button("Agregar como destino").clicked()\n''',
    '''                    && drop_action_button(\n                        ui,\n                        "Agregar como destino",\n                        false,\n                        self.use_light_theme,\n                    )\n                    .clicked()\n''',
    'folder secondary drop action',
)
t = replace_one(
    t,
    '''                if ui.button("Copiar este archivo y elegir destinos").clicked() {\n                    use_source = Some(path.clone());\n                }\n''',
    '''                if drop_action_button(\n                    ui,\n                    "Copiar este archivo y elegir destinos",\n                    true,\n                    self.use_light_theme,\n                )\n                .clicked()\n                {\n                    use_source = Some(path.clone());\n                }\n''',
    'file primary drop action',
)
t = replace_one(
    t,
    '''                    if ui.button("Usar la carpeta que contiene este archivo").clicked() {\n                        use_source = Some(parent.to_path_buf());\n                    }\n''',
    '''                    if drop_action_button(\n                        ui,\n                        "Usar la carpeta que contiene este archivo",\n                        false,\n                        self.use_light_theme,\n                    )\n                    .clicked()\n                    {\n                        use_source = Some(parent.to_path_buf());\n                    }\n''',
    'file secondary drop action',
)
p.write_text(t, encoding='utf-8')


# Main UI: separate engine cleanup from the visual active-copy state, zero all
# terminal metrics, clear stale current-file text, and add a strong drop target.
p = Path('src/app/part4.rs')
t = p.read_text(encoding='utf-8')
t = t.replace('Una carpeta · múltiples destinos', 'Archivo o carpeta · múltiples destinos')
t = t.replace('Carpeta que quieres copiar', 'Archivo o carpeta que quieres copiar')
t = t.replace(
    'Seleccionar carpeta de origen · Ctrl+O',
    'Seleccionar carpeta de origen · Ctrl+O · también puedes arrastrar un archivo',
)

old = '''        let starting = self.starting();
        let running = self.running_job();
        let paused = running && self.job.as_ref().is_some_and(|job| job.is_paused());
        let verifying = running && snaps.iter().any(|dest| dest.phase == DestPhase::Verifying);
        let busy = starting || running;
'''
new = '''        let starting = self.starting();
        let engine_running = self.running_job();
        let running = ui_copy_active(engine_running, all_terminal);
        let paused = running && self.job.as_ref().is_some_and(|job| job.is_paused());
        let verifying = running && snaps.iter().any(|dest| dest.phase == DestPhase::Verifying);
        let busy = starting || engine_running;
        let all_successful = all_terminal
            && snaps
                .iter()
                .all(|dest| dest.phase == DestPhase::Done && dest.files_err == 0);
'''
t = replace_one(t, old, new, 'visual vs engine running state')

old = '''        let dropped_paths: Vec<PathBuf> = ctx.input(|input| {
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
'''
new = '''        let (dropped_paths, hovering_drop): (Vec<PathBuf>, bool) = ctx.input(|input| {
            (
                input
                    .raw
                    .dropped_files
                    .iter()
                    .filter_map(|file| file.path.clone())
                    .collect(),
                !input.raw.hovered_files.is_empty(),
            )
        });
        if !dropped_paths.is_empty() {
            self.accept_drop(dropped_paths, busy);
        }
        if hovering_drop {
            let (title, subtitle) = if busy {
                (
                    "Copia activa",
                    "No se puede cambiar el origen o los destinos ahora",
                )
            } else {
                (
                    "Suelta el archivo o carpeta",
                    "RepartoCopier te mostrará las opciones de copia",
                )
            };
            egui::Area::new(egui::Id::new("drop_affordance"))
                .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
                .order(egui::Order::Foreground)
                .show(ctx, |ui| {
                    egui::Frame::none()
                        .fill(Theme::card(self.use_light_theme))
                        .stroke(egui::Stroke::new(
                            2.0_f32,
                            Theme::accent(self.use_light_theme),
                        ))
                        .rounding(egui::Rounding::same(14.0))
                        .inner_margin(egui::Margin::symmetric(26.0, 20.0))
                        .show(ui, |ui| {
                            ui.set_min_width(360.0);
                            ui.vertical_centered(|ui| {
                                ui.label(
                                    RichText::new("+")
                                        .size(30.0)
                                        .strong()
                                        .color(Theme::accent(self.use_light_theme)),
                                );
                                ui.label(RichText::new(title).size(16.0).strong());
                                ui.label(
                                    RichText::new(subtitle)
                                        .size(11.5)
                                        .color(Theme::muted(self.use_light_theme)),
                                );
                            });
                        });
                });
        }
        self.draw_drop_prompt(ctx);
'''
t = replace_one(t, old, new, 'large drop affordance')

old = '''        } else if running {
            ctx.request_repaint_after(if paused {
                PAUSED_REPAINT
            } else {
                RUNNING_REPAINT
            });
        } else {
            ctx.request_repaint_after(THEME_CHECK_INTERVAL);
        }
'''
new = '''        } else if running {
            ctx.request_repaint_after(if paused {
                PAUSED_REPAINT
            } else {
                RUNNING_REPAINT
            });
        } else if engine_running {
            // Destinations are terminal but the supervisor is still releasing resources.
            ctx.request_repaint_after(STARTING_REPAINT);
        } else {
            ctx.request_repaint_after(THEME_CHECK_INTERVAL);
        }
'''
t = replace_one(t, old, new, 'terminal cleanup repaint')

old = '''                let (status, color): (&str, Color32) = if self.error_flash_until.is_some() {
                    (self.status.as_str(), Theme::error(self.use_light_theme))
                } else if paused {
                    ("Copia en pausa", Theme::warning(self.use_light_theme))
                } else if verifying {
'''
new = '''                let (status, color): (&str, Color32) = if self.error_flash_until.is_some() {
                    (self.status.as_str(), Theme::error(self.use_light_theme))
                } else if all_successful {
                    ("Copia completada", Theme::success(self.use_light_theme))
                } else if all_terminal {
                    (
                        "Copia finalizada con incidencias",
                        Theme::warning(self.use_light_theme),
                    )
                } else if paused {
                    ("Copia en pausa", Theme::warning(self.use_light_theme))
                } else if verifying {
'''
t = replace_one(t, old, new, 'terminal footer state')

old = '''                        .add_sized([126.0, 26.0], egui::Button::new("+ Agregar destinos"))
'''
new = '''                        .add_sized(
                            [146.0, 32.0],
                            egui::Button::new(
                                RichText::new("+  Agregar destinos")
                                    .strong()
                                    .color(Theme::accent(self.use_light_theme)),
                            )
                            .fill(Theme::selected(self.use_light_theme))
                            .stroke(egui::Stroke::new(
                                1.0_f32,
                                Theme::border(self.use_light_theme),
                            )),
                        )
'''
t = replace_one(t, old, new, 'material destination add button')

t = replace_one(
    t,
    '''                    let total_bps = if paused {\n''',
    '''                    let total_bps = if all_terminal || paused {\n''',
    'terminal aggregate speed',
)

old = '''                                                    let last_file = if progress.last_file.is_empty() {
                                                        None
                                                    } else {
                                                        Some(display_path(&progress.last_file))
                                                    };
'''
new = '''                                                    let terminal = matches!(
                                                        progress.phase,
                                                        DestPhase::Done
                                                            | DestPhase::Failed
                                                            | DestPhase::Cancelled
                                                    );
                                                    let last_file = if terminal
                                                        || progress.last_file.is_empty()
                                                    {
                                                        None
                                                    } else {
                                                        Some(display_path(&progress.last_file))
                                                    };
'''
t = replace_one(t, old, new, 'clear terminal last file')
p.write_text(t, encoding='utf-8')


# Regression tests for the exact race seen in the UI screenshots.
p = Path('src/app/part5.rs')
t = p.read_text(encoding='utf-8')
anchor = '''    #[test]
    fn progress_fraction_is_bounded_and_terminal_zero_is_complete() {
        assert_eq!(progress_fraction(0, 0, DestPhase::Done), 1.0_f32);
        assert_eq!(progress_fraction(0, 0, DestPhase::Copying), 0.0_f32);
        assert_eq!(progress_fraction(50, 100, DestPhase::Copying), 0.5_f32);
        assert_eq!(progress_fraction(150, 100, DestPhase::Copying), 1.0_f32);
    }
'''
replacement = anchor + '''
    #[test]
    fn terminal_snapshots_end_the_visual_copy_before_engine_cleanup_finishes() {
        assert!(ui_copy_active(true, false));
        assert!(!ui_copy_active(true, true));
        assert!(!ui_copy_active(false, false));
    }

    #[test]
    fn terminal_and_paused_speed_is_always_zero() {
        let now = Instant::now();
        assert_eq!(visible_bps(900_000_000.0, now, true, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, false, true), 0.0);
        assert!(visible_bps(900_000_000.0, now, false, false) > 0.0);
    }
'''
t = replace_one(t, anchor, replacement, 'terminal UI regression tests')
p.write_text(t, encoding='utf-8')
