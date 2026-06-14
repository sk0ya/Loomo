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
external server** (Ollama was fully removed). The package is `Microsoft.ML.OnnxRuntimeGenAI` (CPU) `0.14.1`, pinned in
`src/Loomo.Ai/Loomo.Ai.csproj`; the native runtime DLLs flow to the App output under
`runtimes/win-x64/native/`. (Bumped from `0.9.0` to load newer ONNX builds — e.g. `onnx-community/Qwen3-8B-ONNX`
int4, whose `GatherBlockQuantized` carries a `bits` attribute that `0.9.0`'s native ORT rejects; the decode-loop
API is unchanged across the bump.)

**Engine** — `Clients/Phi4Engine.cs` (DI singleton, `IDisposable`, implements `ILocalInferenceEngine`) owns the
ORT-GenAI `Model`/`Tokenizer` lifetime. It lazily loads from `ProviderConfig.ModelPath` (a folder containing
`genai_config.json` + `*.onnx` + tokenizer files, e.g. `microsoft/Phi-4-mini-instruct-onnx` CPU int4) and keeps
it resident — model load is the cold cost (tens of seconds on CPU), so it's paid once. Generation is batch-size-1,
serialized with a `SemaphoreSlim`. The decode loop (`AppendTokens` → `GenerateNextToken` → `TokenizerStream.Decode`,
no `ComputeLogits` — same API on 0.9.0 and 0.14.1) runs on a background thread and writes `TextDelta` + a final `AiUsageReported`
(token counts + load/prefill/decode `Stopwatch` timings, self-measured) into a `Channel<AgentEvent>`.

**Prompt format** — chosen per model's `ChatFormat` via the `Clients/ChatPrompt.cs` dispatcher (both the real turn
in `OnnxGenAiClient` and warmup in `LocalLlmWarmupService` go through it, so the warmed KV prefix stays a byte-exact
prefix of the first turn). `Clients/Phi4PromptFormatter.cs` builds the Phi-4 template
(`<|system|>…<|tool|>[toolsJSON]<|/tool|><|end|>` then `<|{role}|>{content}<|end|>`, ending `<|assistant|>`).
`Clients/Qwen3PromptFormatter.cs` builds the Qwen3 **ChatML** template (`<|im_start|>role\ncontent<|im_end|>`,
tools as a Hermes `<tools>…</tools>` block in the system turn, tool results as `<tool_response>` user turns, ending
`<|im_start|>assistant\n<think>\n\n</think>\n\n` to force **no-think**). Shared bits (system text, rg guidance, tool
params) live in `Clients/PromptShared.cs`. Both reuse `WorkspaceContext.Describe` + `EnvironmentProbe`.

**Tool calling** — Phi-4-mini emits a JSON array `[{"name":…,"arguments":{…}}]` in the **body**; Qwen3 emits one or
more Hermes `<tool_call>{…}</tool_call>` blocks (and may wrap thinking in `<think>…</think>`). `OnnxGenAiClient`
buffers the full text, then `ToolCallTextParser.Parse` (shared) turns it into `ToolUseRequested` — it strips
`<think>` blocks, extracts all `<tool_call>` tags, and also handles function-call-style, bare arg objects, alias
keys, and code fences. If it's not a tool call it emits one `TextDelta` + `TurnCompleted` (think-stripped). This
terminal logic mirrors the old Ollama client.

