# §30 LSP アーキテクチャ（ワークスペース所有への再設計）

> 作成日: 2026-07-26 / 状態: **実装済み（2026-07-26）。実機確認は §30.6 P4 が未消化**
> 対象リポジトリ: `C:\Projects\Loomo` ＋ `C:\Projects\Editor`（破壊的変更を許容）
> 上書きした既述: §14.1（04-機能詳細.md）／Editor 側 `CLAUDE.md` §LSP ／ Loomo `CLAUDE.md` の LSP 節
> — いずれも更新済み。
> Editor パッケージ: **1.0.63**（連番運用のまま。nuget.org へは未公開＝ローカル注入で検証）。
>
> **§30.2 は「再設計前の状態」の記録**として残してある（なぜこの構造にしたかの根拠）。
> 現在のコードを読むときは §30.3 以降と、下の「実装との差分」を見ること。

## §30.0 実装との差分（設計書からの逸脱）

着手前の設計は概ねそのまま通ったが、実装で判断を変えた点が5つある。

1. **`ILspDocument`/`ILspWorkspace` のメンバ構成**は §30.3.2 の草案どおりではない（§30.10-1 の予告どおり）。
   実際の分割は「ワークスペース 9／文書 24／ビュー 44」。`ILspDocument` には
   `ServerSupportsFoldingRange` 等の capability と `StatusMessage`/`StateChanged` を持たせ、
   呼び出し/型階層は URI を引数に取るワークスペース側へ置いた。
2. **`StandaloneLspWorkspace` は作らなかった**（判断: 不要）。`VimEditorControlDefaults.CreateOptions()` は
   LSP を配線せず、Editor 単体利用は `NullLspView`＝LSP オフになる。
3. **`ILspServerAdmin` の実装は `LspServerTable` 自身**。`LspWorkspaceService` に実装させると
   ただの委譲層が増えるだけなので、DI で表を直接 `ILspServerAdmin` として公開している。
4. **`LspViewBridge` の置き場所は `Editor.Controls`**（`Editor.Controls.Defaults` ではない）。
   プロセス管理を失った結果 `LspClient` への依存が消え、コントロールから直接生成できるようになったため。
5. **プールのキーのルートは常にプライマリフォルダー**（`Folders[0]`）。ファイル起点の遡り探索は廃止。
   > **⚠️ 撤回（2026-08-05・§32.4.3）**: この 5 は取り消した。`workspaceFolders` を2件以上渡すと
   > **Roslyn がデータフロー解析を要するリファクタリング（メソッドの抽出）を返さなくなる**——
   > `[Loomo]` なら5秒で2件、`[Loomo, AimAssist]` なら120秒待っても0件（実測）。
   > 現在は **ワークスペースフォルダーごとにサーバー1本**で、`initialize` にはそのルート1件だけを載せる。
   > 同じフォルダー内で N 枚開いてもサーバーが1本なのは変わらない（§30 本来の目的は維持）。
   > フォルダー同士が祖先/子孫にならないことは `IWorkspaceService.AddFolder` が保証するので、
   > 下の 6 が禁じた「含んでいるフォルダーをルートに選ぶ」には当たらない。
   実フォルダー一覧は `initialize` の `workspaceFolders` で全件渡す（`37b2858` の挙動を踏襲）。
   → 1本のサーバーが全ルートを見るので、**含んでいるフォルダーをルートに選んではいけない**
   （§30.0-6）。併せて `ILspClient` に `Exited` とマルチルート `InitializeAsync` を昇格させ、
   プールがモック可能になった。
6. **実機で1件バグを見つけて直した。** 最初の実装は `ResolveRoot` が「そのファイルを含むフォルダー」を
   返していた。マルチルート（Loomo＋AimAssist）で起動すると、`initialize` には両フォルダーを渡すのに
   ルートだけ `AimAssist` になり、Loomo 側の `.cs` を開けば**同じ担当範囲の Roslyn がもう1本**立つ
   ——この再設計が潰そうとしていた N プロセス問題そのものだった。常にプライマリを返すよう修正し、
   マルチルートでの共有をテスト（`MultiRoot_StillSharesOneServerAcrossFolders`）で固定した。

---

## §30.1 目的

LSP の**セッション（プロセス・プロトコル・ワークスペーススコープ）をホスト（Loomo）が所有**し、
**エディタコントロールはビュー（表示状態・描画）だけを持つ**構造に作り替える。

現状は逆で、セッションが `VimEditorControl` に紐付いている。そのため LSP が「エディタの機能」になっており、
エディタの外にいる消費者（検索ペイン、EditorSupport、Problems、将来のエージェントツール）が
**タブを経由しないと LSP に触れない**。これが不具合の温床になっている。

---

## §30.2 現状の破綻（実測）

### 30.2.1 レジストリが3つに分裂している

| 経路 | 実際に読んでいるレジストリ | 永続化先 |
|------|---------------------------|----------|
| 設定画面（`LspManagementService`） | `LspServerRegistry.Default` の**都度 new されたインスタンス** | `%APPDATA%/Loomo/lsp-servers.json` |
| 各タブの `LspManager` | 同上（別インスタンス） | 同上（読むだけ） |
| 各タブの `:LspAdd` 等 Ex コマンド | `VimEngineServices.CreateIsolated().LspServers` | **なし（メモリ内のみ）** |

- `Editor.Core/Lsp/LspServerRegistry.cs:75-78` — `Default` は **アクセスごとに新インスタンス**を返す
  （XMLコメントに「share an instance through `VimEngineServices`」と明記されている）。
- `ShellWindow.ViewportSplit.cs:164` が `VimEditorControlOptions.EngineServices` を渡さない
  → `VimEngineRuntime.cs:191` が `CreateIsolated()` を作る
  → `VimEngineServices.cs:36` の `new LspServerRegistry()` は **storePath = null ＝完全メモリ内**。
  つまり `:LspAdd` は保存されず、`:LspList` は Loomo の JSON を読んでもいない。
  `LspServerRegistry.ConfigureDefault` の効果も及ばない。
- **当時は**同じ構造が整形にもあった（旧 `FormatterManagementService.cs:39` の
  `FormatterRegistry.Default`）。後続で解消済み（§30.13）。
