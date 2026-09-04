using System;
using System.Diagnostics;

namespace SourceGit.Models
{
    /// <summary>
    ///     Change notification for repositories that live inside WSL.
    ///
    ///     FileSystemWatcher raises no exception on a \\wsl.localhost path but never fires an
    ///     event either, so a WSL repository would otherwise never refresh on its own. Watching
    ///     has to happen inside the distro: one long-lived process per repository, using
    ///     inotifywait where the distro provides it and a cheap git-status poll where it does not.
    /// </summary>
    public class WslWatcher : IDisposable
    {
        /// <summary>
        ///     Emitted by the poll fallback, which knows that something changed but not what.
        /// </summary>
        public const string AnyChange = "ALL";

        public WslWatcher(string fullpath, Action<string> onChanged)
        {
            _onChanged = onChanged;

            try
            {
                var start = new ProcessStartInfo("wsl");
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add("sh");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(SCRIPT);
                start.WorkingDirectory = fullpath;
                start.RedirectStandardOutput = true;
                start.UseShellExecute = false;
                start.CreateNoWindow = true;

                _proc = new Process() { StartInfo = start };
                _proc.OutputDataReceived += OnLineReceived;
                _proc.Start();
                _proc.BeginOutputReadLine();
            }
            catch
            {
                // No watching is the pre-existing behaviour, so degrade to it silently.
                _proc = null;
            }
        }

        public void Dispose()
        {
            var proc = _proc;
            _proc = null;
            if (proc == null)
                return;

            // The shell traps EXIT and takes inotifywait down with it, but only once it is
            // signalled - killing wsl.exe alone would leave it running inside the distro.
            if (_remotePid > 0)
            {
                try
                {
                    var kill = new ProcessStartInfo("wsl");
                    kill.ArgumentList.Add("-e");
                    kill.ArgumentList.Add("kill");
                    kill.ArgumentList.Add("-TERM");
                    kill.ArgumentList.Add(_remotePid.ToString());
                    kill.UseShellExecute = false;
                    kill.CreateNoWindow = true;
                    Process.Start(kill)?.WaitForExit(3000);
                }
                catch
                {
                    // Falls through to killing the Windows side.
                }
            }

            try
            {
                if (!proc.HasExited)
                    proc.Kill(true);
            }
            catch
            {
                // Already gone.
            }

            proc.Dispose();
        }

        private void OnLineReceived(object sender, DataReceivedEventArgs e)
        {
            var line = e.Data;
            if (string.IsNullOrEmpty(line))
                return;

            if (line.StartsWith("PID:", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), out var pid))
                    _remotePid = pid;
                return;
            }

            _onChanged?.Invoke(line);
        }

        private const string SCRIPT = """
            echo "PID:$$"
            cleanup() { [ -n "$c" ] && kill "$c" 2>/dev/null; }
            trap cleanup INT TERM EXIT

            if command -v inotifywait >/dev/null 2>&1; then
                gd=$(git rev-parse --git-dir 2>/dev/null || echo .git)
                inotifywait -m -q -r -e modify,create,delete,move,attrib \
                    --exclude '(/\.git/(objects|lfs|logs)/|/node_modules/|\.lock$)' \
                    --format '%w%f' . "$gd" 2>/dev/null &
                c=$!
                wait $c
            else
                while :; do
                    cur=$(git status --porcelain=v2 --branch 2>/dev/null | cksum)
                    if [ "$cur" != "$prev" ]; then
                        [ -n "$prev" ] && echo ALL
                        prev=$cur
                    fi
                    sleep 2
                done
            fi
            """;

        private Process _proc = null;
        private int _remotePid = 0;
        private readonly Action<string> _onChanged = null;
    }
}
