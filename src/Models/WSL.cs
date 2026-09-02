using System;
using System.Diagnostics;
using System.Text;

namespace SourceGit.Models
{
    public class WSL
    {
        public string Path { get; set; } = "";

        public bool IsWSLPath()
        {
            return OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Path) &&
                (Path.StartsWith("//wsl.localhost/", StringComparison.OrdinalIgnoreCase) ||
                Path.StartsWith("//wsl$/", StringComparison.OrdinalIgnoreCase));
        }

        private static string _agentSocket = null;

        // Locate a live ssh-agent socket inside WSL.
        //
        // SSH_AUTH_SOCK is exported by whatever starts the agent, which is
        // almost always a shell rc file. "wsl git ..." runs non-interactively
        // and Debian/Ubuntu's stock .bashrc returns early in that case, so the
        // variable is unset and agent-based auth fails with
        // "Permission denied (publickey)".
        //
        // Ask the user's own login shell, interactively, so that whichever rc
        // exports it actually runs - that covers any agent setup rather than a
        // fixed set of socket paths. Markers keep the answer separable from any
        // banner the rc may print. Known locations are a fallback.
        public static string GetAgentSocket()
        {
            if (_agentSocket != null)
                return _agentSocket;

            _agentSocket = string.Empty;
            try
            {
                const string script = """
                    out=$("${SHELL:-/bin/bash}" -ic 'printf "\n<<SOCK>>%s<<END>>" "$SSH_AUTH_SOCK"' 2>/dev/null)
                    s=${out##*<<SOCK>>}; s=${s%%<<END>>*}
                    [ -S "$s" ] && { printf %s "$s"; exit 0; }
                    for c in "$HOME/.ssh/agent.sock" "$XDG_RUNTIME_DIR/ssh-agent.socket" /tmp/ssh-agent.sock; do
                        [ -S "$c" ] && { printf %s "$c"; exit 0; }
                    done
                    exit 1
                    """;

                var probe = new ProcessStartInfo("wsl");
                probe.ArgumentList.Add("-e");
                probe.ArgumentList.Add("sh");
                probe.ArgumentList.Add("-c");
                probe.ArgumentList.Add(script);
                probe.RedirectStandardOutput = true;
                probe.UseShellExecute = false;
                probe.CreateNoWindow = true;

                using var proc = Process.Start(probe);
                if (proc != null)
                {
                    var found = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(10000);
                    if (proc.ExitCode == 0 && !string.IsNullOrEmpty(found))
                        _agentSocket = found;
                }
            }
            catch
            {
                // Leave it empty - git will report the auth failure itself.
            }

            return _agentSocket;
        }

        public void SetEnvironmentForProcess(ProcessStartInfo start)
        {
            start.Environment.Add("LANG", "C");
            start.Environment.Add("LC_ALL", "C");

            if (start.Environment.TryGetValue("SSH_ASKPASS", out var askPassPath) && !string.IsNullOrEmpty(askPassPath) && System.IO.Path.IsPathRooted(askPassPath))
            {
                // Convert Windows path to WSL path
                var driveLetter = askPassPath[0].ToString();
                start.Environment["SSH_ASKPASS"] = askPassPath
                    .Replace($"{driveLetter}:\\", $"/mnt/{driveLetter.ToLowerInvariant()}/")
                    .Replace('\\', '/');
            }

            var agentSocket = GetAgentSocket();
            if (!string.IsNullOrEmpty(agentSocket) && !start.Environment.ContainsKey("SSH_AUTH_SOCK"))
                start.Environment["SSH_AUTH_SOCK"] = agentSocket;

            var wslEnvirionment = new[] { "SSH_AUTH_SOCK", "SSH_ASKPASS", "SSH_ASKPASS_REQUIRE", "SOURCEGIT_LAUNCH_AS_ASKPASS", "GIT_SSH_COMMAND", "LANG", "LC_ALL" };
            var wslEnvBuilder = new StringBuilder();

            foreach (string env in wslEnvirionment)
            {
                if (start.Environment.ContainsKey(env))
                    wslEnvBuilder.Append($"{env}:");
            }

            // Forward environment variables for WSL
            start.Environment.Add("WSLENV", wslEnvBuilder.ToString().TrimEnd(':'));
        }
    }
}
