# 他ツールの新機能からの取り込み候補（2026-11-08・焦点：全領域・期間：11-01 以降の版＋未調査製品は 2026-05-08 以降）

物差し：主役は人間の部屋（§23 / §24 / §26 / §27）。§19.9 の「やらない」（拡張エコシステム・リモート開発・Web 版・共同編集・Notebook）と
クラウド必須・アカウント必須の機能は外した。「いまの Loomo」はコードを grep して確かめた結果。
前回までの 13 本（`feature-scout-2026-10-18*.md`〜`feature-scout-2026-11-01.md`）で推した・見送った・既にあった項目は重ねて挙げていない。

## 推す

### 1. Git ペイン・差分ペインで読んでいたコミットを、軌跡の点にする（元：GitLens 18.1.0・2026-06-09）
- 出典：https://github.com/gitkraken/vscode-gitlens/blob/main/CHANGELOG.md （18.1.0 の「Adds back/forward navigation to the _Commit Graph_ details panel and _Inspect_ view」）
- 元の機能：コミットの詳細の面に「戻る／進む」が付き、前に見ていたコミット・スタッシュ・作業中の変更へ順に戻れる（グラフの行も選び直す）。
- Loomo でやると：Git ペインで選んだコミット・差分ペインで開いたコミット範囲を、`TrailEntryKind` の末尾に足す新しい種別（例 `Commit`）の点として積む。
  target はリポジトリとハッシュ（範囲なら両端）、label は件名。戻ると Git ペイン（または差分ペイン）を前に出してそのコミットを選び直す——
  §24.74 の `loomo://` のコミットへの受け口と同じ道を使える。blame（§24.42）・行の履歴（§24.38）・親コミットと飛び歩いたあとに
  「さっき読んでいたあのコミット」へ戻れる。いまは Git ペインに戻っても**どのコミットを読んでいたかが軌跡に残らない**。
  §27 の「人間のナビゲーション」が、エディタ・ブラウザ・プレビューでは対象まで戻るのに、Git の面ではペイン止まりになっている穴を埋める。
  デデュープは File と同じ考え方（同じコミットを選び直しても 1 点・一覧を矢印キーで流している間は点にしない＝§27.3.3 の「とどまったら確定」）。
- いまの Loomo：部分的にある。軌跡の `Git` 種別は変更系コマンドのログ専用で戻り先を持たない（`docs/設計/27-軌跡.md` §27.2、
  `src/Loomo.App/Views/Shell/Activity/ShellWindow.Trail.cs:220` の `CanJumpToTrailEntry`）。Git ペインへの切替は `Pane` の点になるだけで、
  選んでいたコミットは載らない（`src/Loomo.App/ViewModels/Sessions/TrailModels.cs:13` の種別に該当なし。Git／Diff の View・VM から軌跡への記録は無い）。
- 作業の置き場：Loomo のみ
- 規模：M（新しい種別の記録点・デデュープ・戻るときの選び直し・SQLite の種別の追加。§27.3.5 の表に 1 行）
- 関連：§27・§24.15・§24.74

