# 他ツールの新機能からの取り込み候補（2026-10-31・焦点：10-24 以降に出た版と、前回までの 10 本で見ていない製品・期間：2026-04-30〜10-31）

前回までの 10 本（`feature-scout-2026-10-18*.md`〜`feature-scout-2026-10-26-未調査製品4.md`）で主要製品の一次情報はほぼ読み切っている。
今回は (1) 10-24 以降に新しい版が出た製品と、(2) 一度も読んでいない製品（RAD Debugger・Contour・git 本体・Sapling・Trilium・Pulsar・CotEditor ほか）を見た。
前回までに推した・見送った・既にあった項目は重ねて挙げていない。

物差し：主役は人間の部屋（§23 / §24 / §26 / §27）。§19.9 の「やらない」（拡張エコシステム・リモート開発・Web 版・共同編集・Notebook）と
クラウド必須の機能は外す。

## まず手を付けるなら

1. ブレークポイントとウォッチ式を部屋に残す（復元の穴。いまは Loomo を閉じると全部消える）
2. 端末から出たダイアログを Loomo の手前に出す（同梱の conpty.dll に口があって、まだ呼んでいない）
3. 時間のかかったコマンドが、見ていない間に終わったら知らせる（通知欄・Loomo が裏ならタスクバー）

## 推す

### 1. ブレークポイントとウォッチ式を部屋ごとに残す（元：RAD Debugger v0.9.29-alpha・2026-09-30）
- 出典：https://github.com/EpicGamesExt/raddebugger/releases/tag/v0.9.29-alpha
- 元の機能：ウォッチ式にプロジェクトの印を付け、ターゲットやブレークポイントと同じようにプロジェクトを開くと読み込まれ、閉じると外れるようにした。
- Loomo でやると：部屋（`WorkspaceSnapshot`）にブレークポイント（位置・条件）とウォッチ式を保存し、部屋を開き直す・切り替えると戻す。
  ブレークポイントの行はファイルの編集に合わせてずれるので、保存の時点の行と行の中身を残し、戻すときに中身で引き当て直す（§24.96 の取り消しの履歴と同じ考え方）。
  部屋の「離れて戻ったら同じ場所」が、デバッグの準備にも効くようになる。カーソル行やスクロールまで戻す部屋で、ブレークポイントだけが消えるのは目立つ穴。
- いまの Loomo：未実装。ブレークポイントは `DebugBreakpointsViewModel`（`src/Loomo.App/ViewModels/Debugging/DebugBreakpointsViewModel.cs`）が
  メモリに持つだけで、保存・読み込みのコードが無い（`NetcoredbgDebugService.cs:277` もセッションをまたいで「記憶したまま再送する」だけで、アプリの再起動はまたがない）。
  ウォッチ式も `DebugInspectionViewModel.Watches`（`DebugInspectionViewModel.cs:205`）の `ObservableCollection` だけ。§28 にも保存の方針の記述は無い。
- 作業の置き場：Loomo のみ（スナップショットへの項目追加、両デバッグペイン＝.NET と TS IDE の `Breakpoints` を同じ形で読み書き）
- 規模：M（保存・復元は小さいが、行のずれの引き当てと、2 つのデバッグペイン・複数フォルダーの部屋で鍵（パス）をどう持つかで 1〜2 日）
- 関連：§28・§29・§24（復元）

### 2. 端末から出たダイアログを Loomo の手前に出す（元：VS Code 1.144・2026-10-28）
- 出典：https://code.visualstudio.com/updates/v1_144 （「Dialogs from terminal commands open in front of VS Code on Windows」）
- 元の機能：ローカルの端末ごとに、ConPTY の隠しコンソールウィンドウの持ち主（owner）をその端末が表示されているウィンドウにした（Windows Terminal と同じ）。
  コマンドがコンソールに結び付けて出すダイアログ（例：`Connect-AzAccount` のサインイン選択）が VS Code の後ろに隠れ、コマンドが固まったように見えていたのを直した。
  `terminal.integrated.windowsReparentConsoleWindow` で切れる。`conpty.dll` を使うのが前提。
