# 他ツールの新機能からの取り込み候補（2026-10-18・焦点：前の 3 本で読めなかった製品・期間：2026-04-18〜2026-10-18）

同じ日の 3 本（`feature-scout-2026-10-18.md`＝直近 6 か月、`-未調査製品.md`＝その補足、`-2年.md`＝2 年分）の
「調べた範囲」で「読んでいない」「要約しか読めなかった」とした製品を読んだ 4 本目。前の 3 本に出た候補は繰り返さない。
物差しは同じ（主役は人間の部屋。§19.9 の「やらない」とクラウド必須の機能は外す）。「いまの Loomo」はコードを grep して確かめた結果。
リリース日は GitHub API（`published_at`）か公式ページの日付で照合した。

> 前回の未調査製品の 1「手で閉じたタブを開き直す」（エディタ・ブラウザ）は、ターミナルの分（§24.41）だけ実装されていて、
> エディタとブラウザはまだ。`CommandCatalog` にあるのは `terminal.reopenClosed` だけ（`src/Loomo.App/Input/CommandCatalog.cs:164`）。
> 今回の 2 と同じ場所を触るので、一緒にやると安い。

> 2026-10-19 追記：9 件すべてを実装した（§24.44〜§24.52。1・6・8 は Terminal 1.0.46）。

## まず手を付けるなら

| # | 候補 | 置き場 | 規模 | 理由 |
|---|---|---|---|---|
| 1 | ターミナルの通知を通知欄と袖へ | Terminal ＋ Loomo | S〜M | いまは見えているタブの中で 5 秒出て消えるだけ。§21.5c の受け皿は既にある |
| 2 | タブを閉じたら直前に見ていたタブへ | Loomo のみ | S | 「最後に見た時刻」は §24.29 で既に持っている |
| 3 | ブラウザのローカル HTML を保存で追いつかせる | Loomo のみ | S | EditorSupport には既にある（§24.8）。ブラウザペインにだけ無い |
| 4 | ワークツリーに無視されたフォルダーを引き継ぐ | Loomo のみ | S〜M | ワークツリーの部屋（§24.17）を作ってすぐ動かせる |

## 推す（効き目の大きい順）

### 1. ターミナルの通知（OSC 9／OSC 777）を部屋の通知欄と袖に出す（元：Windows Terminal 1.26 Preview・2026-10-02）
- 出典：https://github.com/microsoft/terminal/releases/tag/v1.26.2734.0 （「We now support applications emitting a notification with OSC 777 (Desktop Notification, from urxvt) and additionally support for displaying a notification when a bell is triggered」）
- 元の機能：コマンドが出す通知のエスケープシーケンス（OSC 777）とベルを、OS のデスクトップ通知として出す（既定は切）。
- Loomo でやると：ターミナルのコマンドが出した通知を、§21.5c の通知欄（`NotificationService.Post`）に「出所＝タブ名」「操作＝そのタブへ」付きで載せ、
  袖のバッジ（§24.1）にも印を付ける。Loomo が裏にいればタスクバーを点滅させる（§24.35 のベルと同じ）。見ていないタブで出た通知も、
  5 秒で消えずに後から読める（周辺視野・留守中 §24.22）。ビルドスクリプトや長い処理が終わったときに知らせる CLI がそのまま使える。
- いまの Loomo：**部分的にある**。Terminal パッケージは OSC 9 の本文（`9;4` の進捗以外）を受け取ると
  `AnsiTerminalBuffer.NotificationRequested` を鳴らす（`C:\Projects\Terminal\src\Terminal.Controls\Buffer\AnsiTerminalBuffer.cs:1701-1716`）が、
  受け手は `TerminalTabView` 内部のトーストだけ（`TerminalTabView.xaml.cs:2291-2298`・`ShowToastNotification` は 5 秒で消えるポップアップ）。
  `TerminalTabView` の公開イベントに通知は無く（`TerminalTabView.xaml.cs:87-206`）、Loomo は受け取れない。OSC 777 は解釈していない。
