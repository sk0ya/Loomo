# 他ツールの新機能からの取り込み候補（2026-10-24・焦点：全領域・期間：2026-04-24〜2026-10-24）

前回までの 5 本（`feature-scout-2026-10-18*.md`・`feature-scout-2026-10-19-ペインとサイドバー.md`）で主要製品の
2026-04〜10-18 は読み切っているので、今回は **(A) 10-18 以降に出た新しい版** と、**(B) これまで一度も見ていない領域**
（Claude Code 自身の changelog・HTTP クライアント・ファイルマネージャ・zellij 0.45）に絞った。前回までに推した・見送った・
既にあった項目は重ねて挙げていない。

## まず手を付けるなら

| # | 候補 | 置き場 | 規模 | 理由 |
|---|---|---|---|---|
| 1 | プログラムが自分で言う状態（OSC 7501）を袖・動いているもの面に | Terminal ＋ Loomo | M | Claude Code が 10-08 から送っている。タイトルの印の推測（§38）を、本人の申告に置き換えられる |
| 3 | `.http` の応答を再起動のあとも残す | Loomo のみ | S〜M | 戻ると応答が消えている＝§24.4 の「場所としての信頼」の穴。`{{login.response…}}` の連鎖も再起動で切れている |
| 5 | 畳んだ中へ飛んだら、そこを開く | Editor | S | §24.43 で折りたたみを復元するようにしたので、軌跡・定義ジャンプが「畳まれた行」に着地するようになった |
| 6 | 直前のペインへ戻る（`Ctrl+W W`） | Loomo のみ | S | Vim の `wincmd p` と同じ。最後のペインはもう覚えている |

## 推す（効き目の大きい順）

### 1. プログラムが自分で言う状態（OSC 7501）を、袖・動いているもの面・TABS に出す（元：Claude Code 2.1.295・2026-10-08／仕様 2026-10-06）
- 出典：https://raw.githubusercontent.com/anthropics/claude-code/main/CHANGELOG.md （2.1.295「Added Program Status Protocol (OSC 7501) support: terminals that implement it can show whether Claude Code is working, waiting on you, or done」。日付は npm の公開時刻）／
  提案記事 https://mitchellh.com/writing/program-status-osc7501 ／仕様 https://www.superlogical.com/rex/docs/build/program-status
- 元の機能：端末の中のプログラムが `ESC ] 7501 ; state=blocked:kind=permission:app=claude-code:msg=<base64> ESC \` のように
  自分の状態（`idle`／`working`／`done`／`blocked`／`error`、`blocked` には `permission`／`question`／`auth`、`working` には `progress`）を端末へ申告する。
  `id` は `build/test` のような階層を持て、`clear` で消す。端末は、プロセスが終わるか次のプロンプト（OSC 133 `A`）が来たら
  `working`／`blocked` を捨て、`done`／`error` は残す——という寿命の決まりまで仕様にある。Claude Code・Terraform・Homebrew・Codex に実装（一部はプラグイン・フォーク）、Ghostty は PR 段階。
- Loomo でやると：いま Claude Code の状態は**タイトルの頭の印**（◐／◑＝作業中・✳＝入力待ち）と Notification フックから推している（§38・
  `src/Loomo.App/Services/Diagnostics/ClaudeCodeTitle.cs:28`）。OSC 7501 は**本人の申告**なので、(1) 承認待ち（`blocked:permission`）と
  質問待ち（`blocked:question`）を分けて袖のバッジ・§24.54 の動いているもの面・§24.53 の TABS の行・§24.17.2 のワークツリーの印に出せる、
  (2) フックを入れていない Claude Code でも承認待ちが取れる、(3) Claude Code 以外（ビルド・デプロイ・`brew` 等）の「人の番」も同じ口で拾える
  ——袖（周辺視野）の精度と範囲が同時に上がる。タイトルの推測はフォールバックとして残す。`msg`／`title` に制御文字があれば捨てる・
  通知は間引く、は仕様の MUST/SHOULD なのでそのまま守る。
- いまの Loomo：未実装（Terminal の `AnsiTerminalBuffer.DispatchOsc`（`C:\Projects\Terminal\src\Terminal.Controls\Buffer\AnsiTerminalBuffer.cs:1799`）が
  扱うのは 9／777／7／133・633 等で 7501 は無い。Loomo 側にも 7501 の字は無い）
- 作業の置き場：**Terminal**（OSC 7501 の解釈と、レコード表（id 階層・寿命の規則）を持ち、`TerminalTabView.ProgramStatusChanged` のような公開イベントで出す。
  §34 の常駐ホストでは `HeadlessTerminal` がレコードを持ち、付け直し時の VT 再生で再送する必要がある。NuGet 公開が要る）＋ **Loomo**（イベントを
  `ClaudeAgentState` と袖・動いているもの面へつなぐ）
- 規模：M（解釈自体は小さいが、レコードの寿命と常駐ホスト越しの再生、既存のタイトル推測との突き合わせがある）
- 関連：§24.1・§24.35・§24.49・§24.53・§24.54・§34・§38

### 2. 部屋の中の場所へのリンク（`loomo://`）——外から「このワークスペースのこのファイルのこの行」へ（元：PowerToys v0.102.2933.0・2026-10-21）
- 出典：https://github.com/microsoft/PowerToys/releases/tag/v0.102.2933.0 （Command Palette「Added copyable command links for opening pages and running commands from other apps, with confirmation on first use」／「Added copyable links to specific settings and extension options that scroll to, focus, and highlight their destinations」）
- 元の機能：アプリの中の「ページ」「設定の項目」へのリンクをコピーでき、ほかのアプリでそのリンクを踏むと、その場所へスクロールして強調する。
  コマンドを走らせるリンクは初回に確認を出す。
