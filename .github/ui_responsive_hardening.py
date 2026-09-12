from pathlib import Path


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one anchor, found {count}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


part1 = Path("src/app/part1.rs")
part3 = Path("src/app/part3.rs")
part4 = Path("src/app/part4.rs")
part5 = Path("src/app/part5.rs")

replace_once(
    part1,
    '''const THEME_CHECK_INTERVAL: Duration = Duration::from_secs(10);
const PATH_CHECK_INTERVAL: Duration = Duration::from_secs(2);
''',
    '''const THEME_CHECK_INTERVAL: Duration = Duration::from_secs(10);
const PATH_CHECK_INTERVAL: Duration = Duration::from_secs(2);
const PATH_EDIT_DEBOUNCE: Duration = Duration::from_millis(250);
const DESTINATION_CHIP_MAX_HEIGHT: f32 = 74.0;
const HEADER_DETAIL_MIN_REMAINING: f32 = 560.0;
''',
    "responsive constants",
)
replace_once(
    part1,
    '''fn source_layout_stacked(available_width: f32) -> bool {
    available_width < 720.0
}
''',
    '''fn source_layout_stacked(available_width: f32) -> bool {
    available_width < 720.0
}

fn actions_layout_stacked(available_width: f32) -> bool {
    available_width < 760.0
}
''',
    "actions breakpoint",
)

methods_anchor = '''    fn theme_choice(
'''
methods = '''    fn draw_copy_options(&mut self, ui: &mut egui::Ui, busy: bool) {
        ui.add_enabled_ui(!busy, |ui| {
            ui.horizontal_wrapped(|ui| {
                ui.checkbox(&mut self.skip_same, "Omitir archivos iguales")
                    .on_hover_text("Evita copiar de nuevo archivos que ya coinciden.");
                ui.checkbox(&mut self.keep_going, "Continuar si un destino falla")
                    .on_hover_text("Los demás destinos continúan si uno presenta un error.");
            });
        });
    }

    fn draw_copy_controls(
        &mut self,
        ui: &mut egui::Ui,
        running: bool,
        paused: bool,
        starting: bool,
        engine_running: bool,
        ready_to_start: bool,
        start_disabled: Option<&str>,
    ) {
        if running {
            if let Some(job) = &self.job {
                let cancel = egui::Button::new(
                    RichText::new("Cancelar")
                        .strong()
                        .color(Theme::error(self.use_light_theme)),
                )
                .fill(Theme::card(self.use_light_theme))
                .stroke(egui::Stroke::new(
                    1.0_f32,
                    Theme::border(self.use_light_theme),
                ));
                if ui
                    .add_sized([92.0, FLUENT_CONTROL_HEIGHT], cancel)
                    .on_hover_text("Cancelar de forma segura la copia actual")
                    .clicked()
                {
                    job.request_cancel();
                }

                let pause_label = if paused { "Continuar" } else { "Pausar" };
                let pause_button = egui::Button::new(
                    RichText::new(pause_label)
                        .strong()
                        .color(Theme::accent(self.use_light_theme)),
                )
                .fill(Theme::selected(self.use_light_theme))
                .stroke(egui::Stroke::new(
                    1.0_f32,
                    Theme::accent(self.use_light_theme),
                ));
                if ui
                    .add_sized([98.0, FLUENT_CONTROL_HEIGHT], pause_button)
                    .clicked()
                {
                    job.set_paused(!paused);
                }
            }
        } else if can_start_new_job(starting, engine_running) {
            let button = egui::Button::new(
                RichText::new("Iniciar copia")
                    .strong()
                    .color(Theme::on_accent(self.use_light_theme)),
            )
            .fill(Theme::accent(self.use_light_theme))
            .stroke(egui::Stroke::NONE)
            .min_size(egui::vec2(124.0, FLUENT_CONTROL_HEIGHT));
            let start_hint = start_disabled.unwrap_or("Iniciar copia a todos los destinos");
            if ui
                .add_enabled(ready_to_start, button)
                .on_hover_text(start_hint)
                .clicked()
            {
                self.start();
            }
        } else {
            ui.spinner();
            ui.weak(if starting { "Preparando…" } else { "Finalizando…" });
        }
    }

'''
replace_once(part3, methods_anchor, methods + methods_anchor, "responsive action methods")

