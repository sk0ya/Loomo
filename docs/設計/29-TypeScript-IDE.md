# §29 TypeScript IDE（TS IDE ペイン）

> 作成: 2026-07-23。ワークスペースに TypeScript / Node.js プロジェクトがあるときだけ現れる、
> dotnet 用 IDE ペイン（§28）と対をなす独立ペイン。デバッガは **vscode-js-debug**（DAP）。

## 29.1 目的と位置づけ

- §28 の IDE ペイン（netcoredbg / dotnet）はそのまま残し、**別ペイン `PaneKind.TsIde`** として追加する
  （`PaneKind` は数値永続のため**末尾追加**）。ゲートは `TsDebugTargetResolver.HasTypeScriptProject`
  （tsconfig.json / package.json を深さ 3・node_modules 等スキップで探す。§28 の `HasCSharpProject` と同じ流儀）。
- 両ペインは共存できる（C# と TS の混在リポジトリでは両トグルが出る）。エディタ連携（ガター・DataTip・実行行・
  右クリック実行系メニュー・問題ジャンプ）は**拡張子ルーティング**（`ShellWindow.Debug.cs` の `ManagerForPath`：
  `.ts/.tsx/.js/.jsx/.mjs/.cjs` → TS IDE、それ以外 → dotnet IDE）。同一ファイルが両方に属することはないので
  ブレークポイントストアは混線しない。「全エディタ」操作（実行行クリア・DataTip 有効化）は発生源マネージャの
  管轄拡張子のエディタに限定し、両デバッガ同時実行でも潰し合わない。

## 29.2 リファクタ（挙動不変・§28 との共通化）

| 抽出元 | 抽出先 | 内容 |
|---|---|---|
| `DapProtocolClient` | `DapConnection`（Services\Debug） | Content-Length フレーミング/seq 突合/イベント/reverse request を **Stream ベース**に。stdio（netcoredbg）は薄ラッパで従来 API 不変。`ReverseRequestHandler` 未設定なら従来どおり無条件 success 応答 |
| `DebugViewModel` | `DebugManagerViewModelBase` | セッション管理（Sessions/ActiveSession 切替配線）・出力フォールバック・エディタ連携イベント・Problems/Breakpoints・IDebugSession 実装。公開プロパティ名は不変（XAML 無改修） |
| `DebugProfilesViewModel` | （パラメータ化） | 起動 VM 結合を `ILaunchConfigurationOwner`（7 プロパティ）へ、プロジェクト探索を ctor デリゲートへ |
| `ProblemsViewModel` | （引数追加） | `SetFromBuildOutput(output, baseDir)`：tsc の cwd 相対パスを絶対化（dotnet 経路は null で不変） |

`NetcoredbgDebugService` は**無改修**（configurationDone 順序保証・dll ロック teardown が壊れやすいため）。
JSON パースは新 `DapModelParser`（純関数）に置き、JS 側だけが使う。

## 29.3 js-debug 連携（`Loomo.Services/Debug/Js/`）

vscode-js-debug は npm 未公開。GitHub Releases の `js-debug-dap-v*.tar.gz` を
`%APPDATA%/Loomo/debug-adapters/js-debug/` へ展開して使う（`JsDebugAdapterLocator`）。
導入判定は **PATH 上の node ＋ dapDebugServer.js の存在**の 2 段。インストールコマンド
（GitHub API で latest → Invoke-WebRequest → tar -xzf）は LSP と同じく**見えるターミナル**で実行する。
未導入バーは node 不在（手動導入案内）と js-debug 不在（ワンクリック）で文言を分ける。

### 接続モデル（netcoredbg との最大の違い）

1. `JsDebugServerProcess`：`node dapDebugServer.js 0 127.0.0.1` を起動（**ポート 0 で OS 割り当て**、
   stdout の listening 行から実ポートをパース。競合レースなし）。1 セッション 1 プロセス。
2. 親接続（TCP）：initialize → launch/attach（`type:"pwa-node"`）。launch は **await しない**
   （応答は configurationDone の後。§28 と同じ流儀）。
3. js-debug が親へ reverse request **`startDebugging`**（`__pendingTargetId` 入り configuration）を送る →
   **同一ポートへ 2 本目の TCP 接続**（子セッション）→ initialize → configuration をそのまま launch/attach →
   子の initialized で記憶済み BP＋例外フィルタ＋configurationDone。
