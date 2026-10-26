# 他ツールの新機能からの取り込み候補（2026-10-26・焦点：前回までの 9 本で見ていない製品・期間：2026-04-26〜10-26）

## まず手を付けるなら

1. 裏で fetch する（#1）——§24.64 の「上流とのずれ」と §24.40 の衝突予告が、いまは最後に手で fetch した時点のまま止まっている
2. マージ・リベースの確認に衝突の予告を出す（#3）——§24.64 の `GitConflictForecast` を流用でき、S
3. コミット一覧に CI の結果を出す（#2）——PR 一覧では既に `statusCheckRollup` を取っているのに、コミット側には無い
4. 消えたファイルを探して戻す（#4）

## 推す（§4 を満たしたもの全部。効き目の大きい順に番号を振る）

### 1. 裏で fetch する——部屋を開いたときと一定の間隔で（元：Git Extensions 7.0.0・2026-05-31／SourceGit v2026.10・2026-05-04）
- 出典：https://github.com/gitextensions/gitextensions/releases/tag/v7.0.0 （`feat(BackgroundFetchPlugin): Add option to fetch on repo opening`）、
  https://github.com/sourcegit-scm/sourcegit/releases/tag/v2026.10 （`Enable Auto-Fetch` をリモートごとに外せる）
- 元の機能：リポジトリを開いたとき・一定の間隔で裏で `git fetch` する。SourceGit はリモートごとに入切できる。
- Loomo でやると：部屋（ワークスペース）を開いたとき・切り替えたときと、N 分ごとに裏で `git fetch --prune` を走らせる。
  Git ペインの ahead/behind、§24.64 の上流とのずれ、§24.40 の分岐元との衝突予告、§24.69 の「上流が消えた」がそのたびに新しくなる。
  離れて戻ったときに「いま見ている数字は古い」ことが無くなる（周辺視野の信頼）。fetch したことは軌跡に点として残さない（人間のナビゲーションではない）。
  リモートごとの入切は `.git/config` ではなく Loomo の設定（ワークスペースごと）に持つ。
- いまの Loomo：未実装。`src` 全体に定期・自動の fetch は無い（`Fetch.*(Timer|Interval)`・「自動 fetch」とも 0 件。
  `GitSessionViewModel.cs:489` の間隔は PR 一覧の取り直しだけ）。
- 作業の置き場：Loomo のみ。fetch の実行は `ChildProcessIo` 経由・`GIT_TERMINAL_PROMPT=0`（資格情報の入力待ちで固まらないように）。
- 規模：M（間隔・入切の設定、認証失敗時に黙って止める扱い、動いているもの面（§24.54）に出すかの判断）
- 関連：§24.40・§24.64・§24.69・§24.54

### 2. コミット一覧に CI の結果を出す（元：Git Extensions 7.0.0・2026-05-31）
- 出典：https://github.com/gitextensions/gitextensions/releases/tag/v7.0.0 （`feat: Add GitHub Actions build server integration`、
  `feat: auto-detect Azure DevOps for build status and PR creation`）
- 元の機能：コミット一覧の各行に、そのコミットのビルド（GitHub Actions／Azure DevOps）の成否を印で出す。リモートから自動で判定する。
- Loomo でやると：Git ペインのコミット一覧の行に ✓／✗／◌ を出し、クリックで実行のページをブラウザペインで開く。
  GitHub は既存の `gh` 連携で `gh run list --json headSha,conclusion,url` を 1 回引いて SHA で引き当てる。Azure DevOps は既存の ADO 連携
  （§24.56）に `_apis/build/builds?sourceVersion=` を足す。push したコミットが落ちたことを、ブラウザを開かずに部屋の中で知れる。
- いまの Loomo：部分的にある。PR 一覧（§24.39）は `statusCheckRollup` を取っている（`Loomo.Services/GitHub/GitHubJson.cs:17`）が、
  コミット一覧にビルドの状態は無い。ADO のビルド API の呼び出しは無い。
- 作業の置き場：Loomo のみ
- 規模：M（GitHub と ADO の 2 系統、取得の間引き（#1 の fetch と同じ間隔に乗せる））
- 関連：§24.39・§24.56

### 3. マージ・リベースの確認に、衝突するファイルの予告を出す（元：SourceGit v2026.12・2026-06-01）
- 出典：https://github.com/sourcegit-scm/sourcegit/releases/tag/v2026.12 （`Testing for merge and show result in Merge popup`、
  `Testing for rebase and show result in Rebase popup`）
- 元の機能：マージ／リベースの画面を開いた時点で、実行したら衝突するかを試し、結果をその画面に出す。
- Loomo でやると：ブランチの右クリックの「マージ」「リベース」の確認に、`git merge-tree` で出した衝突するファイルの一覧を載せる。
  §24.64（上流）・§24.40（分岐元）で既に作った `GitConflictForecast` を、相手を選んだブランチに変えて呼ぶだけ。