- Loomo でやると：エディタ・Diff・ブラウザ・ペグボードのカードの右クリックに「この場所へのリンクをコピー」を足し、
  `loomo://open?workspace=<フォルダー>&file=<相対パス>&line=<行>`（Markdown／HTML の両形式は既存の「リンクをコピー」と同じ作り）を出す。
  Obsidian のノート・Teams・Azure DevOps のコメント・Claude Code への頼みごとに貼った場所から、**どの部屋のどこか**へ一発で戻れる——素材の流れが
  部屋の外とつながり、戻り先を記憶に頼らなくて済む（軌跡の外側版）。踏まれたら、その部屋が開いている Loomo へ回し（「他の Loomo で開く」と同じ振り分け）、
  無ければワークスペースを開いてから着地する。コマンドを走らせるリンクは**作らない**（PowerToys も確認を挟んでいる。ファイルを開くだけに留める）。
- いまの Loomo：未実装。URL プロトコルの登録は既定ブラウザ用の http/https だけ（`src/Loomo.App/Services/Startup/DefaultAppRegistration.cs:142`）、
  外からの起動引数は `--workspace` とファイル・URL だけで行を持たない（`src/Loomo.App/Services/Startup/StartupArguments.cs:7`）。
  リンクのコピーはブラウザのタブ（`ShellWindow.PaneHeaderClose.cs:44`）と Work Item にはある。
- 作業の置き場：Loomo のみ
- 規模：M（スキーム登録・引数の解釈・起動中の Loomo への受け渡し・着地。受け渡しは「他の Loomo で開く」の口を使える）
- 関連：§23.3（ペグボード）・§24.3・§24.4・§27

### 3. `.http` の応答を再起動のあとも残す（元：Bruno v3.4.0・2026-05-20／v4.2.0・2026-09-23）
- 出典：https://usebruno.com/changelog （3.4.0「Last workspace and open tabs are now restored when you reopen the app.」「Scroll positions are now preserved across tabs and requests during your session」／
  4.2.0「Timeline, DevTools Network tab, and request history now show every header actually sent.」）
