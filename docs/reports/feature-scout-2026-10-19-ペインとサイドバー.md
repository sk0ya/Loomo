# 他ツールの新機能からの取り込み候補（2026-10-19・焦点：ペインやサイドバーのタブの新規追加・期間：2026-04〜2026-10）

「新しいペイン種別（`PaneKind`）」「サイドバーの新しい面（`SidebarPanel`）」「既存ペインの中の新しいタブ」の 3 つに絞って見た。
前提として、Loomo には既に 13 ペイン（`WorkspaceStateStore.cs:743-762`）と 9 面（`ShellViewModel.cs:8-25`）がある。
この半年に他ツールで出た「新しい面」の多くは **AI エージェント用の面**（VS Code の Agents ウィンドウ、Zed の Threads サイドバー、
GitKraken の Agent Sessions など）か、Loomo に**既にある面**（Git グラフ、PR、ワークツリー、サブモジュール、表のプレビュー）だった。
そのため推すのは 5 件で、うち新しい面そのものは 2 件（1・4）。残り 3 件は既存の面の中身を増やす案。

> **追記（2026-10-19・実装済み）**: 5 件とも同日に実装した。1 → §24.54、2 → §24.53、3 → §24.56、4 → §28.15、5 → §24.55。
> 4 は GC ヒープ・GC 回数を持たない（EventPipe の参照が要るため見送り、§28.15 に記載）。
> **訂正**: 5 で「ワークツリーだけ消すと部屋だけが取り残される」と書いたのは誤り。Git ペインからの削除は既に
> `WorktreeRemoved` → `RemoveWorktreeRoom` → `WorkspaceRemoved` で部屋と常駐シェルを片付けていた（`ShellWindow.Worktrees.cs:15`）。
> 実装では片付けの窓もこの既存の経路で消している。

## まず手を付けるなら

1. **動いているもの面**：部屋の中で実行中のものを 1 か所にまとめる。周辺視野に一番効く
2. **TABS のターミナル行に要約を出す**：小さく済み、1 の土台にもなる
3. **課題から作業を始める**：Work Items 面と §24.17 ワークツリーと §38 Claude Code をつなぐ

## 推す（効き目の大きい順）

### 1. 動いているもの面——部屋の中で実行中のものを 1 か所に（元：VS Code 1.141・2026-10-07／1.142・2026-10-14、Warp 2026.05.14）
- 出典：https://code.visualstudio.com/updates/v1_141 ・ https://code.visualstudio.com/updates/v1_142 ・ https://docs.warp.dev/terminal/windows/vertical-tabs
- 元の機能：VS Code の Background Shells は、裏で走っているシェルを「コマンド」と「走り始めてからの時間」つきで並べる。
  選ぶと出力を追え、終わったものは一覧から消える。Warp の縦タブの Summary モードは、タブごとに最後のコマンド・cwd・ブランチ・状態の印を出す。
- Loomo でやると：サイドバーの面を 1 つ足す（`SidebarPanel` の末尾に追加。数値で永続化しているのは `PaneKind` のほうなので `SidebarPanel` の順は自由）。
  1 行が 1 つの「動いているもの」で、次を並べる。
  ①ターミナルのタブで前面に居るコマンド（OSC 133 の C〜D の間。経過時間つき。`ShellCommandActivity`）
  ②実行中のタスク（§31.17）
  ③デバッグ・実行のセッション（§28）
  ④Claude Code（§38.8 の一覧と同じ情報。承認待ちを先頭に）
  ⑤言語サーバー（`LspClientPool` の常駐数・再接続中）
  行を押すとそのペインとタブへ移る。停止（Ctrl+C を送る・タスクやセッションを止める）もできる。
  §24.1 の袖のバッジは「どのペインで」を示すが、この面は「部屋全体で何が走っているか」を 1 か所で見せる。
  §24.22 の留守中のまとめは「終わったこと」で、この面は「いま走っていること」なので役目が重ならない。
- いまの Loomo：部分的にある。材料は揃っているが、まとめて見る面が無い。前面のコマンドの判定は
  `ShellWindow.PaneActivity.cs:50`（`IsForegroundBusy`）、Claude Code の一覧はパレットのコマンド `claude.agents`（§38.8）だけ。
  タスク・デバッグ・LSP は、それぞれのペインに入らないと見えない。
- 作業の置き場：Loomo のみ（Terminal 1.0.8 以降の `ShellCommandActivity` で足りる）
- 規模：M（集約する ViewModel と面の View、停止の配線を種類ごとに作る）
- 関連：§24.1、§24.1.1、§24.22、§31.17、§38.8

