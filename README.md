# Git State Widget

A small Windows desktop widget that answers one question at a glance: **is any of my work
sitting in exactly one place?** One row per repo, click a row to unfold its branches. A tray
icon mirrors the worst state so the answer survives a maximised window.

Built with WPF on .NET 10. No scheduled task, no launcher script, no console window: the widget
runs its own timer and starts `git` itself with no window attached, so nothing can flash.

## Severity model (deliberate)

| Colour | Means | Examples |
|---|---|---|
| **Red** | Commits that exist in one place only | ahead of upstream; no upstream and ahead of master; upstream deleted with unmerged work |
| **Amber** | Out of date, or the picture may be stale | behind upstream (current branch only); fetch failed; upstream deleted but merged |
| Grey | Informational | dirty or untracked files; branches fully merged into master ("cleanup candidate") |
| Green | Pushed and current | |

Dirty trees are grey on purpose: these repos are dirty by design, and alarming on them would
train you to ignore the widget.

"Merged" is measured against the repo's default branch: `origin/HEAD` if set, else
`origin/main`, `origin/master`, then a local `main`/`master`. Override per repo with
`DefaultBranch` in the config. The comparison uses `--cherry-pick`, so rebased and cherry-picked
commits count as merged; **squash merges do not**, so a branch that was squash-merged still
shows its commits as "only here". That is a limit of the data, not a bug to fix here.

## Install

```powershell
.\Install.ps1            # build, register in the HKCU Run key, launch
.\Install.ps1 -NoStartup # build and launch only
.\Uninstall.ps1          # stop, remove from startup (also removes the legacy scheduled task)
```

Needs the .NET 10 SDK and `git` on `PATH`. Nothing needs elevation.

## Configure

Repos are **not** in source; they live in `%LOCALAPPDATA%\GitStateWidget\config.json` so the
same build runs on any machine with its own list. Add one with the tray menu ("Add repo…") or
edit the file and choose "Reload config".

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
  icon (left-click) brings it back.
- **Click a repo row** to unfold its local branches: ahead/behind its upstream, ahead/behind
  the default branch, last commit age, and a one-line verdict.
- Tray menu: refresh, add repo, open/reload config, fetch on/off, always on top, start with
  Windows, exit.

`git` runs with `GIT_TERMINAL_PROMPT=0` and the credential manager set non-interactive. An
expired credential therefore shows as an amber "fetch failed" on the row, never as a dialog.

## History

The first version was a PowerShell script on a 5-minute scheduled task that wrote `git-state.html`
into the Artifact Viewer watch folder. Hiding its console proved unreliable (the script's `git
fetch` spawned further console children), so it was replaced by this app on 2026-09-18.