### 2. Loomo を閉じても OS を再起動しても、ターミナルのタブを前の画面ごと戻す（元：kitty 0.50.0・2026-11-03）
- 出典：https://sw.kovidgoyal.net/kitty/changelog/ （0.50.0「A new option restore_session_after_logout to save the state of every kitty instance, including scrollback, when logging out, restarting or shutting down and restore it the next time kitty starts」）
- 元の機能：ログオフ・再起動・シャットダウンのときに、すべての窓の状態をスクロールバックごと保存し、次に起動したときに戻す。
- Loomo でやると：常駐ホストがセッションを持っていないタブ（正常終了で全部殺した後・OS の再起動の後）を、**消さずに**同じ名前・cwd・分割・メモ・色で立て直し、
  §24.72 の書庫に残った画面を「前回の画面」として履歴の側に流してから新しいシェルを起こす。間に「ここまで前回の画面（11/07 18:02 に終了）・シェルは新しく起動」
  の区切りを 1 行入れる。§34 が「戻せないタブは消す」にした理由は「空のシェルを同じ名前で並べると戻ったと見誤る」ことなので、**画面は戻る・シェルは新しい**と
  区切りで言い切れば、その懸念には当たらない。週末に Windows Update で再起動されても、月曜に「どのタブで何をしていたか」が部屋に残る（§24.4 復元の完全性）。
  再生は §34 のホストの「ここから再生／ここまで再生」の括弧を使えば、括弧の中の OSC 133 は新しい出来事として数えない（袖のバッジを誤って立てない）。
  ホストは `WinExe`（コンソールを持たない）なので `SetConsoleCtrlHandler` の終了の合図は届かず、OS の終了の間際に書く作りは当てにできない。
  **出力が落ち着いたら数秒おきに画面を書き出しておく**（終わる瞬間に書かない）形にすれば、再起動でも停電でも直前の画面に戻る。
  書く中身は今の書庫の素の文字（`CreatePlainTextSnapshot`）ではなく、付け直しで使っている `CreateVtSnapshot`（`HostSession.cs:111`）にすると色・リンク・OSC 133 の印まで戻る。
  Claude Code が動いていたタブは、§38 のフックで分かるセッション ID から「`claude --resume` で会話へ戻る」を区切りの横に出す（自動では打たない）。
- いまの Loomo：部分的にある。正常終了のときはホストが画面を書庫へ書く（§24.72）が、次の起動ではそのタブを立て直さない
  （`src/Loomo.App/Services/Workspace/WorkspaceSessionCoordinator.cs:62` の `ResolveRestorableTerminalTabs`、§34「OS の再起動・ログオフ｜消える」）。
  書庫は手で「閉じたターミナルの画面を読む」で開くしかない。ホストは OS の終了を受けていない（`src/Loomo.Pty.Host` に終了の合図の処理が無い）ので、
  再起動では書庫にも残らない。
- 作業の置き場：Loomo のみ（Loomo.App と Loomo.Pty.Host）。ホストの再生の括弧を流用すれば Terminal の改修は要らない見込み。括弧の外から履歴を差し込む
  公開の口が要ると分かったら、Terminal に「起動前の画面を履歴として入れる」口を足して NuGet 公開する。
- 規模：M（タブの立て直しと書庫の結び付け・区切り・ホストの終了の合図。どのタブの書庫かを結ぶため、書庫の .json にタブ ID を足す）
- 関連：§34・§24.72・§24.4・§24.41

### 3. Git の変更一覧と差分ペインのファイルを、ターミナル・エディタ・ペグボードへドラッグで運ぶ（元：Zed v1.28.0-pre・2026-11-04）
- 出典：https://github.com/zed-industries/zed/releases/tag/v1.28.0-pre （「Added support for dragging files from the Git Panel to the terminal and editor panes」）
- 元の機能：Git パネルの変更ファイルを、ターミナルへ落とせばパスが入り、エディタへ落とせば開く。
- Loomo でやると：Git パネル（変更｜比較｜スタッシュ）・Git ペインのコミットのファイル一覧・差分ペインのファイル一覧の行をドラッグの源にする。
  エクスプローラー（`FolderTreeDragDropController`）と同じ `DataObject`（`FileDrop`）を出せば、ターミナルへのパスの差し込み・エディタで開く・ペグボードへ素材として
  置く、の受け口は既存のものがそのまま効く。過去のコミットのファイル（作業ツリーに無い版）は、§24.38 の「その版を読み取り用に開く」の写しを源にするか、
  `loomo://` のリンク（§24.74）として渡す。「いま変えたファイルを Claude Code のプロンプトへ」「このコミットの変更ファイルをペグボードへ」が、
  パスをコピーして貼る手間なしで済む（§24.3 素材の流れ）。
- いまの Loomo：未実装。ドラッグの源はエクスプローラー・ファイル列・ターミナルのリンク・タブ・ペグボードだけで（`DoDragDrop` は
  `src/Loomo.App/Services/FolderTree/FolderTreeDragDropController.cs:72` ほか）、`Views/Git`・`Views/Diff`・`Services/Git`・`Services/Diff` には無い。