- Loomo でやると：ターミナルのタブを表示しているウィンドウ（本体か、切り離したウィンドウか）の HWND を ConPTY に渡す。タブが別ウィンドウへ移ったとき・
  常駐ホスト（§34）のセッションへ繋ぎ直したときにも渡し直す。資格情報マネージャーの窓やサインインの選択が Loomo の後ろに隠れて
  「止まっている」と見誤るのが無くなる——部屋の中の道具が信頼できる、という周辺視野の話。
- いまの Loomo：未実装。同梱の `conpty.dll`（`src/Loomo.App/bin/Debug/conpty.dll`）は `ConptyReparentPseudoConsole` を公開しているが、
  Terminal のソース（`C:\Projects\Terminal\src`）が使っているのは `ConptyCreatePseudoConsole` / `ConptyResizePseudoConsole` / `ConptyClosePseudoConsole` の 3 つだけ。
  Loomo 側にも該当する呼び出しは無い（`AllowSetForegroundWindow` は別の用途で `InstanceRelay.cs:244` にだけある）。
- 作業の置き場：Terminal（`ConPtyNative` に `ConptyReparentPseudoConsole` の呼び出しと、`TerminalTabView` から持ち主の HWND を渡す口。
  受信箱の ConPTY（`TERMINAL_CONPTY=inbox`）では何もしない）＋ Loomo（Pty.Host のパイプの約束に「持ち主の窓」の通知を足す。HPCON はホスト側のプロセスにあるため）。
  NuGet 公開が要る。
- 規模：M（呼び出し自体は 1 行だが、常駐ホストを介すので、繋ぎ直し・切り離し・ウィンドウを閉じたときの渡し直しを確かめる手間が要る）
- 関連：§34・§24.61（同梱の ConPTY）

### 3. 時間のかかったコマンドが、見ていない間に終わったら知らせる（元：Contour 0.8.0・2026-10-23）
- 出典：https://github.com/contour-terminal/contour/releases/tag/v0.8.0.9335 （本文は `metainfo.xml` の 0.8.0 の項）
- 元の機能：フォーカスの無いペインで長いコマンドが終わったら、デスクトップ通知を出す（既定で ON、10 秒以上かかったコマンドが対象）。
- Loomo でやると：`ShellCommandActivity` の `CommandDone` で、所要時間がしきい値（例 10 秒）を超えていて、そのタブを見ていなかったら
  通知欄（§24.49 と同じ「そのタブへ」付き）に「`dotnet test` が失敗しました（✗ 1・2分14秒）」の形で載せる。Loomo のウィンドウが前に無いときは
  タスクバーのボタンを点滅させ、成否の印を重ねる（`TaskbarItemInfo`）。袖のバッジは部屋の中にいるときの周辺視野、§24.22 の「留守中のまとめ」は
  5 分以上の離席——その間（別のアプリで 2〜3 分作業している間に終わったビルド）を埋める。
- いまの Loomo：部分的にある。コマンドの終了は袖のバッジだけ（`ShellWindow.PaneActivity.cs:181-186`）で、通知欄に載るのはコマンドが自分で出す
  OSC 9 / 777 の通知だけ（同 `:63-73`）。タスクバーへ知らせるコード（`FlashWindowEx`・`TaskbarItemInfo`）は無い。
  所要時間は Terminal の `ShellCommandActivityEventArgs` が既に持っている（OSC 133;C〜D の時間）ので、パッケージの改修は要らない。
- 作業の置き場：Loomo のみ
- 規模：S（しきい値の設定・「見ていたか」の判定は袖のバッジの `IsTerminalPaneWatched` を流用）
- 関連：§24.1・§24.35・§24.49・§24.22

### 4. bisect を Git ペインで、判定はターミナルの終了コードから（元：git 2.56.0 の `git bisect --reset-when-found`・2026-09-28）
- 出典：https://github.com/git/git/blob/master/Documentation/RelNotes/2.56.0.adoc （版の日付は `v2.56.0` のコミット日時で照合）
- 元の機能：`git bisect` に、犯人が見つかったら自動で `git bisect reset` し、元の場所か見つかったコミットへ戻るオプションが付いた。
  git 側が「見つけたら片付けて戻る」までを 1 つの流れとして扱うようになった。
