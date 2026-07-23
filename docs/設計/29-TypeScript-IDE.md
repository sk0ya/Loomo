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

- **日常導線は「スクリプト」タブ（npm スクリプト一覧）**：検出した package.json の scripts を
  名前＋実体コマンドで一覧表示し、行の ▶ かダブルクリックで即デバッグ実行（`RunScriptCommand` が
  `TargetProgram` を `npm:名前` に切り替えてから開始 → プロファイルにも保存され、ヘッダの「▶ 開始」は
  **同じスクリプトの再実行**になる）。パッケージコンボは package.json が複数のときだけ表示（構成タブと
  選択共有）。dotnet 風の 3 モード編集・プロファイル管理は「構成」タブ（末尾）に残す上級者向け。
- **実行対象が未設定なら npm モードが既定**：検出スクリプトから `dev` → `start` → 先頭の優先順で
  自動適用（`ApplyNpmDefaultIfEmpty` / `TsProjectDiscovery.PickDefaultScript`。ワークスペース切替・
  プロファイル切替でも再評価）。
- **3 モード**：プログラム（.ts/.js。TS 直接実行は Node 23.6+ の型ストリッピング依存）、**npm スクリプト**
  （`runtimeExecutable:"npm"`）、**ブラウザ**（`chrome:URL` → `type:"pwa-chrome"` launch、webRoot=パッケージ
  ディレクトリ。開発サーバーは別途起動しておく前提）。プロファイル（`DebugLaunchProfile.TargetProgram`）には
  `TsLaunchTarget` の **`npm:スクリプト名` / `chrome:URL`** エンコードで格納（レコード形は dotnet と共用、
  保存先は別ファイル `%APPDATA%/Loomo/tsLaunchProfiles.json`）。
- `JustMyCode` → `skipFiles:["<node_internals>/**"]`。例外フィルタは js-debug の `all`/`uncaught`。
- アタッチは**ポート指定**（`node --inspect` 既定 9229。`DebugAttachConfig.Port` を追加）＋
  **node プロセス列挙**（`TsNodeProcessEnumerator`：WMI `Get-CimInstance Win32_Process` を非表示 pwsh で叩き、
  コマンドラインの `--inspect(-brk)?(=host:port)?` からポートを推定。一覧選択でポート欄へ反映）。
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
- `TsDebugView`：§28 `DebugView` のクローンで 11 タブ（スクリプト0/出力1/問題2/変数3/自動4/コールスタック5/
  テスト6/スレッド7/ブレークポイント8/イミディエイト9/構成10。出力1・変数3・テスト6 の自動切替インデックスは
  dotnet 版と共通のまま）。サブビュー
  （変数/問題/テスト/ブレークポイント/イミディエイト）は同型 VM のため**そのまま流用**。
  スクリプトタブ（インライン）：パッケージコンボ（複数時のみ）＋ `Launch.ScriptItems`（`TsScriptEntry`
  名前＋コマンド）の一覧、▶/ダブルクリックで `RunScriptCommand`。
  `TsDebugConfigView`：未導入バー・プロファイル管理・パッケージコンボ・3 モードラジオ・npm スクリプトコンボ・
  URL 欄・例外チェック・node プロセス一覧＋ポートアタッチ。
- **テストタブ（vitest / jest）**：`TsTestDiscovery` が `*.test.ts`/`*.spec.ts` 系から
  `describe`/`it`/`test` の第 1 引数を波かっこ深度近似で拾い（入れ子 describe は「a &gt; b &gt; タイトル」に連結、
  行番号付き＝ダブルクリックでジャンプ）、`TsTestRunner` が package.json からランナー判定
  （devDependencies → scripts.test）して `npx vitest run --reporter=json` / `npx jest --json` を実行、
  **jest 互換 JSON を 1 本のパーサ**で反映（実 vitest で形状検証済み）。グループ＝ファイル、全実行は
  パッケージごと、ファイル/1 件実行は `-t 葉タイトル`（PowerShell シングルクォート）。VM は
  `TsDebugTestsViewModel`（dotnet 版と**同名メンバー**にして `DebugTestsView` を共有、コードビハインドの
  結合は `ITestExplorer` で吸収）。テストソース監視（`TestSourceWatcher` のパターン/無視ディレクトリを
  パラメータ化）で自動再収集。
- **自動変数（Autos）**：`AutosExtractor` に TS/JS キーワード集合を追加（`console` 等のノイズ除外・
  テンプレートリテラルの空白化）。停止フレームの拡張子で言語を自動選択。
- ペイン配管（新 PaneKind の接点）：`_paneElements`／`TsIdePane`（DebugPane クローン、ツールバーは
  ビルドの代わりに型チェック）／`PaneLabel`・`TrailLogic`「TS IDE」／`PaneIcon.TsIde`（六角形＋虫）＋
  `TsIdePaneToggle`／`StageOrder`／`FocusPane`／ステージ復元・`LoadEnabledSessions`・スナップショット復元の
  非適用時除去（`_tsIdePaneApplicable`、`StageModeCoordinator.TsIdePaneApplicable`）。

## 29.5 Phase 2 の実施状況（2026-07-23）

**実装済み**：vitest/jest テストタブ・ブラウザ（pwa-chrome）デバッグ・Node プロセス列挙アタッチ・
`AutosExtractor` の TS キーワード対応（いずれも 29.3〜29.4 に記載）。

**意図的に見送り**：
- **多重子セッション（cluster / child_process）**：2 個目以降の `startDebugging` は受理応答＋コンソール告知のみ。
  複数子接続のスレッド/停止イベントの統合は複雑さに見合わない（典型的な TS 開発では単一プロセス）。
  必要になったら「子ごとに別 `DebugSessionViewModel`」方向で設計し直す。
- **アタッチポートの永続化**：プロセス列挙（ポート自動検出）で手入力がほぼ不要になったため保留。
