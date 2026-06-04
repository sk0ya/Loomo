# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Loomo（ルーモ）is a C# / WPF (.NET 9) desktop AI-agent app for driving a local dev workspace.
An agent answers natural-language prompts by calling **tools** (function calling) that operate the
**Terminal**, **Editor**, and **FolderTree** UI panes. Name = Loom (織機) × Room. Solution file:
`sk0ya.Loomo.sln`. All root namespaces / assembly names are `sk0ya.Loomo.*`; project folders drop the
prefix (`Loomo.Core`, `Loomo.Ai`, `Loomo.Services`, `Loomo.App`, `Loomo.Tests`).

The authoritative design doc is `docs/設計書.md` (Japanese). Comments, commit messages, and UI strings
are in Japanese — match that when editing.

## Commands

```powershell
dotnet build sk0ya.Loomo.sln
dotnet run --project src/Loomo.App/Loomo.App.csproj      # launch the WPF app
dotnet test                                              # all tests (xUnit)
dotnet test --filter "FullyQualifiedName~DiffUtil"       # single test class / method
```

Requires the .NET 9 SDK on Windows. App and test projects target `net9.0-windows` (WPF);
`Loomo.Core` and `Loomo.Ai` target plain `net9.0`.

## Architecture

Strict one-way dependency: **App → Services/Ai → Core**. `Loomo.Core` is UI-independent (no WPF) and
holds the agent loop, tool contracts, and service *interfaces*; concrete adapters live in `Loomo.Services`
(sk0ya controls + filesystem) and `Loomo.Ai` (AI clients). DI is wired entirely in
`src/Loomo.App/App.xaml.cs` via `Microsoft.Extensions.Hosting`.

### The agent loop — `Loomo.Core/Agent/AgentOrchestrator.cs`

This is the heart. `RunTurnAsync` is an `IAsyncEnumerable<AgentEvent>`:
user input → AI stream → `tool_use` → safety check → approval → tool execute → feed result back to AI →
repeat (max 25 iterations) → final text. Key invariants when editing this file:

- **Context trimming**: before every send it calls `_context.Fit(conversation)` to trim history for the
  provider's window; the *original* `Conversation` is kept intact.
- Empty assistant messages (no text and no tool calls) are **never** appended to history — they cause API errors.
- The tool-result message must immediately follow the assistant message that requested it; trimming
  (`ConversationTrimmer`) keeps the first message a `user` to avoid orphaned `tool_result`s.

### Tools — `Loomo.Core/Tools/`

`IAgentTool` implementations in `Tools/Implementations/` (`WorkspaceTools`, `EditorTools`, `TerminalTools`,
`BrowserTools`). Designed for small local LLMs, so the set favors **bulk-retrieval + text-anchored editing**
over many fine-grained steps: read/search = `get_project_tree` (whole tree in one call), `find_files`,
`search_files`, `read_file`, `get_selection`; editing an existing file = `replace_text_once` (single unique
match) or `apply_patch` (multiple SEARCH/REPLACE blocks) — there is **no line-number-based edit**; new files
= `create_file` (errors if the file exists); plus `open_in_editor`, `get_selection_text`, `replace_selection`,
`run_command`, and the `browser_*` tools (`browser_list_clickables` returns ready-to-use CSS selectors so the
model never guesses them). Each is registered individually in DI and aggregated by `ToolRegistry`.
`DescribeInvocation` produces the human summary shown on the approval card (the editing tools return a unified
diff, expanded to a colored card by `AiBarViewModel.IsDiffTool`). Add a new tool by implementing `IAgentTool`
and registering it in `App.xaml.cs`.

### Safety — `Loomo.Core/Safety/`

`AgentOrchestrator` calls `ISafetyPolicy.Evaluate` **before** every tool execution. `run_command` is matched
against `BlockedCommandPatterns` regexes; a block is returned to the AI as a tool error (never executed).
Approval cards are shown only when `tool.RequiresApproval && !AutoApprove`. Path-traversal is prevented by
`IWorkspaceService.ResolvePath` (throws `UnauthorizedAccessException` outside the workspace root) — all
file-touching tools route through it. `SafetySettings` lives on `AiSettings.Safety` and is DI-shared as a singleton.

### AI clients — `Loomo.Ai/Clients/`