- 作業の置き場：Terminal（`TerminalTabView` に `NotificationRequested` を公開し、ホストが受けたら内部のトーストを出さない口を足す。
  OSC 777 `notify;<題>;<本文>` も同じイベントに流す。NuGet 公開が要る）＋ Loomo（`ShellWindow.PaneActivity.cs` の `HookTerminalActivity` で購読）
- 規模：S〜M（パッケージ側はイベント 1 つと OSC の分岐 1 つ。Loomo 側は既存の通知サービスと袖の優先順に 1 段足す）
- 関連：§21.5c、§24.1、§24.35、§24.22
- 注意：1.26 は Preview。題と本文は外から来る文字列なので、通知欄では 1 行に詰めて制御文字を落とす。

### 2. タブを閉じたら、直前に見ていたタブへ戻る（元：Windows Terminal 1.26 Preview・2026-10-02）
- 出典：https://github.com/microsoft/terminal/releases/tag/v1.26.2734.0 （「When you close a tab, Terminal will now unconditionally jump to the previously most recently used tab」）
- 元の機能：見ていたタブを閉じたとき、隣のタブではなく、その前に見ていたタブを前に出す。
- Loomo でやると：エディタ・ブラウザ・ターミナルのタブを閉じたとき、隣ではなく「最後に見た時刻」がいちばん新しいタブを出す。
  定義へ飛んで読んで閉じたら、飛ぶ前のファイルに戻る——閉じる操作が「戻る」になる（人間のナビゲーション）。
- いまの Loomo：**未実装**。閉じたタブが前に出ていたら、同じ位置の隣を出す（`src/Loomo.App/Services/Layout/PaneTabTransferCoordinator.cs:24-27`、
  `remaining[Math.Min(removedIndex, …)]`）。「最後に見た時刻」は棚（§24.29）のために既に持っている
  （`EditorTabSnapshot.LastSeenUtc`・`src/Loomo.App/Services/Workspace/WorkspaceStateStore.cs:938`、`TabShelfPolicy`）。
- 作業の置き場：Loomo のみ
- 規模：S（`CompleteRemoval` に「次に出す相手」を選ぶ関数を渡す。分割ビューでは同じ分割の中で選ぶ）
- 関連：§24.29、§24.4、未調査製品 1（閉じたタブを開き直す）

### 3. ブラウザで開いているローカルの HTML を、保存したら読み直す（元：VS Code 1.133・2026-08-12）
- 出典：https://code.visualstudio.com/updates/v1_133 （「Auto-reload HTML files in the integrated browser」：ローカルの HTML をディスク上で変えると自動で読み直す。タブごとに切り替えられる）
- 元の機能：統合ブラウザで開いたローカルの HTML ファイルが、ディスク上で変わったら自動で再読み込みされる。
- Loomo でやると：ブラウザペインで `https://file.loomo/…`（§21.5.1）を開いているタブは、そのファイルと同じフォルダーの `.html`・`.css`・`.js` の
  保存を見張って読み直す（スクロール位置は残す）。エディタで書いて保存すると、隣のブラウザが追いつく——素材の流れ（エディタ→ブラウザ）と
  周辺視野。Claude Code が書き換えた HTML もそのまま見える。
- いまの Loomo：**部分的にある**。EditorSupport のプレビューには自動の再読み込みがある（§24.8・`src/Loomo.App/Services/Editor/Support/EditorSupportAutoReload.cs:13`）が、
  ブラウザペインには無い（`Views/Shell/Browser/` と `Services/Browser/` に見張りの処理なし。再読み込みは F5／Ctrl+R の手動だけ・
  `src/Loomo.App/Services/Browser/BrowserChromePolicy.cs:54-55`）。
- 作業の置き場：Loomo のみ
- 規模：S（`DebouncedFolderWatcher` を file.loomo のタブにだけ付け、`CoreWebView2.Reload`。見えていないタブは見えたときに 1 回だけ読み直す）
- 関連：§21.5.1、§24.8