- いまの Loomo：部分的にある。予告の仕組みは上流と分岐元にだけ使われていて、ブランチのマージは確認無しにすぐ走る
  （`Loomo.App/Services/Git/GitSessionOperationController.cs:140-150`）。リベースは §24.78 の書き換え警告だけ出る。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24.40・§24.64・§24.78

### 4. 消えたファイルを探して戻す（元：Magit 4.7.0・2026-08-01）
- 出典：https://github.com/magit/magit/blob/main/docs/CHANGELOG.4 （v4.7.0：`New command magit-show-commit-removing-file`）
- 元の機能：いまは無いファイルについて、それを消したコミットを出す。
- Loomo でやると：検索ペインかパレットに「消えたファイルを探す」を足し、`git log --diff-filter=D --name-only` から名前で絞った一覧
  （消したコミット・日付・作者）を出す。選ぶと消す直前の版を読み取り専用で開き、「作業ツリーへ戻す」で `git restore --source=<commit>^` する。
  ツリーにもタブにも無いものへの入口になり、部屋の「離れて戻る」を過去の削除まで伸ばす。
- いまの Loomo：未実装。`--diff-filter` の呼び出しは無い（`src` 全体で 0 件）。ファイルの履歴（`EditorGutterGitService.cs:67` の
  `GetFileHistoryOutput`）は今あるファイルから引く形。
- 作業の置き場：Loomo のみ
- 規模：M
- 関連：§24.38・§24.59（`loomo://` で消えた版を指せると良い）

### 5. 取り消しの履歴を、タブを閉じても・再起動しても残す（元：Insomnia 13.2.0・2026-08-25）
- 出典：https://github.com/Kong/insomnia/releases/tag/core%4013.2.0 （`feat(undo): broaden undo-history persistence via a stable historyKey`
  #10233、`feat(undo): preserve editor undo history across tab changes` #10299）
- 元の機能：エディタを作り直しても（タブの切替・再描画）安定した鍵で取り消しの履歴を引き継ぐ。
- Loomo でやると：Vim の `undofile` に当たるものを入れる。ファイルの内容のハッシュを鍵に取り消しの木を `%APPDATA%/Loomo/undo/` に書き、
  開き直したとき・再起動したとき・部屋を切り替えて戻ったときに、内容が一致すれば戻す。復元は「キャレット行とスクロール率まで」だが、
  手の中の取り消しだけは今は戻らない。書き出しは §31.15 の `DeferredWriteQueue` に乗せ、打鍵の道には置かない。
- いまの Loomo：未実装。Editor は `undofile`／`udf` を受け付けるが何もしない（`C:\Projects\Editor\src\Editor.Core\Config\VimOptions.cs:175` が `null`）。
  `UndoManager` の状態は時刻つきでメモリにだけある。
- 作業の置き場：Editor（`UndoManager` の書き出し・読み込みの口。NuGet 公開が要る）＋ Loomo（保存先と鍵、`LocalHistoryStore` の隣に置く）
- 規模：M（取り消しの木の直列化と、内容が外で変わったときに捨てる判定）
- 関連：§24（復元）・§31.15・§36（ローカル履歴）

### 6. コミットメッセージで、変えたファイル名・識別子を補完する（元：Magit 4.6.0・2026-07-01）
- 出典：https://github.com/magit/magit/blob/main/docs/CHANGELOG.4 （v4.6.0：`In commit messages, complete-symbol completes modified definitions and symbols from modified lines`、
  `git-commit-insert-changelog-*` で変更した定義の一覧を差し込む）
- 元の機能：コミットメッセージの入力中に、差分で変わった定義名・行の中の語を補完候補にする。
- Loomo でやると：Git パネルのコミットメッセージ欄で、ステージした差分の追加・削除行の識別子と、ファイル名を補完に出す。
  差分ペイン → メッセージへの素材の流れ。C# は LSP のドキュメントシンボルで「変えたメソッド名」まで出せる（§30 のプールを流用）。
- いまの Loomo：未実装。欄は素の `TextBox`（`Loomo.App/Views/Git/GitPanelView.xaml:501`）で補完は無い。
- 作業の置き場：Loomo のみ（`TextBox` のまま簡易ポップアップ）。欄を Composer と同じ LSP 無しの `VimEditorControl` に替えるなら、
  Editor の `HostSupplementalCompletionProvider` がそのまま口になる。
- 規模：M
- 関連：§24.32（書きかけ）・§30

### 7. git が拒んだリポジトリ（所有者が違う）を、確かめてから信頼する（元：SourceGit v2026.22・2026-10-12）
- 出典：https://github.com/sourcegit-scm/sourcegit/releases/tag/v2026.22 （`feature: confirm before trusting a repository refused by git`）。
  Git Extensions 7.0.0 にも `feat(WSL, dubious ownership): Improve first use of WSL repo` がある。
