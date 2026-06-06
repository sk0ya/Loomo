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

**Agent-loop latency (read `docs/エージェントループ知見.md` before "make it faster" work).** On CPU-only
local inference the turn time is **prefill-dominated**, not decode/load. Each AI call's breakdown is recorded as
the `ai.usage` trace (tokens + `loadMs`/`promptEvalMs`/`evalMs`, parsed in `OllamaProtocol.ParseUsage`, emitted
as `AiUsageReported`) and shown live in the AI bar progress ("📊 AI内訳", `AiBarViewModel.FormatUsage`) — use
that to split load vs prefill vs decode before optimizing. Hard-won facts: prefill time does **not** scale
cleanly with token count, cross-turn prefix-cache reuse is unreliable here (a later turn can be *slower* than an
earlier one as the conversation grows — full re-prefill each turn), and prompt compression only helps the cold
first turn. The real lever is **model size** (CPU speed ≈ inversely ∝ params) and GPU VRAM big enough to offload
(check `ollama ps` for the CPU/GPU split). Don't over-invest in prefix-cache tricks for the CPU path.

### Tools — `Loomo.Core/Tools/`

The agent is given **exactly one tool: `pwsh`** (`Tools/Implementations/TerminalTools.cs`), which runs a
PowerShell command line and returns stdout + exit code. Reads, search, listing, file creation and editing are
all expressed as PowerShell (`Get-Content` / `Select-String` / `Get-ChildItem` / `Set-Content`); the system
prompt (`AiSettings.DefaultSystemPrompt`) tells the model so. **Why one tool:** on small CPU-only local LLMs the
per-turn prefill of the tool-definition block dominates latency (~21s for ~12 tools vs ~2.4s for one), so the
set was collapsed to `pwsh`. `ArgHelper` (`Implementations/ArgHelper.cs`) is the shared JSON-arg reader.
`RunCommandTool.RequiresApproval` is true, so every command (reads included) shows an approval card unless
AutoApprove — `DescribeInvocation` renders the command string. `ToolRegistry` aggregates whatever `IAgentTool`s
are registered in `App.xaml.cs`; add a tool by implementing `IAgentTool` and registering it there.

Trade-off of the single-tool design: there is no structured editing tool and therefore **no diff approval
card** (the model edits via shell), no in-app browser automation, and the per-tool `ResolvePath`
workspace-root confinement no longer guards file writes — only `BlockedCommandPatterns` (see Safety) does.
The `IWorkspaceService` / `IEditorService` / `IBrowserService` adapters still exist and back the UI panes.

### Safety — `Loomo.Core/Safety/`

`AgentOrchestrator` calls `ISafetyPolicy.Evaluate` **before** every tool execution. `pwsh` is matched
against `BlockedCommandPatterns` regexes; a block is returned to the AI as a tool error (never executed).
Approval cards are shown only when `tool.RequiresApproval && !AutoApprove` (`pwsh` always requires it).
`BlockedCommandPatterns` is the **only** workspace-scope guard now that file ops go through the shell rather
than `IWorkspaceService.ResolvePath` (which still exists for the UI). `SafetySettings` lives on `AiSettings.Safety`
and is DI-shared as a singleton.

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
installed qwen3 / qwen2.5 / qwen2.5-coder / gemma3 / phi4-mini families, with a safe default for unknown models. Effects:
`think` is sent as `wantThink && SupportsThinking`, so `think:true` only reaches thinking-capable models (others
would error) while `think:false` still goes to every model (harmless, and silences default-on thinking); `tools`
is omitted up front for non-tool models like gemma3 (the error-fallback remains a safety net for misclassified
unknowns); `num_ctx` is widened from Ollama's 4096 default; qwen3 uses different temps for thinking vs
non-thinking. The effective `num_ctx` (`ProviderConfig.NumCtx` override, else profile) is shared by both the
Ollama request and the history-trim budget (`SettingsContextWindowPolicy` caps to it) so the model never
silently truncates context the trimmer thought it kept.

The system prompt is **uniform across models** — there is no per-model prompt injection (the old
`ModelProfile.StyleGuidance` phi4-mini nudge was removed; model-specific prompt text is intentionally avoided).
`AiSettings.DefaultSystemPrompt` is **English instructions / Japanese output** (small local models follow
English tool-calling rules more reliably). Note the agent only ever has the single `pwsh` tool (see Tools), so
the per-turn tool-definition payload is already minimal — this is what cut first-turn prefill from ~21s
(≈12 tools) to ~2.4s on CPU.

**Prompt-prefix caching (perf-critical)** — Ollama reuses the KV cache for the longest byte-identical prompt
*prefix* (system + tools), so re-prefilling the large tool-definition block (~16s on CPU-only machines) is
paid once instead of every turn. The invariant protecting this in `OllamaClient`/`OllamaProtocol.BuildRequest`:
the system prompt must stay byte-stable across a session — anything that varies *within* a session must stay
out of the `system` message and the `tools` array, or the cache busts and re-pays the prefill. The system
prompt (`OllamaPromptBuilder.Build`) is the same `AiSettings.DefaultSystemPrompt` for all models, plus two
*session-stable* additions: search guidance (rg-vs-Select-String, fixed by environment) and the **current
folder** (`WorkspaceContext.Describe` — the workspace root, which only changes when the user opens a different
folder, so it's stable enough for the prefix; switching folders does bust the cache once, which is acceptable).
`BuildRequest` also sends `keep_alive` so the model and its prefix cache stay resident between turns.

`OllamaLauncher.ResolveHost` normalizes `BaseUrl` to the native host and strips a trailing `/v1` left
over from the old OpenAI-compatible config, so existing `settings.json` keeps working.

**Known limitations** (don't assume these exist): no token/cost usage display. Context management is
trim-only (no summarization/compaction). Copilot token exchange + chat use **unofficial** endpoints and
are E2E-unverified.

### Persistence

`%APPDATA%/Loomo/` holds `settings.json` (provider/model/key/BaseUrl/MaxTokens + Safety; legacy SystemPrompt is ignored) and
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

Terminal/Editor come from NuGet packages `sk0ya.Terminal.Controls` (1.0.5) and `sk0ya.Editor.Controls`
(1.0.0), but their **namespaces drop the `sk0ya.` prefix**: `Terminal.Tabs.TerminalTabView`,
`Editor.Controls.VimEditorControl`. Terminal command execution is unified onto the *visible* terminal:
`TerminalService` calls `TerminalTabView.RunCommandAsync(command, ct)` on the UI thread
(`Dispatcher.InvokeAsync(...).Task.Unwrap()`), returning `TerminalCommandResult`. cwd syncs from
`WorkingDirectory` when `IsShellIntegrationActive`, else falls back to detecting `cd`. The package version
is pinned in **one place only**: `src/Loomo.Services/Loomo.Services.csproj` (App references transitively).

Reflecting over these assemblies via the shell tends to hallucinate — dump API to a file and Grep it, or use
`MetadataLoadContext`. The Terminal library source is at `C:\Projects\Terminal` (ConPTY, OSC133 shell
integration; see its `AGENT_API_SPEC.md`).
