pub mod config;
mod git;
mod scanner;
mod tray;

use chrono::{DateTime, Local};
use config::{Config, RepoConfig};
use scanner::{RepoState, Severity};
use serde::Serialize;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;
use tauri::menu::CheckMenuItem;
use tauri::{AppHandle, Emitter, LogicalPosition, Manager, State, WindowEvent};
use tauri_plugin_autostart::MacosLauncher;
use tokio::sync::Notify;

pub struct AppState {
    pub config: Mutex<Config>,
    pub states: Mutex<Vec<RepoState>>,
    pub last_scan: Mutex<Option<DateTime<Local>>>,
    pub scanning: AtomicBool,
    pub wake: Arc<Notify>,
    move_gen: AtomicU64,
}

pub struct MenuState {
    pub fetch: CheckMenuItem<tauri::Wry>,
    pub on_top: CheckMenuItem<tauri::Wry>,
    pub startup: CheckMenuItem<tauri::Wry>,
}

#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct Snapshot {
    headline: String,
    overall: Severity,
    last_scan: Option<DateTime<Local>>,
    scanning: bool,
    fetch: bool,
    interval_minutes: u64,
    config_path: String,
    configured: usize,
    repos: Vec<RepoState>,
}

fn build_snapshot(state: &AppState) -> Snapshot {
    let cfg = state.config.lock().unwrap();
    let states = state.states.lock().unwrap();
    let last_scan = *state.last_scan.lock().unwrap();

    let repos: Vec<RepoState> = if states.is_empty() {
        cfg.repos.iter().map(RepoState::placeholder).collect()
    } else {
        states.clone()
    };
    let overall = if states.is_empty() { Severity::Ok } else { states.iter().map(|s| s.severity).max().unwrap_or(Severity::Ok) };
    let headline = if cfg.repos.is_empty() {
        "No repos configured".to_string()
    } else if states.is_empty() {
        "Scanning\u{2026}".to_string()
    } else {
        let alarms = states.iter().filter(|s| s.severity == Severity::Alarm).count() as u32;
        let warns = states.iter().filter(|s| s.severity == Severity::Warn).count() as u32;
        if alarms > 0 {
            format!("{} need{} attention", scanner::plural(alarms, "repo"), if alarms == 1 { "s" } else { "" })
        } else if warns > 0 {
            format!("{} behind or unfetched", scanner::plural(warns, "repo"))
        } else {
            "All repos pushed and current".to_string()
        }
    };

    Snapshot {
        headline,
        overall,
        last_scan,
        scanning: state.scanning.load(Ordering::Relaxed),
        fetch: cfg.fetch,
        interval_minutes: cfg.interval_minutes,
        config_path: Config::file_path().to_string_lossy().into_owned(),
        configured: cfg.repos.len(),
        repos,
    }
}

fn publish(app: &AppHandle) {
    let state = app.state::<AppState>();
    let snap = build_snapshot(&state);
    tray::update(app, snap.overall, &snap.headline);
    let _ = app.emit("snapshot", &snap);
}

async fn do_scan(app: &AppHandle) {
    let state = app.state::<AppState>();
    if state.scanning.swap(true, Ordering::SeqCst) {
        return;
    }
    publish(app);
    let (repos, fetch): (Vec<RepoConfig>, bool) = {
        let cfg = state.config.lock().unwrap();
        (cfg.repos.clone(), cfg.fetch)
    };
    let results = futures::future::join_all(repos.iter().map(|r| scanner::scan(r, fetch))).await;
    *state.states.lock().unwrap() = results;
    *state.last_scan.lock().unwrap() = Some(Local::now());
    state.scanning.store(false, Ordering::SeqCst);
    publish(app);
}

async fn scheduler(app: AppHandle) {
    let wake = app.state::<AppState>().wake.clone();
    loop {
        do_scan(&app).await;
        let mins = app.state::<AppState>().config.lock().unwrap().interval_minutes.max(1);
        tokio::select! {
            _ = tokio::time::sleep(Duration::from_secs(mins * 60)) => {}
            _ = wake.notified() => {}
        }
    }
}

pub fn request_refresh(app: &AppHandle) {
    app.state::<AppState>().wake.notify_one();
}

pub fn toggle_panel(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        if w.is_visible().unwrap_or(false) {
            let _ = w.hide();
        } else {
            let _ = w.show();
            let _ = w.set_focus();
        }
    }
}

pub fn show_panel(app: &AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.show();
        let _ = w.set_focus();
    }
}

