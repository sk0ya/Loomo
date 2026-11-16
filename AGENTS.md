# AGENTS.md

## What this is

Loomo（ルーモ, Loom × Room）is a Windows-only C# / WPF desktop app: a **room the human works in**. Panes
(Terminal / Editor / Browser / Git / Diff / Debug / … — the authoritative list is the `PaneKind` enum in
`WorkspaceStateStore.cs`) are tiled or docked, detached into windows and restored down to caret and scroll; the
軌跡 rail (§27) records the **human's** navigation. AI work happens in Claude Code inside the terminal (§38); the
in-app AI pane (local inference agent) was removed on 2026-11-14 — `PaneKind.Ai`/`Trace` stay only because the enum
is persisted numerically. Judge changes by the room chapters (§24 / §26 / §27). A large App layer is by design.

- Solution `sk0ya.Loomo.sln`; namespaces/assemblies are `sk0ya.Loomo.*`, project folders drop the prefix.
- Design doc: `docs/設計/` (Japanese). Start at `docs/設計/README.md` (index + §→file map); §N references
  throughout the code resolve there. Chapters and many sources are 30–80KB with very long lines (XAML,
  `MarkdownPage.cs`): Grep the heading/symbol first, then Read only that range.
- Comments, commit messages and UI strings are in Japanese — match that.

## Commands

```powershell
dotnet build sk0ya.Loomo.sln
dotnet run --project src/Loomo.App/Loomo.App.csproj
dotnet test
dotnet test --filter "FullyQualifiedName~DiffUtil"
```

- TFM: each `*.csproj` is the source of truth (App/Tests use `-windows`). The exe is always
  `src/Loomo.App/bin/Debug/sk0ya.Loomo.App.exe` (TFM/RID not appended to the output path, on purpose).
- `Directory.Build.props` pins `RuntimeIdentifier=win-x64` for every project — without it SQLite and other
  natives for every platform land in the output. Don't remove it.
- If a running Loomo locks `bin/Debug` and breaks the build, stop that `sk0ya.Loomo.App.exe` (only Loomo itself)
  and rebuild.

## UI changes — usability is part of done

Any change to a screen (pane, dialog, settings, detached window, pane-header button, context menu, face HTML) goes
through the project skill `ui-usability` (`.claude/skills/ui-usability/SKILL.md`): before coding a new screen, an entry
point move, or removing/hiding/merging an existing feature, write the user's step script and get agreement; build to
its rules (labels say what the thing is, state and an exit are visible, Controls.xaml styles, `Fs*` tokens, palette
keys, keyboard path); then verify before handing over — `tools/ui/Test-XamlChanges.ps1` (static), `tools/ViewHarness`
(renders a view in every theme × font size and flags clipped text, unstyled buttons, overflow, low contrast; read
every PNG), and the real app driven with `tools/ui/LoomoUi.psm1` (SendInput clicks/keys + screenshots) along the
script. Report what was verified and what the human still needs to look at; "未確認" alone is not a report.

## Architecture

One-way dependency **App → Services → Core**. `Loomo.Core` has no WPF: models and service interfaces. Adapters
live in `Loomo.Services` (sk0ya controls, filesystem, LSP, Git).
DI is wired in `src/Loomo.App/App.xaml.cs`; concrete services are registered as concrete **and** interface so one
instance backs both. UI is MVVM (CommunityToolkit.Mvvm), panes talk via `WeakReferenceMessenger`.

### Multi-root workspaces — ask the workspace

A workspace is a **set of folders** (`IWorkspaceService.Folders`), the most repeated defect in this codebase. Use
`workspace.Contains(path)`, `FolderFor(path)`, `FolderForOrPrimary(path)` (base to resolve against),
`ToDisplayPath(path)` — one implementation in `Loomo.Core/Files/WorkspacePaths.cs`. **Never hand-roll `StartsWith`
against a root**; use `PrimaryFolder` only when the primary specifically is meant (terminal cwd, snapshot
identity, default git target). Pass the workspace to resolvers (`FileLinkResolver.TryResolve(workspace, …)`,
`LinkOpenTargetResolver.Resolve`, `EditorSupportContext.For`) instead of choosing a base folder at the call site.
Other `RootPath`s (`WorkspaceSnapshot`, `GitService`, `DebugLaunchProfileStore`) are different concepts.

Git, one level down (§40): don't use the ambient `GitService.RootPath` (= repo selected in the strip) to find a
path's repository. Use `GitService.Repositories.RepositoryFor(path)` and run against `git.ForPath(path)` /
`git.For(root)`. Views needing repo state read `GitWorkspaceStatus` instead of spawning `git status`; any status
must use `--no-optional-locks` (else the `.git` watcher re-fires on its own index write).

### Threading and persistence (§31.15)

Keep editor operations off the UI thread: build the plan where the state is readable, run it on a writer thread
(`FileWritePlan` + `DeferredWriteQueue`, `WorkspaceStateStore.SaveDeferred`, `TrailStore.AppendDeferred`,
`PreparedFileLoad.Prepare`). **No disk write or full-buffer copy (`VimEditorControl.Text`) on a per-keystroke
path.** A syntax language registered into the editor must implement `ILineStateSyntaxLanguage`, or every keystroke
re-tokenizes the whole file. Child-process output is read through `ChildProcessIo` (dedicated threads), never the
thread pool.