- `AppBootstrapper.cs:41` の `EnsureCSharpDefault(LspServerRegistry.Default)` は使い捨てインスタンスへの
  書き込みだが、`Set` が JSON に保存するので**偶然動いている**だけ。

### 30.2.2 ワークスペースクエリがタブに依存する（意味の破綻）

`WorkspaceSymbolSearch.cs:14-29` は、**開いているタブを走査して接続済み `LspManager` を集めてから**
`workspace/symbol` を投げている。したがって検索ペインのクラス／シンボル検索結果は
**そのとき何のタブを開いているかで変わる**。`.cs` タブを1枚も開いていなければ 0 件になる。

同じ構造的欠陥が `RequestWorkspaceDiagnosticsAsync`・呼び出し階層・型階層にもある。
これはインスタンス共有では直らない（層が違う）。

### 30.2.3 タブごとに言語サーバープロセスが立つ

`BuildEditorControl`（`ShellWindow.ViewportSplit.cs:162`）がタブごとに `new LspManager`。
`LspManager._clients`（`LspManager.cs:933`）は**マネージャ内**の実行ファイル別辞書なので、
同一ソリューションの `.cs` を N 枚開けば Roslyn が N プロセス、初期化・プロジェクト解析も N 回。

※ タブを閉じれば `Tabs.cs:170` の `Control.Dispose()` → `VimEditorControl.xaml.cs:1080` の
`_lspManager.Dispose()` が走るので**プロセス残留はしない**。問題は残留ではなく起動チャーンと N 重解析。

### 30.2.4 層の混在

`IEditorLspManager` は **62 メンバ**。同じインターフェースに
`CompletionScrollOffset`（補完ポップアップのスクロール位置＝ビュー状態）と
`GetWorkspaceSymbolsAsync`（ワークスペース全体のクエリ）が同居している。これが 30.2.2/30.2.3 の直接の原因。

### 30.2.5 所有の逆転

`ShellWindow.ViewportSplit.cs:168` で Loomo は `workspaceFoldersProvider: () => _workspace.Folders` を
**Editor へ注入**している。ワークスペースの真実は Loomo が持っているのに、そのワークスペースに対する
セッションは Editor が持つ。ext→server の表も、インストール・カタログ・PATH 検出・促し UX は全部 Loomo なのに
**表そのものだけ Editor**。この 20% が分裂の原因になっている。

---

## §30.3 あるべき構造

分割線は「**プロセスとプロトコルはワークスペース単位、UI 状態はビュー単位**」。

```
┌─────────────────────────────────────────────────────────────┐
│ Loomo（ホスト）                                              │
│                                                              │
│  LspWorkspaceService : ILspWorkspace   ← DI シングルトン      │
│   ├ LspClientPool  key=(executable, workspaceRoot)           │
│   ├ DocumentTable  uri → 参照カウント・書き手（owner）        │
│   ├ LspServerTable ext→server（旧 LspServerRegistry を移管）  │
│   └ event DiagnosticsPublished / ServerStateChanged          │
│                                                              │
│  消費者（すべて ILspWorkspace を直接注入で受ける）             │
│   ├ VimEditorControl（options 経由）                          │
│   ├ WorkspaceSymbolSearch（タブ走査を廃止）                   │
│   ├ EditorSupport（アウトライン・呼び出し階層）                │
│   ├ Problems ペイン（診断ファンアウトを購読）                  │
│   └ 設定画面 / 促しバー / Ex コマンド管理                      │
└─────────────────────────────────────────────────────────────┘
                          ▲ ILspWorkspace / ILspDocument
┌─────────────────────────────────────────────────────────────┐
│ Editor（ライブラリ）                                          │
│  LspViewBridge : IEditorLspView   ← コントロール1個につき1個   │
│   ├ 補完ポップアップの選択/スクロール/可視                     │
│   ├ コードアクションの選択/スクロール/可視                     │
│   ├ breadcrumb・ハイライト・インレイヒント・折り畳みの表示状態 │
│   └ ILspDocument ハンドル1個（現在バッファ）                   │
└─────────────────────────────────────────────────────────────┘
```

### 30.3.1 責務表

| 関心事 | 持ち主 |
|--------|--------|
| プロセス寿命・プール（key = 実行ファイル × ルート） | **Loomo** |
| `initialize` / capability 交渉 / workspace folders | **Loomo** |
| `didOpen`/`didChange`/`didClose` の参照カウント | **Loomo** |
| 診断の受信とファンアウト | **Loomo** |
| `workspace/symbol`・workspace 診断・呼び出し/型階層 | **Loomo** |
| ext→server 対応表・カタログ・インストール・PATH 検出・促し UX | **Loomo**（表も含め全部） |
| 補完ポップアップの選択/スクロール、下線描画、インレイヒント描画、breadcrumb 表示 | **Editor** |
| キーバインド、`:Lsp*` の**入力フロントエンド** | **Editor** |

### 30.3.2 インターフェース（Editor.Core.Lsp に定義、Loomo が実装）