### 2. TABS のターミナル行に要約を出す——cwd・ブランチ・最後のコマンドと終了コード（元：Warp 2026.05.14（v0.2026.05.13.09.15）・2026.06.24）
- 出典：https://docs.warp.dev/terminal/windows/vertical-tabs ・ https://docs.warp.dev/changelog/2026/
- 元の機能：縦タブの Summary モード（2026-05-14）では、各タブに「最後のコマンド・cwd・チェックアウト中のブランチ・状態の印」を
  最大 3 行で出す。2026-06-24 の版から、PR の印も押せるようになった。
- Loomo でやると：サイドバーの TABS の、ターミナルの行の 2 行目に「`~/repo (main)` · `dotnet test` ✗1 · 3分前」のように出す。
  実行中なら経過時間を出す。同じシェルの見出しが並んでいてもタブを見分けられるようになり、一覧を見ただけで
  「どのタブで何を終えたか」が分かる。cwd は §34 の常駐セッションの `WorkingDirectory`、最後のコマンドは §24.20 の組み立て
  （`RecentTerminalCommands.cs:121` の E→C→D）から取れる。
- いまの Loomo：部分的にある。TABS の行が持っているのは見出しと名前だけ（`TabsViewModel.cs:44` の `Title`、`:91` の `CustomName`）。
  cwd は保存されている（`WorkspaceStateStore.cs:996` `TerminalTabSnapshot.WorkingDirectory`）が、TABS には出ていない。
- 作業の置き場：Loomo のみ
- 規模：S〜M（行のテンプレートと、タブごとの最後のコマンドの保持。ブランチはタブの cwd ごとに引くので、間引きが要る）
- 関連：§24.20、§34、TABS（§24.29 の棚と同じ面）

### 3. 課題から作業を始める——Work Item／Issue からブランチ＋ワークツリー＋部屋（元：GitKraken Desktop 12.4.0・2026-08-05）
- 出典：https://help.gitkraken.com/release-notes/current/ （機能の中身。版の日付は ComponentSource の版一覧 https://www.componentsource.com/product/gitkraken で照合）
- 元の機能：Launchpad と左パネルの Issue の右クリックから「エージェントのセッションを始める」を選ぶと、確認画面でブランチ名と
  エージェントを決め、新しいブランチ → ワークツリー → エージェントの起動までを一度に行う。
- Loomo でやると：Work Items 面（`WorkItemsView.xaml:222-243` の行の右クリック）と GitHub の Issue の面（§24.27）に
  「この課題で作業を始める…」を足す。`WorktreeCreateDialog` に、課題から作ったブランチ名（例 `feature/AB1234-タイトル`）を入れて開く。
  作ったワークツリーは `WorktreeRoomSeed` で部屋として開き、§24.48 で Git に載らないものを引き継ぐ。
  希望すればターミナルで `claude` を課題の文面つきで起動する（文面は既にある「指示用にコピー（AB#番号 タイトル）」と同じ形）。
  「課題 → 作業する部屋」が 1 回の操作でつながり、§37 のハーネスペインで Run を課題に結びつけるのも、名前の規約から自然に揃う。
- いまの Loomo：未実装。Work Items 面の右クリックは「ブラウザで開く／Loomo で読む・差分・ワークツリー（PR 用）／各種コピー」だけ。
  Issue からブランチやワークツリーを作る道は無い。
- 作業の置き場：Loomo のみ
- 規模：M（ブランチ名の組み立て規則・ダイアログの初期値・部屋を開いてからの Claude Code 起動）
- 関連：§24.17、§24.17.1、§24.27、§24.27.1、§24.48、§37.3.2、§38

### 4. 実行中アプリのモニター——Debug ペインに「モニター」タブ（元：Rider 2026.2.3・2026-09-28 の Monitoring ツールウィンドウ）
- 出典：https://blog.jetbrains.com/dotnet/2026/09/28/rd-2026-2-3/ （公式ページは 403 だったので、同じブログの RSS の本文で読んだ）
- 元の機能：Monitoring は、アプリを実行するたびに実行時のデータを自動で集め、Performance タブにグラフで出す。
  2026.2.3 で、そのデータから AI にボトルネックを調べさせる操作が加わった（AI の部分は取り込まない）。
- Loomo でやると：Debug ペインのタブ（`DebugView.xaml:109-432`。いまは 実行／問題／デバッグ／変数／自動／コールスタック／スレッド／
  ブレークポイント／イミディエイト／モジュール／テスト／構成）に「モニター」を足す。実行中・デバッグ中のプロセスの
  CPU%・ワーキングセット・スレッド数を時系列で描き、.NET なら GC ヒープと GC 回数も出す。
  ブレークポイントで止まった時刻・例外の時刻に印を付ける。メモリが伸び続けたら袖のバッジ（§24.1）で知らせる。
  作業しながら、動かしているアプリの様子を目の端で見ていられる。
