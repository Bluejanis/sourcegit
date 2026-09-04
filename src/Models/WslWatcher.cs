using System;
using System.Diagnostics;
using System.Threading;

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
        ///     Reported when the watcher knows that something changed but not what - by the poll
        ///     fallback, and after a restart, when changes may have been missed while it was down.
        /// </summary>
        public const string AnyChange = "ALL";

        public WslWatcher(string fullpath, Action<string> onChanged)
        {
            _fullpath = fullpath;
            _onChanged = onChanged;
            Start();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _restartTimer?.Dispose();
                _restartTimer = null;
            }

            StopProcess();
        }

        private void Start()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                try
                {
                    var start = new ProcessStartInfo("wsl");
                    start.ArgumentList.Add("-e");
                    start.ArgumentList.Add("sh");
                    start.ArgumentList.Add("-c");
                    start.ArgumentList.Add(SCRIPT);
                    start.WorkingDirectory = _fullpath;
                    start.RedirectStandardOutput = true;
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;

                    _remotePid = 0;
                    _proc = new Process() { StartInfo = start, EnableRaisingEvents = true };
                    _proc.OutputDataReceived += OnLineReceived;
                    _proc.Exited += OnProcessExited;
                    _proc.Start();
                    _proc.BeginOutputReadLine();
                    _startedAt = DateTime.Now;
                    return;
                }
                catch
                {
                    _proc = null;
                }
            }

            // Starting failed - back off and try again rather than going silent forever.
            ScheduleRestart();
        }

        /// <summary>
        ///     The distro side can die without the app noticing: `wsl --shutdown`, the VM idle
        ///     timeout, or the process being killed. Left alone that stops refreshes silently,
        ///     so bring it back and assume the repository moved on while it was gone.
        /// </summary>
        private void OnProcessExited(object sender, EventArgs e)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
            }

            _onChanged?.Invoke(AnyChange);
            ScheduleRestart();
        }

        private void ScheduleRestart()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                // A watcher that ran for a while was healthy, so treat this as a one-off and
                // retry promptly. Repeated quick failures back off up to half a minute.
                var wasHealthy = (DateTime.Now - _startedAt).TotalSeconds >= HEALTHY_AFTER_SECONDS;
                _restartDelay = wasHealthy
                    ? MIN_RESTART_DELAY
                    : Math.Min(_restartDelay * 2, MAX_RESTART_DELAY);

                _restartTimer?.Dispose();
                _restartTimer = new Timer(_ => Restart(), null, _restartDelay, Timeout.Infinite);
            }
        }

        private void Restart()
        {
            StopProcess();
            Start();
        }

        private void StopProcess()
        {
            Process proc;
            int remotePid;

            lock (_sync)
            {
                proc = _proc;
                remotePid = _remotePid;
                _proc = null;
                _remotePid = 0;
            }

            if (proc == null)
                return;

            proc.Exited -= OnProcessExited;
            proc.OutputDataReceived -= OnLineReceived;

            // The shell traps EXIT and takes inotifywait down with it, but only once it is
            // signalled - killing wsl.exe alone would leave it running inside the distro.
            if (remotePid > 0)
            {
                try
                {
                    var kill = new ProcessStartInfo("wsl");
                    kill.ArgumentList.Add("-e");
                    kill.ArgumentList.Add("kill");
                    kill.ArgumentList.Add("-TERM");
                    kill.ArgumentList.Add(remotePid.ToString());
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
                {
                    lock (_sync)
                        _remotePid = pid;
                }

                return;
            }

            _onChanged?.Invoke(line);
        }

        private const int MIN_RESTART_DELAY = 1000;
        private const int MAX_RESTART_DELAY = 30000;
        private const int HEALTHY_AFTER_SECONDS = 60;

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

        private readonly string _fullpath = string.Empty;
        private readonly Action<string> _onChanged = null;
        private readonly Lock _sync = new();

        private Process _proc = null;
        private int _remotePid = 0;
        private bool _disposed = false;
        private int _restartDelay = MIN_RESTART_DELAY;
        private DateTime _startedAt = DateTime.MinValue;
        private Timer _restartTimer = null;
    }
}
