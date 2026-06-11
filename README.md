# Loomo

**Loomo（ルーモ）** は、ローカルの開発ワークスペースを対象に動作する AI エージェント搭載のデスクトップアプリ（C# / WPF / .NET 9）です。ユーザーは自然言語で指示を出し、AI エージェントが **ツール（function calling）** を通じて Terminal・Editor・FolderTree の各ペインを駆動しながらタスクを遂行します。

推論は **完全ローカル**：ONNX Runtime GenAI をアプリ内（in-process）で駆動し、外部サーバーも API キーも不要です（CPU のみで動作）。

> 名前の由来 = **Loom**（織機＝複数のツールを織り上げる）× **Room**（作業空間）の造語。

## コアコンセプト

- AI エージェントは UI を「人間と同じ道具」として使う。FolderTree で対象を把握し、Terminal 相当のコマンド実行で作業し、Editor で表示・編集する。
- UI の各ペインは **疎結合なサービス + メッセージング（`WeakReferenceMessenger`）** で連携し、エージェントは「ツール（Tool）」を通じてそれらを駆動する。
- **CPU 実行の小型ローカル LLM を前提に設計**する。ツール数は最小（3 つ）に絞り、コンテキスト消費とツール選択ミスを抑える。
- エージェントのコマンド実行は**独立した非対話 PowerShell プロセス**で行い、画面上のターミナルは人間専用に保つ（AI の出力が人間の端末に混ざらない）。

## 技術スタック

| 領域 | 採用 |
|------|------|
| ランタイム | .NET 9（`net9.0` / `net9.0-windows`） |
| UI | WPF（ダーク基調・VS Code 系の操作感・テーマ切替対応） |
| アーキテクチャ | MVVM（CommunityToolkit.Mvvm） |
| DI / 起動 | Microsoft.Extensions.Hosting + DependencyInjection |
| Terminal | `sk0ya.Terminal.Controls` 1.0.5（ConPTY / OSC133 シェル統合） |
| Editor | `sk0ya.Editor.Controls` 1.0.5（Vim エディタ・分割/タブ操作） |
| ローカル推論 | `Microsoft.ML.OnnxRuntimeGenAI` 0.14.1（CPU・in-process） |

## アーキテクチャ

依存は上 → 下の一方向（**App → Services/Ai → Core**）。`Loomo.Core` は UI 非依存で、エージェントループ・ツール契約・サービス抽象を保持します。

```
┌─────────────────────────────────────────────────────────┐
│  Presentation (WPF)  — Loomo.App                         │
│   Views (XAML) ── ViewModels ── ワークスペース/ペイン管理  │
└───────────────┬─────────────────────────────────────────┘
                │  Messenger（イベント） / DI
┌───────────────▼─────────────────────────────────────────┐
│  Agent Core  — Loomo.Core                                │
│   AgentOrchestrator（エージェントループ）                 │
│   ToolRegistry / IAgentTool（run_powershell ほか）        │
│   ISafetyPolicy（実行前安全評価）/ ApprovalService（承認） │
│   ConversationStore / ConversationTrimmer / Observability │
└───────────────┬─────────────────────────────────────────┘
                │  サービス抽象（インターフェース）
┌───────────────▼─────────────────────────────────────────┐
│  Services / Adapters  — Loomo.Services / Loomo.Ai        │
│   ITerminalService → 独立 pwsh プロセス + sk0ya.Terminal │
│   IEditorService   → sk0ya.Editor                        │
│   IWorkspaceService→ ファイルシステム / パス確認・制限     │
│   IAiClient        → OnnxGenAiClient（ローカル ONNX 推論）│
└──────────────────────────────────────────────────────────┘
```

### プロジェクト構成

| プロジェクト | 役割 |
|------|------|
| `Loomo.Core` | UI 非依存のエージェント中核（agent / tools / safety / observability / diff） |
| `Loomo.Ai` | ローカル推論エンジン（ONNX Runtime GenAI）・プロンプト整形・モデル管理・設定保管 |
| `Loomo.Services` | sk0ya コントロール（Terminal / Editor）と FileSystem のアダプタ |
| `Loomo.App` | WPF プレゼンテーション層（DI = Microsoft.Extensions.Hosting） |
| `Loomo.Tests` | 単体テスト + 実機精度評価ハーネス（xUnit） |

ルート名前空間・アセンブリ名はいずれも `sk0ya.Loomo.*`。

### エージェントループ

`AgentOrchestrator.RunTurnAsync` が中核（`IAsyncEnumerable<AgentEvent>`）：

```
ユーザー入力 → AI ストリーム → tool_use → 安全評価 → 承認 → ツール実行
→ 結果を AI へ返す → （繰り返し・最大 25 回） → 最終テキスト
```

- 送信前に毎回履歴をコンテキスト窓に収まるようトリム（元の会話は保持）。
- 小型モデルが壊れた JSON のツール呼び出しを出した場合は、生出力を保持したまま訂正を返して再試行させる（回数上限あり）。

## ローカル AI 推論

