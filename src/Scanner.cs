using System.IO;
using System.Text.RegularExpressions;

namespace GitStateWidget;

public static class Scanner {
    private static readonly Regex Track = new(@"ahead (\d+)|behind (\d+)|(gone)", RegexOptions.Compiled);
    private static readonly Regex Counts = new(@"^(\d+)\s+(\d+)", RegexOptions.Compiled);

    public static async Task<RepoState> ScanAsync(RepoConfig cfg, bool fetch, CancellationToken outer) {
        var r = new RepoState { Config = cfg, ScannedAt = DateTimeOffset.Now };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = cts.Token;

        try {
            if (!Directory.Exists(cfg.Path)) {
                return Fail(r, "path does not exist");
            }
            if (!Directory.Exists(Path.Combine(cfg.Path, ".git")) && !File.Exists(Path.Combine(cfg.Path, ".git"))) {
                return Fail(r, "not a git repo");
            }

            var head = await Git.RunAsync(cfg.Path, ct, "rev-parse", "--abbrev-ref", "HEAD");
            if (!head.Ok || string.IsNullOrWhiteSpace(head.StdOut)) {
                return Fail(r, "could not read HEAD");
            }
            r.CurrentBranch = head.StdOut.Trim();

            if (fetch) {
                var f = await Git.RunAsync(cfg.Path, ct, "fetch", "--quiet", "--no-tags", "--prune");
                if (!f.Ok) {
                    var msg = f.StdErr.Trim();
                    r.FetchError = string.IsNullOrEmpty(msg) ? "fetch failed" : FirstLine(msg);
                }
            }

            r.DefaultBranch = await ResolveDefaultBranchAsync(cfg, ct);

            // One call for every local branch: name, upstream, tracking summary, tip, date, subject.
            var refs = await Git.RunAsync(cfg.Path, ct, "for-each-ref",
                "--format=%(refname:short)%09%(upstream:short)%09%(upstream:track)%09%(objectname:short)%09%(committerdate:iso-strict)%09%(subject)",
                "refs/heads");
            foreach (var line in refs.Lines) {
                var parts = line.Split('\t', 6);
                if (parts.Length < 4) {
                    continue;
                }
                var b = new BranchState {
                    Name = parts[0],
                    Upstream = string.IsNullOrEmpty(parts[1]) ? null : parts[1],
                    Hash = parts[3],
                    Subject = parts.Length > 5 ? parts[5] : ""
                };
                b.IsCurrent = b.Name == r.CurrentBranch;
                if (parts.Length > 4 && DateTimeOffset.TryParse(parts[4], out var when)) {
                    b.When = when;
                }
                foreach (Match m in Track.Matches(parts[2])) {
                    if (m.Groups[1].Success) {
                        b.AheadUpstream = int.Parse(m.Groups[1].Value);
                    } else if (m.Groups[2].Success) {
                        b.BehindUpstream = int.Parse(m.Groups[2].Value);
                    } else if (m.Groups[3].Success) {
                        b.UpstreamGone = true;
                    }
                }
                r.Branches.Add(b);
            }

            if (r.DefaultBranch != null) {
                var defaultLocal = r.DefaultBranch.Contains('/') ? r.DefaultBranch[(r.DefaultBranch.IndexOf('/') + 1)..] : r.DefaultBranch;
                foreach (var b in r.Branches) {
                    b.IsDefault = b.Name == defaultLocal;
                    // --cherry-pick drops commits whose patch already exists on the other side, so a
                    // rebased or cherry-picked branch reads as merged. Squash merges still look unique.
                    var c = await Git.RunAsync(cfg.Path, ct, "rev-list", "--left-right", "--count", "--cherry-pick", b.Name + "..." + r.DefaultBranch);
                    var m = Counts.Match(c.StdOut);
                    if (c.Ok && m.Success) {
                        b.AheadDefault = int.Parse(m.Groups[1].Value);
                        b.BehindDefault = int.Parse(m.Groups[2].Value);
                    }
                }
            }

            var status = await Git.RunAsync(cfg.Path, ct, "status", "--porcelain");
            foreach (var line in status.Lines) {
                if (line.StartsWith("??")) {
                    r.Untracked++;
                } else {
                    r.Dirty++;
                }
            }

            // Detached HEAD: for-each-ref lists no current branch, so synthesise a row for it.
            if (r.Current == null) {
                var last = await Git.RunAsync(cfg.Path, ct, "log", "-1", "--format=%h%x09%cI%x09%s");
                var parts = last.StdOut.Trim().Split('\t', 3);
                var b = new BranchState { Name = r.CurrentBranch == "HEAD" ? "(detached)" : r.CurrentBranch, IsCurrent = true };
                if (parts.Length == 3) {
                    b.Hash = parts[0];
                    if (DateTimeOffset.TryParse(parts[1], out var when)) {
                        b.When = when;
                    }
                    b.Subject = parts[2];
                }
                r.Branches.Insert(0, b);
            }

            foreach (var b in r.Branches) {
                Judge(b);
            }
            // Current branch first, then the default, then alarms, then by recency.
            r.Branches = r.Branches
                .OrderByDescending(b => b.IsCurrent)
                .ThenByDescending(b => b.IsDefault)
                .ThenByDescending(b => b.Severity)
                .ThenByDescending(b => b.When ?? DateTimeOffset.MinValue)
                .ToList();

            r.Severity = r.Branches.Count == 0 ? Severity.Ok : r.Branches.Max(b => b.Severity);
            if (r.FetchError != null && r.Severity < Severity.Warn) {
                r.Severity = Severity.Warn;
            }
            if (r.Severity == Severity.Ok && (r.Dirty > 0 || r.Untracked > 0)) {
                r.Severity = Severity.Info;
            }
            return r;
        } catch (OperationCanceledException) {
            return Fail(r, "scan timed out");
        } catch (Exception ex) {
            return Fail(r, ex.Message);
        }
    }