- いまの Loomo：未実装（プロセスのカウンタを読むコードは補完ホストの Job Object 以外に無い。`dotnet-counters`・EventPipe の参照も無い）。
- 作業の置き場：Loomo のみ。CPU とメモリは `System.Diagnostics.Process` で取れる。GC まで出すなら
  `Microsoft.Diagnostics.NETCore.Client`（EventPipe）の参照を足す。起動した側の PID は DAP の `process` イベントで受け取る必要がある
  （今の `NetcoredbgDebugService` はアタッチの PID しか持っていない）。
- 規模：M（PID の受け取り・サンプリング・グラフ。.NET Framework（§28.12 の NetFxDebug）は CPU とメモリだけにする）
- 関連：§28、§28.12、§24.1

### 5. ワークツリーの片付け——WT タブに大きさ・最後に触った日・マージ済みを出す（元：VS Code 1.141・2026-10-07）
- 出典：https://code.visualstudio.com/updates/v1_141
- 元の機能：Worktree Cleanup エディタは、使われていないワークツリーを、使われていない期間で絞って大きさつきで並べ、選んで消せる。
  PR がマージ済みのものは自動で消す設定や、容量が増えたら片付けを勧める設定もある。
- Loomo でやると：Git ペインの WT タブ（§24.17）の行に「ディスク上の大きさ・最後の更新日（フォルダーとワークスペースの最終使用日）・
  ブランチが既定ブランチにマージ済みか」を出し、「片付ける候補」で絞る。消すときは、そのワークツリーの**部屋**も片付ける。
  具体的には、ワークスペース一覧の項目と、§34 の常駐ホストに残っているそのワークツリーのシェルを、ワークツリーと一緒に消す。
  Loomo はワークツリーごとに部屋を開く（`WorktreeRoomSeed`）ので、ワークツリーだけ消すと部屋だけが取り残される。そこを揃える意味が大きい。
- いまの Loomo：部分的にある。一覧・削除・`prune`（`GitSessionViewModel.Worktrees.cs:198`）はある。
  ただ `GitWorktreeInfo` が持っているのは、パス・HEAD・ブランチ・ロック・prune 可否・未コミット件数まで（`GitWorktreeModels.cs:10-36`）。
  大きさ・最終使用日・マージ済みは持っていない。