- Loomo でやると：コミット一覧で「良い」「悪い」の 2 点を選んで bisect を始め、いま試しているコミット・残りの候補数（あと何歩か）を Git ペインの上に出す。
  判定は「良い／悪い／飛ばす」のボタンのほかに、**ターミナルで最後に走らせたコマンドの成否（OSC 133 の終了コード）をそのまま判定にできる**ようにする——
  ターミナル → Git の素材の流れ。見つかったら、そのコミットを詳細で開き、`--reset-when-found` と同じく元の場所へ戻す。
  bisect 中は袖とステータスに「bisect 中」を出し続ける（忘れて作業を始めるのを防ぐ）。
- いまの Loomo：未実装。`src` に `bisect` を含むのは `MyersDiff.cs`（差分の二分探索の語）だけで、`docs` には言及が無い。
- 作業の置き場：Loomo のみ
- 規模：M（状態の読み取り＝`.git/BISECT_*`・開始／判定／終了の操作・ペイン上の帯・ターミナルの終了コードとの結線）
- 関連：§24.19（reflog の面）・§24.21（コマンドの出力を素材にする）

### 5. ローカル履歴に名前を付けた版を残す（元：Trilium v0.103.0・2026-05-13）
- 出典：https://github.com/TriliumNext/Trilium/releases/tag/v0.103.0
- 元の機能：手で作った版（revision）に説明を付けられるようにした。
- Loomo でやると：エディタの右クリックかパレットから「この時点に名前を付けて残す」（例「リファクタリング前」）。名前付きの版は一覧で目立たせ、
  14 日の保持期限と 1 ファイル 200 版の刈り込みから外す。軌跡（§27）が「どこにいたか」に戻る道具なら、こちらは「中身がどうだったか」に戻る道具で、
  戻る先を人が自分で印せるようになる。
- いまの Loomo：未実装。`LocalHistoryKind` は「開いた時点／保存／履歴から戻す前／変換する前」の 4 種だけで、名前や説明を持つ項目は無い
  （`src/Loomo.App/Services/History/LocalHistoryStore.cs:8-31`）。保持は `Retention = 14日`・`MaxEntriesPerFile = 200` が一律（同 `:47-48`）。
- 作業の置き場：Loomo のみ（版のファイル名に種類が入る形なので、名前は別の小さなファイルに持つ）
- 規模：S
- 関連：§36（ローカル履歴）・§27

### 6. git が止めた操作に、次の一手を添える（元：Zed v1.27.0-pre・2026-10-28 ／ git 2.55.0・2026-06-29）
- 出典：https://github.com/zed-industries/zed/releases/tag/v1.27.0-pre （Git Panel の push 拒否の説明・#47296）、
  https://github.com/git/git/blob/master/Documentation/RelNotes/2.55.0.adoc （`git checkout -m` がローカル変更を stash に残すようになった）
- 元の機能：Zed はローカルのブランチがリモートより遅れていて push が拒まれたとき、「先にリモートの変更を取得して取り込んでから push し直す」と説明するようにした。
  git 2.55 は、ローカル変更があるまま別ブランチへ切り替える `checkout -m` が、衝突の解消に一度しか機会が無かったのを、変更を stash に残して何度でもやり直せるようにした。
- Loomo でやると：(a) push が非早送りで拒まれたら、git の文面の代わりに「リモートに N 件の新しいコミットがあります」と出し、
  その場から「プル（衝突の予告つき・§24.64）」へ進めるボタンを添える。(b) チェックアウトがローカル変更で止まったら、
  「退避して切り替える」「変更を持っていく（`-m`）」「やめる」を選べるようにし、退避した stash を Git パネルのスタッシュタブで示す。
- いまの Loomo：未実装。push の失敗は `RunOpAsync` が git の文面を切り詰めて出すだけ（`GitPanelViewModel.cs:1495-1503`）。
  チェックアウトは素の `git checkout <branch>`（`GitBranchService.cs:353-354`）で、ローカル変更で拒まれたときの扱いは無い。