```csharp
// ワークスペース単位。アプリに1個。
public interface ILspWorkspace
{
    // 文書。null = この拡張子に対応するサーバーが無い/未導入。
    ILspDocument? OpenDocument(string filePath, string initialText);

    // ワークスペーススコープ（タブに依存しない）
    Task<IReadOnlyList<LspSymbolInformation>> GetWorkspaceSymbolsAsync(string query, bool isClass, CancellationToken ct = default);
    Task<LspWorkspaceDiagnosticResult?> RequestWorkspaceDiagnosticsAsync(CancellationToken ct = default);
    Task<CallHierarchyItem?>  PrepareCallHierarchyAsync(string uri, int line, int character);
    Task<CallHierarchyIncomingCall[]?> GetIncomingCallsAsync(CallHierarchyItem item);
    Task<CallHierarchyOutgoingCall[]?> GetOutgoingCallsAsync(CallHierarchyItem item);
    Task<TypeHierarchyItem?>  PrepareTypeHierarchyAsync(string uri, int line, int character);
    Task<TypeHierarchyItem[]?> GetSupertypesAsync(TypeHierarchyItem item);
    Task<TypeHierarchyItem[]?> GetSubtypesAsync(TypeHierarchyItem item);

    // どのビューが開いていても発火する（Problems ペインが直接購読できる）
    event Action<string /*uri*/, IReadOnlyList<LspDiagnostic>>? DiagnosticsPublished;
    event Action? ServerStateChanged;

    bool IsServerAvailableFor(string extension);
}

// 文書ハンドル。Dispose = didClose（参照カウント減）。
public interface ILspDocument : IDisposable
{
    string Uri { get; }
    bool IsReady { get; }              // didOpen 済み
    bool IsWriter { get; }             // このハンドルがテキストの正本か（§30.3.4）
    IReadOnlyList<LspDiagnostic> CurrentDiagnostics { get; }

    void UpdateText(string text);      // IsWriter=false のときは no-op
    Task<LspCompletionResult?> RequestCompletionAsync(int line, int character);
    Task<string?> RequestHoverAsync(int line, int character);
    // ...文書スコープの要求のみ（定義/参照/リネーム/整形/シンボル/折り畳み/インレイ/セマンティック）

    event Action<IReadOnlyList<LspDiagnostic>>? DiagnosticsChanged;
    event Action? Ready;
}
```

`IEditorLspView`（Editor 側）には**ビュー状態だけ**が残る（補完の選択・スクロール・可視、
コードアクションの選択・スクロール・可視、breadcrumb 文字列、ハイライト、`ILspDocument` の保持）。
62 メンバは概ね **ワークスペース 15 / 文書 20 / ビュー 25** に分かれる。

### 30.3.3 プールのキー

`(実行ファイル, ワークスペースルート)`。**拡張子ではない** — Roslyn は `.cs`/`.csx`、
typescript-language-server は `.ts`/`.tsx`/`.js`/`.jsx` を1プロセスで賄うため。
ルートは Loomo が `IWorkspaceService.Folders` から決める（現行の `FindWorkspaceRoot` によるファイル起点の
遡り探索は廃止。マルチルート初期化は `37b2858` で入った挙動を引き継ぐ）。

### 30.3.4 同一ファイルを複数ビューで開いた場合（書き手の一意化）

Loomo は分割ビュー・切り離しウィンドウで**同じファイルを別バッファとして2枚開ける**。
LSP の文書同期は1 URI につき1本しか成立しないので、規則を明示する：

- 1つの URI に対し `didOpen` は**1回だけ**。以降の `OpenDocument` は参照カウントを増やしてハンドルを返す。
- **最初のハンドルが書き手（`IsWriter = true`）**。以降のハンドルは読み手で `UpdateText` は no-op。
- 書き手が `Dispose` されたら、残っているハンドルの先頭へ書き手を移譲し、その時点のテキストで `didChange` を送る。
- 参照カウントが 0 になったら `didClose`。
- 診断は URI 単位なので**全ハンドルへ配る**（読み手のビューにも波線が出る）。

### 30.3.5 サーバープロセスの寿命

- 遅延起動：その `(実行ファイル, ルート)` の最初の `OpenDocument` で起動。
- **アイドル維持**：最後の文書が閉じてもすぐには落とさず、既定 5 分維持してから終了
  （タブを閉じて開き直すたびに Roslyn を再起動しないため）。ワークスペース切替時は即時終了。
- クラッシュ時の再接続・リプレイは現行 `LspManager` の実装（`MaxReconnectAttempts = 3`）を
  ワークスペース側へ移設して踏襲する。

---

## §30.4 設定・永続化の移管

- ext→server 対応表を **Loomo.Services へ移管**（`Loomo.Services/Lsp/LspServerTable.cs`）。
  永続化先・スキーマは現行のまま `%APPDATA%/Loomo/lsp-servers.json`（`{ Overrides, Removed }`）
  → **移行処理は不要**。
- 組み込み既定テーブル（`.cs`/`.py`/`.ts`… の16件）も Loomo 側へ移し、`LspServerCatalog` と統合する。
  現在は「実行ファイルは Editor、インストール手順は Loomo」と割れているものを1か所に寄せる。
- Editor.Core からは `LspServerRegistry` を**削除**する。単体利用のホスト向けには
  `Editor.Controls.Defaults` に `StandaloneLspWorkspace`（組み込みテーブル＋既定の永続化先を持つ
  `ILspWorkspace` 実装）を置き、`VimEditorControlDefaults.CreateOptions()` はそれを使う。
  **Loomo はこれを使わない。**
- `:LspAdd`/`:LspRemove`/`:LspList`/`:LspReset` は Ex コマンドとして残すが、
  ホストが注入した `ILspServerAdmin` へ委譲する。未注入なら「利用できません」と返す。
  → **第二の所有者が消えるので §30.2.1 の分裂はクラスごと消滅する。**
- 整形（`FormatterRegistry`）も当時は同じ構造の欠陥を持っていたが、**このLSP移管作業のスコープ外**とした。
  後続の移管は §30.13 で完了。

---

## §30.5 破棄する概念

| 破棄するもの | 理由 |
|--------------|------|
| `Editor.Core.Lsp.LspServerRegistry` | 所有者が2人いる状態の元凶。Loomo へ移管 |
| `LspServerRegistry.Default` / `ConfigureDefault` | 「都度 new される互換ファクトリ」。存在自体が罠 |
| `VimEngineServices.LspServers` | 上の移管に伴い不要 |
| `VimEditorControlOptions.LspManagerFactory` | `LspWorkspace`（インスタンス注入）に置換 |
| `LspManager` の `_clients` 辞書 | プールがワークスペース側へ移るため |
| `LspManager.FindWorkspaceRoot`（ファイル起点の遡り） | ルートは Loomo の `IWorkspaceService` が正 |
| `WorkspaceSymbolSearch.ConnectedManagers`（タブ走査） | ワークスペースへ直接問い合わせる |
| `workspaceFoldersProvider` コールバック | ワークスペース実装が最初から知っている |

---

## §30.6 作業書

破壊的変更を許容するため、互換シムは作らない。**P1 と P2 の間はビルドが通らない**ので、
Editor 側の変更 → パッケージのローカル注入 → Loomo 側の追随、をひと続きで行う。