- 元の機能：`detected dubious ownership` で git が動かないリポジトリを開いたとき、理由を示し、承認すれば `safe.directory` に足す。
- Loomo でやると：部屋のフォルダーで git がこのエラーを返したら、Git ペインに「このフォルダーは所有者が違うため git が拒否しています」と
  出し、「信頼する」で `git config --global --add safe.directory <path>` を確認つきで実行する。外付けドライブ・別ユーザーで clone したもの・
  WSL のリポジトリで、Git ペインが黙って空になる状況を無くす。
- いまの Loomo：未実装。`safe.directory`・`dubious ownership` の扱いは `src` に無い。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24（Git ペイン）

### 8. 右側・左側のタブをまとめて閉じる（元：KDE Gear 26.08（Dolphin）・2026-08-20）
- 出典：https://kde.org/announcements/gear/26.08.0/
- 元の機能：タブの右クリックで、右側／左側／両側のタブを閉じる。
- Loomo でやると：TABS とタブ帯の右クリックに「右側を閉じる」「左側を閉じる」を足す。§24.77 でキーボードで並べ替えられるようになったので、
  並べてから片側をまとめて片付ける動線になる。閉じたものは §24.41／§24.29 の開き直し・棚に乗る。
- いまの Loomo：部分的にある。「閉じる」「他のタブを閉じる」「すべて閉じる」だけ（`Loomo.App/Views/Tabs/TabsView.xaml:145-151`）。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24.29・§24.41・§24.77

### 9. JSON の絞り込みに一致した数を出す（元：Insomnia 13.2.0・2026-08-25）
- 出典：https://github.com/Kong/insomnia/releases/tag/core%4013.2.0 （`feat: show number of matches when filtering a JSON response` #10261）
- 元の機能：応答の JSON を絞り込むと、一致した数を出す。
- Loomo でやると：JSON の EditorSupport（`.http` の応答の表示にも使う）の絞り込み欄の横に「n 件」を出す。0 件と「まだ描画中」の区別が付く。
- いまの Loomo：部分的にある。絞り込みはある（`Loomo.App/Services/Editor/Support/JsonEditorSupport.cs:552`）が件数は出ない。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：§24.30

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| コミット一覧に git notes の列 | Git Extensions 7.0.0 | notes を使うリポジトリがまれで、部屋の周辺視野への効き目が薄い |
| skip-worktree のファイルを差分に出す | Git Extensions 7.0.0 | 使う人が限られる。効き目が薄い |
| ワークスペースの窓配置を複数ディスプレイにまたがって保存・復元 | Wox v2.3.0（2026-07-04） | Loomo の切り離し窓は部屋ごとに永続化済み。他アプリの窓を並べるのは部屋の外 |
| カウントダウンタイマー・音声入力・ファイルの中身の索引 | Wox v2.4.0（2026-08-23） | 部屋の道具ではない（ランチャーの機能） |
| Zen Mode（気を散らさない表示） | Qt Creator 20（2026-06-17） | ステージ（§24.1）のソロ表示で足りている |
| VCS の操作を「開いている文書」一覧にも | Qt Creator 20 | TABS の右クリックに Git の操作が既にある範囲で足りる。具体的な項目が記事に無い |
| ACP／MCP でエージェントを繋ぐ | Qt Creator 20 | AI が主役の機能。Claude Code 連携（§38）は IDE プロトコルで済んでいる |
| データの表で回すコレクション実行・AI チャット | Hoppscotch 2026.8.0／2026.9.0 | `.http` の範囲を超える／クラウド前提 |
| SSH 鍵の生成・統計の円グラフ・git-flow | SourceGit v2026.14〜v2026.22 | 部屋に効かない |
| 変更を捨てるときに変更・削除を除外する | SourceGit v2026.08 | 期間外（2026-04 以前）かつ効き目が薄い |
| WSL への直接アタッチ・ASAN の起動 | WinDbg 1.2606.22001.0 | Loomo のデバッグは netcoredbg／自前の DAP。WinDbg の道具立ては部屋に関わらない |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| マウスの戻る／進むボタンでペインの履歴を戻る | SourceGit v2026.19 | `Loomo.App/Services/Editor/Interaction/MouseNavigationPolicy.cs:20` |
| バイナリを 16 進で見る | SourceGit v2026.18 | `Loomo.App/Services/Editor/Support/HexEditorSupport.cs` |
| reflog を見る | Magit 4.7.0 | `Loomo.App/ViewModels/Git/GitReflogViewModel.cs` |
| リモートのブランチをワークツリーに置く | SourceGit v2026.22 | `Loomo.Services/Git/GitWorktreeModels.cs:81`（リモートにしか無い名前は追跡付きで作る） |
| 上流とのずれ・上流と比べる | SourceGit v2026.11 | §24.64（`GitService.cs:148`） |
| 選んだ行をパッチとして写す・クリップボードのパッチを当てる | SourceGit v2026.13 | §24.79・§24.38 |
| ツリーの絞り込みでワイルドカード | Dolphin（KDE Gear 26.08） | `Loomo.App/ViewModels/Files/FilesPaneModels.cs:355`（`*`／`?` で全体一致。正規表現は無い） |
| ターミナルのリンクをドラッグで他へ運ぶ | Konsole（KDE Gear 26.08） | §24.52 |
| 右クリックから .gitignore に足す | Git Extensions 7.2.0／SourceGit v2026.14 | `Loomo.App/Services/FileSystem/FilesColumnCommandController.cs:216` |