### 4. ワークツリーを作るとき、無視されているフォルダーを引き継ぐ（元：VS Code 1.140・2026-09-30（実験的））
- 出典：https://code.visualstudio.com/updates/v1_140 （「Reuse ignored folders across worktrees」：`git.worktreeSymlinkFolders` に `.gitignore` 形式で書いた無視フォルダー（`node_modules` など）を、新しいワークツリーへシンボリックリンクで置く）
- 元の機能：ワークツリーごとに依存関係を入れ直したり、大きなビルド成果物を複製したりしなくて済む。
- Loomo でやると：Git ペインの「ワークツリーを作る」ダイアログに「引き継ぐもの」を足す。フォルダー（`node_modules` など）はジャンクションで、
  ファイル（`.env`、`appsettings.Development.json` など、Git に載せない設定）はコピーで置く。指定はリポジトリごとに覚える。
  作ったワークツリーを部屋として開いた（`WorktreeRoomSeed`）直後から、ビルドも実行も動く。
- いまの Loomo：**未実装**。作成は `git worktree add` だけ（`src/Loomo.Services/Git/GitWorktreeModels.cs:78-85`・
  `src/Loomo.App/Services/Git/GitWorktreeCreatePolicy.cs`）で、無視されたファイルを扱う処理は無い。
- 作業の置き場：Loomo のみ
- 規模：S〜M（ジャンクションは管理者権限が要らない。リンクを張ったフォルダーをワークツリーの削除で消さない——
  `worktree remove` の前にリンクだけ外す——のが要点）
- 関連：§24.17

### 5. 変更を通しで読む——複数ファイルの差分を 1 本に並べる（元：Visual Studio 2026 18.9・2026-08-11）
- 出典：https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes （18.9 Git Tooling「Multi-file summary diff」：複数ファイルの変更行をまとめた 1 つの表示）
- 元の機能：変更したファイル全部の差分を、ファイルを切り替えずに上から下へ続けて読める。
- Loomo でやると：Diff ペインの変更ファイル一覧に「通しで読む」を足し、各ファイルの見出し（パス・増減の行数 §24.32）と差分を 1 本に並べる。
  見出しを押すとそのファイルの左右差分（§24.16）へ、行をダブルクリックするとエディタのその行へ。コミットの前（§24.34）や Claude Code が
  まとめて書き換えたあとに、どこを触ったかを一度に読める。読んでいた位置は差分のセッションと一緒に戻す（§24.4）。
- いまの Loomo：**未実装**。Diff ペインはファイル一覧から 1 ファイルずつ開く（`src/Loomo.App/Views/Diff/DiffSessionView.xaml:437`・
  `DiffSessionViewModel.FileListHeader`）。複数ファイルを 1 本にする面は無い。
- 作業の置き場：Loomo のみ
- 規模：M（既存の unified 表示（`DiffFlowDocumentRenderer`・分割して足していく `ChunkedAppendState`）をファイルの数だけつなぐ。大きな変更では
  見えるファイルの分だけ組み立てる）
- 関連：§24.15、§24.16、§24.25、§24.34

### 6. ターミナルの行に、出た時刻を持たせる（元：tmux 3.8・2026-09-09）
- 出典：https://raw.githubusercontent.com/tmux/tmux/master/CHANGES （3.8「Add capture-pane -I to include the time each line entered history」）、
  リリース日は https://github.com/tmux/tmux/releases/tag/3.8
- 元の機能：スクロールバックの各行が履歴に入った時刻を持ち、取り出すときに付けられる。
- Loomo でやると：ターミナルの行にマウスを載せると、その行が出た時刻を出す（スクロールバーの横に時刻の目盛りを出してもよい）。
  軌跡（§27）でその時刻へスクラブすると、その頃のターミナルの行まで飛べる。離れて戻ったとき（§24.22 留守中のまとめ）に
  「どの出力が留守中に出たか」が分かる。§24.26 で素材にしたターミナルの出力にも時刻を付けられる。
- いまの Loomo：**未実装**。Terminal パッケージの行には時刻が無い（`C:\Projects\Terminal\src` に行の時刻の扱いなし）。
  コマンド単位の時刻は OSC 133 から取っている（§24.1・§24.20）が、行単位は無い。
