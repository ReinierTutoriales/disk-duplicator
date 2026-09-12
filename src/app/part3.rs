impl CopierApp {
    pub fn new_with_source(launch_source: Option<PathBuf>) -> Self {
        refresh_system_accent();
        let settings = load_settings();
        let use_light_theme = resolve_theme(settings.theme);
        let source = launch_source
            .map(|path| path.to_string_lossy().into_owned())
            .unwrap_or_default();
        let status = if source.is_empty() {
            "Listo para copiar un archivo o carpeta a múltiples destinos".to_owned()
        } else {
            "Origen precargado · agrega uno o más destinos".to_owned()
        };
        Self {
            source,
            dests: Vec::new(),
            skip_same: true,
            keep_going: true,
            status,
            job: None,
            workers: Vec::new(),
            startup_rx: None,
            show_credits: false,
            show_settings: false,
            pending_drop: None,
            error_flash_until: None,
            theme_preference: settings.theme,
            use_light_theme,
            last_theme_check: Instant::now(),
            applied_theme: None,
            fonts_initialized: false,
            path_errors: Vec::new(),
            last_path_check: Instant::now(),
            paths_key: u64::MAX,
        }
    }

    fn copy_snapshot(&self) -> Result<CopyPlan, String> {
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

    fn starting(&self) -> bool {
        self.startup_rx.is_some()
    }

    fn running_job(&self) -> bool {
        self.job
            .as_ref()
            .is_some_and(|job| job.running.load(Ordering::Relaxed))
    }

    fn existing_dir(path: &str) -> Option<PathBuf> {
        let path = PathBuf::from(path.trim());
        (path.is_dir()).then_some(path)
    }

    fn pick_source_dir(&self) -> Option<String> {
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

    fn pick_destination_dirs(&self) -> Option<Vec<String>> {
        let mut dialog = rfd::FileDialog::new().set_title("Seleccionar uno o más destinos");
        let start = self
            .dests
            .last()
            .and_then(|dest| Self::existing_dir(dest))
            .or_else(|| {
                Self::existing_dir(&self.source).and_then(|source| {
                    source.parent().map(PathBuf::from).filter(|parent| parent.is_dir())
                })
            });
        if let Some(start) = start {
            dialog = dialog.set_directory(start);
        }
        dialog.pick_folders().map(|paths| {
            paths
                .into_iter()
                .map(|path| path.to_string_lossy().into_owned())
                .collect()
        })
    }

    fn add_destinations(&mut self, selected: Vec<String>) -> usize {
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
        .default_width(430.0)
        .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
        .frame(window_frame(ctx, self.use_light_theme))
        .show(ctx, |ui| match &pending {
            PendingDrop::Folder(path) => {
                let shown = display_path(&path.to_string_lossy());
                ui.label(RichText::new(compact_path(&shown, 64)).strong());
                ui.label(RichText::new("¿Qué quieres hacer con esta carpeta?").color(Theme::muted(self.use_light_theme)));
                ui.add_space(SPACING_SM);
                if drop_action_button(
                    ui,
                    "Usar como origen y elegir destinos",
                    true,
                    self.use_light_theme,
                )
                .clicked()
                {
                    use_source = Some(path.clone());
                }
                if !self.source.trim().is_empty()
                    && !same_path(&self.source, &path.to_string_lossy())
                    && drop_action_button(
                        ui,
                        "Agregar como destino",
                        false,
                        self.use_light_theme,
                    )
                    .clicked()
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
                    RichText::new("Puedes distribuir este archivo exacto por FAN-OUT o usar su carpeta contenedora como origen.")
                        .color(Theme::muted(self.use_light_theme)),
                );
                ui.add_space(SPACING_SM);
                if drop_action_button(
                    ui,
                    "Copiar este archivo y elegir destinos",
                    true,
                    self.use_light_theme,
                )
                .clicked()
                {
                    use_source = Some(path.clone());
                }
                if let Some(parent) = path.parent() {
                    if drop_action_button(
                        ui,
                        "Usar la carpeta que contiene este archivo",
                        false,
                        self.use_light_theme,
                    )
                    .clicked()
                    {
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

    fn paths_key(&self) -> u64 {
        let mut hasher = DefaultHasher::new();
        self.source.hash(&mut hasher);
        self.dests.hash(&mut hasher);
        hasher.finish()
    }

    fn validate_paths(&self) -> Vec<String> {
        let mut errors = Vec::new();
        let source = self.source.trim();
        if !source.is_empty() {
            let path = PathBuf::from(source);
            if !path.exists() {
                errors.push("El origen no existe.".to_owned());
            } else if !path.is_dir() && !path.is_file() {
                errors.push("El origen no es un archivo regular ni una carpeta.".to_owned());
            }
        }

        for (index, dest) in self.dests.iter().enumerate() {
            let path = PathBuf::from(dest.trim());
            if !path.exists() {
                errors.push(format!("El destino {} no está disponible.", index + 1));
            } else if !path.is_dir() {
                errors.push(format!("El destino {} no es una carpeta.", index + 1));
            }
        }
        errors
    }

    fn flash_error(&mut self, message: String) {
        self.status = message;
        self.error_flash_until = Some(Instant::now() + ERROR_FLASH);
    }

    fn save_theme(&mut self) {
        refresh_system_accent();
        self.use_light_theme = resolve_theme(self.theme_preference);
        self.applied_theme = None;
        self.last_theme_check = Instant::now();
        if let Err(error) = save_settings(AppSettings {
            theme: self.theme_preference,
        }) {
            self.flash_error(error);
        }
    }

    fn start(&mut self) {
        if self.source.trim().is_empty() {
            self.flash_error("Selecciona un archivo o carpeta de origen.".to_owned());
            return;
        }

        if self.dests.is_empty() {
            self.flash_error("Agrega al menos un destino.".to_owned());
            return;
        }

        if let Some(error) = self.validate_paths().into_iter().next() {
            self.flash_error(error);
            return;
        }

        let source = PathBuf::from(self.source.trim());
        let dests = self
            .dests
            .iter()
            .map(|dest| PathBuf::from(dest.trim()))
            .collect();
        let opts = CopyOpts {
            verify: true,
            skip_same: self.skip_same,
            keep_going: self.keep_going,
        };
        let (tx, rx) = mpsc::channel();

        self.job = None;
        self.workers.clear();
        self.error_flash_until = None;
        self.status = "Analizando origen y destinos…".to_owned();
        self.startup_rx = Some(rx);

        thread::spawn(move || {
            let _ = tx.send(start_job(source, dests, opts));
        });
    }

    fn poll_startup(&mut self) {
        let outcome = self.startup_rx.as_ref().and_then(|rx| match rx.try_recv() {
            Ok(result) => Some(Ok(result)),
            Err(mpsc::TryRecvError::Disconnected) => {
                Some(Err("El análisis previo terminó inesperadamente.".to_owned()))
            }
            Err(mpsc::TryRecvError::Empty) => None,
        });
        let Some(outcome) = outcome else {
            return;
        };

        self.startup_rx = None;
        match outcome {
            Ok(Ok((state, handles))) => {
                self.error_flash_until = None;
                let files = state.files_total.load(Ordering::Relaxed);
                let destinations = state.dests.lock().map(|d| d.len()).unwrap_or(0) as u64;
                self.status = format!(
                    "Preparado · {} · {} · {}",
                    count_label(files, "archivo", "archivos"),
                    format_bytes(state.bytes_total.load(Ordering::Relaxed)),
                    count_label(destinations, "destino", "destinos")
                );
                self.job = Some(state);
                self.workers = handles;
            }
            Ok(Err(error)) | Err(error) => self.flash_error(error),
        }
    }

    fn theme_choice(
        ui: &mut egui::Ui,
        current: ThemePreference,
        choice: ThemePreference,
        light: bool,
        width: f32,
    ) -> bool {
        let selected = current == choice;
        let fill = if selected {
            Theme::selected(light)
        } else {
            Theme::card(light)
        };
        let stroke = egui::Stroke::new(
            if selected { 1.5_f32 } else { 1.0_f32 },
            if selected {
                Theme::accent(light)
            } else {
                Theme::border(light)
            },
        );

        let response = egui::Frame::none()
            .fill(fill)
            .stroke(stroke)
            .rounding(egui::Rounding::same(9.0))
            .inner_margin(egui::Margin::symmetric(10.0, 10.0))
            .show(ui, |ui| {
                ui.set_width(width.max(84.0));
                ui.set_min_height(76.0);
                ui.vertical_centered(|ui| {
                    ui.label(
                        RichText::new(theme_glyph(choice))
                            .size(22.0)
                            .color(if selected {
                                Theme::accent(light)
                            } else {
                                Theme::muted(light)
                            }),
                    );
                    ui.label(RichText::new(choice.label()).strong().size(14.0));
                    ui.label(
                        RichText::new(theme_card_caption(choice))
                            .size(11.5)
                            .color(Theme::muted(light)),
                    );
                });
            })
            .response
            .interact(egui::Sense::click());

        response.clicked()
    }

    fn draw_settings(&mut self, ctx: &egui::Context) {
        if !self.show_settings {
            return;
        }

        let screen = ctx.screen_rect();
        let width = (screen.width() - 64.0).clamp(360.0, 560.0);
        let height = (screen.height() - 96.0).clamp(300.0, 350.0);
        let mut open = true;
        let mut requested_theme = None;

        egui::Window::new("Ajustes")
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .fixed_size(egui::vec2(width, height))
            .constrain_to(screen)
            .frame(window_frame(ctx, self.use_light_theme))
            .open(&mut open)
            .show(ctx, |ui| {
                ui.set_width(ui.available_width());
                ui.label(
                    RichText::new("Apariencia")
                        .size(18.0)
                        .strong()
                        .color(Theme::accent(self.use_light_theme)),
                );
                ui.label(
                    RichText::new("Elige cómo quieres ver RepartoCopier.")
                        .size(12.5)
                        .color(Theme::muted(self.use_light_theme)),
                );
                ui.add_space(SPACING_MD);

                let choices = [
                    ThemePreference::System,
                    ThemePreference::Light,
                    ThemePreference::Dark,
                ];
                let available = ui.available_width();
                if available >= 390.0 {
                    let card_width = ((available - 2.0 * SPACING_SM) / 3.0).max(100.0);
                    ui.horizontal(|ui| {
                        for choice in choices {
                            if Self::theme_choice(
                                ui,
                                self.theme_preference,
                                choice,
                                self.use_light_theme,
                                card_width,
                            ) {
                                requested_theme = Some(choice);
                            }
                        }
                    });
                } else {
                    for choice in choices {
                        if Self::theme_choice(
                            ui,
                            self.theme_preference,
                            choice,
                            self.use_light_theme,
                            ui.available_width(),
                        ) {
                            requested_theme = Some(choice);
                        }
                        ui.add_space(SPACING_XS);
                    }
                }

                ui.add_space(SPACING_MD);
                ui.separator();
                ui.add_space(SPACING_SM);
                ui.horizontal_wrapped(|ui| {
                    ui.label(
                        RichText::new(theme_glyph(self.theme_preference))
                            .size(17.0)
                            .color(Theme::accent(self.use_light_theme)),
                    );
                    ui.label(
                        RichText::new(theme_description(self.theme_preference))
                            .size(12.5)
                            .color(Theme::muted(self.use_light_theme)),
                    );
                });
                ui.add_space(SPACING_XS);
                ui.label(
                    RichText::new("Los cambios se aplican y guardan automáticamente.")
                        .size(11.5)
                        .color(Theme::muted(self.use_light_theme)),
                );
            });

        self.show_settings = open;
        if let Some(theme) = requested_theme {
            if theme != self.theme_preference {
                self.theme_preference = theme;
                self.save_theme();
            }
        }
    }

    fn draw_about(&mut self, ctx: &egui::Context) {
        if !self.show_credits {
            return;
        }

        let screen = ctx.screen_rect();
        let width = (screen.width() - 48.0).clamp(360.0, 540.0);
        let height = (screen.height() - 64.0).clamp(360.0, 540.0);
        let mut open = true;

        egui::Window::new("Acerca de RepartoCopier")
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
            .fixed_size(egui::vec2(width, height))
            .constrain_to(screen)
            .frame(window_frame(ctx, self.use_light_theme))
            .open(&mut open)
            .show(ctx, |ui| {
                egui::ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        card_frame(self.use_light_theme).show(ui, |ui| {
                            ui.vertical_centered(|ui| {
                                ui.label(
                                    RichText::new("RepartoCopier")
                                        .strong()
                                        .size(25.0)
                                        .color(Theme::accent(self.use_light_theme)),
                                );
                                ui.label(
                                    RichText::new("Archivo o carpeta · múltiples destinos")
                                        .color(Theme::muted(self.use_light_theme)),
                                );
                                ui.add_space(SPACING_SM);
                                ui.label(
                                    RichText::new(format!(
                                        "VERSIÓN {}",
                                        env!("CARGO_PKG_VERSION")
                                    ))
                                    .small()
                                    .strong()
                                    .color(Theme::verify(self.use_light_theme)),
                                );
                            });
                        });

                        ui.add_space(SPACING_MD);
                        ui.vertical_centered(|ui| {
                            ui.label(
                                RichText::new("Simple por fuera. Potente por dentro.")
                                    .strong()
                                    .size(17.0),
                            );
                            ui.add_space(SPACING_XS);
                            ui.label(
                                RichText::new(
                                    "Copias múltiples con reanudación, verificación BLAKE3 y recuperación segura.",
                                )
                                .small()
                                .color(Theme::muted(self.use_light_theme)),
                            );
                        });

                        ui.add_space(SPACING_LG);
                        if ui.available_width() >= 410.0 {
                            ui.columns(2, |columns| {
                                card_frame(self.use_light_theme).show(&mut columns[0], |ui| {
                                    ui.label(
                                        RichText::new("DESARROLLADO POR")
                                            .small()
                                            .color(Theme::muted(self.use_light_theme)),
                                    );
                                    ui.add_space(SPACING_XS);
                                    ui.label(
                                        RichText::new("ReinierTutoriales")
                                            .strong()
                                            .color(Theme::accent(self.use_light_theme)),
                                    );
                                    ui.small("© 2026");
                                });
                                card_frame(self.use_light_theme).show(&mut columns[1], |ui| {
                                    ui.label(
                                        RichText::new("PROYECTO")
                                            .small()
                                            .color(Theme::muted(self.use_light_theme)),
                                    );
                                    ui.add_space(SPACING_XS);
                                    ui.label(
                                        RichText::new("Código abierto")
                                            .strong()
                                            .color(Theme::success(self.use_light_theme)),
                                    );
                                    ui.small("Licencia MIT");
                                });
                            });
                        } else {
                            card_frame(self.use_light_theme).show(ui, |ui| {
                                ui.label(
                                    RichText::new("DESARROLLADO POR")
                                        .small()
                                        .color(Theme::muted(self.use_light_theme)),
                                );
                                ui.label(
                                    RichText::new("ReinierTutoriales")
                                        .strong()
                                        .color(Theme::accent(self.use_light_theme)),
                                );
                                ui.small("© 2026 · Código abierto · Licencia MIT");
                            });
                        }

                        ui.add_space(SPACING_LG);
                        ui.vertical_centered(|ui| {
                            ui.label(
                                RichText::new("¿Te resulta útil RepartoCopier?")
                                    .strong()
                                    .size(16.0),
                            );
                            ui.label(
                                RichText::new("Apoya el proyecto con una ★ en GitHub.")
                                    .color(Theme::muted(self.use_light_theme)),
                            );
                            ui.add_space(SPACING_SM);
                            ui.hyperlink_to(
                                RichText::new("Abrir repositorio en GitHub ↗")
                                    .strong()
                                    .color(Theme::accent(self.use_light_theme)),
                                "https://github.com/ReinierTutoriales/disk-duplicator",
                            );
                        });
                    });
            });

        self.show_credits = open;
    }
}
