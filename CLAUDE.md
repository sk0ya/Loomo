# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Loomo（ルーモ）is a C# / WPF (.NET 9) desktop AI-agent app for driving a local dev workspace.
An agent answers natural-language prompts by calling **tools** (function calling) that operate the
**Terminal**, **Editor**, and **FolderTree** UI panes. Name = Loom (織機) × Room. Solution file:
`sk0ya.Loomo.sln`. All root namespaces / assembly names are `sk0ya.Loomo.*`; project folders drop the
prefix (`Loomo.Core`, `Loomo.Ai`, `Loomo.Services`, `Loomo.App`, `Loomo.Tests`).

The authoritative design doc is `docs/設計/` (Japanese; start at `docs/設計/README.md` for the index + §→file
map). It was split out of the old single `docs/設計書.md` (now a thin redirect), but section numbers (§N) are
preserved across the split, so existing "§21"/"§25"-style cross-references still resolve via the README map.
Comments, commit messages, and UI strings are in Japanese — match that when editing.

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
local inference the cold cost is **model load** (tens of seconds, paid once since `OnnxGenAiEngine` keeps the model
resident) and per-turn time is **prefill-dominated**. Each AI call's breakdown is recorded as the `ai.usage`
trace (tokens + `loadMs`/`promptEvalMs`/`evalMs`, **self-measured** in `OnnxGenAiEngine.RunSync` via `Stopwatch` —
load time only counts on the first turn, first-token time ≈ prefill, the rest ≈ decode — emitted as
`AiUsageReported`) and shown live in the AI bar progress ("📊 AI内訳", `AiBarViewModel.FormatUsage`) — use that
to split load vs prefill vs decode before optimizing. The real lever is **model size** (CPU speed ≈ inversely ∝
params) and keeping the context small (`ModelProfiles.Phi4Mini.NumCtx` is deliberately 8192).

### Tools — `Loomo.Core/Tools/`

The agent has **four tools**: `run_powershell` (the workhorse), structured `write_file` and `edit_file`, plus
`web_search`.
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
- **`web_search{query}`** (`Tools/Implementations/WebSearchTool.cs`) looks up external info: it drives
  `IBrowserService` (the **visible** browser pane's active tab — same "AI operates the visible panes" philosophy
  as Terminal/Editor, no separate window) to navigate Bing, then **structured-extracts** just the answer box
  (summary / live scores / standings / weather) plus the top organic results (title + **real URL** + snippet)
  via `EvaluateScriptAsync(BingExtractScript)` — related-search / image / video / shopping carousels and the
  footer (≈30% of the visible text, measured) are never collected. The earlier reasons for *not* doing this were
  fixed in the script: the answer box is grabbed explicitly (`li.b_ans`), and the `bing.com/ck/a?…&u=a1<base64url>`
  redirect href is decoded back to the real URL (else falls back to the displayed `cite`). Organic results live
  **deeper than** `#b_results`'s direct children, so the selector is the descendant `#b_results li.b_algo` (the
  strict `>` child selector missed them — that was the real fragility). `TrimAnswerTail` drops the answer box's
  trailing widgets (cut at the first 「さらに表示」「すべて表示」「すべて閲覧」「YouTube視聴回数」 marker).
  Size is bounded by **structure** (`MaxResults`=6 × `MaxSnippetChars`, `MaxAnswerChars`), with `MaxResultChars`
  as a final safety cap. If extraction yields nothing (DOM change / no answer-or-results, after one 700ms retry
  for late-injected results), it **falls back** to `GetVisibleTextAsync` + `CleanPageText`, which strips only
  **safe** chrome (cookie-consent banner, skip links, search-tab nav row, echoed query, breadcrumb `›` display-URL
  lines, "…を表示" show-more buttons, adjacent dupes, blank lines) by trimmed whole-line match — never substring,
  so the older raw-text behavior is preserved as the floor. An earlier DOM link-density (jusText-style) extractor
  was tried and dropped. If no browser tab is realized, `IsAvailable` is false and it returns a recoverable error.