- 作業の置き場：Loomo のみ
- 規模：S〜M（作業ツリーのファイルは S。過去の版を運ぶところまでやると M）
- 関連：§24.3・§24.52・§24.15

### 4. ワークツリーの行から、そのワークツリーで決まったコマンドを回す（元：GitLens 19.2.0・2026-09-16）
- 出典：https://github.com/gitkraken/vscode-gitlens/blob/main/CHANGELOG.md （19.2.0「Adds a _Run Default Task_ button to worktree working-changes (WIP) rows in the _Commit Graph_」）
- 元の機能：ワークツリーの作業中の行に「既定のタスクを実行」ボタン。1 回目にどのタスクを既定にするか聞き、以後は 1 クリックでそのワークツリーで走らせる。
- Loomo でやると：Git ペインの WT タブの各行に ▶ を置き、そのワークツリーを cwd にした**新しい**ターミナルタブ（§24.3.1：既存のタブには打たない）で
  決めたコマンド（`dotnet test` など）を走らせる。既定のコマンドはリポジトリごとに 1 つ覚え、最初は §24.20 の「最近のコマンド」から選ばせる。
  終わりは OSC 133 で袖のバッジ・動いているもの面（§24.54）・時間のかかったコマンドの通知（§24.97）に乗り、行の右に最後の結果（✓／✗・いつ）を出す。
  並べて進めているワークツリーのどれが緑かを、部屋を移らずに一覧で見られる（周辺視野）。
- いまの Loomo：部分的にある。WT の行の右クリックは「ターミナルでこの場所へ移動」（`src/Loomo.App/Views/Git/GitSessionView.xaml:663`）で、cd を送るだけ。
  そのワークツリーで何かを走らせて結果を行に戻す道は無い。行に出ているのは Claude Code の状態（§24.17 の `WorktreeAgentMarks`）と変更数。
