import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow, LogicalSize } from "@tauri-apps/api/window";

type Severity = "ok" | "info" | "warn" | "alarm";

interface Branch {
  name: string;
  isCurrent: boolean;
  isDefault: boolean;
  upstream: string | null;
  upstreamGone: boolean;
  aheadUpstream: number;
  behindUpstream: number;
  aheadDefault: number | null;
  behindDefault: number | null;
  hash: string;
  when: string | null;
  subject: string;
  severity: Severity;
  verdict: string;
}

interface Repo {
  name: string;
  path: string;
  note: string | null;
  currentBranch: string;
  defaultBranch: string | null;
  branches: Branch[];
  dirty: number;
  untracked: number;
  error: string | null;
  fetchError: string | null;
  severity: Severity;
}

interface Snapshot {
  headline: string;
  overall: Severity;
  lastScan: string | null;
  scanning: boolean;
  fetch: boolean;
  intervalMinutes: number;
  configPath: string;
  configured: number;
  repos: Repo[];
}

const PANEL_WIDTH = 640;
const expanded = new Set<string>();
let snapshot: Snapshot | null = null;

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

function plural(n: number, word: string): string {
  return n === 1 ? `${n} ${word}` : `${n} ${word}s`;
}

function ago(iso: string | null): string {
  if (!iso) return "-";
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return "-";
  const mins = Math.floor((Date.now() - t) / 60000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins} min ago`;
  const hrs = Math.floor(mins / 60);
  if (hrs < 24) return `${hrs} hr ago`;
  const days = Math.floor(hrs / 24);
  return days === 1 ? "1 day ago" : `${days} days ago`;
}

function stamp(s: Snapshot): string {
  if (!s.lastScan) return "not scanned yet";
  const d = new Date(s.lastScan);
  const when = d.toLocaleDateString(undefined, { weekday: "short", day: "numeric", month: "short" })
    + ", " + d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", hour12: false });
  const fetch = s.fetch ? "fetched" : "no fetch, behind counts may be stale";
  return `${when} · ${fetch} · every ${s.intervalMinutes} min`;
}

function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

/** ▲ahead ▼behind, or none / gone. */
function counts(c: { ahead: number; behind: number } | null, gone: boolean, wide: boolean, aheadIsAlarm = true): HTMLElement {
  const box = el("div", "counts" + (wide ? "" : " bcell c"));
  if (gone) {
    box.classList.add("gone");
    box.textContent = "gone";
    return box;
  }
  if (!c) {
    box.classList.add("none");
    box.textContent = wide ? "no upstream" : "none";
    return box;
  }
  const up = el("span", "up", `▲${c.ahead}`);
  if (c.ahead > 0) up.classList.add(aheadIsAlarm ? "hot" : "neutral");
  const down = el("span", "down", ` ▼${c.behind}`);
  if (c.behind > 0 && aheadIsAlarm) down.classList.add("hot");
  box.append(up, down);
  return box;
}

function branchList(r: Repo): HTMLElement {
  const wrap = el("div", "branches");
  const head = el("div", "bgrid bhead");
  const def = r.defaultBranch ? "vs " + r.defaultBranch.slice(r.defaultBranch.indexOf("/") + 1) : "vs default";
  head.append(el("span"), el("span", "", "branch"), el("span", "c", "vs upstream"), el("span", "c", def), el("span", "", "last commit"), el("span"));
  wrap.append(head);

  for (const b of r.branches) {
    const g = el("div", "bgrid");
    g.append(el("div", `dot small ${b.severity}`));
    const name = el("div", "bname" + (b.isCurrent ? " cur" : ""), b.name);
    name.title = `${b.hash}  ${b.subject}`;
    if (b.isCurrent) name.append(el("span", "mark", "◀"));
    g.append(name);
    g.append(counts(b.upstream ? { ahead: b.aheadUpstream, behind: b.behindUpstream } : null, b.upstreamGone, false));
    if (b.isDefault) {
      g.append(el("div", "bcell c", "default"));
    } else if (b.aheadDefault === null) {
      g.append(el("div", "bcell c", "–"));
    } else {
      g.append(counts({ ahead: b.aheadDefault, behind: b.behindDefault ?? 0 }, false, false, false));
    }
    g.append(el("div", "bcell", ago(b.when)));
    const v = el("div", `bcell r sev-${b.severity}`, b.verdict);
    v.title = b.verdict;
    g.append(v);
    wrap.append(g);
  }
  return wrap;
}

function repoBlock(r: Repo, scanned: boolean): HTMLElement {
  const block = el("div", "block");
  const open = expanded.has(r.name);
  const row = el("div", "row" + (open ? "" : " collapsed"));
  row.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    if (expanded.has(r.name)) expanded.delete(r.name); else expanded.add(r.name);
    render();
  });

  row.append(el("div", `dot ${r.severity}`));

  const name = el("div", "name");
  const line1 = el("div", "line1");
  line1.append(el("b", "", r.name));
  if (r.currentBranch) line1.append(el("span", "branch", r.currentBranch));
  name.append(line1);
  const sub = el("div", "sub");
  if (r.error) {
    sub.textContent = r.error;
    sub.classList.add("bad");
  } else if (!scanned) {
    sub.textContent = "scanning…";
  } else {
    const bits: string[] = [];
    const others = r.branches.length - 1;
    if (others > 0) {
      const alarms = r.branches.filter((b) => !b.isCurrent && b.severity === "alarm").length;
      bits.push(plural(others, "other branch") + (alarms > 0 ? `, ${alarms} unbacked` : ""));
    }
    if (r.dirty > 0) bits.push(`${r.dirty} modified`);
    if (r.untracked > 0) bits.push(`${r.untracked} untracked`);
    if (bits.length === 0) bits.push("clean");
    if (r.fetchError) {
      bits.push("fetch failed: " + r.fetchError);
      sub.classList.add("warn");
    }
    sub.textContent = bits.join(" · ");
  }
  name.append(sub);
  row.append(name);

  const cur = r.branches.find((b) => b.isCurrent);
  if (cur && !r.error) {
    row.append(counts(cur.upstream ? { ahead: cur.aheadUpstream, behind: cur.behindUpstream } : null, cur.upstreamGone, true));
    row.append(el("div", `verdict sev-${cur.severity}`, cur.verdict));
  } else {
    row.append(el("div"), el("div"));
  }
  row.append(el("div", "chev", open ? "▾" : "▸"));
  block.append(row);

  if (open && !r.error && scanned) block.append(branchList(r));
  return block;
}

function render() {
  const s = snapshot;
  const body = $("body");
  body.replaceChildren();
  if (!s) {
    fit();
    return;
  }
  const headline = $("headline");
  headline.textContent = s.headline;
  headline.className = `headline ${s.overall}`;
  $("stamp").textContent = stamp(s);
  $("refresh").classList.toggle("busy", s.scanning);

  if (s.configured === 0) {
    body.append(el("div", "empty", `Right-click the tray icon and choose “Add repo…”, or edit\n${s.configPath}`));
    fit();
    return;
  }
  const card = el("div", "card");
  const scanned = s.lastScan !== null;
  for (const r of s.repos) card.append(repoBlock(r, scanned));
  body.append(card);
  fit();
}

/** Size the window to the content; the panel has no chrome of its own to do it. */
async function fit() {
  await new Promise((r) => requestAnimationFrame(r));
  const h = Math.ceil($("panel").getBoundingClientRect().height) + 2;
  try {
    await getCurrentWindow().setSize(new LogicalSize(PANEL_WIDTH, Math.max(120, h)));
  } catch {
    // Not fatal; the next render tries again.
  }
}

async function load() {
  snapshot = await invoke<Snapshot>("get_snapshot");
  render();
}

window.addEventListener("DOMContentLoaded", async () => {
  $("refresh").addEventListener("click", () => invoke("refresh_now"));
  $("hide").addEventListener("click", () => invoke("hide_panel"));
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") invoke("hide_panel");
  });
  document.addEventListener("contextmenu", (e) => e.preventDefault());

  await listen<Snapshot>("snapshot", (e) => {
    snapshot = e.payload;
    render();
  });
  setInterval(() => { if (snapshot) render(); }, 30000);
  await load();
});