### P0. 準備 — 完了

- [x] `IEditorLspManager` の 62 メンバを **ワークスペース / 文書 / ビュー** に仕分けした
      （結果は §30.0-1。見積り 15/20/25 に対し実測 9/24/44）。
- [x] 現行 LSP 関連テストのうち**本番配線を検証していないもの**を特定（§30.7）。
      作業ブランチは切らず、両リポジトリとも `main` へ直接コミットした
      （Editor も同一変更セットで動く必要があり、片方だけブランチにすると検証できないため）。

### P1. Editor 側の層分離（`C:\Projects\Editor`）— 完了

- [x] `Editor.Core/Lsp/ILspWorkspace.cs`（`ILspServerAdmin` 同居）・`ILspDocument.cs` を新設。
      `LspServerDef`/`LspServerEntry`/`LspServerOrigin` と `LspExtensions.NormalizeExt` を
      `LspServerDef.cs` へ退避。
- [x] `IEditorLspManager` → `Editor.Controls/Lsp/IEditorLspView.cs`。ワークスペーススコープのメンバを削除し、
      `ILspDocument? Document` を追加。`NullLspManager` → `NullLspView`。
- [x] `LspManager` → `Editor.Controls/Lsp/LspViewBridge.cs`（**Defaults ではない**、§30.0-4）。
      `_clients`・`CreateClient`・`OnClientExited`・`FindWorkspaceRoot`・再接続を削除。
      文書の準備完了は `ILspDocument.StateChanged` 起点になり、折り畳み/シンボルの再試行だけ残った。
- [x] `LspClient`/`LspProcess` は `Editor.Controls.Defaults/Lsp/` に `public` のまま残置。
      `ILspClient` に `Exited` とマルチルート `InitializeAsync` を追加（ホストがプールを組めるように）。
- [x] `Editor.Core/Lsp/LspServerRegistry.cs` と `VimEngineServices.LspServers` を削除。
- [x] `StandaloneLspWorkspace` は**作らない**判断（§30.0-2）。`VimEditorControlDefaults.CreateOptions()` から
      LSP 配線を除去。
- [x] `VimEditorControlOptions`：`LspManagerFactory` を削除、`ILspWorkspace? LspWorkspace` と
      `ILspServerAdmin? LspServerAdmin` を追加。`VimEditorControl` に `LspDocument` プロパティを公開。
- [x] `LspCommands` を `ILspServerAdmin?` 委譲へ。未注入なら
      「LSP: server configuration is not available in this host」。
      `VimEngine`/`VimEngineRuntime`/`ExCommandProcessor` に `lspServerAdmin` を通した。
- [x] `CLAUDE.md` §LSP を新構造へ差し替え（「`Default` はプロセス全体のシングルトン」という誤記を削除）。
- [x] バージョン **1.0.63**（連番運用）。**nuget.org へは push していない。**

### P2. Loomo 側のワークスペース実装（`C:\Projects\Loomo`）— 完了

- [x] `Loomo.Services/Lsp/LspServerTable.cs` — 旧 `LspServerRegistry` を移設。
      組み込み16件は `LspServerCatalog` から**導出**（統合済み。`LspServerTarget` で拡張子ごとの
      languageId を持つ）。永続化先・スキーマは現行踏襲。旧 C# サーバー設定は読み込み時に破棄。
- [x] `Loomo.Services/Lsp/LspClientPool.cs` — key `(実行ファイル, ルート)`、アイドル5分（1分周期の掃除）、
      クラッシュ再接続（3回・0.5s/1.5s/4.5s バックオフ）。接続生成は差し替え可能（テスト用）。
- [x] `Loomo.Services/Lsp/LspDocumentTable.cs` ＋ `LspDocumentHandle.cs` — URI 別参照カウントと
      書き手の一意化・移譲（§30.3.4）、診断の全ハンドル配布、サーバー変更時の載せ替え。
- [x] `Loomo.Services/Lsp/LspWorkspaceService.cs : ILspWorkspace, IDisposable`。ルートは
      `IWorkspaceService.Folders` から決め、`FoldersChanged` で**即時全終了**。
      `ILspServerAdmin` は `LspServerTable` 自身が実装（§30.0-3）。
- [x] DI 登録を `AddLoomoLsp` に切り出し（本番配線をテストから呼べるようにするため）。
      表は具象＋`ILspServerAdmin`、セッションは具象＋`ILspWorkspace` で同一インスタンス。
- [x] `LspManagementService` を表の注入必須に変更（既定コンストラクタを削除）。
      組み込みが Roslyn になったので、Roslyn を BuiltIn に見せる補正と `.cs` の Reset 特例を削除
      （前者は残すと**無効化した `.cs` が BuiltIn に見える**バグになる）。
- [x] `AppBootstrapper.Initialize` から `LspServerRegistry.ConfigureDefault` と `EnsureCSharpDefault` を削除。

### P3. 消費者の付け替え — 完了

- [x] `ShellWindow.ViewportSplit.cs` — `LspWorkspace`/`LspServerAdmin` を渡す。
      `workspaceFoldersProvider` と `_editorLspManagers`（ConditionalWeakTable）を廃止し、
      `GetLspDocument(tab)` ＝ `tab.Control.LspDocument` へ。
- [x] `WorkspaceSymbolSearch.cs` — `ConnectedManagers`/`MergeAsync` を削除し `ILspWorkspace` へ直接問い合わせ。
      マージ・重複排除はセッション側へ移動。**タブを開いていなくても効く**（ルートのプロジェクトマーカーから
      言語を割り出してサーバーを起こす）。
- [x] `ShellWindow.EditorSupport.cs` / `CodeEditorSupportAnalysis.cs` — 文書スコープはハンドル、
      呼び出し階層はワークスペースへ。`LspMatchesFile` は `ILspDocument.FilePath` 比較になった。
- [x] 促しバーの評価を `OnActiveEditorFileChanged(tab)` の1点へ集約し、`LoadFile` の**後**に呼ぶ。
      同じパスの二度目は捨てる。判定結果はアウトラインの案内（`EvaluateLspPrompt`）と共用。
