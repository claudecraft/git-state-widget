namespace GitStateWidget;

/// <summary>
/// Ordered worst-last so Max() gives the dominant state.
///
/// The severity model is deliberate and carried over from the first version:
///   Alarm  - commits that exist in exactly one place (unpushed, or a branch with no upstream
///            that has work master does not). Losing this machine loses them.
///   Warn   - out of date (behind upstream), upstream deleted, or the fetch itself failed.
///   Info   - dirty / untracked trees and fully-merged branches. These trees are dirty by
///            design; alarming on them trains you to ignore the widget.
/// </summary>
public enum Severity {
    Ok = 0,
    Info = 1,
    Warn = 2,
    Alarm = 3
}

public sealed class RepoConfig {
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>Overrides detection of the branch that "merged" is measured against, e.g. "origin/main".</summary>
    public string? DefaultBranch { get; set; }
}

public sealed class BranchState {
    public string Name { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool IsDefault { get; set; }
    public string? Upstream { get; set; }
    public bool UpstreamGone { get; set; }
    public int AheadUpstream { get; set; }
    public int BehindUpstream { get; set; }
    /// <summary>Null when there is no default branch to compare against.</summary>
    public int? AheadDefault { get; set; }
    public int? BehindDefault { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset? When { get; set; }
    public string Subject { get; set; } = "";
    public Severity Severity { get; set; }
    /// <summary>Short reason shown next to the branch, e.g. "3 unpushed".</summary>
    public string Verdict { get; set; } = "";
}

public sealed class RepoState {
    public RepoConfig Config { get; set; } = new();
    public string Name => Config.Name;
    public string Path => Config.Path;
    public string CurrentBranch { get; set; } = "";
    public string? DefaultBranch { get; set; }
    public List<BranchState> Branches { get; set; } = new();
    public int Dirty { get; set; }
    public int Untracked { get; set; }
    public string? Error { get; set; }
    public string? FetchError { get; set; }
    public Severity Severity { get; set; }
    public DateTimeOffset ScannedAt { get; set; }

    public BranchState? Current => Branches.FirstOrDefault(b => b.IsCurrent);
}