- 作業の置き場：Loomo のみ
- 規模：S（拒否の文面の読み分けと、既存の操作（プルの予告・スタッシュ）への接続）
- 関連：§24.64・§24.31・§24.92

### 7. 中身の無い部屋を開いたら README を出す（元：Zed v1.27.0-pre・2026-10-28）
- 出典：https://github.com/zed-industries/zed/releases/tag/v1.27.0-pre （`on_new_window` に `"readme"`・#65389）
- 元の機能：新しいウィンドウを開いたとき、README を既定で開く設定を足した。
- Loomo でやると：初めて開くフォルダー（復元するタブが無い部屋）なら、ルートの README を Markdown のプレビュー（EditorSupport）で開いておく。
  戻ってきた部屋は復元が優先なので出さない。新しいワークツリーの部屋（§24.17）や、課題から始める部屋（§24.56）に入ったときの「最初に目に入るもの」になる。
- いまの Loomo：未実装。`src/Loomo.App` で README を扱うのはフォルダー比較の注記と EditorSupport の表示名の例だけ
  （`FolderComparison.cs:151`・`EditorSupport.cs:25`）。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24.17・§24.56

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| デバッガのホバーで変数を木で開く | Zed v1.26.2（2026-10-28） | 部屋の性質（周辺視野・素材の流れ・復元・軌跡）ではなく IDE の品質の話。いまの DataTip は文字列 1 つ（`DebugInspectionViewModel.cs:246`、Editor の `DataTipEvaluator` も `Task<string?>`）なので、やるなら §31 の台帳に載せる方が合う |
| 余白のクリックで「カーソル行まで実行」・行き先の表示・ブレークポイントを選んだスレッドに限る | RAD Debugger v0.9.29-alpha（2026-09-30） | 同上（IDE の品質）。「カーソル行まで実行」自体は右クリックに既にある（`SelectionActionMenuBuilder.cs:119`） |
| ファイルツリーを拡張子ごとにまとめて並べる | Zed v1.26.2 | 部屋に効かない（並び順の好み） |
| 絞り込んで見えている変更だけを一括でステージするキー | SourceGit v2026.23（2026-10-26） | Loomo の変更一覧には絞り込み自体が無く、元の機能は既存の絞り込みへのキーの追加。部屋に効かない |
| コミットから一部のファイル／ハンクを外して上に新しいコミットを作る（`but split`） | GitButler 0.23.0（2026-10-27） | §24.62（コミットから変更を外して作業ツリーへ戻す）と新しいコミットの作成で同じことができる。差分が小さい |
| ワークツリーにラベルを付ける（`sl worktree label`） | Sapling 0.2.20261028（2026-10-28） | ワークツリーはブランチ名とフォルダー名で見分けられていて、ターミナルのタブのメモ（§24.73）もある。足す効き目が小さい |
| `/watch` でページの変化を AI に見張らせる | Firefox 159（2026-10-27） | AI 前提・外部の AI バックエンドが要る |
| Arc / Dia からスペースを取り込む・Boosts の共有 | Zen 1.24b（2026-10-29） | 他ブラウザからの移行・共有でクラウド前提 |
| ワークスペースとタブの同期 | Floorp v12.21.0（2026-10-27） | 端末間の同期でクラウド前提 |
| 端末間でアクティビティを同期・作業時間の報告 | ActivityWatch v0.14.0（2026-10-06） | 部屋の外（OS 全体の時間記録）の道具。部屋の 1 日のまとめは §24.23 が持っている |
| Copilot CLI のセッションを記録・再開する | atuin v18.24.4（2026-10-30） | Claude Code 以外の AI ツールの記録。Loomo の住人は Claude Code（§38） |
| Markdown を描画／生の表示で切り替えるキー | Warp 2026.10.15 | Loomo はエディタと EditorSupport のプレビューを並べる作り（§23）で、切り替えより並べる方が部屋の考え方に合う |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| 終わったコマンドの出力を畳む・スクロールバーに成否の印・コマンドのスティッキーヘッダー | Contour 0.8.0 | Terminal の `TerminalTabView.Folding.cs`・`TerminalTabView.ScrollMarkers.cs`・`TerminalTabView.StickyScroll.cs`（§24.20） |
| 印のホバーで終了コード・所要時間 | Contour 0.8.0 | Terminal `TerminalTabView.StickyScroll.cs:80`（`FormatCommandMeta`：`✗ 2 · 1.2s`） |
| 出力の中のパスと URL を Ctrl+クリックで開く | Contour 0.8.0 | Terminal 1.0.53（`e02e6859`）。Claude Code の中でも開ける |
| タブ・分割・保存した配置・タブの色 | Contour 0.7.0 | §34（タブ ID と分割の復元）・§24.63（タブの色） |
| 解消済みにする前に衝突マーカーの残りを確かめる（`git add --resolved`） | git 2.56.0 | 「解決済みにする」は残りの衝突が 0 のときだけ押せる（`DiffConflictViewModel.cs:226`） |
| コミットを落とす・fixup（`git history drop` / `fixup`） | git 2.55.0 / 2.56.0 | interactive rebase のダイアログ（`InteractiveRebaseDialog.xaml.cs`）と fixup + rebase（`GitPanelViewModel.cs:1388`） |
| amend の前後で何が変わったかを見る（`sl show -t`） | Sapling 0.2.20260811 | reflog の面の「操作の前後」（§24.19・`GitReflogEntry.PreviousHash`） |
| push で上流を自動で設定する | SourceGit v2026.23 | 上流が無ければ `push -u`（`GitBranchService.cs:205-213`） |
| 古いワークツリーを探して消す・容量の表示 | VS Code 1.144（Manage worktree storage） | §24.55（`WorktreeCleanupViewModel.cs:61` の容量、`:134` の「N 件・○ MB を空けます」） |
| ワークツリーとの間で変更を移す | GitButler 0.23.0 | §24.81（未コミットの変更を別のワークツリーへ写す・移す） |
| 外で変わったファイルを保存するときの確認 | Pulsar 1.132.0（`core.promptOnConflict`） | §24.57。未保存の編集を抱えたまま外で変わるとバーで「このまま保存すると外の変更を上書きします」と出す（`ShellWindow.ExternalFileChanges.cs:147`）。保存は上書きを選んだこととして扱う |
| 検索結果を全部まとめて開く | Trilium v0.103.0 | §35 の抜粋タブ（マルチバッファ） |
| タブを検索するキー | Firefox 159 | 部屋のタブを種類を問わず最近使った順に巡る（§24.65・`RoomTabMru`） |
| フォルダー全体の文字列検索・文書の履歴を前後に移る | CotEditor 7.1.0 | Search ペイン・軌跡（§27）と直前のペインへ戻る（§24.58） |