- [x] `LspSettingsViewModel` の「開き直すと有効」文言を削除（その場で反映されるため）。
- [x] **Problems ペイン**（2026-08-02）— `ILspWorkspace.DiagnosticsPublished`を背景スレッドから購読し、
      DispatcherへマーシャリングしてBuild/LSPを発生源付きで統合する。Roslynのpull diagnosticsは変更後300msで
      取得し、文書版の古い応答を破棄する。空のfull reportで古い項目を消し、`unchanged`／取得失敗では維持する。

### P4. 検証

- [x] `dotnet build` / `dotnet test` 両リポジトリ（Editor 1252 件・Loomo 1257 件、いずれも全通過）。
- [x] Editor パッケージのローカル注入で通した（`dotnet pack` → キャッシュ掃除 → `restore --configfile`）。
      **nuget.org への公開はしていない。**
- [x] **実機起動**（2026-07-26）：DI 解決・ウィンドウ表示・セッション復元まで通り、
      マルチルート（`C:\Projects\Loomo` ＋ `C:\Projects\AimAssist`）で Roslyn は**1プロセス**、
      両フォルダーの `.cs` が同じサーバーへ `didOpen` された（`%TEMP%\editor-lsp-debug.log` で確認）。
      この確認で §30.0-6 のバグを発見・修正している。
- [ ] 残りの実機確認（**次に触るときここから**）：
  - [x] `.cs` を複数開いて Roslyn プロセスが**1個**であること（上記の起動確認で確認済み）。
  - [ ] `.cs` タブを1枚も開かずに検索ペインのクラス検索が結果を返すこと。
  - [ ] 設定画面で `.py` のサーバーを変更 → **開き直さずに**該当タブへ反映されること。
  - [ ] `:LspList` の内容が設定画面と一致し、`:LspAdd` が `%APPDATA%/Loomo/lsp-servers.json` に残ること。
  - [ ] 同じファイルを分割ビューで2枚開き、片方で編集して**両方に**波線が出ること（§30.3.4）。
  - [ ] タブを全部閉じて5分以内に開き直すとサーバーが再起動しないこと。
- [x] `docs/設計/04-機能詳細.md` §14.1・`03-UIとレイアウト.md` §26・`README.md` を更新。
- [x] Loomo `CLAUDE.md` の LSP 節を新構造へ差し替え。

---

## §30.7 テスト方針（現状の穴）

現行の LSP 関連 79 テストが全部通るのに本番が壊れているのは、
テストが `internal LspManagementService(terminal, registry)` で**レジストリを注入している**ため。
つまり**本番の配線だけが検証対象外**になっている。再発を防ぐため、以下を必ず追加する。

- **配線テスト**：設定画面経由の追加が、コントロールに渡るのと**同一の**テーブルから読めること。
- **Ex コマンドの参照先テスト**：`:LspAdd` の結果が同じテーブル・同じ JSON に出ること。
- **永続化先テスト**：ストアパスが `%APPDATA%/Loomo/` であること。
- **プール共有テスト**：同一ルートの2文書で `LspClient` の生成が1回であること。
- **参照カウントテスト**：2ハンドル→1つ Dispose で `didClose` が飛ばないこと、
  書き手の移譲が起きること（§30.3.4）。
- **促し評価テスト**：`OpenFileInNewEditorTabAsync` で評価が `LoadFile` **後**に1回だけ呼ばれること。

---

## §30.8 非目標（今回やらないこと）

- 整形（`FormatterRegistry`）の同型移管 — この計画時点では未着手。現在は §30.13 で完了。
- LSP をエージェントツールとして公開すること（`ILspWorkspace` が単一の入口になるので**後から容易**になる、
  というのが今回の副次的な狙い）。
- LSP クライアント実装（`LspClient`/`LspProcess`）の Loomo への移設 — Editor 側の部品として再利用する。
- セマンティックトークン・インレイヒントの描画仕様の変更 — ビュー側は現状維持。

> **完了追記（2026-08-01）**：上記で非目標だった整形レジストリの同型移管は、後続作業として §30.13 で完了した。

---

## §30.9 スレッド親和性

現行 `LspManager` は `Dispatcher` を保持し、「UI に見える状態は必ずディスパッチャスレッド」で統一している。
`ILspWorkspace` は DI サービスでディスパッチャを持たないため、**境界を明示する**：

- **ワークスペース／文書ハンドルの内部状態はスレッドセーフ**（プール・参照カウント・診断キャッシュは lock 保護）。
  背景の JSON-RPC 読み取りスレッドから直接更新してよい。
- **`ILspWorkspace` / `ILspDocument` のイベントは背景スレッドで発火する**ことを契約として明記する。
  ディスパッチャへのマーシャリングは**購読側（`LspViewBridge`・Problems ペイン・EditorSupport）の責務**。
  現行 `LspManager` が内部で行っている `Dispatcher.Invoke` を、ビュー側へ移す。
- 切り離しウィンドウ（`DetachedWindowManager`）は `win.Show()` で**同一 UI スレッド**に作られるため、
  ディスパッチャは1つ。複数ディスパッチャ対応は不要。

この契約を破ると「診断は届くのに波線が出ない／たまにクラッシュする」という再現しにくい不具合になるので、
`ILspWorkspace` の XML コメントに**背景スレッド発火であることを必ず書く**。

---

## §30.10 この作業書の確度（未検証部分）

以下は**設計であって検証済みの実装計画ではない**。着手時に崩れる可能性がある箇所を明示しておく。

1. **62 メンバの3分割は未検証。** §30.3.2 の「ワークスペース 15 / 文書 20 / ビュー 25」は見積り。
   P0 の仕分け作業が実質的な設計本体で、そこで割れないメンバが出る可能性がある
   （例：`RequestFormattingAsync` は文書スコープだがタブ幅設定というビューの都合を引数に取る、
   `GetBreadcrumb` は文書シンボルのキャッシュとカーソル位置の両方に跨る）。
   **P0 の結果によっては §30.3.2 のインターフェース定義を書き直す。**