- 元の機能：開いていたタブ・スクロール位置・JSON の開閉を戻し、送ったリクエストの履歴（実際に送ったヘッダーまで）を残す。
- Loomo でやると：`.http` の EditorSupport（§24.30）が持つ「各リクエストの最後の応答」を、ワークスペースごとに `%APPDATA%/Loomo/` へ
  書き残し（`DeferredWriteQueue` で UI スレッドの外・§31.15）、開き直したときに状態・所要時間・本文を戻す。古い数回ぶんを残せば「前回と比べる」
  （§24.21 のコマンド出力と同じ形）にもつながる。いまは再起動すると応答が全部消え、`{{login.response.body.$.token}}` を使う後続のリクエストも
  ログインを打ち直すまで送れない——戻った場所が戻っていない、という §24.4 の穴。
- いまの Loomo：未実装（応答はメモリ上の辞書だけ・`src/Loomo.App/Services/Editor/Support/HttpEditorSupport.cs:24`。§24.30 の「まだ無いもの」に「応答の永続化」とある）
- 作業の置き場：Loomo のみ
- 規模：S〜M（保存の形と大きさの上限・秘密を含みうるヘッダーの扱い（`Authorization` は伏せる）を決める）
- 関連：§24.4・§24.21・§24.30・§31.15

### 4. `.http` の応答の中の URL から次の一手へ（元：Bruno v4.2.0・2026-09-23）
- 出典：https://usebruno.com/changelog （4.2.0「Click a URL in the response body to open it as a new request, or Cmd/Ctrl+click to open it in the browser.」）
- 元の機能：応答本文の URL を押すとそれを新しいリクエストにし、Ctrl+クリックでブラウザで開く。
- Loomo でやると：応答本文の URL を、Ctrl+クリックで**ブラウザペイン**に開き、右クリックで「この URL を `.http` に足す」（いまのリクエストの下に
  `GET <URL>` を書き足して選ぶ）にする。ページングの `next`、作成した資源の `Location`、`html_url` など、API を辿る作業がペイン間でつながる。
- いまの Loomo：未実装（応答本文は読み取り専用の `TextBox` でリンクにならない・`src/Loomo.App/Views/Editor/HttpRequestView.xaml:171`）
- 作業の置き場：Loomo のみ
- 規模：S（URL の検出はターミナルのリンク化と同じ規則を使える。本文の表示をリンクを持てる形に替える）
- 関連：§21.5.1・§24.3・§24.30

### 5. 畳んだ中へ飛んだら、そこを開く（元：Zed 1.25.0 Preview・2026-10-14／1.25.2 安定版・2026-10-21）
- 出典：https://zed.dev/releases/stable/1.25.2 （「Added automatic unfolding of folded sections when jumping to a location via go to line」）
- 元の機能：行へ移動・場所へのジャンプで着地した先が畳まれていたら、その折りたたみを開く。
- Loomo でやると：§24.43 で折りたたみが再起動のあとも戻るようになったので、軌跡の点・定義へ移動・検索結果・Problems・アウトラインから飛んだ先が
  **畳まれた中**だと、カーソルは畳みの頭の行に寄せられ、目的の行は見えない。着地の行を隠している折りたたみだけを開けば、
  「過去の地点へ戻れる」（§27）と「畳んだまま戻る」（§24.43）がぶつからない。手で `zc` した直後のカーソルの寄せ（`ClampCursorToVisible`）はそのまま。
- いまの Loomo：未実装（Editor で折りたたみを開くのは `zo`／`za`／`zR` だけ・`C:\Projects\Editor\src\Editor.Core\Engine\VimEngineRuntime.cs:554`。
  隠れた行のカーソルは畳みの頭へ寄せるだけ・`Editor.Core/Engine/Commands/FoldCommands.cs:113`）
- 作業の置き場：**Editor**（キャレットをプログラムから動かす入口——行へ移動・`SetCaret` 系——で、着地行を隠す折りたたみを `OpenFold` する。NuGet 公開が要る）
- 規模：S
- 関連：§24.43・§27・§36