`IAiClient` abstracts the provider; `AiClientFactory.ResolveCurrent()` reads the singleton `AiSettings`
**every turn**, so settings changes apply immediately. The only implemented client is `OllamaClient`
(provider `Local`), talking to a local Ollama via its **native API** (`/api/chat`, `/api/tags`) — the
wire logic lives in `OllamaProtocol` (internal static). All HTTP goes through `Http/HttpRetry`
(exponential backoff on 429/5xx/408 + `HttpRequestException`, honors `Retry-After`).

**Streaming**: `OllamaProtocol.SendChatAsync` reads Ollama's NDJSON stream (`stream:true`) line by line.
Thinking arrives in its own `message.thinking` field (surfaced as `ThinkingDelta`), body in
`message.content` (`TextDelta`), and `message.tool_calls` carry `arguments` as a **JSON object** (not a
string). Thinking is toggled with the native boolean `think` from the user setting `ProviderConfig.Thinking`
(a simple on/off — Ollama's `think` is boolean, so there's no low/medium/high). If the model rejects tools,
the client retries once with `includeTools:false`.

**Per-model profiles** — `Clients/ModelProfiles.cs`: `Resolve(model)` maps a model name (family prefix) to a
`ModelProfile` that gates `tools`/`think` by capability and supplies the recommended `num_ctx` + sampling
(`options`). Grounded in each family's official recommendations and `ollama show` capabilities; covers the
installed qwen3 / qwen2.5 / qwen2.5-coder / gemma3 families, with a safe default for unknown models. Effects:
`think` is sent as `wantThink && SupportsThinking`, so `think:true` only reaches thinking-capable models (others
would error) while `think:false` still goes to every model (harmless, and silences default-on thinking); `tools`
is omitted up front for non-tool models like gemma3 (the error-fallback remains a safety net for misclassified
unknowns); `num_ctx` is widened from Ollama's 4096 default; qwen3 uses different temps for thinking vs
non-thinking. The effective `num_ctx` (`ProviderConfig.NumCtx` override, else profile) is shared by both the
Ollama request and the history-trim budget (`SettingsContextWindowPolicy` caps to it) so the model never
silently truncates context the trimmer thought it kept.

`OllamaLauncher.ResolveHost` normalizes `BaseUrl` to the native host and strips a trailing `/v1` left
over from the old OpenAI-compatible config, so existing `settings.json` keeps working.

**Known limitations** (don't assume these exist): no token/cost usage display. Context management is
trim-only (no summarization/compaction). Copilot token exchange + chat use **unofficial** endpoints and
are E2E-unverified.

### Persistence

`%APPDATA%/Loomo/` holds `settings.json` (provider/model/key/BaseUrl/MaxTokens/SystemPrompt + Safety) and
`sessions/*.json`. **API keys are DPAPI-encrypted (CurrentUser)** — classes touching this are
`[SupportedOSPlatform("windows")]`. Sessions auto-save in the turn-completion `finally`; restore via the
Sessions panel (`ConversationStore` raises a `Changed` event).

### UI — `Loomo.App`

MVVM (CommunityToolkit.Mvvm). Layout: ActivityBar | sidebar | TerminalTabView | VimEditorControl, plus a
full-width expandable AI bar at the bottom. `ShellViewModel.ActivePanel` switches the sidebar between
Explorer / Sessions / Settings (clicking the active icon closes it). Panes are loosely coupled via
`WeakReferenceMessenger`. Concrete services are registered twice in DI (as concrete + interface) so the
same instance backs both — Views resolve the concrete control adapter, tools resolve the interface.

## Working with the sk0ya control libraries (important)

Terminal/Editor come from NuGet packages `sk0ya.Terminal.Controls` (1.0.3) and `sk0ya.Editor.Controls`
(1.0.0), but their **namespaces drop the `sk0ya.` prefix**: `Terminal.Tabs.TerminalTabView`,
`Editor.Controls.VimEditorControl`. Terminal command execution is unified onto the *visible* terminal:
`TerminalService` calls `TerminalTabView.RunCommandAsync(command, ct)` on the UI thread
(`Dispatcher.InvokeAsync(...).Task.Unwrap()`), returning `TerminalCommandResult`. cwd syncs from
`WorkingDirectory` when `IsShellIntegrationActive`, else falls back to detecting `cd`. The package version
is pinned in **one place only**: `src/Loomo.Services/Loomo.Services.csproj` (App references transitively).

Reflecting over these assemblies via the shell tends to hallucinate — dump API to a file and Grep it, or use
`MetadataLoadContext`. The Terminal library source is at `C:\Projects\Terminal` (ConPTY, OSC133 shell
integration; see its `AGENT_API_SPEC.md`).