- 作業の置き場：Terminal（スクロールバックの行に時刻を 1 つ持たせ、行から時刻・時刻から行を引く公開 API を足す。常駐ホスト（§34）の
  `HeadlessTerminal` の再生でも時刻を落とさない。NuGet 公開が要る）＋ Loomo（表示と軌跡からの移動）
- 規模：M（行あたり 8 バイトの追加。再生の VT スナップショットに時刻を載せる形を決める必要がある）
- 関連：§27、§24.22、§24.26、§34

### 7. ブラウザのタブから URL を写す（元：Firefox 152・2026-06-16）
- 出典：https://www.firefox.com/en-US/firefox/152.0/releasenotes/ （「copy links via the tab context menu by right-clicking a tab and selecting Share > Copy Link … without switching to the tab first」）
- 元の機能：タブを前に出さずに、右クリックからそのページの URL を写す。
- Loomo でやると：TABS とペインの見出しのブラウザのタブの右クリックに「URL をコピー」「Markdown のリンクとしてコピー（`[題](URL)`）」を足す。
  開いている調べ物を、切り替えずにエディタやコンポーザへ貼れる（素材の流れ：ブラウザ→エディタ／ターミナル）。
- いまの Loomo：**部分的にある**。TABS の右クリックの「パスをコピー」はファイルのパスだけ（`src/Loomo.App/ViewModels/Shell/TabsViewModel.cs:965-970`・
  `tab.FilePath` が無いブラウザのタブでは何もしない）。Work Items には同じ形の「URL をコピー」「リンクをコピー（Markdown / HTML）」がある
  （`src/Loomo.App/Views/AzureDevOps/WorkItemsView.xaml:243-246`）。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24.3、§24.24、§21.5.1

### 8. ターミナルの中のリンク・パスをドラッグで他のペインへ（元：kitty 0.47.0・2026-05-19）
- 出典：https://sw.kovidgoyal.net/kitty/changelog/ （0.47.0「Allow optionally dragging URLs with the mouse」）、リリース日は
  https://github.com/kovidgoyal/kitty/releases/tag/v0.47.0
- 元の機能：ターミナルに出た URL をマウスでつかんで、ほかのアプリへドラッグできる。
- Loomo でやると：ターミナルの検出済みのリンク（URL・`path:line`）を Ctrl を押しながらドラッグすると、エディタへ落とせばそのファイルのその行、
  ブラウザへ落とせばその URL、コンポーザ・エディタの本文へ落とせば文字列として入る。ファイルを外からターミナルへ落とす向き
  （`FileDropPolicy`）の逆で、部屋の中を素材が手で運べる（§24.3）。
- いまの Loomo：**未実装**。Terminal パッケージにドラッグを始める処理は無い（`C:\Projects\Terminal\src` に `DoDragDrop` なし）。
  クリックで開く道（`HyperlinkActivated`・`SourceLocationParser`）はある。
- 作業の置き場：Terminal（リンクの上で Ctrl＋ドラッグしたら `DataObject`（テキスト＋URL／ファイルなら `FileDrop`）でドラッグを始める。NuGet 公開が要る）
  ＋ Loomo（エディタとブラウザの受け口で `path:line` を解く。`FileDropPolicy` に足す）
- 規模：S〜M
- 関連：§24.3、§24.7、§24.26

### 9. 番号でタブを選ぶ（元：Visual Studio 2026 18.11・2026-10-13）
- 出典：https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes （18.11 IDE「Numbered document tab shortcuts - Ctrl+Numpad1-9 to jump to document tabs」）
- 元の機能：数字キーで N 番目のタブへ直接移る。
- Loomo でやると：いまフォーカスのあるペイン（エディタ／ブラウザ／ターミナル）の N 番目のタブへ移るコマンドを 9 つ足す。
  ピン留めしたタブ（§24.29）は先頭に並ぶので、ピンの 1〜3 番に毎回戻る、という使い方になる。
- いまの Loomo：**未実装**。タブは前後へ回す（`CycleEditorTab`・`src/Loomo.App/Views/Shell/Layout/ShellWindow.ViewportSplit.cs:66,323`）だけで、
  番号で移るコマンドは `CommandCatalog` に無い（タブの分類は新しいタブを作る 3 つだけ・`CommandCatalog.cs:261-264`）。
