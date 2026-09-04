# This fork

A patched [SourceGit](https://github.com/sourcegit-scm/sourcegit) that can drive WSL
repositories properly. It replaces SourceTree, which corrupts WSL checkouts (flattened
symlinks, dropped exec bits) because Git for Windows runs the git process against files it
reaches over the 9p share.

The rule everything follows from: **git runs in the same operating system as the working
tree.** WSL paths get `wsl git`, `C:\` paths get `git.exe`.

## Branches — get this right

| Branch | Contents |
|---|---|
| `wsl-eval` | Upstream-clean. Only things that could go to sourcegit-scm. |
| `personal-build` | `wsl-eval` + the app icon. **This is what gets built and run.** |

`personal-build` is rebased onto `wsl-eval`; it is **never** merged back. The icon is personal
branding upstream would not take, and keeping it off `wsl-eval` is what lets that branch stay
a clean diff for a pull request.

`origin` is the Bluejanis fork, `upstream` is sourcegit-scm.

## Building

```sh
git submodule update --init          # AvaloniaEdit is a submodule
dotnet build src/SourceGit.csproj -c Release
```

Two things that waste an hour if you skip them:

- **Init the submodule first.** Without it the build fails with ~118 `CS0246 type not found`
  errors in files you never touched, which looks like your change broke something.
- **Use the Windows dotnet**, not WSL's. From WSL that means
  `"/mnt/c/Program Files/dotnet/dotnet.exe"` with a Windows-style project path.
- **Close the running app before building** or the copy to `SourceGit.exe` fails on a file lock.

## What was added, and why

### `Models/WSL.cs` — dispatch and ssh
Chooses `wsl git` for `\\wsl.localhost` paths. `GetAgentSocket()` exists because `wsl.exe` runs
git **non-interactively**, and Debian/Ubuntu's stock `.bashrc` (the one in `/etc/skel`) returns
early in that case — so `SSH_AUTH_SOCK` is never exported and every fetch and push fails with
`Permission denied (publickey)`. `bash -lc` does not help: `.profile` sources `.bashrc`, which
then bails. The fix asks the user's own login shell for the socket and forwards it via WSLENV.

### `Models/WslWatcher.cs` — change notification
`FileSystemWatcher` on a `\\wsl.localhost` path raises **no error and never fires an event**.
Measured, not assumed. So no Windows-side GUI can notice a WSL repo changing; watching has to
happen inside the distro. One process per open repository:

| | inotify | poll fallback |
|---|---|---|
| CPU, 24 repos | 0% | 27% of a core |
| Latency | 1 ms | up to 2 s |

`inotify-tools` is what selects the fast path. It installs without a sudo password via
`wsl -d <distro> -u root -e apt-get install -y inotify-tools`.

Every open tab is watched, not only the visible one, because the tab bar renders per-tab
`DirtyState` — watching just the foreground repo would leave every other tab's dirty dot
silently wrong. The watcher restarts itself if its process dies (`wsl --shutdown`, the VM idle
timeout) and reports a change on the way back, since events were missed while it was down.

## Upstream status

- **ssh-agent fix** — posted as a comment on PR #1357. Awaiting a response.
- **WSL watcher** — not raised. Open an issue before sending code: it adds a background
  process per repository and an optional dependency, which a maintainer will have views about.
- **Uncommitted-changes node in the graph** — SourceGit keeps history and local changes in
  separate views, unlike SourceTree. Not a setting; it is a model difference. No upstream issue
  asks for it.

## Conventions

Match the surrounding code — upstream's style, not a new one. Keep commits scoped so that
anything upstream-worthy stays cherry-pickable off `wsl-eval`.
