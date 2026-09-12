from pathlib import Path

p = Path('src/app/part1.rs')
s = p.read_text(encoding='utf-8')

repls = [
("const SPACING_LG: f32 = 18.0;", "const SPACING_LG: f32 = 20.0;\nconst FLUENT_RADIUS_SM: f32 = 6.0;\nconst FLUENT_RADIUS_MD: f32 = 8.0;\nconst FLUENT_RADIUS_LG: f32 = 12.0;\nconst FLUENT_CONTROL_HEIGHT: f32 = 32.0;"),
("Color32::from_rgb(243, 243, 243)", "Color32::from_rgb(246, 246, 246)"),
("Color32::from_rgb(32, 32, 32)", "Color32::from_rgb(28, 28, 28)"),
("Color32::from_rgb(249, 249, 249)", "Color32::from_rgb(243, 243, 243)"),
("Color32::from_rgb(39, 39, 39)", "Color32::from_rgb(32, 32, 32)"),
("Color32::from_rgb(255, 255, 255)\n        } else {\n            Color32::from_rgb(45, 45, 45)", "Color32::from_rgb(253, 253, 253)\n        } else {\n            Color32::from_rgb(44, 44, 44)"),
("let percent = if light { 12 } else { 22 };", "let percent = if light { 8 } else { 16 };"),
("Color32::from_rgb(229, 229, 229)", "Color32::from_rgb(224, 224, 224)"),
("Color32::from_rgb(61, 61, 61)", "Color32::from_rgb(58, 58, 58)"),
("style.spacing.item_spacing = egui::vec2(SPACING_SM, 6.0);\n        style.spacing.button_padding = egui::vec2(12.0, 7.0);\n        style.visuals.window_rounding = egui::Rounding::same(10.0);", "style.spacing.item_spacing = egui::vec2(SPACING_SM, SPACING_SM);\n        style.spacing.button_padding = egui::vec2(14.0, 7.0);\n        style.spacing.interact_size.y = FLUENT_CONTROL_HEIGHT;\n        style.visuals.window_rounding = egui::Rounding::same(FLUENT_RADIUS_LG);"),
(".rounding(egui::Rounding::same(8.0))\n        .inner_margin(egui::Margin::symmetric(12.0, 9.0))", ".rounding(egui::Rounding::same(FLUENT_RADIUS_MD))\n        .inner_margin(egui::Margin::symmetric(14.0, 11.0))"),
(".rounding(egui::Rounding::same(10.0))", ".rounding(egui::Rounding::same(FLUENT_RADIUS_LG))"),
]
for old,new in repls:
    if old not in s:
        raise SystemExit(f'part1 pattern not found: {old[:80]!r}')
    s = s.replace(old,new,1)
p.write_text(s, encoding='utf-8')

p = Path('src/app/part4.rs')
s = p.read_text(encoding='utf-8')
repls = [
("ui.heading(RichText::new(\"RepartoCopier\").strong());", "ui.label(RichText::new(\"RepartoCopier\").strong().size(19.0));"),
("ui.menu_button(\"Copia\", |ui| {", "ui.menu_button(RichText::new(\"Copia\").strong(), |ui| {"),
("[146.0, 32.0]", "[154.0, 34.0]"),
(".rounding(egui::Rounding::same(10.0)),", ".rounding(egui::Rounding::same(FLUENT_RADIUS_MD)),"),
(".rounding(egui::Rounding::same(6.0))\n                            .inner_margin(egui::Margin::symmetric(7.0, 3.0))", ".rounding(egui::Rounding::same(FLUENT_RADIUS_MD))\n                            .inner_margin(egui::Margin::symmetric(9.0, 5.0))"),
("[88.0, 30.0]", "[92.0, 34.0]"),
("[92.0, 30.0]", "[98.0, 34.0]"),
(".min_size(egui::vec2(116.0, 30.0));", ".min_size(egui::vec2(124.0, 34.0));"),
("const CARD_ROW_HEIGHT: f32 = 105.0;", "const CARD_ROW_HEIGHT: f32 = 112.0;"),
]
for old,new in repls:
    if old not in s:
        raise SystemExit(f'part4 pattern not found: {old[:80]!r}')
    s = s.replace(old,new,1)
p.write_text(s, encoding='utf-8')

# Add a regression test proving the Fluent sizing contract remains intentional.
p = Path('src/app/part5.rs')
s = p.read_text(encoding='utf-8')
needle = "#[test]\nfn source_layout_stacks_at_narrow_widths()"
if needle not in s:
    raise SystemExit('part5 anchor not found')
test = "#[test]\nfn fluent_metrics_keep_windows_control_density() {\n    assert_eq!(FLUENT_CONTROL_HEIGHT, 32.0);\n    assert_eq!(FLUENT_RADIUS_SM, 6.0);\n    assert_eq!(FLUENT_RADIUS_MD, 8.0);\n    assert_eq!(FLUENT_RADIUS_LG, 12.0);\n}\n\n"
s = s.replace(needle, test + needle, 1)
p.write_text(s, encoding='utf-8')