2. **§30.3.4 の書き手一意化は新規ロジックで、既存実装が無い。** 現行は「2タブ＝2プロセス」なので
   両方が独立に動いていた。移行後は読み手ビューでの編集がサーバーへ届かなくなるため、
   **書き手の移譲を実装し損ねると「片方のビューだけ補完が古い」という新種の不具合になる**。
   P4 の実機確認（分割ビュー2枚）は必須。
3. **アイドル維持中のワークスペース切替**は、プールのキーにルートが入っているので原理的には安全だが、
   Roslyn がメモリを抱えたまま残る。§30.11 の判断次第。

---

## §30.11 判断が必要な点（着手前に確認）

1. **Editor パッケージのバージョン** — 連番運用（1.0.63）か、破壊的変更として `2.0.0` か。
2. **`StandaloneLspWorkspace` を用意するか** — Editor 単体利用者を切り捨てて良いなら不要で、
   Editor 側の削除量がさらに減る。
3. **アイドル維持時間 5 分** の妥当性（Roslyn の再起動コストとメモリのトレードオフ）。
4. **ワークスペース切替時**にサーバーを落とすか維持するか（複数ワークスペースを行き来する運用頻度次第）。

---

## §30.12 促しバーの抑止ルール（2026-07-31 修正）

「言語サーバーが設定されていません」の案内が**邪魔**という指摘に対する是正。3点あった。

1. **言語サーバーが存在しえない拡張子にまで出ていた。** `EvaluateForFile` は未設定の拡張子なら無条件に
   `NotConfigured` を返していたので、`.png` や `.zip` を開いただけで案内が出た。
   → `LspManagementService.PromptableSourceExtensions`（プログラミング言語のソース拡張子のキュレーション）
   に含まれる拡張子だけ `NotConfigured` を出す。カタログ／対応表にある拡張子は手前の分岐で処理されるので、
   この表は「LSP はあるが Loomo のカタログに無い言語」（`.java`/`.kt`/`.swift`…）を拾うためのもの。
   文書・設定・データ形式（`.md`/`.json`/`.xml`/`.csv`…）は専用の EditorSupport 提供者があるので**入れない**。
2. **「今後表示しない」が EditorSupport の案内に効いていなかった。** 抑止判定が `LspPromptViewModel.Show`
   の内部にしか無く、アウトライン側（`EvaluateLspPrompt` → `LspNoticeModel.Build`）は素の判定結果を使って
   いたため、バーを消しても同じ文言が EditorSupport ペインに出続けた。
   → 抑止を共有フィルタ `LspPromptViewModel.Filter` に切り出し、**バーとアウトラインの両方**が通す。
   キャッシュ（`_lastLspPrompt`）は素の判定結果のままにして、参照のたびにフィルタを適用する
   （そうすると「閉じた直後」の再評価でも抑止が効く。案内は 200ms の再試行ティックで描き直される）。
3. **「今後表示しない」が再起動で消えていた。** `LoomoSettings.Lsp` を `SettingsStore` の DTO
   （`PersistedSettings`）が**保存も読込もしていなかった**ため、`DismissedPromptExtensions` はプロセス内
   だけの状態だった。→ `PersistedLsp` を追加。拡張子は正規化（先頭ドット・小文字）して保持する。

---

## §30.13 FormatterRegistry のホスト所有（2026-08-01 完了）

LSP 移管後も残っていた整形側の分裂を解消した。Loomo の DI がアプリ共有の
`VimEngineServices` と、その `Formatters` を同じシングルトンとして所有する。

- `FormatterManagementService` は `FormatterRegistry.Default` を都度取得せず、共有インスタンスを
  コンストラクタで受け取る。
- 全 `VimEditorControl` に同じ `VimEngineServices` を渡すため、`:FmtSet` / `:FmtRemove` / 自動検出による登録と
  設定画面の適用・解除が即座に同じ表へ反映される。
- 永続化先は従来どおり `%APPDATA%/Loomo/formatters.json`。静的 `ConfigureDefault` による起動順依存は廃止した。
- 本番 DI 自体を `FormatterWiringTests` で検証し、「設定だけ別インスタンス」の回帰を防ぐ。

---

## §30.14 サーバー由来 URI の正規化 —— `LspUri`（2026-08-03 修正）

**症状**：TypeScript で `RenameSymbol` が必ず失敗し（`ワークスペース外の文書は編集できません: C:\c:\…`）、
`.ts` の診断（波線）も出ていなかった。C# では両方とも正常。補完・ホバー・アウトラインだけは効く。

**原因**：サーバーごとに file URI の綴りが違う。Roslyn は `file:///c:/work/a.cs`、
**`vscode-uri` 系（typescript-language-server・VS Code 本体）はドライブのコロンを百分率符号化して
`file:///c%3A/work/a.ts` を返す**（実測済み。rename / references / publishDiagnostics すべてこの形）。
.NET はこの形を DOS パスと認識できず、`new Uri(uri).LocalPath` が `/c:/work/a.ts` を返す。結果：

| 経路 | 起きること |
| --- | --- |
| rename / コードアクション | 「編集中のファイル自身」に当たらず全部が他ファイル扱い → ホストが `Path.GetFullPath` して `C:\c:\…` → ルート外判定で失敗 |
| publishDiagnostics | `LspDocumentTable` の URI 照合が外れ、診断が丸ごと捨てられる（tsserver は pull 診断非対応なので push が唯一の経路） |
| 参照一覧 / 定義ジャンプ / 呼び出し階層 / 問題パネル | 開けないパスになる |

補完・ホバーが無事だったのは、**こちらが送った URI をそのまま使う往復リクエスト**だから。

**対処**：変換点を1つに畳んだ。`Editor.Core.Lsp.LspUri`（`Normalize` / `TryToLocalPath` /
`MatchesPath` / `FromPath` / `Comparer`）を追加し、

- **Editor**：`LspClient` がサーバーから受け取る URI を**プロトコル境界で** `Normalize`
  （定義・参照・シンボル検索・階層・publishDiagnostics・workspace edit のキー、`LspWorkspaceDiagnosticParser`）。
  消費側（`VimEditorControl` の現在バッファ照合・参照一覧、`LspViewBridge` の定義ジャンプ）も `LspUri` 経由に統一。