**Why so few, and why these:** on small CPU-only local LLMs the tool-definition prefill matters, but the old
"~21s for ~12 tools vs ~2.4s for one" figure was an **Ollama-era** measurement where the cross-turn prefix KV
cache didn't work. Under ONNX the cache **does** work (see `OnnxGenAiEngine` / `docs/エージェントループ知見.md` §2.1),
so the stable `system+tools` prefix is prefilled ~once (first turn/warmup), not every turn. The remaining cost
of adding tools is **selection reliability** (more options → more chance a small model picks wrong → extra
iterations), so the set is kept small, disjoint, and verb-named. `ArgHelper` (`Implementations/ArgHelper.cs`) is
the shared JSON-arg reader; each tool's `*Contract` (`PwshContract`, `WriteFileContract`, `EditFileContract`)
holds its name + canonical/alias arg keys, normalized in `NormalizeArguments`. All four set `RequiresApproval`,
so each invocation shows an approval card unless AutoApprove — `DescribeInvocation` renders the summary (the
file tools include a line-count/preview diff). `ToolRegistry` aggregates whatever `IAgentTool`s are registered in
`App.xaml.cs`; add a tool by implementing `IAgentTool` and registering it there.

Still missing (don't assume): no general browser *automation* tool (click/type/navigate as agent steps). The
`IBrowserService` adapter exposes click/type/navigate and backs the UI pane, but only `web_search` wires it to
the agent so far (navigate + read text); the click/type surface is not yet exposed as a tool.

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
`src/Loomo.Ai/Loomo.Ai.csproj` (see there for the exact version); the native runtime DLLs flow to the App output
under `runtimes/win-x64/native/`. (It was bumped at one point to load newer ONNX builds — some int4 models'
`GatherBlockQuantized` carries a `bits` attribute that the older native ORT rejected; the decode-loop API was
unchanged across the bump.)

**Engine** — `Clients/OnnxGenAiEngine.cs` (DI singleton, `IDisposable`, implements `ILocalInferenceEngine`) owns the
ORT-GenAI `Model`/`Tokenizer` lifetime. It lazily loads from `ProviderConfig.ModelPath` (a folder containing
`genai_config.json` + `*.onnx` + tokenizer files, a CPU int4 ONNX model) and keeps
it resident — model load is the cold cost (tens of seconds on CPU), so it's paid once. Generation is batch-size-1,
serialized with a `SemaphoreSlim`. The decode loop (`AppendTokens` → `GenerateNextToken` → `TokenizerStream.Decode`,
no `ComputeLogits` — API stable across the ORT-GenAI bumps) runs on a background thread and writes `TextDelta` + a final `AiUsageReported`
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

**Model acquisition** — `ModelDownloadService.Catalog` is the source of truth for the downloadable ONNX (CPU int4,
ORT-GenAI-compatible) models (read it for the current list — don't hardcode model names here, they drift).
**Only repos whose target folder has `genai_config.json` work** — a transformers.js-targeted repo's *root*/`onnx/`
has no `genai_config.json`, but such repos often ship a genai-compatible build under an
`onnxruntime/cpu_and_mobile/<variant>/` subfolder instead. Bigger models load but can be non-viable on CPU (an 8B
int4 measured ~262s/turn here — see `docs` / memory). `DownloadAsync(DownloadableModel, …)` streams into
`%APPDATA%/Loomo/models/<FolderName>/` (resumable, cancellable); the settings panel has a download-model dropdown +
download button + folder picker. `ModelCatalogService` enumerates local ONNX model folders (those with
`genai_config.json`) under that root for the model dropdown — no HTTP.

**Known limitations** (don't assume these exist): thinking/reasoning is not surfaced (the local models in use are
run non-thinking). Context management is trim-only (no summarization/compaction). The `IBrowserService` /
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

Terminal/Editor come from NuGet packages `sk0ya.Terminal.Controls` and `sk0ya.Editor.Controls`, but their
**namespaces drop the `sk0ya.` prefix**: `Terminal.Tabs.TerminalTabView`,
`Editor.Controls.VimEditorControl`. **The agent's command execution does NOT flow to the visible terminal** —
`TerminalService.RunCommandAsync` always runs the command in an independent non-interactive PowerShell
`Process` (`RunViaProcessAsync`) so AI output never mixes into the human's terminal; the visible terminal is
human-only. cwd is tracked via `cd` detection (`TrackChdir`). `SetWorkingDirectory` still drives
`TerminalTabView.RunCommandAsync` on the UI thread to make the *visible* terminal follow the opened folder.
The package versions are pinned in **one place only**: `src/Loomo.Services/Loomo.Services.csproj` (App
references transitively) — check there for the exact versions in use.

Terminal auto-injects OSC 133 shell integration into interactive pwsh, and exposes the
`TerminalTabView.ShellCommandActivity` public event (command phase + exit code, for human-typed commands
too) — Loomo's stage-wing activity badges (`ShellWindow.PaneActivity.cs`, 設計書 §24.1) are built on it.

**LSP is enabled.** `BuildEditorControl` (`ShellWindow.ViewportSplit.cs`) passes
`LspManagerFactory = d => new LspManager(d)` (namespace `Editor.Controls.Lsp`, from `…Defaults`), so the editor
gets completion / diagnostics / go-to-definition etc. — **only when the matching language server is on `PATH`**.
The Composer editor (`ShellWindow.Composer.cs`) deliberately stays LSP-less.

The extension→server **mapping** is the editor library's `LspServerRegistry` (built-ins + user changes), but Loomo
redirects its persistence into its own folder via `LspServerRegistry.ConfigureDefault("%APPDATA%/Loomo/lsp-servers.json")`
in `App.OnStartup` (before any editor control is built). On top of that mapping Loomo owns the **install/management
UX** (the actual user-facing feature):
- `Services/Lsp/LspServerCatalog.cs` — known servers with **install commands** (`dotnet tool …`, `npm i -g …`,
  `winget …`, etc.) and docs URLs. `LspManagementService` detects whether each executable is on `PATH`
  (`ExecutableResolver`), runs installs in the **visible** terminal (`ITerminalService.TryRunInVisibleTerminal`),
  and adds/removes/resets registry entries.
- Settings overlay has a **言語サーバー (LSP)** category (`LspSettingsViewModel`, `SettingsCategory.Lsp`): per-server
  rows with install status, an Install button, add/remove/reset.
- Opening a file whose server is missing shows a dismissible **inline prompt bar** above the editor
  (`LspPromptViewModel`, evaluated in `SetActiveEditorTab`); "今後表示しない" persists to
  `AiSettings.Lsp.DismissedPromptExtensions` (settings.json).

The editor's own `:LspAdd`/`:LspRemove`/`:LspList`/`:LspReset` ex commands still work (same registry). See Editor
`CLAUDE.md` §LSP.

**Document formatting (`:Format`) is CLI-backed, not LSP-only.** Many text-LSP servers (e.g. `marksman` for
Markdown) don't implement `textDocument/formatting`, so the editor also has an extension→CLI-formatter registry
(`Editor.Core.Formatting.FormatterRegistry`, stdin→stdout). Loomo redirects its persistence into its own folder via
`FormatterRegistry.ConfigureDefault("%APPDATA%/Loomo/formatters.json")` in `App.OnStartup` (right after the LSP one).
There are **no built-in default mappings**: a configured CLI formatter wins over LSP; with none configured the editor
falls back to LSP formatting; and if that's empty too it probes `PATH` for the extension's `KnownFormatters`
candidates (prettier/dprint/black/…), using and registering the first installed one. Users can also set one explicitly
via the editor's `:FmtSet <ext> <cmd>` / `:FmtList` / `:FmtRemove` ex commands. On top of that, Loomo owns the same
**install/management UX** as for LSP (the LSP analog, now built):
- `Services/Formatting/FormatterCatalog.cs` — known CLI formatters with **install commands** (`npm i -g prettier`,
  `pip install black`, `winget …`, `rustup …`) and docs URLs. Its `Executable`/`Args` are kept identical to the editor's
  `KnownFormatters` so that "apply" registers the same mapping the editor would auto-probe.
- `FormatterManagementService` detects whether each executable is on `PATH` (reuses `ExecutableResolver` from the LSP
  side), runs installs in the **visible** terminal (`ITerminalService.TryRunInVisibleTerminal`), and applies/unapplies a
  catalog formatter across its extensions or adds/removes a custom `ext→cmd` mapping (since formatters have **no built-in
  defaults**, the settings list is **catalog-driven** rather than registry-driven — the LSP list shows registry built-ins,
  this one shows the catalog plus any custom entries).
- Settings overlay has an **整形 (Formatter)** category (`FormatterSettingsViewModel`, `SettingsCategory.Formatter`):
  per-formatter rows with install/apply status, Install / 適用 / 手順 / 解除 buttons, and a custom add form.

Reflecting over these assemblies via the shell tends to hallucinate — dump API to a file and Grep it, or use
`MetadataLoadContext`. The Terminal library source is at `C:\Projects\Terminal` (ConPTY, OSC133 shell
integration; see its `AGENT_API_SPEC.md`).
