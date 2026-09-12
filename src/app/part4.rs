fn display_path(path: &str) -> String {
    let trimmed = path.trim();
    if let Some(rest) = trimmed.strip_prefix("\\\\?\\UNC\\") {
        format!(r"\\{rest}")
    } else if let Some(rest) = trimmed.strip_prefix("\\\\?\\") {
        rest.to_owned()
    } else {
        trimmed.to_owned()
    }
}

fn effective_destination_label(source: &str, destination: &str) -> String {
    let destination = display_path(destination);
    let source_path = PathBuf::from(source.trim());
    let Some(source_name) = source_path
        .file_name()
        .and_then(|name| name.to_str())
        .filter(|name| !name.is_empty())
    else {
        return destination;
    };
    display_path(&PathBuf::from(destination).join(source_name).to_string_lossy())
}

impl eframe::App for CopierApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if !self.fonts_initialized {
            setup_fonts(ctx);
            self.fonts_initialized = true;
        }
        self.poll_startup();
        self.poll_path_validation();

        if self.last_theme_check.elapsed() >= THEME_CHECK_INTERVAL {
            let previous_theme = self.use_light_theme;
            let previous_accent = SYSTEM_ACCENT_RGB.load(Ordering::Relaxed);

            if self.theme_preference == ThemePreference::System {
                self.use_light_theme = detect_system_theme();
            }
            refresh_system_accent();

            let accent_changed =
                SYSTEM_ACCENT_RGB.load(Ordering::Relaxed) != previous_accent;
            if self.use_light_theme != previous_theme || accent_changed {
                self.applied_theme = None;
            }
            self.last_theme_check = Instant::now();
        }

        if self.applied_theme != Some(self.use_light_theme) {
            apply_theme(ctx, self.use_light_theme);
            self.applied_theme = Some(self.use_light_theme);
        }

        let snaps = self.job.as_ref().map(|job| job.snapshot()).unwrap_or_default();
        let all_terminal = !snaps.is_empty()
            && snaps.iter().all(|dest| {
                matches!(
                    dest.phase,
                    DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled
                )
            });
        let starting = self.starting();
        let engine_running = self.running_job();
        let running = ui_copy_active(engine_running, all_terminal);
        let paused = running && self.job.as_ref().is_some_and(|job| job.is_paused());
        let verifying = running && snaps.iter().any(|dest| dest.phase == DestPhase::Verifying);
        let busy = starting || engine_running;
        let drop_locked = drop_input_locked(starting, running);
        let all_successful = all_terminal
            && snaps
                .iter()
                .all(|dest| dest.phase == DestPhase::Done && dest.files_err == 0);
        let now = Instant::now();
        let (dropped_paths, hovering_drop): (Vec<PathBuf>, bool) = ctx.input(|input| {
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
            self.accept_drop(dropped_paths, drop_locked);
        }
        if hovering_drop {
            let (title, subtitle) = if drop_locked {
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
                        .rounding(egui::Rounding::same(FLUENT_RADIUS_LG))
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


        if let Some(until) = self.error_flash_until {
            if now >= until {
                self.error_flash_until = None;
            } else {
                ctx.request_repaint_after(until.saturating_duration_since(now));
            }
        }

        if starting {
            ctx.request_repaint_after(STARTING_REPAINT);
        } else if running {
            ctx.request_repaint_after(if paused {
                PAUSED_REPAINT
            } else {
                RUNNING_REPAINT
            });
        } else if engine_running {
            ctx.request_repaint_after(STARTING_REPAINT);
        } else {
            ctx.request_repaint_after(PATH_CHECK_INTERVAL);
        }

        let key = self.paths_key();
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

        let path_error_count = self.path_errors.len();
        let mut start_disabled =
            start_disabled_reason(&self.source, self.dests.len(), path_error_count);
        if start_disabled.is_none() && self.validated_paths_key != Some(key) {
            start_disabled = Some("Validando rutas…");
        }
        let ready_to_start = start_disabled.is_none();

        let save_copy_shortcut = ctx.input_mut(|input| {
            input.consume_shortcut(&egui::KeyboardShortcut::new(
                egui::Modifiers::CTRL,
                egui::Key::S,
            ))
        });
        if save_copy_shortcut {
            self.save_copy_dialog();
        }

        let load_copy_shortcut = !busy
            && ctx.input_mut(|input| {
                input.consume_shortcut(&egui::KeyboardShortcut::new(
                    egui::Modifiers::CTRL | egui::Modifiers::SHIFT,
                    egui::Key::O,
                ))
            });
        if load_copy_shortcut {
            self.load_copy_dialog();
        }

        let escape_pressed = ctx.input(|input| input.key_pressed(egui::Key::Escape));
        if escape_pressed {
            self.show_settings = false;
            self.show_credits = false;
        }

        if !busy {
            let open_source = ctx.input_mut(|input| input.consume_shortcut(&egui::KeyboardShortcut::new(egui::Modifiers::CTRL, egui::Key::O)));
            if open_source {
                if let Some(path) = self.pick_source_dir() {
                    self.source = path;
                }
            }

            let add_destinations = ctx.input_mut(|input| input.consume_shortcut(&egui::KeyboardShortcut::new(egui::Modifiers::CTRL, egui::Key::D)));
            if add_destinations {
                if let Some(paths) = self.pick_destination_dirs() {
                    let added = self.add_destinations(paths);
                    if added > 0 {
                        self.status = count_label(added as u64, "destino agregado", "destinos agregados");
                    }
                }
            }
        }

        egui::TopBottomPanel::top("header_v2").show(ctx, |ui| {
            ui.add_space(SPACING_XS);
            ui.horizontal(|ui| {
                ui.label(RichText::new("RepartoCopier").strong().size(19.0));
                if ui.available_width() > 430.0 {
                    ui.label(
                        RichText::new("Archivo o carpeta · múltiples destinos")
                            .color(Theme::muted(self.use_light_theme)),
                    );
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let settings = egui::Button::new(RichText::new("⚙").size(17.0)).frame(false);
                    if ui.add(settings).on_hover_text("Ajustes").clicked() {
                        self.show_settings = true;
                    }
                    ui.menu_button(RichText::new("Copia").strong(), |ui| {
                        let can_save = !self.source.trim().is_empty() && !self.dests.is_empty();
                        if ui
                            .add_enabled(can_save, egui::Button::new("Salvar copia…   Ctrl+S"))
                            .on_hover_text("Salva origen, destinos y opciones. El progreso seguro permanece en cada destino.")
                            .clicked()
                        {
                            ui.close_menu();
                            self.save_copy_dialog();
                        }
                        if ui
                            .add_enabled(!busy, egui::Button::new("Cargar copia…   Ctrl+Shift+O"))
                            .on_hover_text("Carga origen, destinos y opciones. No inicia la copia automáticamente.")
                            .clicked()
                        {
                            ui.close_menu();
                            self.load_copy_dialog();
                        }
                    });
                    if starting {
                        ui.weak("Validando…");
                    }
                });
            });
            ui.add_space(SPACING_XS);
        });

        egui::TopBottomPanel::bottom("footer_v2").show(ctx, |ui| {
            ui.add_space(2.0);
            ui.horizontal(|ui| {
                let (status, color): (&str, Color32) = if self.error_flash_until.is_some() {
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
                    (
                        "Verificando integridad…",
                        Theme::verify(self.use_light_theme),
                    )
                } else {
                    (self.status.as_str(), Theme::muted(self.use_light_theme))
                };
                let max_chars = ((ui.available_width() / 8.0) as usize).clamp(32, 88);
                ui.colored_label(color, compact_path(status, max_chars));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .add_sized([82.0, FLUENT_CONTROL_HEIGHT], egui::Button::new("Acerca de"))
                        .clicked()
                    {
                        self.show_credits = true;
                    }
                });
            });
            ui.add_space(2.0);
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            ui.add_space(2.0);

            let stacked_source = source_layout_stacked(ui.available_width());
            if stacked_source {
                ui.vertical(|ui| {
                    ui.label(
                        RichText::new("ORIGEN")
                            .size(10.5)
                            .strong()
                            .color(Theme::muted(self.use_light_theme)),
                    );
                    ui.add_enabled_ui(!busy, |ui| {
                        let button_width = 88.0;
                        let field_width = (ui.available_width() - button_width - SPACING_SM).max(150.0);
                        ui.horizontal(|ui| {
                            ui.add_sized(
                                [field_width, FLUENT_CONTROL_HEIGHT],
                                egui::TextEdit::singleline(&mut self.source)
                                    .hint_text("Archivo o carpeta que quieres copiar"),
                            );
                            ui.allocate_ui_with_layout(
                                egui::vec2(button_width, FLUENT_CONTROL_HEIGHT),
                                egui::Layout::left_to_right(egui::Align::Center),
                                |ui| self.source_picker_menu(ui),
                            );
                        });
                    });
                });
            } else {
                ui.horizontal(|ui| {
                    ui.label(
                        RichText::new("ORIGEN")
                            .size(10.5)
                            .strong()
                            .color(Theme::muted(self.use_light_theme)),
                    );
                    let button_width = 88.0;
                    let label_width = 54.0;
                    let field_width =
                        (ui.available_width() - button_width - label_width - SPACING_SM).max(150.0);
                    ui.add_enabled_ui(!busy, |ui| {
                        ui.add_sized(
                            [field_width, FLUENT_CONTROL_HEIGHT],
                            egui::TextEdit::singleline(&mut self.source)
                                .hint_text("Archivo o carpeta que quieres copiar"),
                        );
                        ui.allocate_ui_with_layout(
                            egui::vec2(button_width, FLUENT_CONTROL_HEIGHT),
                            egui::Layout::left_to_right(egui::Align::Center),
                            |ui| self.source_picker_menu(ui),
                        );
                    });
                });
            }

            ui.add_space(SPACING_XS);

            ui.horizontal(|ui| {
                ui.label(
                    RichText::new(format!("DESTINOS  ({})", self.dests.len()))
                        .size(10.5)
                        .strong()
                        .color(Theme::muted(self.use_light_theme)),
                );
                ui.add_enabled_ui(!busy, |ui| {
                    if ui
                        .add_sized(
                            [154.0, FLUENT_CONTROL_HEIGHT],
                            egui::Button::new(
                                RichText::new("+  Agregar destinos")
                                    .strong()
                                    .color(Theme::accent(self.use_light_theme)),
                            )
                            .fill(Theme::selected(self.use_light_theme))
                            .stroke(egui::Stroke::new(
                                1.0_f32,
                                Theme::border(self.use_light_theme),
                            ))
                            .rounding(egui::Rounding::same(FLUENT_RADIUS_MD)),
                        )
                        .on_hover_text(
                            "Selecciona uno o varios destinos · Ctrl+D. Usa Ctrl o Shift para selección múltiple.",
                        )
                        .clicked()
                    {
                        if let Some(paths) = self.pick_destination_dirs() {
                            let added = self.add_destinations(paths);
                            if added > 0 {
                                self.status = count_label(
                                    added as u64,
                                    "destino agregado",
                                    "destinos agregados",
                                );
                            }
                        }
                    }
                });
                if self.dests.len() > 1 && !busy {
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui
                            .add_sized([84.0, FLUENT_CONTROL_HEIGHT], egui::Button::new("Quitar todos"))
                            .on_hover_text("Eliminar todos los destinos")
                            .clicked()
                        {
                            self.dests.clear();
                            self.status = "Se eliminaron todos los destinos".to_owned();
                        }
                    });
                }
            });

            if !self.dests.is_empty() {
                ui.add_space(2.0);
                let mut remove = None;
                ui.horizontal_wrapped(|ui| {
                    ui.spacing_mut().item_spacing = egui::vec2(5.0, 4.0);
                    for (index, dest) in self.dests.iter().enumerate() {
                        let progress = snaps.get(index);
                        let (state_label, state_color) = progress
                            .map(|p| {
                                phase_label(
                                    p.phase,
                                    p.files_err,
                                    self.use_light_theme,
                                    paused,
                                )
                            })
                            .unwrap_or(("LISTO", Theme::muted(self.use_light_theme)));

                        let shown_destination = progress
                            .map(|progress| display_path(&progress.label))
                            .unwrap_or_else(|| effective_destination_label(&self.source, dest));

                        let chip = egui::Frame::none()
                            .fill(Theme::card(self.use_light_theme))
                            .stroke(egui::Stroke::new(
                                1.0_f32,
                                Theme::border(self.use_light_theme),
                            ))
                            .rounding(egui::Rounding::same(FLUENT_RADIUS_SM))
                            .inner_margin(egui::Margin::symmetric(9.0, 5.0))
                            .show(ui, |ui| {
                                ui.horizontal(|ui| {
                                    ui.spacing_mut().item_spacing.x = 4.0;
                                    ui.label(
                                        RichText::new(format!("{:02}", index + 1))
                                            .size(10.0)
                                            .color(Theme::muted(self.use_light_theme)),
                                    );
                                    ui.label(
                                        RichText::new(compact_path(&shown_destination, 24))
                                            .strong()
                                            .size(11.0),
                                    )
                                    .on_hover_text(&shown_destination);
                                    ui.colored_label(
                                        state_color,
                                        RichText::new(state_label).size(10.0).strong(),
                                    );
                                    if !busy
                                        && ui
                                            .add(
                                                egui::Button::new(RichText::new("×").size(13.0))
                                                    .frame(false),
                                            )
                                            .on_hover_text("Quitar destino")
                                            .clicked()
                                    {
                                        remove = Some(index);
                                    }
                                });
                            });
                        chip.response.on_hover_text(&shown_destination);
                    }
                });
                if let Some(index) = remove {
                    self.dests.remove(index);
                }
            }

            if let Some(first_error) = self.path_errors.first() {
                let suffix = if path_error_count > 1 {
                    format!(" · +{} más", path_error_count - 1)
                } else {
                    String::new()
                };
                ui.colored_label(
                    Theme::warning(self.use_light_theme),
                    RichText::new(format!("⚠ {first_error}{suffix}")).size(11.5),
                )
                .on_hover_text(self.path_errors.join("\n"));
            }

            ui.add_space(2.0);
            ui.separator();
            ui.add_space(2.0);

            ui.horizontal(|ui| {
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

            if let Some(job) = &self.job {
                if !snaps.is_empty() {
                    let average = snaps
                        .iter()
                        .map(|progress| {
                            f64::from(progress_fraction(
                                progress.written,
                                progress.total,
                                progress.phase,
                            ))
                        })
                        .sum::<f64>()
                        / snaps.len() as f64;

                    let aggregate_write_bps: f64 = snaps
                        .iter()
                        .map(|progress| {
                            visible_bps(
                                progress.bps_recent,
                                progress.last_tick,
                                progress.phase,
                                paused,
                            )
                        })
                        .sum();
                    let fanout_bps = logical_fanout_bps(
                        snaps
                            .iter()
                            .filter(|progress| progress.phase == DestPhase::Copying)
                            .map(|progress| {
                                visible_bps(
                                    progress.bps_recent,
                                    progress.last_tick,
                                    progress.phase,
                                    paused,
                                )
                            }),
                    );

                    let eta = if paused {
                        "En pausa".to_owned()
                    } else if all_terminal {
                        "—".to_owned()
                    } else {
                        let mut known = true;
                        let mut eta_secs = 0.0f64;
                        for progress in &snaps {
                            let remaining = progress.total.saturating_sub(progress.written);
                            if remaining == 0
                                || matches!(
                                    progress.phase,
                                    DestPhase::Done
                                        | DestPhase::Failed
                                        | DestPhase::Cancelled
                                )
                            {
                                continue;
                            }
                            if progress.bps <= 0.0 {
                                known = false;
                                break;
                            }
                            eta_secs = eta_secs.max(remaining as f64 / progress.bps);
                        }
                        if known {
                            format_duration(eta_secs)
                        } else {
                            "—".to_owned()
                        }
                    };

                    let terminal_errors = snaps
                        .iter()
                        .any(|dest| dest.phase == DestPhase::Failed || dest.files_err > 0);
                    let terminal_cancelled = snaps
                        .iter()
                        .any(|dest| dest.phase == DestPhase::Cancelled);

                    ui.add_space(SPACING_XS);
                    card_frame(self.use_light_theme).show(ui, |ui| {
                        ui.horizontal(|ui| {
                            ui.label(RichText::new("Progreso general").strong().size(12.0));
                            let (label, color) = if paused {
                                ("PAUSADO", Theme::warning(self.use_light_theme))
                            } else if verifying {
                                (
                                    "VERIFICANDO INTEGRIDAD",
                                    Theme::verify(self.use_light_theme),
                                )
                            } else if all_terminal && terminal_cancelled {
                                ("CANCELADO", Theme::muted(self.use_light_theme))
                            } else if all_terminal && terminal_errors {
                                (
                                    "FINALIZADO CON ERRORES",
                                    Theme::warning(self.use_light_theme),
                                )
                            } else if all_terminal {
                                ("COMPLETO", Theme::success(self.use_light_theme))
                            } else {
                                ("COPIANDO", Theme::accent(self.use_light_theme))
                            };
                            ui.colored_label(color, RichText::new(label).strong().size(10.5));
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    let total_size =
                                        format_bytes(job.bytes_total.load(Ordering::Relaxed));
                                    if verifying {
                                        ui.weak(format!("{total_size} · validación final BLAKE3"));
                                    } else {
                                        ui.weak(format!("{total_size} · ETA copia {eta}"));
                                    }
                                    let speed_text = if verifying {
                                        "FAN-OUT —".to_owned()
                                    } else {
                                        format!("FAN-OUT {}", format_bps(fanout_bps))
                                    };
                                    ui.label(RichText::new(speed_text).strong().size(11.5))
                                        .on_hover_text(format!(
                                            "Escritura agregada a destinos: {}",
                                            format_bps(aggregate_write_bps)
                                        ));
                                },
                            );
                        });
                        ui.add(
                            egui::ProgressBar::new(average as f32)
                                .desired_height(14.0)
                                .fill(
                                    if all_terminal
                                        && !terminal_errors
                                        && !terminal_cancelled
                                    {
                                        Theme::success(self.use_light_theme)
                                    } else {
                                        Theme::accent(self.use_light_theme)
                                    },
                                )
                                .show_percentage(),
                        );
                    });

                    ui.add_space(SPACING_XS);

                    const CARD_ROW_HEIGHT: f32 = 112.0;
                    const ROW_SPACING: f32 = 6.0;

                    let grid_width = ui.available_width();
                    let columns: usize = if grid_width >= 1320.0 && snaps.len() >= 3 {
                        3
                    } else if grid_width >= 760.0 && snaps.len() > 1 {
                        2
                    } else {
                        1
                    };
                    let cell_width = if columns > 1 {
                        ((grid_width - SPACING_SM * (columns.saturating_sub(1) as f32))
                            / columns as f32)
                            .max(260.0)
                    } else {
                        grid_width.max(280.0)
                    };
                    let rows = snaps.len().div_ceil(columns);
                    let estimated_grid_height = rows as f32 * CARD_ROW_HEIGHT
                        + rows.saturating_sub(1) as f32 * ROW_SPACING;
                    let available_grid_height = ui.available_height().max(96.0);

                    let render_grid = |ui: &mut egui::Ui| {
                        egui::Grid::new("progress_grid_v5")
                            .num_columns(columns)
                            .spacing(egui::vec2(SPACING_SM, ROW_SPACING))
                            .show(ui, |ui| {
                                for row in snaps.chunks(columns) {
                                    for progress in row {
                                        let fraction = progress_fraction(
                                            progress.written,
                                            progress.total,
                                            progress.phase,
                                        );
                                        let (label, color) = phase_label(
                                            progress.phase,
                                            progress.files_err,
                                            self.use_light_theme,
                                            paused,
                                        );
                                        let shown_label = display_path(&progress.label);

                                        ui.allocate_ui_with_layout(
                                            egui::vec2(cell_width, 0.0),
                                            egui::Layout::top_down(egui::Align::Min),
                                            |ui| {
                                                card_frame(self.use_light_theme).show(ui, |ui| {
                                                    ui.set_width((cell_width - 20.0).max(240.0));
                                                    ui.set_min_height(72.0);
                                                    ui.horizontal(|ui| {
                                                        ui.label(
                                                            RichText::new(compact_path(
                                                                &shown_label,
                                                                36,
                                                            ))
                                                            .strong()
                                                            .size(11.5),
                                                        )
                                                        .on_hover_text(&shown_label);
                                                        ui.with_layout(
                                                            egui::Layout::right_to_left(
                                                                egui::Align::Center,
                                                            ),
                                                            |ui| {
                                                                ui.colored_label(
                                                                    color,
                                                                    RichText::new(label)
                                                                        .size(10.0)
                                                                        .strong(),
                                                                );
                                                            },
                                                        );
                                                    });
                                                    ui.add(
                                                        egui::ProgressBar::new(fraction)
                                                            .desired_height(11.0)
                                                            .fill(
                                                                if progress.phase
                                                                    == DestPhase::Done
                                                                    && progress.files_err == 0
                                                                {
                                                                    Theme::success(
                                                                        self.use_light_theme,
                                                                    )
                                                                } else {
                                                                    Theme::accent(
                                                                        self.use_light_theme,
                                                                    )
                                                                },
                                                            )
                                                            .show_percentage(),
                                                    );
                                                    let speed = visible_bps(
                                                        progress.bps_recent,
                                                        progress.last_tick,
                                                        progress.phase,
                                                        paused,
                                                    );
                                                    ui.horizontal(|ui| {
                                                        ui.weak(format_bps(speed));
                                                        ui.with_layout(
                                                            egui::Layout::right_to_left(
                                                                egui::Align::Center,
                                                            ),
                                                            |ui| {
                                                                ui.weak(format!(
                                                                    "{} hechos · {} omit. · {} err.",
                                                                    progress.files_done,
                                                                    progress.files_skip,
                                                                    progress.files_err
                                                                ));
                                                            },
                                                        );
                                                    });

                                                    let last_file = if progress.phase
                                                        != DestPhase::Copying
                                                        || progress.last_file.is_empty()
                                                    {
                                                        None
                                                    } else {
                                                        Some(display_path(&progress.last_file))
                                                    };
                                                    let last_file_text = last_file
                                                        .as_deref()
                                                        .map(|path| compact_path(path, 48))
                                                        .unwrap_or_else(|| "\u{00A0}".to_owned());
                                                    let response = ui.weak(
                                                        RichText::new(last_file_text).size(9.5),
                                                    );
                                                    if let Some(last_file) = last_file {
                                                        response.on_hover_text(last_file);
                                                    }
                                                });
                                            },
                                        );
                                    }
                                    ui.end_row();
                                }
                            });
                    };

                    if estimated_grid_height <= available_grid_height {
                        render_grid(ui);
                    } else {
                        egui::ScrollArea::vertical()
                            .id_salt("progress_grid_zone_v5")
                            .max_height(available_grid_height)
                            .auto_shrink([false, false])
                            .scroll_bar_visibility(
                                egui::scroll_area::ScrollBarVisibility::VisibleWhenNeeded,
                            )
                            .show(ui, render_grid);
                    }
                }
            } else if starting {
                ui.add_space(SPACING_XS);
                ui.weak("Analizando origen y destinos…");
            } else {
                ui.add_space(SPACING_XS);
                ui.weak("Selecciona un origen y uno o más destinos para comenzar.");
            }
        });

        self.draw_settings(ctx);
        self.draw_about(ctx);

        if !busy && all_terminal {
            let ok = snaps
                .iter()
                .filter(|dest| dest.phase == DestPhase::Done && dest.files_err == 0)
                .count();
            let with_errors = snaps
                .iter()
                .filter(|dest| dest.phase == DestPhase::Done && dest.files_err > 0)
                .count();
            let failed = snaps
                .iter()
                .filter(|dest| dest.phase == DestPhase::Failed)
                .count();
            let cancelled = snaps
                .iter()
                .filter(|dest| dest.phase == DestPhase::Cancelled)
                .count();

            self.status = if cancelled > 0 {
                format!("Cancelado · {cancelled} destino(s)")
            } else if failed > 0 || with_errors > 0 {
                format!(
                    "Finalizado con errores · {ok} correcto(s) · {} con problemas",
                    failed + with_errors
                )
            } else {
                format!("Completado · {ok}/{} sin errores", snaps.len())
            };
        }
    }
}