- **Loomo**：`LspDocumentTable`（`Find` / `OnDiagnostics` は引く前に `Normalize`）、
  `LspWorkspaceService.ClientForUri`、`CodeEditorSupport.TryUriToLocalPath`、`ProblemsViewModel`、
  そして workspace edit の対象解決を `LspWorkspaceEditPaths.ResolveInWorkspace`（ShellWindow から切り出し・テスト可能）へ。

**不変条件**：サーバー由来の URI は `new Uri(uri).LocalPath` で直接パスにしない。必ず `LspUri` を通す。
URI をキーにする辞書は `LspUri.Comparer`（大文字小文字無視。ドライブ文字の大小がサーバーで揺れる）。

回帰テスト：`LspUriTests`（Editor.Core）、`LspProtocolTests` の workspace edit キー正規化（Editor.Controls）、
`LspWorkspaceEditPathsTests` / `LspWorkspaceServiceTests.Diagnostics_ReachTheDocumentEvenWhenTheServerEncodesTheDriveColon`
/ `CodeEditorSupportTests` / `ProblemsViewModelTests`（Loomo）。
実機（typescript-language-server 5.3.0 + Mindoodle/frontend）でも rename が編集中バッファに当たることを確認済み。
必要パッケージ：`sk0ya.Editor.Controls` **1.0.74**。

---

## §30.15 サーバー起動は PATH 解決後のフルパスで（2026-08-03 修正）

**症状**：「言語サーバーへの接続待ちです」から一向に進まない。言語サーバーのプロセスは1つも立っていない。

**原因**：**「導入済み」の判定と、実際の起動でパス解決が食い違っていた。**

- 判定（`ExecutableResolver.IsOnPath`）は PATHEXT を総当たりするので、npm のグローバル導入
  （`typescript-language-server.cmd`。`.exe` は無い）を「導入済み」と正しく認識する。
- 起動（`LspClientPool` → `new LspClient(def.Executable, …)` → `Process.Start`）は**素の名前**を渡していた。
  `UseShellExecute=false` の `CreateProcess` は拡張子なしの名前に `.exe` しか補完しないため、
  `.cmd`/`.bat` シムは **`Win32Exception`（指定されたファイルが見つかりません）で起動できない**。

その結果 `LspClientPool.Create` が null を返し、`LspDocumentTable.Open` も null。エディタは LSP 文書を
持てないので、案内は「導入済みだが接続待ち」（`LspNoticeModel.Build(prompt: null)`）のまま止まる。
**失敗はログに落ちるだけで UI には出ない**ので、待てば繋がるように見えるのが最悪だった。
（拡張子→サーバーの割り当てにフルパスの上書きが入っていた間は動いていたため、
上書きを消した時点＝組み込みの素の名前に戻った時点から再現するようになったと見られる。）

**対処**：
- `ExecutableResolver.Resolve(executable)` を追加（`IsOnPath` はこれに委譲）。**判定と起動が同じ解決を通る。**
- `LspClientPool` の既定 connect は `ExecutableResolver.Resolve(def.Executable) ?? def.Executable` で起動する。
  フルパスなら `CreateProcess` が `.cmd`/`.bat` をコマンドインタプリタ経由で起動できる。
- 起動失敗を握り潰さない：`_startFailures` に理由を残し、`Statuses` が
  `LspServerRuntimeState.Failed` として返す（設定画面の言語サーバー行に出る）。

回帰テスト：`ExecutableResolverTests`（`.cmd` シム／`.exe` 補完／拡張子つき指定／絶対パス／未導入）、
`LspWorkspaceServiceTests.FailedProcessStart_IsVisibleAsAFailedServerInsteadOfSilence`。
実機確認：`%APPDATA%\npm\typescript-language-server.cmd` を解決先として起動 → initialize 成功・
documentSymbol 5件（＝アウトラインが出る状態）。

---

## §30.16 「黙って繋がらない」を潰す（2026-08-03 修正）

§30.14 / §30.15 と同じ日の実測で残っていた4件。共通しているのは
**「繋がらないのに、繋がらない理由がどこにも出ない」**こと。

### 30.16.1 C# は既定のままでは絶対に繋がらなかった（カタログの自己矛盾）

カタログの C# 実行ファイルは `%USERPROFILE%\.dotnet\tools\.store\…\Microsoft.CodeAnalysis.LanguageServer.exe`
という**ツールストア深部のフルパス**だった。ところがこの綴りは PATH 上に無く、導入判定
（`ExecutableResolver.IsOnPath`）が常に「未導入」を返す。しかも同じカタログのインストールコマンドが入れる
`dotnet tool install --global roslyn-language-server` が作るのは
`%USERPROFILE%\.dotnet\tools\roslyn-language-server.cmd`（このディレクトリは PATH 上）＝**別の綴り**。
つまり**案内どおり入れても「未導入」のまま**で、既定の状態から C# が繋がることはなかった。

対処：既定の実行ファイルを**グローバルツールのシム名** `roslyn-language-server` にした。シムは `%*` を
実体へそのまま渡すので `RoslynArgs`（`--stdio --autoLoadProjects --telemetryLevel off`）はそのまま効き、
起動は §30.15 の `ExecutableResolver.Resolve` が `.cmd` をフルパスへ解決する。版を含むフルパスを既定に
持つこと自体が誤りだった（ツールを更新した時点で存在しないパスになる）。

既存利用者への影響：`.cs` の**上書き**は元々「組み込みと違う綴り」だけが残る仕組みなので、今回の変更で
壊れる設定は無い。念のため旧組み込みの綴り（ストア深部フルパス）も `IsSupersededCSharpServer` の対象に加え、
上書きとして残っていたら捨てて組み込みへ戻す。シム名 `roslyn-language-server` の上書きも従来どおり畳む
（**今はそれ自体が組み込み**なので抱える意味が無く、`Args` の欠けた古い綴りだけが残る）。

### 30.16.2 失敗の理由がどこにも出ない

`LspNoticeModel.Build` は「未導入 / 接続待ち」の2状態しか持たず、**プロセスが起動できなくても
initialize に失敗しても「言語サーバーへの接続待ちです」**と出続けていた（待てば繋がるように見える）。

