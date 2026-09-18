//! Runs git with a process we create ourselves: no console window, streams captured, and every
//! interactive prompt forbidden. An expired credential therefore surfaces as a failed fetch in
//! the panel rather than as a dialog, and nothing git spawns can flash a window.

use std::path::Path;
use std::process::Stdio;
use std::time::Duration;
use tokio::process::Command;

pub struct GitResult {
    pub exit_code: i32,
    pub stdout: String,
    pub stderr: String,
}

impl GitResult {
    pub fn ok(&self) -> bool {
        self.exit_code == 0
    }

    pub fn lines(&self) -> impl Iterator<Item = &str> {
        self.stdout.lines().map(|l| l.trim_end_matches('\r')).filter(|l| !l.is_empty())
    }

    fn failed(msg: impl Into<String>) -> Self {
        Self { exit_code: -1, stdout: String::new(), stderr: msg.into() }
    }
}

pub async fn run(repo: &Path, args: &[&str]) -> GitResult {
    let mut cmd = Command::new("git");
    cmd.args(args)
        .current_dir(repo)
        .env("GIT_TERMINAL_PROMPT", "0")
        .env("GCM_INTERACTIVE", "never")
        .env("GIT_SSH_COMMAND", "ssh -oBatchMode=yes")
        .env("LC_ALL", "C")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true);

    #[cfg(windows)]
    {
        const CREATE_NO_WINDOW: u32 = 0x0800_0000;
        cmd.creation_flags(CREATE_NO_WINDOW);
    }

    let child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => return GitResult::failed(format!("could not start git: {e}")),
    };

    match tokio::time::timeout(Duration::from_secs(60), child.wait_with_output()).await {
        Ok(Ok(out)) => GitResult {
            exit_code: out.status.code().unwrap_or(-1),
            stdout: String::from_utf8_lossy(&out.stdout).into_owned(),
            stderr: String::from_utf8_lossy(&out.stderr).into_owned(),
        },
        Ok(Err(e)) => GitResult::failed(e.to_string()),
        Err(_) => GitResult::failed("timed out"),
    }
}