## 調べた範囲

- 一次情報を読んだもの：SourceGit の GitHub Releases（v2026.10〜v2026.22・2026-05-04〜10-12、本文の全項目）、Git Extensions の GitHub Releases
  （v7.0.0・v7.0.1・v7.1.0・v7.2.0・v7.2.1、2026-05-31〜08-18。7.0.0 は `feat` 行を全部）、Magit の `docs/CHANGELOG.4`（v4.6.0・v4.7.0・v4.7.1）、
  tig 2.6.1（2026-06-13）、gitu v0.42.0・v0.43.0、Insomnia の GitHub Releases（core@13.1.0〜13.3.0）と PR #10233 の本文、
  Hoppscotch の GitHub Releases（2026.5.0〜2026.9.0）、Flow Launcher v2.1.2〜v2.1.4、Wox v2.3.0・v2.4.0、Qt Creator 20 のリリース記事（2026-06-17）、
  KDE Gear 26.08 の公式告知（2026-08-20）。日付は GitHub API で照合（Qt Creator・KDE は記事の日付）。
- 部屋に関わる項目が無かったもの：tig 2.6.1（修正と小さな改善だけ）、gitu（Magit 型の TUI。Loomo に既にある操作）、Flow Launcher（修正だけ）、
  Meld 3.24.0（2026-06-20。ビルドと CI の項目だけ）。
- 期間内の版が無かった・見つからなかったもの：TortoiseGit（2.19.0・2.19.1 は 2026-06 だが GitHub に Releases が無く、リリースノートの本文は読めず）、
  KDiff3・gitui（期間内の版なし）、Araxis Merge（2025 系が最新）、Kate 単体の変更（KDE Gear 26.08 の告知に項目なし）、
  Windows 11 のエクスプローラー（2026 年の新機能の一次情報が見つからず）。
- 一次情報を読めなかったもの：Eclipse IDE 2026-09 の New & Noteworthy（メーリングリストの下書きだけ）、WinDbg 1.2610（要約だけ）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）——前回までと同じ理由。
- 前回までの 9 本（`feature-scout-2026-10-18*.md`〜`feature-scout-2026-10-25-未調査製品3.md`）で推した・見送った・既にあった項目は重ねて挙げていない。

## 実装済み（2026-10-26）

推した 9 件はすべて実装した。設計は `docs/設計/03-UIとレイアウト.md` の各節。

| 推す | 設計 | 実機で確かめたこと |
|---|---|---|
| 1. 裏で fetch する | §24.92 | 部屋を開いて数秒で遠隔だけのコミットが `origin/main` に現れ ↓1、フェッチのボタンに最後に取得した時刻 |
| 2. コミット一覧に CI の結果 | §24.93 | VGrid の一覧で ✓／✗、載せると説明、押すと Actions の実行ページがブラウザペインで開く |
| 3. マージ・リベースの前に衝突の予告 | §24.88 | 衝突するブランチのマージで a.txt を並べて確認、リベースの確認にも添える |
| 4. 消えたファイルを探して戻す | §24.94 | パレットに消したファイル、「作業ツリーへ戻す」でステージされて開く |
| 5. 取り消しの履歴を残す | §24.96（Editor 1.0.109） | タブを閉じて開き直す／Loomo を閉じて開き直すと `u` で閉じる前の編集を戻せる |
| 6. コミットメッセージの補完 | §24.95 | 打ちかけの語の下にファイル名、Tab で入る |
| 7. 所有者が違うリポジトリを信頼する | §24.91 | **実機では未確認**（別ユーザーか管理者権限で作ったフォルダーが要る）。git の文面の読み取りと文面はテストで確認 |
| 8. これより後・前のタブを閉じる | §24.89 | TABS の右クリックで後ろのタブだけ閉じる |
| 9. JSON の絞り込みに一致数 | §24.90 | 「2 件」「一致なし」 |