## 調べた範囲

- 10-24 以降に出た版で一次情報を読んだもの：VS Code 1.144（2026-10-28。見出しを全部、非 AI の 4 項目は本文まで）、Zed v1.26.2・v1.27.0-pre（10-28）、
  GitHub Desktop 3.6.9-beta1〜3.6.10（Git for Windows の更新と修正だけ）、SourceGit v2026.23（10-26）、GitButler 0.23.0・0.23.1（10-27・10-28）、
  Claude Code 2.1.311〜2.1.316（10-26〜10-31。§38 の連携に関わる新しい口は無かった）、Zen 1.24b（10-29）、Floorp v12.21.0（10-27）、
  atuin v18.24.3・v18.24.4（10-29・10-30）、PowerToys v0.102.3001・v0.103.3006 preview（10-27・10-28。Workspaces の CLI と Command Palette のドック）、
  Warp 2026.10.15・2026.10.22。日付は GitHub API（Warp はページの日付）で照合。
- 初めて一次情報を読んだ製品：RAD Debugger（v0.9.26-alpha〜v0.9.29-alpha・2026-05-19〜09-30。本文があったのは 0.9.29 だけ）、
  Contour（v0.7.0・v0.8.0。GitHub Releases の要約と `metainfo.xml` の項目。0.7.0 は途中まで）、git 本体の RelNotes（2.55.0・2.56.0 の「UI, Workflows & Features」を全部。
  2.54.0 は 04-20 で期間外）、Sapling（2026-05-22〜10-28 の 4 版）、Trilium（v0.103.0〜v0.106.1）、Pulsar（1.132.0〜1.133.0）、
  CotEditor（7.0.3〜7.1.3。機能の版は 7.1.0）、ActivityWatch（v0.14.0・v0.14.1）、kakoune v2026.05.21、Firefox 159（10-27。公式の開発者向けノートは未完成で、
  ニュース記事で確認）。