### 6. 直前のペインへ戻る（元：zellij v0.45.0・2026-08-20）
- 出典：https://github.com/zellij-org/zellij/releases/tag/v0.45.0 （「A new `FocusLastPane` action returns focus to the previously focused pane」）
- 元の機能：1 つ前にフォーカスしていたペインへ戻る（もう一度押すと元へ）。
- Loomo でやると：パレットとキーに「直前のペインへ」を足す。既定キーは `Ctrl+W W`（`Ctrl+W P` はパレットが使っている。Vim の `Ctrl+W w` と同じ指使いで、
  `pane.focus.*` が `Ctrl+W H/J/K/L` なのとそろう）。エディタ⇄ターミナル⇄ブラウザの往復が方向を考えずに 1 キーになる。
  §24.44（タブを閉じたら直前のタブへ）のペイン版。
- いまの Loomo：未実装（ペインの移動は上下左右だけ・`src/Loomo.App/Input/CommandCatalog.cs:126`。ただし最後にフォーカスしたペインは
  復元用に覚えている・`src/Loomo.App/Services/Layout/PaneFocusNavigationPolicy.cs:18`）
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24.2・§24.44・§26

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| 環境の切り替え・継承（親の環境を受け継ぐ）・コレクションの既定の環境 | Bruno v4.1.0（2026-08-20）・v4.2.0（2026-09-23） | `.http` の道具の品質。§24.30 の「まだ無いもの」（`http-client.env.json` の切替）に既に載っているので、そちらで扱う |
| JSON の応答にパンくず（いまの位置のパス） | Yaak 2026.8.0（2026-09-17） | 応答の見せ方の調整で、部屋に効かない |
| 入れ子のセッション（ssh 先の zellij を外側から操作） | zellij v0.45.0（2026-08-20） | 非目標（リモート開発） |
| Kitty graphics protocol | zellij v0.45.0 | Terminal の描画機能。部屋への効き目が小さい |
| ペインの枠をタイトル 1 行にする | zellij v0.45.0 | 見た目の調整 |
| サイドバーのフォルダーを自然順（Folder 2 < Folder 10）に | Files v4.3（2026-10-11） | 道具の品質。Loomo のツリーの並べ方の比較器までは確かめていない |
| 名前をまとめて打って複数作る（Bulk create） | yazi v26.8.15（2026-08-15） | ファイル操作の網羅で、部屋に効かない |
| 長い Git 操作の進捗を出す | Files v4.3 | Git 操作の道具の品質 |
| 見出しの末尾へ追記する URI／CLI | Obsidian 1.15.0（2026-10-22） | ノートアプリの機能。部屋の外からの入口は推す 2 で扱う |
| 積み重ねた PR（stacked PR） | GitKraken 12.6.0 | GitHub 依存の Git 操作の網羅 |
| 使われていないワークツリーの片付け（エージェントのセッション単位） | VS Code 1.143（2026-10-22） | Loomo の §24.55 が同じことを人のワークツリーで既にやっている（下の「既にあった」） |
| 切り離したバックグラウンドのシェルの出力を見る | VS Code 1.143 | AI のセッションの道具。人のコマンドは §24.54 の動いているもの面にある |
| 診断の強調を 1 ファイル 5,000 件まで | VS Code 1.143 | エディタの道具の品質 |
| マウスのボタン固定・カーソル隠し・ZoomIt の書き起こし | PowerToys v0.102.2933.0 | 部屋の外の OS の道具 |
| AI サイドバー・Apps | Bruno v4.0.0（2026-07-23） | AI が主役 |
| mod（Claude Code の画面に自前の面を足す仕組み）向けの各種フック | Claude Code 2.1.29x〜2.1.310（2026-10） | Claude Code 自身を拡張する話で、Loomo 側の取り込みではない |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| 続いたコミットを選んで変更をまとめて見る | GitKraken 12.6.0 | コミット一覧は複数選択でき、両端の範囲の差分を開く（`src/Loomo.App/ViewModels/Git/GitSessionViewModel.cs:710` `OpenDiffForCommits`） |
| 複数ファイルの差分の横にファイル一覧、選ぶとそのファイルへ | VS Code 1.143（実験的） | §24.50 通し表示（`DiffSessionViewModel.Continuous.cs:132` `TryScrollToContinuousFile`） |
| ワークツリーの片付け（大きさ・最後に触った・取り込み済み） | VS Code 1.143 | §24.55 |
| ワークツリーごとの作業中の印 | GitKraken 12.6.1（2026-10-20） | §24.17.2（`src/Loomo.App/Services/Git/WorktreeAgentMarks.cs`） |
| OSC 133：出力ごと選ぶ・最後のコマンドの出力をコピー | zellij v0.45.0 | Terminal の `CopyLastCommandOutput`（`C:\Projects\Terminal\src\Terminal.Controls\Tabs\TerminalTabView.CommandOutput.cs:23`）・§24.21・§24.26 |
| OSC 9／777 の通知をまとめて扱う | zellij v0.45.0 | §24.49 |
| 解決できない変数を知らせる | Bruno v4.3.0（2026-10-22） | §24.30（解決できない変数は空にせず名前を出し、URL が組めなければ送らない） |
| SVG のプレビュー | Zed 1.25.0 | EditorSupport のプレビュー対象（`src/Loomo.App/Services/Editor/Support/EditorSupport.cs:364`） |
| タブを引いてペインを作る | Files v4.2（2026-07-05） | ペインのドラッグとドロップ先の案内（`PaneDropPreviewPolicy`・`WingDropZone`・§21） |
| ごみ箱から戻す | yazi v26.8.15 | エクスプローラーの Undo（`src/Loomo.App/Services/FileSystem/FileOperationHistory.cs:455` `RecycleBin.TryRestore`・§24.12） |
| 最近のタブ／ファイルへ素早く移る | Zed 1.25.0（タブ切替のプレビュー設定） | `nav.recentFiles`（`src/Loomo.App/Input/CommandCatalog.cs:196`）・パレットのプレビュー |