old_path_block = '''        let key = self.paths_key();
        let key_changed = key != self.paths_key;
        if key_changed {
            self.paths_key = key;
            self.path_errors.clear();
            self.validated_paths_key = None;
            // Drop a stale receiver so a slow old UNC/removable path cannot block
            // validation of newly selected paths. Its worker exits when the OS call returns.
            self.path_validation_rx = None;
        }
        if busy {
            self.path_errors.clear();
            self.path_validation_rx = None;
        } else if key_changed
            || (self.path_validation_rx.is_none()
                && self.last_path_check.elapsed() >= PATH_CHECK_INTERVAL)
        {
            self.last_path_check = Instant::now();
            self.start_path_validation(key, ctx);
        }
'''
new_path_block = '''        let key = self.paths_key();
        let key_changed = key != self.paths_key;
        if key_changed {
            self.paths_key = key;
            self.path_errors.clear();
            self.validated_paths_key = None;
            self.last_path_check = Instant::now();
            // Drop a stale receiver so a slow old UNC/removable path cannot block
            // validation of newly selected paths. Its worker exits when the OS call returns.
            self.path_validation_rx = None;
        }
        if busy {
            self.path_errors.clear();
            self.path_validation_rx = None;
        } else if self.path_validation_rx.is_none() {
            let validation_due = if self.validated_paths_key == Some(key) {
                self.last_path_check.elapsed() >= PATH_CHECK_INTERVAL
            } else {
                self.last_path_check.elapsed() >= PATH_EDIT_DEBOUNCE
            };
            if validation_due {
                self.last_path_check = Instant::now();
                self.start_path_validation(key, ctx);
            }
        }
'''
replace_once(part4, old_path_block, new_path_block, "path validation debounce")

replace_once(
    part4,
    '''                if ui.available_width() > 430.0 {
''',
    '''                if ui.available_width() > HEADER_DETAIL_MIN_REMAINING {
''',
    "header responsive detail",
)
replace_once(
    part4,
    '''                let max_chars = ((ui.available_width() / 8.0) as usize).clamp(32, 88);
''',
    '''                let status_width = (ui.available_width() - 104.0).max(160.0);
                let max_chars = ((status_width / 8.0) as usize).clamp(24, 88);
''',
    "footer reserved width",
)

replace_once(
    part4,
    '''                let mut remove = None;
                ui.horizontal_wrapped(|ui| {
''',
    '''                let mut remove = None;
                egui::ScrollArea::vertical()
                    .id_salt("destination_chips_v2")
                    .max_height(DESTINATION_CHIP_MAX_HEIGHT)
                    .auto_shrink([false, true])
                    .scroll_bar_visibility(
                        egui::scroll_area::ScrollBarVisibility::VisibleWhenNeeded,
                    )
                    .show(ui, |ui| {
                        ui.horizontal_wrapped(|ui| {
''',
    "destination chip scroll start",
)
replace_once(
    part4,
    '''                        chip.response.on_hover_text(&shown_destination);
                    }
                });
                if let Some(index) = remove {
''',
    '''                        chip.response.on_hover_text(&shown_destination);
                    }
                        });
                    });
                if let Some(index) = remove {
''',
    "destination chip scroll end",
)

