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
- **Malformed tool-call recovery**: small models sometimes emit a tool-call-looking body that isn't valid JSON.
  `OnnxGenAiClient` surfaces it as `ToolCallParseFailed(rawText)` (not a terminal error). The loop keeps the raw
  output (visible in the UI + history) and feeds back a correction so the model retries, up to
  `MaxToolCallParseRetries` (then it stops with an `AgentError` that includes the raw output). The parser
  (`ToolCallTextParser`) also salvages the **first** well-formed object from an otherwise-broken array, so one
  bad entry in a batch doesn't drop a valid leading call.

**Agent-loop latency (read `docs/エージェントループ知見.md` before "make it faster" work).** On CPU-only
local inference the cold cost is **model load** (tens of seconds, paid once since `Phi4Engine` keeps the model
resident) and per-turn time is **prefill-dominated**. Each AI call's breakdown is recorded as the `ai.usage`
trace (tokens + `loadMs`/`promptEvalMs`/`evalMs`, **self-measured** in `Phi4Engine.RunSync` via `Stopwatch` —
load time only counts on the first turn, first-token time ≈ prefill, the rest ≈ decode — emitted as
`AiUsageReported`) and shown live in the AI bar progress ("📊 AI内訳", `AiBarViewModel.FormatUsage`) — use that
to split load vs prefill vs decode before optimizing. The real lever is **model size** (CPU speed ≈ inversely ∝
params) and keeping the context small (`ModelProfiles.Phi4Mini.NumCtx` is deliberately 8192).

### Tools — `Loomo.Core/Tools/`

The agent has **three tools**: `run_powershell` (the workhorse), plus structured `write_file` and `edit_file`.
- **`run_powershell`** (`Tools/Implementations/TerminalTools.cs`) runs a PowerShell command line and returns
  stdout + exit code. Reads, search, listing, build, test are all expressed as PowerShell
  (`Get-Content` / `Select-String` / `Get-ChildItem` / `dotnet …`); the system prompt
  (`AiSettings.DefaultSystemPrompt`) tells the model so.
- **`write_file{path,content}`** (`Tools/Implementations/WriteFileTool.cs`) creates/overwrites a file, and
  **`edit_file{path,old_string,new_string}`** (`EditFileTool.cs`) does a unique exact-match replace
  (0/multiple matches → clean recoverable error; never a botched in-place edit). Both take the content as its
  own JSON arg, which sidesteps the PowerShell-syntax × JSON **double-escaping** that small models fail at when
  writing files via the shell. Both resolve paths through `IWorkspaceService.ResolvePath` (workspace-root
  confinement) and open the result in the editor pane.

**Why so few, and why these:** on small CPU-only local LLMs the tool-definition prefill matters, but the old
"~21s for ~12 tools vs ~2.4s for one" figure was an **Ollama-era** measurement where the cross-turn prefix KV
cache didn't work. Under ONNX the cache **does** work (see `Phi4Engine` / `docs/エージェントループ知見.md` §2.1),
so the stable `system+tools` prefix is prefilled ~once (first turn/warmup), not every turn. The remaining cost
of adding tools is **selection reliability** (more options → more chance a small model picks wrong → extra
iterations), so the set is kept small, disjoint, and verb-named. `ArgHelper` (`Implementations/ArgHelper.cs`) is
the shared JSON-arg reader; each tool's `*Contract` (`PwshContract`, `WriteFileContract`, `EditFileContract`)
holds its name + canonical/alias arg keys, normalized in `NormalizeArguments`. All three set `RequiresApproval`,
so each invocation shows an approval card unless AutoApprove — `DescribeInvocation` renders the summary (the
file tools include a line-count/preview diff). `ToolRegistry` aggregates whatever `IAgentTool`s are registered in
`App.xaml.cs`; add a tool by implementing `IAgentTool` and registering it there.