- 部屋に関わる項目が無かったもの：fish 4.7.0〜4.9.3（GitHub の本文は件数の要約だけで、changelog は読んでいない）、SiYuan v3.8.7〜v3.8.9（ノートと DB の編集）、
  ShareX（本文が公式 changelog へのリンクだけ）、kakoune、Pulsar 1.133.0（本文なし）、GitHub Desktop の 10-24 以降の版。
- 前回までに読んだ版が最新だったもの：GitKraken Desktop（12.6.1・10-20）、Obsidian（1.15.0・10-22）、Fork（Windows 2.23・Mac 2.71）、Windows Terminal（10-02 以降の版なし）、
  lazygit・zellij・Ghostty・Helix・WezTerm（10-20 以降の正式版なし）。
- 見つからなかった・読めなかったもの：Visual Studio 2026 18.12（検索に出ず、18.11 までは前回読了）、IntelliJ IDEA／Rider 2026.3 EAP（記事が見つからず。
  intellij-community のタグは 2026.2.4 の修正版まで）、bisect を UI に載せた 2026 年の Git クライアント（見つからず。推す 4 は git 本体の版を出典にした）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）——前回までと同じ理由。
  Positron（データサイエンス＝非目標）、xpipe（リモート接続＝非目標）、komorebi・GlazeWM（ウィンドウマネージャー。期間内の版が 1 つ／無し）。

## 実装済み（2026-10-31）

推した 7 件のうち 6 件を Loomo に入れ、残る 1 件（2）は Terminal 側だけ済ませた。テストは Loomo 5343 件・Terminal 1483 件がすべて通る。**どれも実機の目視はまだ**。

| 推す | 設計 | コミット | 確かめたこと |
|---|---|---|---|
| 1. ブレークポイントとウォッチ式を部屋に残す | §28.16 | `0b3b8407` | `DebugRoomStateTests`（条件ごと写して戻す・消えたファイルは戻さない・ウォッチ式がセッションをまたぐ・ワークツリーの種でパスを付け替える） |
| 2. 端末から出たダイアログを手前に | （Terminal 側のみ） | Terminal `4af0c8c` | Terminal：`ITerminalSession.SetOwnerWindow`（既定で何もしない）・`ConPtySession` が `ConptyReparentPseudoConsole` を呼ぶ・ラッパーは中へ回す・`TerminalTabView` が開始時と読み込み直し（切り離し）で渡す。**Loomo 側は未着手**：常駐ホスト（§34）経由のタブは `RemotePtySession` → パイプ（新しいフレーム）→ `Pty.Host` の `ConPtySession` へ HWND を運ぶ必要があり、これは新しい Terminal パッケージの API に依存する。パッケージの公開待ち |
| 3. 時間のかかったコマンドの終わりを知らせる | §24.97 | `b74e9e33` | `LongCommandNoticeTests` |
| 4. bisect を Git ペインで | §24.100 | `820d69dc` | `GitBisectParserTests`、`GitBisectServiceTests`（実 git で 8 版から犯人を見つけて元へ戻る） |
| 5. ローカル履歴に名前を付けた版 | §36.8 | `7ad4af9b` | `LocalHistory_LabeledVersionsSurvivePruning_AndKeepTheirName` |
| 6. git が止めた操作に次の一手 | §24.99 | `79147baf` | `GitRefusalTests`、`GitCheckoutRefusalServiceTests`（実 git：退避して切り替える・切替先が無ければ退避を戻す） |
| 7. 初めて開く部屋で README | §24.98 | `683ced0d` | `RoomReadmeTests` |