- 作業の置き場：Loomo のみ
- 規模：M（既定のコマンドの保存・新しいタブで走らせる・OSC 133 の結果を行へ戻す）
- 関連：§24.17・§24.20・§24.54・§24.97

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| 除外したフォルダーの中の特定のファイルだけ見せる（`files.exclude` の `!`） | VS Code 1.145（2026-11-04） | Loomo のツリーには隠す設定そのものが無い（`bin`/`obj` 等は監視から外すだけ・`DebouncedFolderWatcher.cs:73`）ので前提が無い |
| Mermaid のサブグラフの題を線が避ける | VS Code 1.145 | 描画の品質。部屋に効かない |
| Timeline API の確定（拡張が Timeline に項目を足す） | VS Code 1.145 | 拡張の口（非目標） |
| 差分・ターミナルの Ctrl＋ホイールで文字の大小 | Zed v1.28.0-pre（11-04）／kitty 0.50.0（11-03） | 操作感の品質。ターミナルはキーで大小できる（Terminal の `TerminalKeyBindingCatalog` の `IncreaseFontSize`） |
| ワークツリーの選択をブランチ名で絞る・Git Graph の Refs 列 | Zed v1.28.0-pre | 前回までで扱い済み／列幅は既に変えられる（`GitLogColumnResizeController`） |
| 他の窓に吸着する窓（ドック）・タブを名前付きの作業場として使う・シェーダー | kitty 0.50.0 | 端末アプリの窓の管理。Loomo はペインとワークスペースで持つ |
| リンクの行き先をマウスの横にツールチップで出す | kitty 0.50.0 | 端末の品質 |
| `:editor` で 3 面以上の衝突を解く・バイナリを検索から外す | jj v0.47.0（11-04） | jj 固有／検索の品質 |
| チェックアウトごとの端末とエージェントの印 | GitButler 0.23.2（11-02・実験） | ワークツリーごとのターミナルと Claude Code の印は §24.17 にある |
| 使っていないワークツリーを 2 日でまとめる | GitButler 0.23.2（実験） | §24.55 の片付け（最後に触った日）で足りる |
| リポジトリごとに使うアカウントを選ぶ | GitHub Desktop 3.6.11-beta1（11-03） | アカウント管理。資格情報は聞かれたときに部屋で答える（§24.101） |
| コミットまでの push・ステージ分だけ／選んだファイルだけスタッシュ・共同作成者を足す・署名の印・リモート追加の到達確認・1 本のタグを push | GitLens 18.1.0〜19.5.0（06-09〜10-27） | Git の道具の品質。部屋に効かない（前回までの判断と同じ） |
| 無視されたファイルもスタッシュに入れる・GitLab の OAuth・SHA-256 リポジトリ・OpenSSH | IntelliJ IDEA 2026.3（11-05） | Git の道具の品質／アカウント／リモート接続 |
| ステータスバーの部品の並べ替え・ツールウィンドウの縦の名前 | IntelliJ IDEA 2026.3 | 見た目の設定。部屋の性質に効かない |
| Markdown を保存するとき行末の空白を削る・プレビューの絶対パスのリンク | IntelliJ IDEA 2026.3 | エディタの品質（行末の空白は `.editorconfig` を明示したときに保存で効く・§30 の Format on save） |
| 端末を ghostty のエミュレーターに・複数行の URL | IntelliJ IDEA 2026.3 | 端末の描画の品質 |
| `.http` の環境の切替 | Posting 2.11.0（09-26）・3.0.0-beta.0（10-13） | §24.30 の「まだ無いもの」に既に載っている（10-24 の判断と同じ） |
| 送った応答の履歴（100 件）・変数の出所の画面・段階ごとの所要時間・応答の中の検索・JSON の木・リクエストのタブ・外からの遠隔操作 | Posting 2.11.0・3.0.0-beta.0 | `.http` の道具の品質。応答は §24.30.2 で残している |
| 作業中の説明を操作をまたいで残す・Redo | gg v0.41.0（09-07）・v0.46.0（10-20） | コミットメッセージの書きかけは §24.32 にある／jj 固有 |
| その場で PR の絞り込みの区画を足す・消す | gh-dash v4.26.0（09-21） | PR 一覧の道具の品質（§24.27） |
| 送るヘッダーを外す・本文付きで失敗・設定ファイル | Hurl 8.1.0（10-27） | CLI の HTTP クライアントの品質 |
| 応答の中の検索・WebSocket の待ち | kulala.nvim v6.31.0〜v6.35.0（09-25〜10-19） | `.http` の道具の品質 |
| MCP のタスクを裏へ回す・許可を聞かずに拒む設定ほか | Claude Code 2.1.317〜2.1.319（11-03〜11-06） | §38 の連携（タイトル・フック・IDE の口）に関わる新しい口は無い。2.1.318 の「IDE の端末では IDE 拡張を入れない」は VS Code 系の話で Loomo は対象外 |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| 現在のブランチの上流と既定ブランチを裏で fetch する | Zed v1.28.0-pre | §24.92 |
| 新しい窓で README を開く | Zed v1.27.0（11-04） | §24.98 |
| push が「手元が遅れている」で拒まれたら取り込めと言う | Zed v1.27.0 | §24.99（`GitBranchService.ExplainPushRefusalAsync`） |
| プログラムが自分の状態を言う（OSC 7501）をタブに出す | kitty 0.50.0 | §24.61 |
| 変更の塊・ファイルを Git のパッチとして写す | GitButler 0.23.2 | §24.79 |
| PR をワークツリーで開く | IntelliJ IDEA 2026.3 | §24.27（「ワークツリーで開く（別の部屋）」）・§42 |
| fixup コミット | GitLens 19.1.0 | `src/Loomo.Services/Git/GitRebaseService.cs`（過去のコミットへの amend を fixup＋autosquash で） |
| タグを push する | GitLens 19.1.0 | `GitSessionOperationController.cs:25` の `GitTagOperation.Push`／`PushAll` |
| ワークツリーをターミナルで開く・兄弟のワークツリーの同じファイルと比べる・作業中の変更を別のワークツリーへ写す | GitLens 18.1.0・18.3.0 | `GitSessionView.xaml:663`・§24.80・§24.81 |
| 作業中のファイルごとの増減の行数 | GitLens 18.3.0 | §24.32 |

## 調べた範囲