- 作業の置き場：Loomo のみ
- 規模：S〜M（大きさは裏で数える。マージ済みは `branch --merged`。部屋とシェルの連動削除）
- 関連：§24.17、§24.17.1、§24.48、§34

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| Agents ウィンドウ（セッション一覧・グリッド・サブエージェントの記録・Quick Chat） | VS Code 1.120（2026-05-13）〜1.141（2026-10-07） | AI が主役。Claude Code を部屋の住人として見る面は §38.8 にある |
| Canvases（チャットの横で、エージェントと一緒にカンバン等を編集する面） | VS Code 1.142（2026-10-14） | AI が主役。TODO の面（`SidebarPanel.Todo`）・Work Items 面がある |
| Prompt Timeline（プロンプトごとの点を並べて飛べる） | VS Code 1.134（2026-08-19） | AI の会話の道具。人の移動の軌跡は §27 の役目 |
| エージェント向けコメントを付けられる Markdown エディタ | VS Code 1.131（2026-07-29） | AI が主役 |
| 統合ブラウザのデバイスエミュレーション（画面サイズ・タッチ） | VS Code 1.124（2026-05-28） | ブラウザペインの道具の品質で、面の追加ではない（焦点外） |
| 統合ブラウザのサイトごとの権限 | VS Code 1.127（2026-07-01） | 同上 |
| Threads サイドバー・Terminal Threads | Zed 1.3.5（2026-05-20）・1.20.1（2026-09-16） | AI が主役 |
| タブグループ（名前付きで畳める束） | Warp 2026.07.23 | 束ねる単位は Loomo ではワークスペース（部屋）そのもの。TABS は種類とドメインで既にまとまり（`TabsView.xaml:309-367`）、棚（§24.29）もある |
| Breakpoints ウィンドウからデータブレークポイントを足す | Rider 2026.2.3（2026-09-28） | デバッガの道具の品質。netcoredbg が `dataBreakpoints` に対応していない |
| Podman のコンテナを「プロセスにアタッチ」に出す | Visual Studio 2026 18.10（2026-09-08） | コンテナの面を持つ話になり、部屋への効き目が小さい |
| Ad Scripts（Application パネルの下の面） | Chrome DevTools（Chrome 153／154、2026-10） | 部屋に効かない |
| Projects・Automations・Recently Deleted | Raycast 2.3（2026-09-11） | AI の会話の整理 |
| Bases のカンバン表示 | Obsidian 1.14.0（2026-09-02） | 前回（`feature-scout-2026-10-18-残りの製品.md`）見送り済み |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| Git のサブモジュールの面（一覧・初期化・同期） | Visual Studio 2026 18.9（2026-08-11） | Git セッションの参照タブ「サブモジュール」（`GitSessionView.xaml:613-618`・`:808-818`） |
| 複数ファイルの差分を 1 本に並べる | Visual Studio 2026 18.9 | §24.50 |
| 2 つのコミットを比べる | Visual Studio 2026 18.9 | `ComparePointsDialog`（2 点比較） |
| ワークツリーごとのカード（ブランチ・未コミット・ahead/behind・PR・エージェントの状態） | GitKraken 12.0.0（2026-04-14） | §24.17 の WT タブ、§24.17.2（ワークツリーの Claude Code）、§24.39（ブランチの PR）。Claude Code の承認の許可・拒否を Loomo から押す口は無い（AI 側の話なので推さない） |
| Git グラフの面・コミット情報の面 | Zed 1.2.3（2026-05-13） | §24.15・§24.15.1 |
| Staged／Unstaged の変更を 1 本で読む | Zed 1.11.3（2026-07-15）・1.14.2（2026-08-05） | §24.18・§24.50 |
| CSV／TSV の表プレビュー | Zed 1.17.2（2026-08-26） | VGrid の EditorSupport |
| パネルの配置の切り替え（classic／agentic） | Zed 1.1.5（2026-05-06） | 表示モード（`mode.toggle`）と保存レイアウト（`SavedLayout`・`WorkspaceStateStore.cs:826`） |
| 今のウィンドウの並びをレイアウトとして保存 | Raycast 2.2（2026-09-08） | 同上（`stage.cycle` で保存レイアウトを回す） |
| Markdown の表示とソースの切り替え | Warp 2026.10.15 | EditorSupport の Markdown プレビュー |
| ローカルの HTML を統合ブラウザで開く | VS Code 1.121（2026-05-20） | §21.5.1（`https://file.loomo/…`）・§24.45 |
| Markdown の差分をレンダリングで見る | VS Code 1.132（2026-08-05） | §24.10 |
| NuGet のツールウィンドウ | Rider 2026.2（2026-07-22） | `NuGetPackagesWindow`（§36） |
| SQLite のファイルを中身ごと見る | （面としての候補を考えたとき） | `DatabaseEditorSupport`（`.db`/`.sqlite`/`.sqlite3` を読み取り専用で） |
| テストのカバレッジ | （面としての候補を考えたとき） | `EditorCoverageMarkerMapper`・Debug ペインのテストタブ |

## 調べた範囲

- 一次情報を読んだもの：VS Code updates 1.120〜1.142（全 23 版。各版で「新しい view／panel／サイドバー／タブ／エディタの種類／ウィンドウ」を確認）、
  Zed stable releases（1.1.5〜1.24.2、2026-05〜10）、Visual Studio 2026 リリースノート（18.6〜18.11）、
  Warp changelog 2026 と縦タブのドキュメント、GitKraken Desktop release notes（12.0.0〜12.6.0）、
  JetBrains .NET ブログ・IntelliJ IDEA ブログの RSS（Rider 2026.2.1〜2026.2.3、2026.3 EAP、IntelliJ IDEA 2026.2.3・2026.3 EAP の記事本文）、
  Obsidian changelog（1.13.6〜1.14.4）、Raycast for macOS changelog（2.1〜2.8）。
- 検索結果の要約だけのもの：Chrome DevTools の 2026-10 の記事、Vivaldi 8.0（2026-05-21）、PowerToys 0.97〜0.101、Windows Terminal 1.25／1.26。
  後の 2 つは前回のレポートで一次情報を読み済み。GitKraken 12.4.0 の日付は ComponentSource の版一覧で照合した。
- 一次情報を読めなかったもの：Rider 2026.2 と IntelliJ IDEA 2026.2 の What's New（公式ページはスクリプトで描かれ、ブログは 403）、
  Rider の Monitoring ツールウィンドウのヘルプ（URL が見つからず 404）。
- 見ていないもの：Arc（開発停止）、Cursor／Windsurf（AI が主役）、Notion（クラウド前提）、Edge のサイドバー。
- 前回までの 4 本（`feature-scout-2026-10-18*.md`）で推した・見送った・既にあった項目は、重ねて挙げていない。
