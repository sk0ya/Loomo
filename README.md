# Loomo

**Loomo（ルーモ）** は、ローカルの開発ワークスペースを対象に動作する AI エージェント搭載のデスクトップアプリ（C# / WPF）です。ユーザーは自然言語で指示を出し、AI エージェントが **Terminal（コマンド実行）** と **Editor（ファイル表示・編集）**、**FolderTree（対象把握）** を「ツール（function calling）」として駆動しながらタスクを遂行します。

> 名前の由来 = **Loom**（織機＝複数のツールを織り上げる）× **Room**（作業空間）の造語。

## コアコンセプト

- AI エージェントは UI を「人間と同じ道具」として使う。FolderTree で対象を把握し、Terminal で実行し、Editor で表示・編集する。
- UI の各ペインは **疎結合なサービス + メッセージング（`WeakReferenceMessenger`）** で連携し、エージェントは「ツール（Tool）」を通じてそれらを駆動する。
- 「AI が操作する」「人間が操作する」を同じ経路（ツール → サービス → メッセージ → View）に集約し、テスト・記録・再現を容易にする。

## 技術スタック

| 領域 | 採用 |
|------|------|
| ランタイム | .NET 9（`net9.0` / `net9.0-windows`） |
| UI | WPF（ダーク基調・VS Code 系の操作感） |
| アーキテクチャ | MVVM（CommunityToolkit.Mvvm） |
| DI / 起動 | Microsoft.Extensions.Hosting + DependencyInjection |
| Terminal | [`sk0ya.Terminal.Controls`](https://www.nuget.org/) 1.0.3（ConPTY / OSC133 シェル統合） |
| Editor | `sk0ya.Editor.Controls` 1.0.0（Vim エディタ） |
| AI クライアント | Claude / OpenAI 互換 / GitHub Copilot / Stub（`IAiClient` で抽象化） |

## アーキテクチャ

依存は上 → 下の一方向。AI プロバイダは `IAiClient` 抽象の背後に隠され、差し替え可能です。

```
┌─────────────────────────────────────────────────────────┐
│  Presentation (WPF)  — Loomo.App                         │
│   Views (XAML) ── ViewModels ── Converters               │
└───────────────┬─────────────────────────────────────────┘
                │  Messenger（イベント） / DI
┌───────────────▼─────────────────────────────────────────┐
│  Agent Core  — Loomo.Core                                │
│   AgentOrchestrator（エージェントループ）                 │
│   ToolRegistry / IAgentTool 群                            │
│   ApprovalService（実行承認）/ ConversationStore / DiffUtil│
└───────────────┬─────────────────────────────────────────┘
                │  サービス抽象（インターフェース）
┌───────────────▼─────────────────────────────────────────┐
│  Services / Adapters  — Loomo.Services / Loomo.Ai        │
│   ITerminalService → sk0ya.Terminal                      │
│   IEditorService   → sk0ya.Editor                        │
│   IWorkspaceService→ ファイルシステム / FolderTree状態     │
│   IAiClient        → Claude / OpenAI / Copilot / Stub    │
└──────────────────────────────────────────────────────────┘
```

### プロジェクト構成

| プロジェクト | 役割 |
|------|------|
| `Loomo.Core` | UI 非依存のエージェント中核（agent / tools / abstractions / diff） |
| `Loomo.Ai` | AI クライアント実装（Claude / OpenAI 互換 / Copilot / Stub）と設定保管 |
| `Loomo.Services` | sk0ya コントロール（Terminal / Editor）と FileSystem のアダプタ |
| `Loomo.App` | WPF プレゼンテーション層（DI = Microsoft.Extensions.Hosting） |
| `Loomo.Tests` | 単体テスト |

ルート名前空間・アセンブリ名はいずれも `sk0ya.Loomo.*`。

## UI レイアウト

```
┌────┬──────────┬───────────────┬────────────────┐
│ A  │  Folder  │   Terminal    │     Editor     │
│ c  │  Tree /  │  (sk0ya.      │   (sk0ya.      │
│ t  │ Sessions │   Terminal)   │    Editor)     │
│ B  │ Settings │               │                │
├────┴──────────┴───────────────┴────────────────┤
│  AI バー（全幅・下部・展開式）                   │
└──────────────────────────────────────────────────┘
```

ActivityBar のアイコンでサイドバーのパネル（Explorer / Sessions / Settings）を切り替えます（同一アイコン再クリックで閉じる）。

## エージェントツール

エージェントが function calling で利用できるツール:

| ツール | 説明 |
|------|------|
| `list_directory` | ディレクトリ一覧の取得 |
| `read_file` | ファイル読み取り |
| `get_selection` | エディタの選択範囲取得 |
| `run_command` | 可視ターミナルでのコマンド実行 |
| `open_in_editor` | エディタでファイルを開く |
| `propose_edit` | 編集提案（統合差分を承認カードで色分け表示し、承認後に適用） |

## AI プロバイダ設定

- 既定は **Stub**（API キー不要・オフライン動作）。
- 設定画面（ActivityBar ⚙）でプロバイダ / モデル / API キー / BaseUrl / MaxTokens / SystemPrompt を編集。
- 永続化先: `%APPDATA%/Loomo/settings.json`。**API キーは DPAPI（CurrentUser）で暗号化**して保存。
- 設定はターン毎に読まれるため、変更が即時反映されます。
- **Copilot** は GitHub Device Flow でサインインし、トークン交換のうえ `api.githubcopilot.com` へ接続（非公式仕様のため E2E 未検証）。

## 主な機能

- **差分ビュー**: `propose_edit` が行 LCS ベースの統合差分を生成し、承認カードで追加（緑）/ 削除（赤）を色分け表示。
- **セッション保存・復元**: 会話を `%APPDATA%/Loomo/sessions/*.json` に自動保存し、Sessions パネルから復元。
- **可視ターミナル一本化**: コマンド実行は別プロセスではなく、画面上の `TerminalTabView` 上で実行（シェル統合により cwd を同期）。

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

設計の正本は [`docs/設計書.md`](docs/設計書.md) を参照してください。