- 11-01 以降に出た版で一次情報を読んだもの：VS Code 1.145（2026-11-04。見出しを全部、非 AI の 4 項目は本文まで）、Zed v1.27.0・v1.28.0-pre（11-04）、
  kitty 0.50.0（11-03。changelog の詳細を全部）、jj v0.47.0（11-04）、GitButler 0.23.2（11-02）、GitHub Desktop 3.6.8〜3.6.11-beta1（11-02〜11-03）、
  Claude Code 2.1.317〜2.1.319（11-03〜11-06）、IntelliJ IDEA 2026.3 の What's New と What's Fixed（11-05。ブログの RSS の本文）、
  PowerToys v0.102.3101・v0.103.3062 preview（修正と依存の更新だけ）、atuin v18.24.5（AI のセッション取り込みが中心）、Zen 1.24.1b、Floorp v12.21.1、
  lazygit v0.66.3（修正だけ）、Gitnuro 2.1.0（修正だけ）、Yaak v2026.9.1・v2026.10.0-beta.2（修正と MCP）。日付は GitHub API とブログの日付で照合。
- 初めて一次情報を読んだ製品（2026-05-08 以降の版）：GitLens（v18.0.0〜v19.5.0・05-27〜10-27。CHANGELOG の非 AI の Added／Changed を全部）、
  Posting（2.11.0・3.0.0-beta.0）、gg（v0.41.0〜v0.46.0）、gh-dash（v4.24.0〜v4.26.2）、CopyQ（v16.0.0・v17.0.0）、Heynote（v2.9.1）、
  Hurl（8.1.0）、kulala.nvim（v6.29.0〜v7.0.0 の feat 行）。
- 部屋に関わる項目が無かったもの：CopyQ（保存先の移動と修正）、Heynote 2.9.1（修正だけ）、git-cola v4.19.0（本文がリンクだけで、relnotes は読んでいない）、
  rio v0.5.29（本文がリンクだけ）、Notepad4・espanso・Wox・nushell・starship（版の一覧だけ。エディタ単体・入力補助・ランチャー・シェルで、本文は読んでいない）。
- 期間内の版が無かった・見つからなかったもの：Rider 2026.3（.NET ブログの RSS に 11-08 時点で告知なし）、Visual Studio 2026 18.12（検索に出ず）、
  Warp（10.22 より新しい changelog の項目が見つからず）、Windows Terminal・Ghostty・WezTerm・Helix・Alacritty（11-01 以降の版なし）、
  Ditto・DevToys・lazydocker・gitui・ImHex・Hyper・Lite XL（GitHub に期間内の Releases なし）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）——前回までと同じ理由。

## 実装済み（2026-11-08）

推した 4 件をすべて入れた。設計は各節。テストは Loomo 5596 件がすべて通る。**どれも Loomo を起動しての実機の目視はまだ**。

| 推す | 設計 | 確かめたこと |
|---|---|---|
| 1. 読んでいたコミットを軌跡の点に | §24.103（§27 の記録点の表にも 1 行） | `TrailViewModelTests`（同じコミットは 1 点・ハッシュでない rev は積まない・リポジトリがあるときだけ戻れる・記号と絵姿が一意） |
| 2. Loomo を全部閉じてもタブを前回の画面ごと戻す | §34.13 | `TerminalScreenRestoreTests`（本物の cmd と本物のパイプで、最後の Loomo の `Leave` で画面を残し、同じタブ ID の次のシェルで前回の画面→区切りの順に届く・手で閉じたタブは残さない・片付けで kitty キーボード／マウス／代替画面を落とす）。話し合いで「戻すのは画面だけ・Claude Code だけは `claude --resume` で会話へ戻す」に決めた。撮るのは OS の終了の間際ではなく、最後の Loomo の正常終了でホストが全部殺すとき（上の推す 2 の「数秒おきに書き出す」案は採らなかった） |
| 3. Git・差分のファイルをドラッグで運ぶ | §24.102 | 既存の VM のパス解決に乗るだけなので単体テストは足していない。実機のドラッグは未確認 |
| 4. ワークツリーの行からコマンドを回す | §24.104 | `WorktreeRunsTests`（印の 3 状態・リポジトリごとに覚える・ブランチの行に付いて外れる） |