old_actions = '''            ui.horizontal(|ui| {
                ui.add_enabled_ui(!busy, |ui| {
                    ui.checkbox(&mut self.skip_same, "Omitir archivos iguales")
                        .on_hover_text("Evita copiar de nuevo archivos que ya coinciden.");
                    ui.checkbox(
                        &mut self.keep_going,
                        "Continuar si un destino falla",
                    )
                    .on_hover_text(
                        "Los demás destinos continúan si uno presenta un error.",
                    );
                });

                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if running {
                        if let Some(job) = &self.job {
                            let cancel = egui::Button::new(
                                RichText::new("Cancelar")
                                    .strong()
                                    .color(Theme::error(self.use_light_theme)),
                            )
                            .fill(Theme::card(self.use_light_theme))
                            .stroke(egui::Stroke::new(
                                1.0_f32,
                                Theme::border(self.use_light_theme),
                            ));
                            if ui.add_sized([92.0, 34.0], cancel).clicked() {
                                job.request_cancel();
                            }
                            let pause_label = if paused { "Continuar" } else { "Pausar" };
                            let pause_button = egui::Button::new(
                                RichText::new(pause_label)
                                    .strong()
                                    .color(Theme::accent(self.use_light_theme)),
                            )
                            .fill(Theme::selected(self.use_light_theme))
                            .stroke(egui::Stroke::new(
                                1.0_f32,
                                Theme::accent(self.use_light_theme),
                            ));
                            if ui.add_sized([98.0, 34.0], pause_button).clicked() {
                                job.set_paused(!paused);
                            }
                        }
                    } else if can_start_new_job(starting, engine_running) {
                        let button = egui::Button::new(
                            RichText::new("Iniciar copia")
                                .strong()
                                .color(Theme::on_accent(self.use_light_theme)),
                        )
                        .fill(Theme::accent(self.use_light_theme))
                        .stroke(egui::Stroke::NONE)
                        .min_size(egui::vec2(124.0, 34.0));
                        let start_hint = start_disabled
                            .unwrap_or("Iniciar copia a todos los destinos");
                        if ui
                            .add_enabled(ready_to_start, button)
                            .on_hover_text(start_hint)
                            .clicked()
                        {
                            self.start();
                        }
                    } else {
                        ui.spinner();
                        ui.weak(if starting { "Preparando…" } else { "Finalizando…" });
                    }
                });
            });
'''
new_actions = '''            let stacked_actions = actions_layout_stacked(ui.available_width());
            if stacked_actions {
                self.draw_copy_options(ui, busy);
                ui.add_space(SPACING_XS);
                ui.horizontal(|ui| {
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        self.draw_copy_controls(
                            ui,
                            running,
                            paused,
                            starting,
                            engine_running,
                            ready_to_start,
                            start_disabled,
                        );
                    });
                });
            } else {
                ui.horizontal(|ui| {
                    self.draw_copy_options(ui, busy);
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        self.draw_copy_controls(
                            ui,
                            running,
                            paused,
                            starting,
                            engine_running,
                            ready_to_start,
                            start_disabled,
                        );
                    });
                });
            }
'''
replace_once(part4, old_actions, new_actions, "adaptive action row")

replace_once(
    part5,
    '''    fn source_layout_stacks_at_narrow_widths() {
        assert!(source_layout_stacked(680.0));
        assert!(source_layout_stacked(719.0));
        assert!(!source_layout_stacked(720.0));
        assert!(!source_layout_stacked(960.0));
    }
''',
    '''    fn source_layout_stacks_at_narrow_widths() {
        assert!(source_layout_stacked(680.0));
        assert!(source_layout_stacked(719.0));
        assert!(!source_layout_stacked(720.0));
        assert!(!source_layout_stacked(960.0));
    }

    #[test]
    fn action_row_stacks_before_controls_compete_for_width() {
        assert!(actions_layout_stacked(680.0));
        assert!(actions_layout_stacked(759.0));
        assert!(!actions_layout_stacked(760.0));
        assert!(!actions_layout_stacked(960.0));
    }

    #[test]
    fn path_validation_debounce_is_shorter_than_periodic_refresh() {
        assert!(PATH_EDIT_DEBOUNCE < PATH_CHECK_INTERVAL);
        assert!(DESTINATION_CHIP_MAX_HEIGHT <= 80.0);
    }
''',
    "responsive tests",
)

print("responsive UI hardening applied")
