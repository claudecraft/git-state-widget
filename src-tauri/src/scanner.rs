//! One scan of one repository. The severity model is deliberate and carried over unchanged:
//!
//!   Alarm  commits that exist in exactly one place (unpushed, or a branch with no upstream
//!          that has work the default branch does not). Losing this machine loses them.
//!   Warn   out of date (behind upstream), upstream deleted, or the fetch itself failed.
//!   Info   dirty / untracked trees and fully-merged branches. These trees are dirty by
//!          design; alarming on them trains you to ignore the widget.

use crate::config::RepoConfig;
use crate::git;
use chrono::{DateTime, FixedOffset, Local};
use regex::Regex;
use serde::Serialize;
use std::path::Path;
use std::sync::OnceLock;

#[derive(Serialize, Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
#[serde(rename_all = "lowercase")]
pub enum Severity {
    Ok,
    Info,
    Warn,
    Alarm,
}

#[derive(Serialize, Clone, Debug, Default)]
#[serde(rename_all = "camelCase")]
pub struct BranchState {
    pub name: String,
    pub is_current: bool,
    pub is_default: bool,
    pub upstream: Option<String>,
    pub upstream_gone: bool,
    pub ahead_upstream: u32,
    pub behind_upstream: u32,
    pub ahead_default: Option<u32>,
    pub behind_default: Option<u32>,
    pub hash: String,
    pub when: Option<DateTime<FixedOffset>>,
    pub subject: String,
    pub severity: Severity,
    /// Short reason shown next to the branch, e.g. "3 commits unpushed".
    pub verdict: String,
}

impl Default for Severity {
    fn default() -> Self {
        Severity::Ok
    }
}

#[derive(Serialize, Clone, Debug)]
#[serde(rename_all = "camelCase")]
pub struct RepoState {
    pub name: String,
    pub path: String,
    pub note: Option<String>,
    pub current_branch: String,
    pub default_branch: Option<String>,
    pub branches: Vec<BranchState>,
    pub dirty: u32,
    pub untracked: u32,
    pub error: Option<String>,
    pub fetch_error: Option<String>,
    pub severity: Severity,
    pub scanned_at: DateTime<Local>,
}

impl RepoState {
    pub fn placeholder(cfg: &RepoConfig) -> Self {
        Self {
            name: cfg.name.clone(),
            path: cfg.path.clone(),
            note: cfg.note.clone(),
            current_branch: String::new(),
            default_branch: None,
            branches: Vec::new(),
            dirty: 0,
            untracked: 0,
            error: None,
            fetch_error: None,
            severity: Severity::Ok,
            scanned_at: Local::now(),
        }
    }

    fn fail(mut self, error: &str) -> Self {
        self.error = Some(error.to_string());
        self.severity = Severity::Alarm;
        self
    }
}

fn track_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"ahead (\d+)|behind (\d+)|(gone)").unwrap())
}

fn counts_re() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"^(\d+)\s+(\d+)").unwrap())
}

pub fn plural(n: u32, word: &str) -> String {
    if n == 1 { format!("{n} {word}") } else { format!("{n} {word}s") }
}