- **エンジン**: `Phi4Engine` が ORT-GenAI の Model/Tokenizer をプロセス内に常駐保持。モデルロード（数十秒）は初回のみで、以降のターンは prefill + decode のみ。
- **対応モデル**（設定画面からダウンロード可能・CPU int4・ORT-GenAI 互換）:
  - `microsoft/Phi-4-mini-instruct-onnx`
  - `lokinfey/Qwen3-1.7B-ONNX-INT4-CPU`（速度優先）
  - `lokinfey/Qwen3-4B-ONNX-INT4-CPU`（品質優先）
- **モデル別プロファイル**: コンテキスト窓・サンプリング・チャット書式（Phi-4 テンプレート / Qwen3 ChatML + Hermes ツール呼び出し）をモデル名から自動解決。
- **計測**: 各 AI 呼び出しのトークン数とロード/prefill/decode 時間を自己計測し、AI バーに内訳（📊）をライブ表示。トレース（`ai.usage`）にも記録。
- ダウンロード先は `%APPDATA%/Loomo/models/`（再開可能・キャンセル可能）。任意のフォルダのモデルもフォルダ選択で利用可。

## エージェントツール

ツールは **3 つだけ** に絞っています。CPU 実行の小型 LLM では、ツールが多いほど選択ミスと無駄な反復が増えるためです。

| ツール | 説明 |
|------|------|
| `run_powershell` | PowerShell コマンドラインを独立プロセスで実行し、標準出力と終了コードを返す（読み取り・検索・一覧・ビルド・テストはすべてこれで行う） |
| `write_file` | `{path, content}` でファイルを作成/上書きし、エディタで開く |
| `edit_file` | `{path, old_string, new_string}` で一意一致の厳密置換（0 件 / 複数一致は安全なエラー） |

ファイル系ツールが独立しているのは、シェル経由のファイル書き込みで小型モデルが失敗しがちな **PowerShell × JSON の二重エスケープ**を回避するためです。

### 安全機構

- すべてのツール実行前に `ISafetyPolicy` が評価。`run_powershell` はブロックパターン（正規表現）と照合し、違反はツールエラーとして AI に返す（実行されない）。
- `write_file` / `edit_file` のパスは**ワークスペースルート内に制限**。
- 3 ツールとも実行前に**承認カード**を表示（自動承認モードあり）。ファイルツールは行数・差分プレビュー付き。

## UI

```
┌────┬──────────┬──────────────────────────────────────┐
│ A  │ Explorer │   ペイン領域（ワークスペース単位）       │
│ c  │ Tabs     │   Terminal / Editor / Browser /        │
│ t  │ Sessions │   EditorSupport(Mdプレビュー) /         │
│ B  │ Git      │   Git / Diff / Trace                   │
│ a  │ Appear.  │                                        │
│ r  │ Settings │                                        │
├────┴──────────┴──────────────────────────────────────┤
│  AI バー（全幅・下部・展開式・実行内訳のライブ表示）      │
└────────────────────────────────────────────────────────┘
```

- **ワークスペース**: ペイン構成を複数保持して切り替え。レイアウトは自動保存・復元。
- **ペイン操作**: ドラッグ並べ替え、ペイン内分割（`Ctrl+W` `v`/`s`）、キーボードリサイズ（`Ctrl+W` → `Shift+h/j/k/l`）、マルチモニタ跨ぎ最大化。
- **FolderTree**: 拡張子別カラーアイコン、ピン留め、ルート切替、単クリックのプレビュータブ（VS Code 風）、「ターミナルにセット」。
- **Git**: サイドバーの Git パネル + コミットグラフの Git セッション、コミット差分の Diff セッション連携。
- **Markdown プレビュー**: EditorSupport ペインで自動表示。相対パス画像と mermaid 図に対応。
- **テーマ**: Appearance パネルからカラーテーマを切替（`DynamicResource` ベース）。

## 主な機能

- **セッション保存・復元**: 会話を `%APPDATA%/Loomo/sessions/*.json` にターン完了ごとに自動保存し、Sessions パネルから復元。
- **操作トレース**: エージェントの全操作（AI 呼び出し・ツール実行・usage）を `%APPDATA%/Loomo/traces/*.jsonl` に記録し、Trace セッションで閲覧。
- **差分プレビュー**: ファイル編集の承認カードに追加（緑）/ 削除（赤）の色分け差分を表示。
- **設定の即時反映**: 設定はターン毎に読み直すため、モデル変更などが次ターンから即適用。

## 永続化

`%APPDATA%/Loomo/` 配下に `settings.json`（プロバイダ / モデルパス / 安全設定。API キーは DPAPI で暗号化保存だがローカル推論では不要）、`models/`（ダウンロード済み ONNX モデル）、`sessions/`、`traces/` を保持。

## ビルドと実行

前提: [.NET 9 SDK](https://dotnet.microsoft.com/)（Windows）。

```powershell
# ビルド
dotnet build sk0ya.Loomo.sln

# 実行
dotnet run --project src/Loomo.App/Loomo.App.csproj

# テスト
dotnet test
```

## ドキュメント

- 設計の正本: [`docs/設計書.md`](docs/設計書.md)
- エージェントループの性能知見（ロード / prefill / decode の分解、モデルサイズと速度）: [`docs/エージェントループ知見.md`](docs/エージェントループ知見.md)
- 実機精度の評価レポート: `docs/reports/`（`AgentCapabilityHarness` = 25 タスク × 複数試行の実モデル評価）
