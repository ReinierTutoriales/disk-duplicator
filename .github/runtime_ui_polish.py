from pathlib import Path


def replace_one(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)

# Centralize the rule that terminal/paused copy speeds are visually zero.
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
fn visible_bps(bps: f64, last_tick: Instant, terminal: bool, paused: bool) -> f64 {
    if terminal || paused {
        0.0
    } else {
        shown_bps(bps, last_tick)
    }
}
'''
t = replace_one(t, anchor, replacement, 'visible speed helper')
p.write_text(t, encoding='utf-8')

# UI: terminal speed must be exactly zero; add a clear drag affordance and accurate source wording.
p = Path('src/app/part4.rs')
t = p.read_text(encoding='utf-8')
t = t.replace('Una carpeta · múltiples destinos', 'Archivo o carpeta · múltiples destinos')
t = t.replace('Carpeta que quieres copiar', 'Archivo o carpeta que quieres copiar')
t = t.replace('Seleccionar carpeta de origen · Ctrl+O', 'Seleccionar carpeta de origen · Ctrl+O · también puedes arrastrar un archivo')

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
            let text = if busy {
                "Copia activa · no se puede cambiar el origen"
            } else {
                "Suelta el archivo o carpeta · RepartoCopier te mostrará las opciones"
            };
            egui::Area::new("drop_affordance".into())
                .anchor(egui::Align2::CENTER_CENTER, egui::Vec2::ZERO)
                .order(egui::Order::Foreground)
                .show(ctx, |ui| {
                    egui::Frame::none()
                        .fill(Theme::card(self.use_light_theme))
                        .stroke(egui::Stroke::new(2.0_f32, Theme::accent(self.use_light_theme)))
                        .rounding(egui::Rounding::same(10.0))
                        .inner_margin(egui::Margin::symmetric(18.0, 12.0))
                        .show(ui, |ui| {
                            ui.label(RichText::new(text).strong());
                        });
                });
        }
        self.draw_drop_prompt(ctx);
'''
t = replace_one(t, old, new, 'drop affordance')

old = '''                    let aggregate_bps = if paused {
                        0.0
                    } else {
                        snaps
                            .iter()
                            .map(|progress| {
                                shown_bps(progress.bps_recent, progress.last_tick)
                            })
                            .sum()
                    };
'''
new = '''                    let aggregate_bps = if all_terminal {
                        0.0
                    } else {
                        snaps
                            .iter()
                            .map(|progress| {
                                let terminal = matches!(
                                    progress.phase,
                                    DestPhase::Done | DestPhase::Failed | DestPhase::Cancelled
                                );
                                visible_bps(progress.bps_recent, progress.last_tick, terminal, paused)
                            })
                            .sum()
                    };
'''
t = replace_one(t, old, new, 'aggregate terminal speed')

old = '''                                                    let display_bps = if paused
                                                        || matches!(
                                                            progress.phase,
                                                            DestPhase::Done
                                                                | DestPhase::Failed
                                                                | DestPhase::Cancelled
                                                        )
                                                    {
                                                        0.0
                                                    } else {
                                                        shown_bps(
                                                            progress.bps_recent,
                                                            progress.last_tick,
                                                        )
                                                    };
'''
new = '''                                                    let terminal = matches!(
                                                        progress.phase,
                                                        DestPhase::Done
                                                            | DestPhase::Failed
                                                            | DestPhase::Cancelled
                                                    );
                                                    let display_bps = visible_bps(
                                                        progress.bps_recent,
                                                        progress.last_tick,
                                                        terminal,
                                                        paused,
                                                    );
'''
t = replace_one(t, old, new, 'destination terminal speed')
p.write_text(t, encoding='utf-8')

# Regression test: stale throughput must never survive a terminal or paused state.
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
    fn terminal_and_paused_speed_is_always_zero() {
        let now = Instant::now();
        assert_eq!(visible_bps(900_000_000.0, now, true, false), 0.0);
        assert_eq!(visible_bps(900_000_000.0, now, false, true), 0.0);
        assert!(visible_bps(900_000_000.0, now, false, false) > 0.0);
    }
'''
t = replace_one(t, anchor, replacement, 'terminal speed regression')
p.write_text(t, encoding='utf-8')