pub async fn scan(cfg: &RepoConfig, fetch: bool) -> RepoState {
    let mut r = RepoState::placeholder(cfg);
    let path = Path::new(&cfg.path);

    if !path.is_dir() {
        return r.fail("path does not exist");
    }
    if !path.join(".git").exists() {
        return r.fail("not a git repo");
    }

    let head = git::run(path, &["rev-parse", "--abbrev-ref", "HEAD"]).await;
    if !head.ok() || head.stdout.trim().is_empty() {
        return r.fail("could not read HEAD");
    }
    r.current_branch = head.stdout.trim().to_string();

    if fetch {
        let f = git::run(path, &["fetch", "--quiet", "--no-tags", "--prune"]).await;
        if !f.ok() {
            let msg = f.stderr.trim();
            r.fetch_error = Some(if msg.is_empty() { "fetch failed".to_string() } else { msg.lines().next().unwrap_or("fetch failed").trim_end_matches('\r').to_string() });
        }
    }

    r.default_branch = resolve_default_branch(cfg, path).await;

    // One call for every local branch: name, upstream, tracking summary, tip, date, subject.
    let refs = git::run(path, &[
        "for-each-ref",
        "--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(objectname:short)%09%(committerdate:iso-strict)%09%(subject)",
        "refs/heads",
    ]).await;
    for line in refs.lines() {
        let parts: Vec<&str> = line.splitn(6, '\t').collect();
        if parts.len() < 4 {
            continue;
        }
        let mut b = BranchState {
            name: parts[0].to_string(),
            upstream: if parts[1].is_empty() { None } else { Some(parts[1].to_string()) },
            hash: parts[3].to_string(),
            subject: parts.get(5).unwrap_or(&"").to_string(),
            ..Default::default()
        };
        b.is_current = b.name == r.current_branch;
        if let Some(when) = parts.get(4) {
            b.when = DateTime::parse_from_rfc3339(when).ok();
        }
        for cap in track_re().captures_iter(parts[2]) {
            if let Some(a) = cap.get(1) {
                b.ahead_upstream = a.as_str().parse().unwrap_or(0);
            } else if let Some(bh) = cap.get(2) {
                b.behind_upstream = bh.as_str().parse().unwrap_or(0);
            } else if cap.get(3).is_some() {
                b.upstream_gone = true;
            }
        }
        r.branches.push(b);
    }

    if let Some(def) = r.default_branch.clone() {
        let default_local = def.split_once('/').map(|(_, rest)| rest.to_string()).unwrap_or_else(|| def.clone());
        for b in r.branches.iter_mut() {
            b.is_default = b.name == default_local;
            // --cherry-pick drops commits whose patch already exists on the other side, so a
            // rebased or cherry-picked branch reads as merged. Squash merges still look unique.
            let range = format!("{}...{}", b.name, def);
            let c = git::run(path, &["rev-list", "--left-right", "--count", "--cherry-pick", &range]).await;
            if c.ok() {
                if let Some(m) = counts_re().captures(c.stdout.trim()) {
                    b.ahead_default = m[1].parse().ok();
                    b.behind_default = m[2].parse().ok();
                }
            }
        }
    }

    let status = git::run(path, &["status", "--porcelain"]).await;
    for line in status.lines() {
        if line.starts_with("??") {
            r.untracked += 1;
        } else {
            r.dirty += 1;
        }
    }

    // Detached HEAD: for-each-ref lists no current branch, so synthesise a row for it.
    if !r.branches.iter().any(|b| b.is_current) {
        let last = git::run(path, &["log", "-1", "--format=%h%x09%cI%x09%s"]).await;
        let parts: Vec<&str> = last.stdout.trim().splitn(3, '\t').collect();
        let mut b = BranchState {
            name: if r.current_branch == "HEAD" { "(detached)".to_string() } else { r.current_branch.clone() },
            is_current: true,
            ..Default::default()
        };
        if parts.len() == 3 {
            b.hash = parts[0].to_string();
            b.when = DateTime::parse_from_rfc3339(parts[1]).ok();
            b.subject = parts[2].to_string();
        }
        r.branches.insert(0, b);
    }

    for b in r.branches.iter_mut() {
        judge(b);
    }
    // Current branch first, then the default, then alarms, then by recency.
    r.branches.sort_by(|a, b| {
        b.is_current.cmp(&a.is_current)
            .then(b.is_default.cmp(&a.is_default))
            .then(b.severity.cmp(&a.severity))
            .then(b.when.cmp(&a.when))
    });

    r.severity = r.branches.iter().map(|b| b.severity).max().unwrap_or(Severity::Ok);
    if r.fetch_error.is_some() && r.severity < Severity::Warn {
        r.severity = Severity::Warn;
    }
    if r.severity == Severity::Ok && (r.dirty > 0 || r.untracked > 0) {
        r.severity = Severity::Info;
    }
    r
}

/// The branch "merged" is measured against. Config override first, then origin/HEAD, then
/// origin/main or origin/master, then a local main or master. None if none exist.
async fn resolve_default_branch(cfg: &RepoConfig, path: &Path) -> Option<String> {
    if let Some(d) = cfg.default_branch.as_ref().filter(|d| !d.trim().is_empty()) {
        return Some(d.clone());
    }
    let sym = git::run(path, &["symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD"]).await;
    if sym.ok() && !sym.stdout.trim().is_empty() {
        return Some(sym.stdout.trim().to_string());
    }
    for candidate in ["refs/remotes/origin/main", "refs/remotes/origin/master", "refs/heads/main", "refs/heads/master"] {
        let v = git::run(path, &["rev-parse", "--verify", "-q", candidate]).await;
        if v.ok() {
            return Some(candidate.trim_start_matches("refs/remotes/").trim_start_matches("refs/heads/").to_string());
        }
    }
    None
}

fn judge(b: &mut BranchState) {
    if b.upstream.is_some() && !b.upstream_gone {
        if b.ahead_upstream > 0 {
            b.severity = Severity::Alarm;
            b.verdict = format!("{} unpushed", plural(b.ahead_upstream, "commit"));
            return;
        }
        if b.behind_upstream > 0 && b.is_current {
            b.severity = Severity::Warn;
            b.verdict = format!("{} behind upstream", plural(b.behind_upstream, "commit"));
            return;
        }
        if !b.is_default && b.ahead_default == Some(0) {
            b.severity = Severity::Info;
            b.verdict = "merged, cleanup candidate".to_string();
            return;
        }
        b.severity = Severity::Ok;
        b.verdict = if b.is_default { "current" } else { "pushed" }.to_string();
        return;
    }

    if b.upstream_gone {
        match b.ahead_default {
            Some(n) if n > 0 => {
                b.severity = Severity::Alarm;
                b.verdict = format!("upstream deleted, {} only here", plural(n, "commit"));
            }
            _ => {
                b.severity = Severity::Warn;
                b.verdict = "upstream deleted, merged".to_string();
            }
        }
        return;
    }

    // No upstream at all.
    match b.ahead_default {
        Some(n) if n > 0 => {
            b.severity = Severity::Alarm;
            b.verdict = format!("{} only here", plural(n, "commit"));
        }
        Some(_) => {
            b.severity = Severity::Info;
            b.verdict = "merged, local only".to_string();
        }
        None => {
            // No default branch to compare against either: nothing backs this up.
            b.severity = Severity::Alarm;
            b.verdict = "nothing backs this up".to_string();
        }
    }
}