4. **停止・スレッド・変数・BP の実体は子セッション側**。対象向けリクエストは子があれば子へ、まだなら親へ
   （`ActiveConn`）。setBreakpoints は親子両方へ。出力は親子両方から。terminated/接続断はどちら由来でも終了処理へ。
5. 終了：親へ disconnect（terminateDebuggee=launch のみ true）→ 全接続とサーバプロセスを破棄。
   **js-debug は terminated 後の disconnect に応答しない**ので 2 秒タイムアウトで破棄続行（実測）。

制限（v1）：2 個目以降の `startDebugging`（cluster / child_process）は受理応答＋コンソール告知のみ。
js-debug は modules リクエスト非対応（モジュールタブ無し）、gotoTargets 非対応（次のステートメント設定無し）。
実機検証は `JsDebugServiceIntegrationTests`（launch→BP 停止→検査→評価→続行→出力→終了の全シーケンス。
node/js-debug 未導入環境では検証せずパス）。

### 起動構成

- **2 モード**：プログラム（.ts/.js。TS 直接実行は Node 23.6+ の型ストリッピング依存）と **npm スクリプト**
  （`runtimeExecutable:"npm"`）。プロファイル（`DebugLaunchProfile.TargetProgram`）には `TsLaunchTarget` の
  **`npm:スクリプト名`** エンコードで格納（レコード形は dotnet と共用、保存先は別ファイル
  `%APPDATA%/Loomo/tsLaunchProfiles.json`）。
- `JustMyCode` → `skipFiles:["<node_internals>/**"]`。例外フィルタは js-debug の `all`/`uncaught`。
- アタッチは**ポート指定**（`node --inspect` 既定 9229。`DebugAttachConfig.Port` を追加）。プロセス列挙は Phase 2。
- `BuildFirst` は「開始前に tsc 型チェック」（エラーで起動中止。tsconfig 無しなら何もしない）。

## 29.4 VM / ビュー（`Loomo.App`）

- `TsDebugViewModel`（`DebugManagerViewModelBase` 派生）＝ファサード。`TsDebugLaunchViewModel`
  （2 モード・型チェック・ステップ系・`ILaunchConfigurationOwner`）、`TsDebugAttachViewModel`（ポート）、
  `DebugProfilesViewModel`（探索デリゲート＝`TsProjectDiscovery.Discover`）。`FindBuildTarget()` は
  tsconfig ディレクトリ。DI は `JsDebugSessionFactory` を**具象登録**（`IDebugSessionFactory` の既定は
  netcoredbg のまま）。`ShellViewModel.TsIde`。
- **問題タブ**：型チェック（ヘッダの「✔ 型チェック」/開始前チェック）が
  `Set-Location <tsconfigのdir>; npx tsc --noEmit --pretty false` を実行し `ReportBuildOutput(output, dir)` へ。
  §28 と同じ `ProblemsViewModel`/`DebugProblemsView` を第 2 インスタンスで使う（MSBuild 正規表現が tsc の
  `path(line,col): error TS1234: msg` にそのままマッチ）。
- `TsDebugView`：§28 `DebugView` のクローンで 9 タブ（構成0/出力1/問題2/変数3/自動4/コールスタック5/
  スレッド6/ブレークポイント7/イミディエイト8）。テストタブは Phase 2。サブビュー
  （変数/問題/ブレークポイント/イミディエイト）は同型 VM のため**そのまま流用**。
  `TsDebugConfigView`：未導入バー・プロファイル管理・パッケージコンボ・モードラジオ・npm スクリプトコンボ・
  例外チェック・ポートアタッチ。
- ペイン配管（新 PaneKind の接点）：`_paneElements`／`TsIdePane`（DebugPane クローン、ツールバーは
  ビルドの代わりに型チェック）／`PaneLabel`・`TrailLogic`「TS IDE」／`PaneIcon.TsIde`（六角形＋虫）＋
  `TsIdePaneToggle`／`StageOrder`／`FocusPane`／ステージ復元・`LoadEnabledSessions`・スナップショット復元の
  非適用時除去（`_tsIdePaneApplicable`、`StageModeCoordinator.TsIdePaneApplicable`）。

## 29.5 Phase 2（今回やらない）

vitest/jest テストタブ（`DebugTestsViewModel` の TS 版）・ブラウザ（pwa-chrome）デバッグ・
Node プロセス列挙アタッチ・アタッチポート永続化・多重子セッション（cluster）・
`AutosExtractor` の TS キーワード最適化（v1 は C# 版流用——キーワードがほぼ重なるため実害小）。
