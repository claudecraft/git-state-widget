//! The always-present mirror of the panel: a dot the colour of the worst repo, the headline as
//! tooltip, and the housekeeping menu. Left-click toggles the panel.

use crate::scanner::Severity;
use crate::{AppState, MenuState};
use tauri::image::Image;
use tauri::menu::{CheckMenuItem, Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager};
use tauri_plugin_autostart::ManagerExt as _;
use tauri_plugin_dialog::DialogExt;
use tauri_plugin_opener::OpenerExt;

pub const TRAY_ID: &str = "main";

pub fn build(app: &AppHandle) -> tauri::Result<()> {
    let state = app.state::<AppState>();
    let cfg = state.config.lock().unwrap().clone();
    let autostart = app.autolaunch().is_enabled().unwrap_or(false);

    let show = MenuItem::with_id(app, "toggle", "Show / hide panel", true, None::<&str>)?;
    let refresh = MenuItem::with_id(app, "refresh", "Refresh now", true, None::<&str>)?;
    let add = MenuItem::with_id(app, "add", "Add repo\u{2026}", true, None::<&str>)?;
    let open = MenuItem::with_id(app, "open", "Open config file", true, None::<&str>)?;
    let reload = MenuItem::with_id(app, "reload", "Reload config", true, None::<&str>)?;
    let fetch = CheckMenuItem::with_id(app, "fetch", "Fetch before each scan", true, cfg.fetch, None::<&str>)?;
    let on_top = CheckMenuItem::with_id(app, "ontop", "Always on top", true, cfg.always_on_top, None::<&str>)?;
    let startup = CheckMenuItem::with_id(app, "startup", "Start with Windows", true, autostart, None::<&str>)?;
    let exit = MenuItem::with_id(app, "exit", "Exit", true, None::<&str>)?;

    let menu = Menu::with_items(app, &[
        &show,
        &refresh,
        &PredefinedMenuItem::separator(app)?,
        &add,
        &open,
        &reload,
        &PredefinedMenuItem::separator(app)?,
        &fetch,
        &on_top,
        &startup,
        &PredefinedMenuItem::separator(app)?,
        &exit,
    ])?;

    app.manage(MenuState { fetch: fetch.clone(), on_top: on_top.clone(), startup: startup.clone() });

    TrayIconBuilder::with_id(TRAY_ID)
        .icon(draw_dot(Severity::Ok))
        .tooltip("Git State \u{2013} scanning\u{2026}")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| on_menu(app, event.id().as_ref()))
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click { button: MouseButton::Left, button_state: MouseButtonState::Up, .. } = event {
                crate::toggle_panel(tray.app_handle());
            }
        })
        .build(app)?;
    Ok(())
}

fn on_menu(app: &AppHandle, id: &str) {
    match id {
        "toggle" => crate::toggle_panel(app),
        "refresh" => crate::request_refresh(app),
        "add" => add_repo(app),
        "open" => {
            let path = crate::config::Config::file_path();
            if let Err(e) = app.opener().open_path(path.to_string_lossy(), None::<&str>) {
                app.dialog().message(format!("Could not open {}\n{e}", path.display())).title("Git State").show(|_| {});
            }
        }
        "reload" => crate::reload_config(app),
        "fetch" => {
            let state = app.state::<AppState>();
            let menu = app.state::<MenuState>();
            let on = menu.fetch.is_checked().unwrap_or(true);
            let mut cfg = state.config.lock().unwrap();
            cfg.fetch = on;
            cfg.save();
        }
        "ontop" => {
            let menu = app.state::<MenuState>();
            let on = menu.on_top.is_checked().unwrap_or(false);
            crate::set_always_on_top_impl(app, on);
        }
        "startup" => {
            let menu = app.state::<MenuState>();
            let on = menu.startup.is_checked().unwrap_or(false);
            let result = if on { app.autolaunch().enable() } else { app.autolaunch().disable() };
            if let Err(e) = result {
                let _ = menu.startup.set_checked(app.autolaunch().is_enabled().unwrap_or(false));
                app.dialog().message(format!("Could not update startup: {e}")).title("Git State").show(|_| {});
            }
        }
        "exit" => crate::exit_app(app),
        _ => {}
    }
}

fn add_repo(app: &AppHandle) {
    let handle = app.clone();
    app.dialog().file().set_title("Pick the root of a git repository to watch").pick_folder(move |picked| {
        if let Some(p) = picked {
            if let Ok(path) = p.into_path() {
                crate::add_repo_impl(&handle, path);
            }
        }
    });
}

pub fn update(app: &AppHandle, overall: Severity, headline: &str) {
    if let Some(tray) = app.tray_by_id(TRAY_ID) {
        let _ = tray.set_icon(Some(draw_dot(overall)));
        let mut tip = format!("Git State \u{2013} {headline}");
        if tip.len() > 120 {
            tip.truncate(120);
        }
        let _ = tray.set_tooltip(Some(tip));
    }
}

/// A 32x32 anti-aliased dot, with a soft ring when alarmed.
fn draw_dot(sev: Severity) -> Image<'static> {
    let (r, g, b) = match sev {
        Severity::Alarm => (0xFF, 0x6B, 0x6B),
        Severity::Warn => (0xFF, 0xB4, 0x54),
        _ => (0x5E, 0xC2, 0x7A),
    };
    let ring = sev == Severity::Alarm;
    const N: usize = 32;
    let mut px = vec![0u8; N * N * 4];
    let c = N as f32 / 2.0;
    for y in 0..N {
        for x in 0..N {
            let dx = x as f32 + 0.5 - c;
            let dy = y as f32 + 0.5 - c;
            let d = (dx * dx + dy * dy).sqrt();
            let mut a = (8.5 - d).clamp(0.0, 1.0);
            if ring {
                let band = (1.5 - (d - 12.0).abs()).clamp(0.0, 1.0) * 0.35;
                a = a.max(band);
            }
            let i = (y * N + x) * 4;
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = (a * 255.0) as u8;
        }
    }
    Image::new_owned(px, N as u32, N as u32)
}