    /// <summary>
    /// The branch "merged" is measured against. Config override first, then origin/HEAD, then
    /// origin/main or origin/master, then a local main or master. Null if none exist.
    /// </summary>
    private static async Task<string?> ResolveDefaultBranchAsync(RepoConfig cfg, CancellationToken ct) {
        if (!string.IsNullOrWhiteSpace(cfg.DefaultBranch)) {
            return cfg.DefaultBranch;
        }
        var sym = await Git.RunAsync(cfg.Path, ct, "symbolic-ref", "-q", "--short", "refs/remotes/origin/HEAD");
        if (sym.Ok && !string.IsNullOrWhiteSpace(sym.StdOut)) {
            return sym.StdOut.Trim();
        }
        foreach (var candidate in new[] { "refs/remotes/origin/main", "refs/remotes/origin/master", "refs/heads/main", "refs/heads/master" }) {
            var v = await Git.RunAsync(cfg.Path, ct, "rev-parse", "--verify", "-q", candidate);
            if (v.Ok) {
                return candidate.Replace("refs/remotes/", "").Replace("refs/heads/", "");
            }
        }
        return null;
    }

    private static void Judge(BranchState b) {
        if (b.Upstream != null && !b.UpstreamGone) {
            if (b.AheadUpstream > 0) {
                b.Severity = Severity.Alarm;
                b.Verdict = Plural(b.AheadUpstream, "commit") + " unpushed";
                return;
            }
            if (b.BehindUpstream > 0 && b.IsCurrent) {
                b.Severity = Severity.Warn;
                b.Verdict = Plural(b.BehindUpstream, "commit") + " behind upstream";
                return;
            }
            if (!b.IsDefault && b.AheadDefault == 0) {
                b.Severity = Severity.Info;
                b.Verdict = "merged, cleanup candidate";
                return;
            }
            b.Severity = Severity.Ok;
            b.Verdict = b.IsDefault ? "current" : "pushed";
            return;
        }

        if (b.UpstreamGone) {
            if (b.AheadDefault is > 0) {
                b.Severity = Severity.Alarm;
                b.Verdict = "upstream deleted, " + Plural(b.AheadDefault.Value, "commit") + " only here";
            } else {
                b.Severity = Severity.Warn;
                b.Verdict = "upstream deleted, merged";
            }
            return;
        }

        // No upstream at all.
        if (b.AheadDefault is > 0) {
            b.Severity = Severity.Alarm;
            b.Verdict = Plural(b.AheadDefault.Value, "commit") + " only here";
        } else if (b.AheadDefault == 0) {
            b.Severity = Severity.Info;
            b.Verdict = "merged, local only";
        } else {
            // No default branch to compare against either: nothing backs this up.
            b.Severity = Severity.Alarm;
            b.Verdict = "nothing backs this up";
        }
    }

    private static RepoState Fail(RepoState r, string error) {
        r.Error = error;
        r.Severity = Severity.Alarm;
        return r;
    }

    private static string FirstLine(string s) {
        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i].TrimEnd('\r');
    }

    public static string Plural(int n, string word) {
        return n == 1 ? n + " " + word : n + " " + word + "s";
    }

    public static string Ago(DateTimeOffset? t) {
        if (t == null) {
            return "-";
        }
        var mins = (int)(DateTimeOffset.Now - t.Value).TotalMinutes;
        if (mins < 1) {
            return "just now";
        }
        if (mins < 60) {
            return mins + " min ago";
        }
        var hrs = mins / 60;
        if (hrs < 24) {
            return hrs + " hr ago";
        }
        var days = hrs / 24;
        return days == 1 ? "1 day ago" : days + " days ago";
    }
}
