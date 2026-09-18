using System.Diagnostics;
using System.Text;

namespace GitStateWidget;

public sealed record GitResult(int ExitCode, string StdOut, string StdErr) {
    public bool Ok => ExitCode == 0;
    public IEnumerable<string> Lines => StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));
}

/// <summary>
/// Runs git with the process created by us, not by a shell: CreateNoWindow plus redirected
/// streams means neither git nor anything it spawns (git-remote-https, the credential manager)
/// ever gets a console to flash. The environment also forbids every interactive prompt, so an
/// expired credential surfaces as a failed fetch in the panel rather than as a dialog.
/// </summary>
public static class Git {
    public static async Task<GitResult> RunAsync(string repoPath, CancellationToken ct, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = repoPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GCM_INTERACTIVE"] = "never";
        psi.Environment["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes";
        psi.Environment["LC_ALL"] = "C";

        using var p = new Process { StartInfo = psi };
        try {
            p.Start();
        } catch (Exception ex) {
            return new GitResult(-1, "", "could not start git: " + ex.Message);
        }

        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try {
            await p.WaitForExitAsync(ct);
        } catch (OperationCanceledException) {
            try {
                p.Kill(entireProcessTree: true);
            } catch {
            }
            return new GitResult(-2, "", "timed out");
        }
        return new GitResult(p.ExitCode, await stdout, await stderr);
    }
}
