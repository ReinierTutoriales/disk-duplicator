impl eframe::App for CopierApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        if !self.fonts_initialized {
            setup_fonts(ctx);
            self.fonts_initialized = true;
        }
        self.poll_startup();

        if self.last_theme_check.elapsed() >= THEME_CHECK_INTERVAL {
            if self.theme_preference == ThemePreference::System {
                self.use_light_theme = detect_system_theme();
            }
            refresh_system_accent();
            self.last_theme_check = Instant::now();
            self.applied_theme = None;
        }

        if self.applied_theme != Some(self.use_light_theme) {
            apply_theme(ctx, self.use_light_theme);
            self.applied_theme = Some(self.use_light_theme);
        }

        let snaps = self.job.as_ref().map(|job| job.snapshot()).unwrap_or_default();
        let all_terminal = !snaps.is_empty()
            && snaps.iter().all(|dest| {
                matches!(dest.phase, DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled)
            });
        let starting = self.starting();
        let running = self.running_job() && !all_terminal;
        let paused = running && self.job.as_ref().is_some_and(|job| job.is_paused());
        let verifying = running && snaps.iter().any(|dest| dest.phase == DestPhase::Verifying);
        let busy = starting || running;
        let now = Instant::now();

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
            ctx.request_repaint_after(if paused { PAUSED_REPAINT } else { RUNNING_REPAINT });
        } else {
            ctx.request_repaint_after(THEME_CHECK_INTERVAL);
        }

        let key = self.paths_key();
        if key != self.paths_key || self.last_path_check.elapsed() >= PATH_CHECK_INTERVAL {
            self.paths_key = key;
            self.last_path_check = Instant::now();
            self.path_errors = if busy { Vec::new() } else { self.validate_paths() };
        }

        let path_error_count = self.path_errors.len();
        let ready_to_start = !self.source.trim().is_empty()
            && !self.dests.is_empty()
            && path_error_count == 0;

        egui::TopBottomPanel::top("header_v2").show(ctx, |ui| {
            ui.add_space(SPACING_XS);
            ui.horizontal(|ui| {
                ui.heading(RichText::new("RepartoCopier").strong());
                if ui.available_width() > 430.0 {
                    ui.label(
                        RichText::new("Una carpeta · múltiples destinos")
                            .color(Theme::muted(self.use_light_theme)),
                    );
                }
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    let settings = egui::Button::new(RichText::new("⚙").size(17.0)).frame(false);
                    if ui.add(settings).on_hover_text("Ajustes").clicked() {
                        self.show_settings = true;
                    }
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
                } else if paused {
                    ("Copia en pausa", Theme::warning(self.use_light_theme))
                } else if verifying {
                    ("Verificando integridad…", Theme::verify(self.use_light_theme))
                } else {
                    (self.status.as_str(), Theme::muted(self.use_light_theme))
                };
                let max_chars = ((ui.available_width() / 8.0) as usize).clamp(24, 88);
                ui.colored_label(color, compact_path(status, max_chars));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui.add_sized([82.0, 24.0], egui::Button::new("Acerca de")).clicked() {
                        self.show_credits = true;
                    }
                });
            });
            ui.add_space(2.0);
        });

        egui::CentralPanel::default().show(ctx, |ui| {
            egui::ScrollArea::vertical()
                .auto_shrink([false, false])
                .show(ui, |ui| {
                    ui.add_space(SPACING_XS);
                    ui.label(
                        RichText::new("ORIGEN")
                            .size(11.5)
                            .strong()
                            .color(Theme::muted(self.use_light_theme)),
                    );

                    ui.horizontal(|ui| {
                        let button_width = 92.0;
                        let field_width = (ui.available_width() - button_width - SPACING_SM).max(160.0);
                        ui.add_enabled_ui(!busy, |ui| {
                            ui.add_sized(
                                [field_width, 32.0],
                                egui::TextEdit::singleline(&mut self.source)
                                    .hint_text("Selecciona la carpeta que quieres copiar"),
                            );
                            if ui
                                .add_sized([button_width, 32.0], egui::Button::new("Examinar"))
                                .on_hover_text("Seleccionar carpeta de origen")
                                .clicked()
                            {
                                if let Some(path) = self.pick_source_dir() {
                                    self.source = path;
                                }
                            }
                        });
                    });

                    ui.add_space(SPACING_MD);
                    ui.horizontal(|ui| {
                        ui.label(
                            RichText::new(format!("DESTINOS  ({})", self.dests.len()))
                                .size(11.5)
                                .strong()
                                .color(Theme::muted(self.use_light_theme)),
                        );
                        ui.add_enabled_ui(!busy, |ui| {
                            if ui
                                .add_sized([132.0, 28.0], egui::Button::new("+ Agregar destinos"))
                                .on_hover_text("Selecciona uno o varios destinos. Usa Ctrl o Shift para selección múltiple.")
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
                                    .add_sized([88.0, 28.0], egui::Button::new("Quitar todos"))
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
                        ui.add_space(SPACING_XS);
                        let mut remove = None;
                        ui.horizontal_wrapped(|ui| {
                            ui.spacing_mut().item_spacing = egui::vec2(6.0, 6.0);
                            for (index, dest) in self.dests.iter().enumerate() {
                                let progress = snaps.get(index);
                                let (state_label, state_color) = progress
                                    .map(|p| phase_label(p.phase, p.files_err, self.use_light_theme, paused))
                                    .unwrap_or(("LISTO", Theme::muted(self.use_light_theme)));
                                let short = PathBuf::from(dest)
                                    .file_name()
                                    .and_then(|name| name.to_str())
                                    .filter(|name| !name.is_empty())
                                    .map(str::to_owned)
                                    .unwrap_or_else(|| dest.trim_end_matches(['\\', '/']).to_owned());
                                let short = if short.is_empty() { dest.clone() } else { short };

                                let chip = egui::Frame::none()
                                    .fill(Theme::card(self.use_light_theme))
                                    .stroke(egui::Stroke::new(1.0_f32, Theme::border(self.use_light_theme)))
                                    .rounding(egui::Rounding::same(7.0))
                                    .inner_margin(egui::Margin::symmetric(9.0, 5.0))
                                    .show(ui, |ui| {
                                        ui.horizontal(|ui| {
                                            ui.label(
                                                RichText::new(format!("{:02}", index + 1))
                                                    .size(10.5)
                                                    .color(Theme::muted(self.use_light_theme)),
                                            );
                                            ui.label(RichText::new(compact_path(&short, 22)).strong())
                                                .on_hover_text(dest);
                                            ui.colored_label(state_color, RichText::new(state_label).size(10.5).strong());
                                            if !busy
                                                && ui
                                                    .add(egui::Button::new(RichText::new("×").size(14.0)).frame(false))
                                                    .on_hover_text("Quitar destino")
                                                    .clicked()
                                            {
                                                remove = Some(index);
                                            }
                                        });
                                    });
                                chip.response.on_hover_text(dest);
                            }
                        });
                        if let Some(index) = remove {
                            self.dests.remove(index);
                        }
                    }

                    if path_error_count > 0 {
                        ui.colored_label(
                            Theme::warning(self.use_light_theme),
                            format!("⚠ {path_error_count} problema(s) de ruta"),
                        )
                        .on_hover_text(self.path_errors.join("\n"));
                    }

                    ui.add_space(SPACING_XS);
                    ui.separator();
                    ui.add_space(SPACING_XS);
                    ui.horizontal_wrapped(|ui| {
                        ui.add_enabled_ui(!busy, |ui| {
                            ui.checkbox(&mut self.skip_same, "Omitir archivos iguales")
                                .on_hover_text("Evita copiar de nuevo archivos que ya coinciden.");
                            ui.checkbox(&mut self.keep_going, "Continuar si un destino falla")
                                .on_hover_text("Los demás destinos continúan si uno presenta un error.");
                        });
                    });

                    ui.add_space(SPACING_SM);
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if running {
                            if let Some(job) = &self.job {
                                if ui.add_sized([88.0, 30.0], egui::Button::new("Cancelar")).clicked() {
                                    job.request_cancel();
                                }
                                let pause_label = if paused { "Continuar" } else { "Pausar" };
                                if ui.add_sized([88.0, 30.0], egui::Button::new(pause_label)).clicked() {
                                    job.set_paused(!paused);
                                }
                            }
                        } else if !starting {
                            let button = egui::Button::new(RichText::new("Iniciar copia").strong());
                            if ui.add_enabled(ready_to_start, button).clicked() {
                                self.start();
                            }
                        }
                    });

                    if starting {
                        ui.add_space(SPACING_LG);
                        ui.vertical_centered(|ui| {
                            ui.spinner();
                            ui.label(RichText::new("Preparando la copia…").strong());
                        });
                    } else if let Some(job) = &self.job {
                        if !snaps.is_empty() {
                            let average = snaps
                                .iter()
                                .map(|progress| {
                                    if progress.total == 0 {
                                        if progress.phase == DestPhase::Done { 1.0 } else { 0.0 }
                                    } else {
                                        progress.written as f64 / progress.total as f64
                                    }
                                })
                                .sum::<f64>()
                                / snaps.len() as f64;

                            let total_bps = if paused {
                                0.0
                            } else {
                                snaps.iter()
                                    .map(|progress| shown_bps(progress.bps_recent, progress.last_tick))
                                    .sum()
                            };

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
                                        || matches!(progress.phase, DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled)
                                    {
                                        continue;
                                    }
                                    if progress.bps <= 0.0 {
                                        known = false;
                                        break;
                                    }
                                    eta_secs = eta_secs.max(remaining as f64 / progress.bps);
                                }
                                if known { format_duration(eta_secs) } else { "—".to_owned() }
                            };

                            let terminal_errors = snaps.iter().any(|dest| {
                                dest.phase == DestPhase::Failed || dest.files_err > 0
                            });
                            let terminal_cancelled = snaps.iter().any(|dest| dest.phase == DestPhase::Cancelled);

                            ui.add_space(SPACING_MD);
                            card_frame(self.use_light_theme).show(ui, |ui| {
                                ui.horizontal(|ui| {
                                    ui.label(RichText::new("Progreso general").strong());
                                    let (label, color) = if paused {
                                        ("PAUSADO", Theme::warning(self.use_light_theme))
                                    } else if verifying {
                                        ("VERIFICANDO INTEGRIDAD", Theme::verify(self.use_light_theme))
                                    } else if all_terminal && terminal_cancelled {
                                        ("CANCELADO", Theme::muted(self.use_light_theme))
                                    } else if all_terminal && terminal_errors {
                                        ("FINALIZADO CON ERRORES", Theme::warning(self.use_light_theme))
                                    } else if all_terminal {
                                        ("COMPLETO", Theme::success(self.use_light_theme))
                                    } else {
                                        ("COPIANDO", Theme::accent(self.use_light_theme))
                                    };
                                    ui.colored_label(color, RichText::new(label).strong());
                                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                        ui.weak(format!("{} · {eta}", format_bytes(job.bytes_total.load(Ordering::Relaxed))));
                                        ui.label(RichText::new(format_bps(total_bps)).strong());
                                    });
                                });
                                ui.add(
                                    egui::ProgressBar::new(average as f32)
                                        .desired_height(16.0)
                                        .fill(if all_terminal && !terminal_errors && !terminal_cancelled {
                                            Theme::success(self.use_light_theme)
                                        } else {
                                            Theme::accent(self.use_light_theme)
                                        })
                                        .show_percentage(),
                                );
                            });

                            ui.add_space(SPACING_SM);
                            let columns = if ui.available_width() >= 760.0 && snaps.len() > 1 { 2 } else { 1 };
                            egui::Grid::new("progress_grid_v3")
                                .num_columns(columns)
                                .spacing(egui::vec2(SPACING_SM, SPACING_SM))
                                .show(ui, |ui| {
                                    for (index, progress) in snaps.iter().enumerate() {
                                        let fraction = if progress.total == 0 {
                                            if progress.phase == DestPhase::Done { 1.0 } else { 0.0 }
                                        } else {
                                            (progress.written as f32 / progress.total as f32).clamp(0.0, 1.0)
                                        };
                                        let (label, color) = phase_label(
                                            progress.phase,
                                            progress.files_err,
                                            self.use_light_theme,
                                            paused,
                                        );
                                        let cell_width = if columns == 2 {
                                            ((ui.available_width() - SPACING_SM) / 2.0).max(300.0)
                                        } else {
                                            ui.available_width().max(300.0)
                                        };

                                        ui.allocate_ui_with_layout(
                                            egui::vec2(cell_width, 72.0),
                                            egui::Layout::top_down(egui::Align::Min),
                                            |ui| {
                                                card_frame(self.use_light_theme).show(ui, |ui| {
                                                    ui.set_width((cell_width - 20.0).max(260.0));
                                                    ui.horizontal(|ui| {
                                                        ui.label(RichText::new(compact_path(&progress.label, 34)).strong())
                                                            .on_hover_text(&progress.label);
                                                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                                            ui.colored_label(color, RichText::new(label).size(10.5).strong());
                                                        });
                                                    });
                                                    ui.add(
                                                        egui::ProgressBar::new(fraction)
                                                            .desired_height(12.0)
                                                            .fill(if progress.phase == DestPhase::Done && progress.files_err == 0 {
                                                                Theme::success(self.use_light_theme)
                                                            } else {
                                                                Theme::accent(self.use_light_theme)
                                                            })
                                                            .show_percentage(),
                                                    );
                                                    let speed = if paused || matches!(progress.phase, DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled) {
                                                        0.0
                                                    } else {
                                                        shown_bps(progress.bps_recent, progress.last_tick)
                                                    };
                                                    ui.horizontal(|ui| {
                                                        ui.weak(format_bps(speed));
                                                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                                            ui.weak(format!(
                                                                "{} ✓  ·  {} omit.  ·  {} err.",
                                                                progress.files_done, progress.files_skip, progress.files_err
                                                            ));
                                                        });
                                                    });
                                                });
                                            },
                                        );

                                        if (index + 1) % columns == 0 {
                                            ui.end_row();
                                        }
                                    }
                                    if snaps.len() % columns != 0 {
                                        ui.end_row();
                                    }
                                });
                        }
                    } else {
                        ui.add_space(SPACING_LG);
                        ui.vertical_centered(|ui| {
                            ui.label(RichText::new("Listo para copiar").size(20.0).strong());
                            ui.label(
                                RichText::new("Selecciona un origen y agrega uno o más destinos.")
                                    .color(Theme::muted(self.use_light_theme)),
                            );
                        });
                    }
                });
        });

        self.draw_settings(ctx);
        self.draw_about(ctx);

        if !busy && all_terminal {
            let ok = snaps.iter()
                .filter(|dest| dest.phase == DestPhase::Done && dest.files_err == 0)
                .count();
            let with_errors = snaps.iter()
                .filter(|dest| dest.phase == DestPhase::Done && dest.files_err > 0)
                .count();
            let failed = snaps.iter().filter(|dest| dest.phase == DestPhase::Failed).count();
            let cancelled = snaps.iter().filter(|dest| dest.phase == DestPhase::Cancelled).count();

            self.status = if cancelled > 0 {
                format!("Cancelado · {cancelled} destino(s)")
            } else if failed > 0 || with_errors > 0 {
                format!("Finalizado con errores · {ok} correcto(s) · {} con problemas", failed + with_errors)
            } else {
                format!("Completado · {ok}/{} sin errores", snaps.len())
            };
        }
    }
}