`%APPDATA%/Loomo/` holds `settings.json`, `window.json` (main window placement), `workspaces/`, `trail.db`, `provenance.db` (§43), `faces/` + `faces-state.json` (§44.2), `motion/` (§44.3), `orders/` (§44.4; worktrees under
`%LOCALAPPDATA%/Loomo/orders/`), `themes/<app|editor|terminal|preview>/` (user color themes per area, §3), `lsp-servers.json`, `formatters.json`.

### sk0ya control libraries

Terminal/Editor are the NuGet packages `sk0ya.Terminal.Controls` / `sk0ya.Editor.Controls`, but their namespaces
drop the prefix (`Terminal.Tabs.TerminalTabView`, `Editor.Controls.VimEditorControl`). Versions are pinned only in
the root `Directory.Build.props`; `-p:UseLocalEditor=true` / `-p:UseLocalTerminal=true` build against
`C:\Projects\Editor` / `C:\Projects\Terminal`. Reflecting over these assemblies from the shell tends to
hallucinate — dump the API to a file and Grep it.

- **The visible terminal is human-only.** `TerminalService.RunCommandAsync` runs in a separate
  PowerShell process. Don't type into a tab whose foreground is busy (`TerminalService.IsForegroundBusy`).
- **Persistent sessions (§34):** tabs attach to the resident `sk0ya.Loomo.Pty.Host` over a named pipe; tab id is the
  session key. `RemotePtySession.Dispose` kills the shell; a crash just drops the pipe. Both pipe ends must be
  `PipeOptions.Asynchronous` (sync handles deadlock). Terminals use the bundled ConPTY (`x64\OpenConsole.exe`) —
  anything copying the exe folder must copy subfolders too.
- Every tab's environment (Claude Code IDE integration, hook pipe, `EDITOR`) is set in `ConfigureTerminalSession`.

### LSP, formatting, refactoring (§30, §32, §36)

Loomo owns LSP sessions, editors only hold `ILspDocument` handles. `LspServerTable` is the only extension→server
mapping (also `ILspServerAdmin` for settings and `:Lsp*` commands); `LspClientPool` runs one server per
(executable, workspace root); `LspDocumentTable` refcounts `didOpen`/`didClose`. `ILspWorkspace`/`ILspDocument`
events fire on background threads — marshal yourself. Workspace edits go through `ShellWindow.ApplyLspWorkspaceEdit`
(file operations before text edits); server-initiated `workspace/applyEdit` arrives on the LSP read thread, so the
UI must `await`, never block. Formatting is CLI-first (`FormatterRegistry`, no built-in mappings), LSP fallback.

### Claude Code integration (§38)

`ClaudeIdeServer`/`ClaudeIdeTools` speak Claude Code's private VS Code protocol — tool names and result strings
(`FILE_SAVED`, `DIFF_REJECTED`, …) are theirs, don't rename. Answers go through the Dispatcher. Busy/idle comes from
the raw window title (`◐/◑` / `✳`). The hook (`sk0ya.Loomo.McpBridge.exe hook`, §45.3.1; the old PowerShell script still works) is installed into the user's global `~/.claude/settings.json`,
so it must stay a no-op outside Loomo.

`LoomoMcpServer` (§43.6 D) is a separate standard MCP server (the IDE protocol only exposes two tools to the model):
tools that **place things in front of the human** (`show_file` / `show_url` / `show_changes` / `show_face`) plus the
read-only `face_guide` / `motion_guide`, and nothing else — no typing into the visible terminal, no closing tabs, no layout changes, no
focus stealing, no file-writing tools. Loomo never writes the
user's Claude config for it; the user registers once via the palette command. Claude Code reaches it through the
stdio bridge `src/Loomo.McpBridge` (copied to `%LOCALAPPDATA%/Loomo/mcp-bridge/`), which re-reads
`mcp-endpoint.json` on every call — **don't hand the endpoint to shells via environment variables**: resident
shells (§34) predate them and outlive port changes.

### Faces (§44.2)

A face is a small HTML tool Claude Code writes into `%APPDATA%/Loomo/faces/<id>/` (`face.json` + `index.html`); it shows
up as a tab of the Face pane without rebuilding Loomo. The lines are held in `FaceRoomApi` / `FaceTabHost` — keep them
when extending: each face has its own origin (`https://<id>.face.loomo`) and `window.loomo` exists only there; a face can
use only the APIs and run only the named commands its `face.json` declares (fixed command line; page values arrive via
`LOOMO_ARG_n` / `$LoomoInput`); the human approves that declaration once per fingerprint (stored outside the face folder);
commands run hidden, never in the visible terminal; nothing is written into the user's repositories. A WebView2 inside a
hidden pane never finishes initialising — load when visible, don't await it from an MCP call.
Room features that need a view are grown as built-in faces (`BuiltInFaces`: 動き §44.3, 言いつけ §44.4) on top of
`IFaceApiExtension` APIs (`motion`, `orders`) instead of WPF views. Standing orders run `claude -p` in a hidden worktree
and never touch the human's working tree until 採る (`cherry-pick --no-commit`, conflict forecast first); never push.

### .NET Framework projects (§28.12)

Legacy (non-SDK) csproj builds go through VS `MSBuild.exe`, decided only in `MsBuildToolchain.For(target)`.
.NET Framework debugging uses the bundled ICorDebug DAP adapter `src/Loomo.NetFxDebug` (built x64 and `Build32/`).