Still missing (don't assume): no in-app browser automation tool. The `IBrowserService` adapter exists and backs
the UI pane but no tool wires it to the agent.

### Safety — `Loomo.Core/Safety/`

`AgentOrchestrator` calls `ISafetyPolicy.Evaluate` **before** every tool execution. `run_powershell` is matched
against `BlockedCommandPatterns` regexes; a block is returned to the AI as a tool error (never executed).
Approval cards are shown only when `tool.RequiresApproval && !AutoApprove` (all three tools require it).
`BlockedCommandPatterns` guards shell commands; **file writes via `write_file`/`edit_file` are confined to the
workspace root by `IWorkspaceService.ResolvePath`** (a `pwsh` `Set-Content` still bypasses that and is only
guarded by the block list). `SafetySettings` lives on `AiSettings.Safety` and is DI-shared as a singleton.

### AI clients — `Loomo.Ai/Clients/`

`IAiClient` abstracts the provider; `AiClientFactory.ResolveCurrent()` reads the singleton `AiSettings`
**every turn**, so settings changes apply immediately. The only implemented client is `OnnxGenAiClient`
(provider `Local`), which drives an **in-process ONNX Runtime GenAI** engine — there is **no HTTP / no
external server** (Ollama was fully removed). The package is `Microsoft.ML.OnnxRuntimeGenAI` (CPU), pinned in
`src/Loomo.Ai/Loomo.Ai.csproj`; the native runtime DLLs flow to the App output under
`runtimes/win-x64/native/`.

**Engine** — `Clients/Phi4Engine.cs` (DI singleton, `IDisposable`, implements `ILocalInferenceEngine`) owns the
ORT-GenAI `Model`/`Tokenizer` lifetime. It lazily loads from `ProviderConfig.ModelPath` (a folder containing
`genai_config.json` + `*.onnx` + tokenizer files, e.g. `microsoft/Phi-4-mini-instruct-onnx` CPU int4) and keeps
it resident — model load is the cold cost (tens of seconds on CPU), so it's paid once. Generation is batch-size-1,
serialized with a `SemaphoreSlim`. The decode loop (`AppendTokens` → `GenerateNextToken` → `TokenizerStream.Decode`,
v0.9.0 API — no `ComputeLogits`) runs on a background thread and writes `TextDelta` + a final `AiUsageReported`
(token counts + load/prefill/decode `Stopwatch` timings, self-measured) into a `Channel<AgentEvent>`.

**Prompt format** — `Clients/Phi4PromptFormatter.cs` builds the Phi-4 chat-template string directly:
`<|system|>…<|tool|>[toolsJSON]<|/tool|><|end|>` then `<|{role}|>{content}<|end|>` per message, ending with
`<|assistant|>`. Tool definitions go in the system turn; tool results render as role `tool`
(`<|tool|>content<|end|>`). Reuses `WorkspaceContext.Describe` (current folder) and `EnvironmentProbe` (rg guidance).

**Tool calling** — Phi-4-mini emits tool calls as a JSON array `[{"name":…,"arguments":{…}}]` in the **body**
(no structured channel). `OnnxGenAiClient` buffers the full text, then `ToolCallTextParser.Parse` (shared, also
handles function-call-style, bare arg objects, alias keys, code fences) turns it into `ToolUseRequested`; if it's
not a tool call it emits one `TextDelta` + `TurnCompleted`. This terminal logic mirrors the old Ollama client.

**Per-model profiles** — `Clients/ModelProfiles.cs`: `Resolve(model)` maps a model/folder name to a `ModelProfile`
holding the context window (`NumCtx`) and sampling (temp/top_p/repetition_penalty → ORT-GenAI `SetSearchOption`).
Only `Phi4Mini` (matches both `phi4-mini` and `phi-4-mini`, so the download folder name resolves) and a `Default`
remain. The effective context window (`ProviderConfig.NumCtx` override, else profile, via `EffectiveNumCtx`) is
shared by the engine's `max_length` and the history-trim budget (`SettingsContextWindowPolicy` caps to it) so the
model never silently truncates context the trimmer thought it kept.

The system prompt is **uniform across models** (no per-model injection). `AiSettings.DefaultSystemPrompt` is
**English instructions / Japanese output** (small local models follow English tool-calling rules more reliably).
The agent only ever has the single `pwsh` tool (see Tools), so the per-turn tool-definition payload is minimal.

**Model acquisition** — `ModelDownloadService` fetches `microsoft/Phi-4-mini-instruct-onnx` (CPU int4) from
Hugging Face into `%APPDATA%/Loomo/models/<name>/` (streamed, resumable, cancellable); the settings panel has a
download button + folder picker. `ModelCatalogService` enumerates local ONNX model folders (those with
`genai_config.json`) under that root for the model dropdown — it no longer does any HTTP.

**Known limitations** (don't assume these exist): thinking/reasoning is not surfaced (phi4-mini-instruct is a
non-thinking model). Context management is trim-only (no summarization/compaction). The `IBrowserService` /
Copilot remnants are unused by the agent.

### Persistence

`%APPDATA%/Loomo/` holds `settings.json` (provider/model/`modelPath`/MaxTokens + Safety; legacy
`baseUrl`/`numGpu`/`thinking`/SystemPrompt fields are read for back-compat but ignored), `models/` (downloaded
ONNX models), and `sessions/*.json`. **API keys are DPAPI-encrypted (CurrentUser)** — classes touching this are
`[SupportedOSPlatform("windows")]` (kept for forward-compat; the local engine needs no key). Sessions auto-save
in the turn-completion `finally`; restore via the Sessions panel (`ConversationStore` raises a `Changed` event).

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