- 作業の置き場：Loomo のみ
- 規模：S（キーは要検討。Ctrl＋数字は Vim のカウントとぶつからないが、ブラウザの WebView2 が Ctrl＋数字を取るので、ブラウザでは
  `BrowserChromePolicy` で先に受ける。既定は未割り当てでもよい）
- 関連：§24.2、§24.29、§26.2

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| 浮かぶペイン（並びの上に重ねる非モーダルのペイン） | tmux 3.7（2026-06-26）・3.8（2026-09-09） | 袖のライブミニチュア（§24.1）・切り離した窓（`Detach/`）・常に手前（§24.36）で同じ役をしている |
| 複数ファイルの衝突を「全部こちら／全部向こう」で一括解消 | Visual Studio 2026 18.11（2026-10-13） | Git 操作の網羅で、部屋の性質に効かない（いまは塊ごとの採用・`DiffConflictViewModel.cs:231-234`） |
| OS のライト／ダークに合わせて自動でテーマを切り替える・テーマの色を IDE で編集 | Visual Studio 2026 18.6（2026-05-12）・18.7（2026-06-09） | 外観の好みで、周辺視野・素材の流れ・復元・軌跡のどれにも効かない（Loomo は未実装・`ThemeManager.cs` に OS の設定を読む処理なし） |
| `if` の条件のどの項が結果を決めたかをデバッガで示す | Visual Studio 2026 18.10（2026-09-08） | デバッガの道具の品質。DAP には無く、式の分解と評価から作る大きな話 |
| 差分の塊の見出しに近くの宣言名を出す | jj v0.46.0（2026-10-07） | Git の `@@` 行の関数名（funcname）がそのまま出ている。LSP で正確にするのは道具の品質 |
| 差分の表示を幅で自動に切り替える（inline／side by side） | VS Code 1.137（2026-09-09） | 表示の調整。手で切り替える道は既にある（`DiffSessionView.xaml:368`） |
| 画像などバイナリの変更を差分に出す | VS Code 1.137（2026-09-09） | 差分の網羅。いまは「バイナリファイルは比較できません」（`DiffComparisonSourceResolver.cs:36`）。要望が出たら EditorSupport の画像表示を左右に置く形で |
| リサイズ中に列×行を重ねて出す | Windows Terminal 1.26 Preview（2026-10-02）・VS Code 1.131（2026-07-29） | 部屋に効かない |
| 1 つのカーソルに複数行を貼ると行ごとに下へ配る | VS Code 1.141（2026-10-07） | エディタの道具の品質 |
| 括弧の色分け（rainbow brackets）・データブレークポイント | Rider 2026.3 EAP（2026-09-07） | 道具の品質（EAP で正式版でもない） |
| 複数のコミットに同じコマンドを回す（`jj run`） | jj v0.43.0（2026-07-02）〜v0.44.0 | Git 操作の網羅 |
| Bases のカンバン表示・ハイライトの色 | Obsidian 1.14.0（2026-09-02） | ノートアプリの機能で、部屋に効かない |
| タブを AI で束ねる・Smart Window | Firefox 154・155（2026-08〜09） | AI とクラウドが主役 |
| コマンド履歴の端末間同期 | atuin v18.19〜v18.24（2026-08〜10） | クラウド必須。手元の最近のコマンドは §24.20 にある |
| ファイル 1 つの C# アプリの雛形 | Rider 2026.2（2026-07-22） | 一次情報を読めていない（ComponentSource の要約だけ）。部屋への効き目も小さい |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| 閉じても未保存の変更を残す（Hot Exit） | Visual Studio 2026 18.11 | エディタのタブは本文と変更済みの印ごと保存する（`EditorTabSnapshot.Text`／`IsModified`・`WorkspaceStateStore.cs:912,923`） |
| コミットを作業ツリーと比べる | Visual Studio 2026 18.11 | コミットの右クリック「作業ツリーと比較」（`GitSessionView.xaml:1109`）・reflog にも（`GitReflogView.xaml:231`） |
| ワークツリー | Visual Studio 2026 18.9 | §24.17 |
| PR をレビューし承認する | Visual Studio 2026 18.7 | §24.27・§24.27.1 |
| タブから「他を閉じる」 | VS Code 1.134 | `TabsViewModel.cs:279-280`・ペインの見出しの右クリック（`ShellWindow.PaneHeaderClose.cs:31`） |
| ブラウザのお気に入り | VS Code 1.123 | ブックマーク（`BrowserBookmarkTree`・§21.5.1） |
| アドレスバーから検索 | VS Code 1.125 | URL でない入力は検索へ（`WorkspaceSessionCoordinator.cs:290`） |
| ローカルの HTML を統合ブラウザで開く | VS Code 1.121・1.134 | `https://file.loomo/…`（§21.5.1・`LocalFileServer.cs`）。読み直しは無い→推す 3 |
| `git diff` の `i/` `w/` などの接頭辞付きのパスをリンクにする | VS Code 1.130 | `SourceLocationParser.GitPrefixLetters`（`src/Loomo.Core/Files/SourceLocationParser.cs:30-32`。`--no-index` の `1/` `2/` は未対応） |
| Markdown の差分をレンダリングで見る | VS Code 1.120 | §24.10 |
| 名前付きのウィンドウに開いていたものを覚える（workspaces） | Windows Terminal 1.26 Preview | Loomo のワークスペースと §34 の常駐セッション |
| OSC 9;4 の進捗 | tmux 3.7・kitty 0.47.0 | §24.35 |
| OSC 133 をイベントにする | tmux 3.8 | `ShellCommandActivity`（§24.1） |
| 付け直したときに前の画面をスクロールバックへ戻す | tmux 3.8（`clear-on-attach`） | §34 の再生（VT スナップショット） |
| 縦に並んだタブ | kitty 0.48.0 | TABS（サイドバーのタブ一覧） |
| IDE の検査結果を Claude Code に渡す | Rider 2026.2.1（2026-08-19、品質チェックのフック） | `getDiagnostics` に問題一覧の全件で答える（`ShellWindow.ClaudeIde.cs:12`・§38） |
| 押しているキーで使えるショートカットを出す（Shortcut Guide） | PowerToys v0.100.0（2026-06-10） | 連鎖キーの続き（§24.2.2）・ショートカットの窓（§26.2） |

