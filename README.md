# Git State Widget

A small desktop widget that answers one question at a glance: **is any of my git work
sitting in exactly one place?** One row per repo, click a row to unfold its branches. A tray
icon mirrors the worst state so the answer survives a maximised window.

Built with [Tauri 2](https://tauri.app): a Rust backend that runs `git` and a small HTML panel.
Windows today; the code has no Windows-only parts, so macOS and Linux builds are a matter of
running the build there.

## Severity model (deliberate)

| Colour | Means | Examples |
|---|---|---|
| **Red** | Commits that exist in one place only | ahead of upstream; no upstream and ahead of master; upstream deleted with unmerged work |
| **Amber** | Out of date, or the picture may be stale | behind upstream (current branch only); fetch failed; upstream deleted but merged |
| Grey | Informational | dirty or untracked files; branches fully merged into master ("cleanup candidate") |
| Green | Pushed and current | |

Dirty trees are grey on purpose: working trees are dirty by design, and alarming on them would
train you to ignore the widget.

"Merged" is measured against the repo's default branch: `origin/HEAD` if set, else
`origin/main`, `origin/master`, then a local `main`/`master`. Override per repo with
`DefaultBranch` in the config. The comparison uses `git rev-list --cherry-pick`, so rebased and
cherry-picked commits count as merged; **squash merges do not**, so a branch that was
squash-merged still shows its commits as "only here". That is a limit of the data, not a bug.

## Reading the columns

- **vs upstream**: `▲n` commits here that the tracking branch lacks (unpushed); `▼n` commits
  there that you haven't pulled. `none` means the branch has no tracking branch at all. `gone`
  means it had one and the remote branch has since been deleted.
- **vs master**: the same pair against the default branch. `▲0` with `none` beside it is a
  local branch whose work is already in master: safe to delete.
- The verdict on the right is the one-line reason for the row's colour.

## Build and run

Needs [Rust](https://rustup.rs), Node 18+, `git` on `PATH`, and on Windows the MSVC build tools
and the WebView2 runtime (present on Windows 11).

```powershell
npm install
npm run tauri build --no-bundle   # exe in src-tauri/target/release/
npm run tauri build               # also produces an NSIS installer in target/release/bundle/
npm run tauri dev                 # live-reload development
```

Nothing needs elevation. "Start with Windows" in the tray menu writes a per-user Run key.

## Configure

Repos are **not** in source; they live in `%LOCALAPPDATA%\GitStateWidget\config.json`
(`~/Library/Application Support/GitStateWidget` on macOS) so the same build runs on any
machine with its own list. Add one with the tray menu ("Add repo…") or edit the file and
choose "Reload config". A config written by the earlier .NET version loads unchanged.

```json
{
  "IntervalMinutes": 5,
  "Fetch": true,
  "AlwaysOnTop": false,
  "StartHidden": false,
  "Repos": [
    { "Name": "MyRepo", "Path": "C:\\Src\\MyRepo", "Note": "optional", "DefaultBranch": null }
  ]
}
```

If the file fails to parse, the widget starts with no repos, writes the parser error to
`config-error.txt` beside it, and refuses to save over the broken file.

## Using it

- **Drag** the header to move; position is remembered. **Esc** or ✕ hides the panel; the tray
  icon (left-click) brings it back. Launching a second copy just raises the first.
- **Click a repo row** to unfold its local branches.
- Tray menu: refresh, add repo, open/reload config, fetch on/off, always on top, start with
  Windows, exit.

`git` runs with no console window, `GIT_TERMINAL_PROMPT=0`, the credential manager set
non-interactive and SSH in batch mode. It uses whatever credentials git on the machine already
has and stores nothing of its own. An expired credential shows as an amber "fetch failed" on
the row, never as a dialog.

## Layout

```
src/            panel: index.html, styles.css, main.ts (vanilla TypeScript, Vite)
src-tauri/src/  backend: config.rs, git.rs, scanner.rs (severity model), tray.rs, lib.rs
```

## History

- **v0.1** (2026-09-16): a PowerShell script on a 5-minute scheduled task wrote `git-state.html`
  into a folder-watching viewer. Hiding its console proved unreliable because `git fetch`
  spawns further console children.
- **v0.1.5** (2026-09-18, one day): a WPF/.NET 10 tray app. Fixed the flashing by owning
  process creation, but Windows-only by construction.
- **v0.2** (2026-09-18): this Tauri port, same severity model and config, one codebase for
  every desktop.