対処：状態の出所を分けた。促し（未導入／未設定）は対応表と PATH しか見ないので**起動の失敗は判らない**。
失敗は LSP セッション側にしか無い（`LspWorkspaceService.ServerStatuses` ＝ §30.15 で `Failed` を残すようにした
`LspClientPool.Statuses`）。そこで

- `LspNoticeModel.FindFailure(statuses, ext, executable, displayName)`（純ロジック）で
  「この拡張子の担当サーバーが `Failed` か」を判定し、`LspNoticeModel.Build(prompt, failure)` が
  **失敗した事実＋理由（`LastError`）＋「LSP 設定を開く」**を返す（`Notice.IsFailure`）。
- 拡張子→担当サーバーの解決は `LspManagementService.ResolveServerFor(ext)` に置いた（対応表の所有者は1つ）。
- Shell 側は既存の1点（`EvaluateLspPrompt` の隣＝`ShellWindow.Tabs.cs`）に `EvaluateLspFailure` を足し、
  **促しが無いときだけ**引く。促しがあるならそちらが具体的（インストール導線つき）なので優先。
  失敗は待っても解消しないので、待機文言の猶予（`CodeConnectingNoticeGraceTicks`）を待たずに出す。

促しバー（`LspPromptViewModel`）と「今後表示しない」フィルタの経路は変えていない（§30.12 のまま）。
**再起動ボタンは案内に置かなかった**：起動そのものに失敗した場合はプールにクライアントが居らず
`LspClientPool.Restart` が false を返す（＝押しても何も起きない）ため、既存の設定画面の行（状況バッジ＋
再試行）へ誘導する形にした。`ShellWindow` の `_lspWorkspace` は、この実行時状態がエディタへ渡す
`ILspWorkspace` に載っていない Loomo 側の概念なので、実装型（`LspWorkspaceService`）で受けている。

### 30.16.3 リセットが取り返しのつかない操作だった

`LspServerTable.Reset` / `Remove` は `%APPDATA%/Loomo/lsp-servers.json` の上書きを消すだけで、
直前の内容がどこにも残らなかった（これで「動いていた割り当て」を失う事故が起きた）。

対処：`Save()` が書き込む**直前に**現行ファイルを `lsp-servers.backup.json`（`BackupPathFor`）へ複製する。
1世代のみ・複製失敗は無視（保険であって前提条件ではない）。世代を積む仕掛けは設定1つに対して過剰なので作らない。

### 30.16.4 促し対象なのにカタログ候補が無い拡張子

`PromptableSourceExtensions` にあるのにカタログに候補が無いと、案内は「設定で追加できます」しか出せない。
実測で穴だったもののうち、根拠の取れたものだけ足した：

- `.mts` / `.cts`（languageId `typescript`）、`.mjs` / `.cjs`（`javascript`）を
  typescript-language-server のターゲットへ追加。根拠は VS Code 組み込みの言語定義
  （`extensions/typescript-basics`＝`.ts/.cts/.mts`、`extensions/javascript`＝`.js/.mjs/.cjs`）。
  どちらも同じ tsserver が受ける。
- `.svelte` → `svelteserver`（npm `svelte-language-server`、`--stdio`）を新規追加。
- 併せて `CodeEditorSupport.CodeExtensions`（EditorSupport のコードアウトライン対象）にも
  `.mts/.cts/.mjs/.cjs` を足した。サーバーを割り当てても構造アウトラインに来ないと片手落ちになるため。
- **`.vue` は足さなかった**：公式の `@vue/language-server` は `initializationOptions`
  （`typescript.tsdk` のパス、`vue.hybridMode`）が要り、`LspServerDef`（実行ファイル＋引数＋languageId）では
  表現できない。入れれば「起動はするが動かない」になるので、`initializationOptions` を持てるように
  なるまで保留（それ自体が別の設計判断）。

---

## §30.17 使用箇所ポップアップは呼び出したビューに紐づける（2026-08-03 修正）

**症状**：「使用箇所」（Find References）の一覧が、エディタ分割や検出元の位置と無関係な場所に、
読んでいるコードを覆う形で出る。

**原因**：`ReferencesPopup` が `PlacementTarget=PaneHost` ＋ `Placement=Center`、つまり
**ペイン領域全体の中央**固定だった。結果の出どころ（イベントを発火したエディタビュー）と
表示位置に関係が無く、分割時は操作していない側の上に出る。

**対処**：配置を純ロジック `ReferencesPopupPlacement`（`Place` / `OffsetFrom`）へ切り出し、
`ShellWindow.References.cs` の `PlaceReferencesPopup` が

- 基準を **`FindReferencesResult` の `sender`**（＝いま操作しているエディタビュー。分割・切り離しを
  問わず正しい。`_activeEditorTab` より正確）に取り、`PlacementMode.Relative` ＋ オフセットで置く。
- 置き方は「そのビューの**下端に沿わせる**（左端揃え）」。一覧はクリックしてジャンプするための
  一時表示なので、キャレット周辺を覆いにくい下側へ寄せる。ビューより背が高ければ上端から。
- ウィンドウのクライアント領域でクランプし、画面外・ウィンドウ外に出さない。
  ポップアップ自体がウィンドウより大きいときは左上を優先する。
- 実寸は開く前に `Popup.Child` を測って求める（件数で高さが変わるため）。

**用途による出し分け**：同じポップアップはワークスペース診断（`TitlePrefix == "DIAGNOSTICS"`）や
呼び出し階層でも使う。これらも「いま見ているビュー」を基準にするのが自然なので**同じ扱い**にし、
基準ビューが取れないとき（エディタを1枚も開いていない診断など）だけ `PaneHost` を基準にした
——ただし中央固定には戻さず、同じ計算でペイン領域の下端に出す。テーマ・フォントスケールの
使い方（`{DynamicResource …}` / `UiFontManager.Scaled`）は変更していない。

回帰テスト：`ReferencesPopupPlacementTests`（呼び出したビュー内に収まる／操作していない側の分割を
覆わない／下端寄せ／狭いビューでは上端から／ウィンドウ外へ出ない／相対オフセット換算）。
実機での見た目確認は未実施（アプリ起動中でビルド出力がロックされうるため）。