pub fn set_always_on_top_impl(app: &AppHandle, on: bool) {
    {
        let state = app.state::<AppState>();
        let mut cfg = state.config.lock().unwrap();
        cfg.always_on_top = on;
        cfg.save();
    }
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.set_always_on_top(on);
    }
    if let Some(menu) = app.try_state::<MenuState>() {
        let _ = menu.on_top.set_checked(on);
    }
    show_panel(app);
}

pub fn add_repo_impl(app: &AppHandle, path: PathBuf) {
    let state = app.state::<AppState>();
    {
        let mut cfg = state.config.lock().unwrap();
        let p = path.to_string_lossy().trim_end_matches(['\\', '/']).to_string();
        if cfg.repos.iter().any(|r| r.path.eq_ignore_ascii_case(&p)) {
            return;
        }
        let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| p.clone());
        cfg.repos.push(RepoConfig { name, path: p, note: None, default_branch: None });
        cfg.save();
    }
    publish(app);
    request_refresh(app);
}

pub fn reload_config(app: &AppHandle) {
    let state = app.state::<AppState>();
    let cfg = Config::load();
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.set_always_on_top(cfg.always_on_top);
    }
    if let Some(menu) = app.try_state::<MenuState>() {
        let _ = menu.fetch.set_checked(cfg.fetch);
        let _ = menu.on_top.set_checked(cfg.always_on_top);
    }
    *state.config.lock().unwrap() = cfg;
    state.states.lock().unwrap().clear();
    publish(app);
    request_refresh(app);
}

pub fn exit_app(app: &AppHandle) {
    app.state::<AppState>().config.lock().unwrap().save();
    app.exit(0);
}

#[tauri::command]
fn get_snapshot(state: State<'_, AppState>) -> Snapshot {
    build_snapshot(&state)
}

#[tauri::command]
fn refresh_now(app: AppHandle) {
    request_refresh(&app);
}

#[tauri::command]
fn hide_panel(app: AppHandle) {
    if let Some(w) = app.get_webview_window("main") {
        let _ = w.hide();
    }
}

fn restore_placement(app: &AppHandle) {
    let Some(w) = app.get_webview_window("main") else { return };
    let cfg = app.state::<AppState>().config.lock().unwrap().clone();
    let _ = w.set_always_on_top(cfg.always_on_top);
    match (cfg.left, cfg.top) {
        (Some(l), Some(t)) => {
            let _ = w.set_position(LogicalPosition::new(l, t));
        }
        _ => {
            // Top-right of the primary work area, matching where the first version sat.
            if let Ok(Some(mon)) = w.primary_monitor() {
                let sf = mon.scale_factor();
                let size = mon.size().to_logical::<f64>(sf);
                let pos = mon.position().to_logical::<f64>(sf);
                let _ = w.set_position(LogicalPosition::new(pos.x + size.width - 640.0 - 24.0, pos.y + 24.0));
            }
        }
    }
}

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| show_panel(app)))
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_autostart::init(MacosLauncher::LaunchAgent, None))
        .manage(AppState {
            config: Mutex::new(Config::load()),
            states: Mutex::new(Vec::new()),
            last_scan: Mutex::new(None),
            scanning: AtomicBool::new(false),
            wake: Arc::new(Notify::new()),
            move_gen: AtomicU64::new(0),
        })
        .invoke_handler(tauri::generate_handler![get_snapshot, refresh_now, hide_panel])
        .setup(|app| {
            let handle = app.handle().clone();
            tray::build(&handle)?;
            restore_placement(&handle);
            let start_hidden = handle.state::<AppState>().config.lock().unwrap().start_hidden;
            if !start_hidden {
                show_panel(&handle);
            }
            tauri::async_runtime::spawn(scheduler(handle));
            Ok(())
        })
        .on_window_event(|window, event| match event {
            WindowEvent::CloseRequested { api, .. } => {
                // Closing the panel hides it; Exit lives in the tray menu.
                api.prevent_close();
                let _ = window.hide();
            }
            WindowEvent::Moved(pos) => {
                let app = window.app_handle().clone();
                let sf = window.scale_factor().unwrap_or(1.0);
                let logical = pos.to_logical::<f64>(sf);
                let state = app.state::<AppState>();
                let gen = state.move_gen.fetch_add(1, Ordering::SeqCst) + 1;
                // Debounce: a drag fires many Moved events; write once it has settled.
                tauri::async_runtime::spawn(async move {
                    tokio::time::sleep(Duration::from_millis(800)).await;
                    let state = app.state::<AppState>();
                    if state.move_gen.load(Ordering::SeqCst) == gen {
                        let mut cfg = state.config.lock().unwrap();
                        cfg.left = Some(logical.x);
                        cfg.top = Some(logical.y);
                        cfg.save();
                    }
                });
            }
            _ => {}
        })
        .run(tauri::generate_context!())
        .expect("error while running Git State Widget");
}