## 調べた範囲

- 一次情報を読んだもの：Windows Terminal の GitHub Releases（v1.25.1171.0〜v1.26.2734.0 の Preview・2026-04-29〜10-02）、
  tmux の `CHANGES`（3.7・3.8）、kitty の changelog（0.47.0〜0.49.2）、jj の GitHub Releases（v0.41.0〜v0.46.0 の New features）、
  PowerToys の GitHub Releases（v0.99.0・v0.100.0）、Logseq の GitHub Releases（2.0.1・2.0.2）、
  Visual Studio 2026 のリリースノート（18.6〜18.11）、VS Code updates（1.118〜1.142 の見出しを全部、部屋に関わる 14 項目は本文まで）、
  JetBrains の .NET ブログの RSS（Rider 2026.2.1・2026.3 EAP の本文）、Obsidian changelog（1.14.0〜1.14.3）、Firefox のリリースノート（151〜158）。
- 期間内に機能のリリースが無かったもの：Neovim（0.12.0 は 2026-03-29 で期間外。期間内は 0.12.2〜0.12.6 の修正版だけ）、
  Helix・Alacritty・Ghostty（GitHub Releases に期間内の版なし）。
- 一次情報を読めなかったもの：Rider 2026.2／IntelliJ IDEA 2026.2 の What's New（公式ページは 403 か本文がスクリプトで描かれる。
  リリース日 2026-07-22 と主な項目は ComponentSource の要約だけ）。
- 版の一覧だけ見て本文を読んでいないもの：atuin v18.19〜v18.24、GitHub Desktop 3.6.4〜3.6.8-beta2（3.6.5 以降は前回の未調査製品で読んだ）。
- 見ていないもの：Arc（開発が止まっている）、Windsurf（AI が主役なので除いた）、Chrome／Edge DevTools、Notion。