**Per-model profiles** — `Clients/ModelProfiles.cs`: `Resolve(model)` maps a model/folder name to a `ModelProfile`
holding the context window (`NumCtx`) and sampling (temp/top_p/repetition_penalty → ORT-GenAI `SetSearchOption`).
`Phi4Mini` (matches `phi4-mini`/`phi-4-mini`), `Qwen3` (matches `qwen3`/`qwen-3`, so the `qwen3-*-cpu-int4` download
folders resolve; carries `Format = ChatFormat.Qwen3` + Qwen's recommended non-thinking sampling), and a `Default`.
Each profile also carries a `ChatFormat` selecting the prompt formatter (above). The effective context window
(`ProviderConfig.NumCtx` override, else profile, via `EffectiveNumCtx`) is
shared by the engine's `max_length` and the history-trim budget (`SettingsContextWindowPolicy` caps to it) so the
model never silently truncates context the trimmer thought it kept.

System prompts are **English instructions / Japanese output** (small local models follow English tool-calling rules
more reliably). There are now two, picked by `ChatFormat`: `AiSettings.DefaultSystemPrompt` (Phi-4, JSON-array
tool-call examples) and `AiSettings.Qwen3SystemPrompt` (Hermes `<tool_call>` examples + no-think; restructured
2026-06 into general principles — facts only from tool results, no success claims after errors, complete & verify
all parts, exact old_string copy). **The Qwen3 prompt's few-shot examples must not name harness seed files**
(README.md etc.) — that contaminated the capability eval once; `Qwen3PromptFormatterTests` now asserts it.
`BuildSystemPrompt(profile, format)` chooses.

**Model acquisition** — `ModelDownloadService.Catalog` lists the downloadable ONNX (CPU int4, ORT-GenAI-compatible)
models: `microsoft/Phi-4-mini-instruct-onnx`, plus `lokinfey/Qwen3-1.7B-ONNX-INT4-CPU` and
`lokinfey/Qwen3-4B-ONNX-INT4-CPU`. **Only repos whose target folder has `genai_config.json` work** — an
`onnx-community/Qwen3-*-ONNX` repo's *root*/`onnx/` is transformers.js-targeted (no `genai_config.json`), but its
`onnxruntime/cpu_and_mobile/<variant>/` subfolder *does* ship a genai-compatible build (e.g.
`onnx-community/Qwen3-8B-ONNX` → `onnxruntime/cpu_and_mobile/cpu-int4-kld-block-128/` has `genai_config.json`; loads
under ORT `0.14.1`, but 8B int4 is ~262s/turn on this CPU — non-viable, see `docs` / memory).
`DownloadAsync(DownloadableModel, …)` streams into `%APPDATA%/Loomo/models/<FolderName>/` (resumable, cancellable);
the settings panel has a download-model dropdown + download button + folder picker. `ModelCatalogService` enumerates
local ONNX model folders (those with `genai_config.json`) under that root for the model dropdown — no HTTP.

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

Terminal/Editor come from NuGet packages `sk0ya.Terminal.Controls` (1.0.14) and `sk0ya.Editor.Controls`
(1.0.7), but their **namespaces drop the `sk0ya.` prefix**: `Terminal.Tabs.TerminalTabView`,
`Editor.Controls.VimEditorControl`. **The agent's command execution does NOT flow to the visible terminal** —
`TerminalService.RunCommandAsync` always runs the command in an independent non-interactive PowerShell
`Process` (`RunViaProcessAsync`) so AI output never mixes into the human's terminal; the visible terminal is
human-only. cwd is tracked via `cd` detection (`TrackChdir`). `SetWorkingDirectory` still drives
`TerminalTabView.RunCommandAsync` on the UI thread to make the *visible* terminal follow the opened folder.
The package version
is pinned in **one place only**: `src/Loomo.Services/Loomo.Services.csproj` (App references transitively).

Terminal 1.0.7 auto-injects OSC 133 shell integration into interactive pwsh; 1.0.8 exposes the
`TerminalTabView.ShellCommandActivity` public event (command phase + exit code, for human-typed commands
too) — Loomo's stage-wing activity badges (`ShellWindow.PaneActivity.cs`, 設計書 §24.1) are built on it.

Reflecting over these assemblies via the shell tends to hallucinate — dump API to a file and Grep it, or use
`MetadataLoadContext`. The Terminal library source is at `C:\Projects\Terminal` (ConPTY, OSC133 shell
integration; see its `AGENT_API_SPEC.md`).
