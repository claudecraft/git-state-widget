//! Everything machine-specific lives in `%LOCALAPPDATA%\GitStateWidget\config.json`
//! (`~/Library/Application Support/GitStateWidget` on macOS, `~/.local/share/GitStateWidget`
//! on Linux), never in source. Keys are PascalCase so a config written by the earlier .NET
//! version loads unchanged.

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "PascalCase", default)]
pub struct RepoConfig {
    pub name: String,
    pub path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
    /// Overrides detection of the branch that "merged" is measured against, e.g. "origin/main".
    #[serde(skip_serializing_if = "Option::is_none")]
    pub default_branch: Option<String>,
}

impl Default for RepoConfig {
    fn default() -> Self {
        Self { name: String::new(), path: String::new(), note: None, default_branch: None }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(rename_all = "PascalCase", default)]
pub struct Config {
    pub interval_minutes: u64,
    pub fetch: bool,
    pub always_on_top: bool,
    pub start_hidden: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub left: Option<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub top: Option<f64>,
    pub repos: Vec<RepoConfig>,
    /// True when the file existed but could not be parsed. Saving is then refused so the broken
    /// file is not replaced with an empty one.
    #[serde(skip)]
    pub load_failed: bool,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            interval_minutes: 5,
            fetch: true,
            always_on_top: false,
            start_hidden: false,
            left: None,
            top: None,
            repos: Vec::new(),
            load_failed: false,
        }
    }
}

impl Config {
    pub fn dir() -> PathBuf {
        dirs::data_local_dir().unwrap_or_else(|| PathBuf::from(".")).join("GitStateWidget")
    }

    pub fn file_path() -> PathBuf {
        Self::dir().join("config.json")
    }

    pub fn load() -> Config {
        let path = Self::file_path();
        if path.exists() {
            match std::fs::read_to_string(&path).map_err(|e| e.to_string()).and_then(|s| serde_json::from_str::<Config>(&s).map_err(|e| e.to_string())) {
                Ok(mut cfg) => {
                    if cfg.interval_minutes < 1 {
                        cfg.interval_minutes = 1;
                    }
                    return cfg;
                }
                Err(err) => {
                    // A broken config must not stop the widget, and must not be overwritten either:
                    // the panel shows no repos, the error sits beside the file, and the tray menu
                    // opens it for repair.
                    let _ = std::fs::write(Self::dir().join("config-error.txt"), format!("{}\n{}", chrono::Local::now().to_rfc3339(), err));
                    return Config { load_failed: true, ..Config::default() };
                }
            }
        }
        let fresh = Config::default();
        fresh.save();
        fresh
    }

    pub fn save(&self) {
        if self.load_failed {
            return;
        }
        let _ = std::fs::create_dir_all(Self::dir());
        if let Ok(json) = serde_json::to_string_pretty(self) {
            let _ = std::fs::write(Self::file_path(), json);
        }
    }
}
