# AGENTS.md

## What this is

Loomo（ルーモ, Loom × Room）is a Windows-only C# / WPF desktop app: a **room the human works in**. Panes
(Terminal / Editor / Browser / Git / Diff / Debug / … / **Ai** — the authoritative list is the `PaneKind` enum in
`WorkspaceStateStore.cs`) are tiled, staged, detached into windows and restored down to caret and scroll; the
軌跡 rail (§27) records the **human's** navigation. The AI agent is **one pane, not the product** — judge changes by
the room chapters (§23 / §24 / §26 / §27), not by how much AI is in them. A large App layer is by design.

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
- `Directory.Build.props` pins `RuntimeIdentifier=win-x64` for every project — without it ONNX/LLamaSharp/SQLite
  natives for every platform land in the output. Don't remove it.
- If a running Loomo locks `bin/Debug` and breaks the build, stop that `sk0ya.Loomo.App.exe` (only Loomo itself)
  and rebuild.

## Architecture

One-way dependency **App → Services/Ai → Core**. `Loomo.Core` has no WPF: agent loop, tool contracts, service
interfaces. Adapters live in `Loomo.Services` (sk0ya controls, filesystem, LSP, Git) and `Loomo.Ai` (inference).
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

`%APPDATA%/Loomo/` holds `settings.json`, `models/`, `sessions/`, `history/`, `lsp-servers.json`,
`formatters.json`. API keys are DPAPI-encrypted.

### sk0ya control libraries

Terminal/Editor are the NuGet packages `sk0ya.Terminal.Controls` / `sk0ya.Editor.Controls`, but their namespaces
drop the prefix (`Terminal.Tabs.TerminalTabView`, `Editor.Controls.VimEditorControl`). Versions are pinned only in
the root `Directory.Build.props`; `-p:UseLocalEditor=true` / `-p:UseLocalTerminal=true` build against
`C:\Projects\Editor` / `C:\Projects\Terminal`. Reflecting over these assemblies from the shell tends to
hallucinate — dump the API to a file and Grep it.

- **The visible terminal is human-only.** The agent's `TerminalService.RunCommandAsync` runs in a separate
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
the raw window title (`◐/◑` / `✳`). The hook script is installed into the user's global `~/.claude/settings.json`,
so it must stay a no-op outside Loomo.

### AI agent (§7, §25 — read `docs/エージェントループ知見.md` before performance work)

`Loomo.Core/Agent/AgentOrchestrator.RunTurnAsync`: input → AI stream → tool call → safety → approval → execute →
feed back (max 25 iterations). Invariants: trim a copy via `_context.Fit`, keep the original `Conversation`; never
append an empty assistant message; a tool result immediately follows its request and trimming keeps the first
message a `user`; malformed tool calls (`ToolCallParseFailed`) are fed back for a bounded retry.

- **Four tools** (`Loomo.Core/Tools/Implementations/`): `run_powershell`, `write_file`, `edit_file`, `web_search`.
  Kept small on purpose (selection reliability of small models). Add one by implementing `IAgentTool` and
  registering it in `App.xaml.cs`. File tools resolve paths via `IWorkspaceService.ResolvePath`.
- **Safety:** `ISafetyPolicy.Evaluate` runs before every tool; `run_powershell` is checked against
  `BlockedCommandPatterns`.
- **Inference** is in-process ONNX Runtime GenAI (`OnnxGenAiEngine`, CPU, model kept resident) with llama.cpp as a
  second backend; no HTTP server. Prompt format per model via `ChatPrompt` (Phi-4 / Qwen3); profiles in
  `ModelProfiles`; downloadable models in `ModelDownloadService.Catalog` (don't hardcode model names).
  System prompts (`SystemPrompts.cs`) are English instructions / Japanese output; Qwen3 few-shot examples must not
  name harness seed files (asserted by tests).
- **Inline completion** (§31.13, `Loomo.Ai/Completion/`): a separate `sk0ya.Loomo.Completion.Host` process at
  BelowNormal priority so typing is never blocked; quality comes from `FimCandidateFilter`. Completion models live
  under `models/completion/` so the chat model dropdown doesn't list them.

### .NET Framework projects (§28.12)

Legacy (non-SDK) csproj builds go through VS `MSBuild.exe`, decided only in `MsBuildToolchain.For(target)`.
.NET Framework debugging uses the bundled ICorDebug DAP adapter `src/Loomo.NetFxDebug` (built x64 and `Build32/`).