## 調べた範囲

- 一次情報を読んだもの：Claude Code の CHANGELOG（2.1.120〜2.1.310 の「Added」を全部。版の日付は npm レジストリの公開時刻で照合）、
  OSC 7501 の提案記事と仕様（superlogical.com）、Zed 1.25.0 Preview・1.25.2・1.25.3、VS Code 1.143（見出しと非 AI の 4 項目は本文まで。日付は GitHub Releases）、
  GitKraken Desktop release notes（12.6.0・12.6.1）、Bruno changelog（3.2.2〜4.3.0）、Yaak の GitHub Releases（2026.8.0〜2026.9.0）、
  Files の公式ブログ（v4.2・v4.3）と GitHub Releases の版一覧、zellij v0.45.0、yazi の CHANGELOG（v26.5.6〜Unreleased）、
  PowerToys v0.102.2933.0、GitHub Desktop 3.6.8-beta3〜3.6.9-beta1、Obsidian changelog（1.14.4〜1.15.0）、Sublime Text dev builds（4212・4214）、
  Fork for Windows release notes（2.23 が最新）、Warp changelog 2026（10.15 が最新）。
- 日付は GitHub API で照合：VS Code、Windows Terminal、lazygit、zellij、yazi、Yaak、PowerToys、GitHub Desktop、jj、Neovim、Helix、WezTerm、Ghostty。
- 10-18 以降に機能の版が無かったもの：Windows Terminal（最新 10-02）、lazygit（0.66.1・0.66.2 は修正版）、jj（0.46.0 は 10-07・前回読了）、
  Neovim（0.12.6 は修正版）、Helix・WezTerm・Ghostty（期間内の正式版なし。Ghostty の OSC 7501 は PR 段階）、Sublime Text（修正だけ）、Fork（2.23 は 09-25 で前回読了）。
- 一次情報を読めなかったもの：Visual Studio 2026 18.12（見つからず。18.11 は前回読了）、Directory Opus 13.25（2026-08-21。要約だけで本文なし）、
  Files v4.0.38〜v4.1.3 の本文（版と日付だけ）。
- 見ていないもの：Chrome／Edge DevTools（ブラウザペインは WebView2 の DevTools をそのまま使うので除いた）、DBeaver／DataGrip、Total Commander、
  Wallaby／NCrunch、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）。
