# 他ツールの新機能からの取り込み候補（2026-10-25・焦点：前回までに見ていない製品・期間：2026-04-25〜2026-10-25）

同じ日の 2 本（`feature-scout-2026-10-25.md`・`-未調査製品2.md`）までの 8 本で、主要製品の一次情報はほぼ読み切っている。
今回は **まだ一度も読んでいない製品**として、GNU Emacs 31.1・WinMerge・Visual Studio 2026 18.11 の未採用分・GitHub CLI・Zettlr・
Floorp・Everything 1.5・Directory Opus・Joplin・Microsoft Edit・PowerShell を見た。前回までに推した・見送った・既にあった項目は重ねて挙げていない。

物差しは前回と同じ：主役は人間の部屋（§23 / §24 / §26 / §27）。§19.9 の「やらない」（拡張エコシステム・リモート開発・Web 版・
共同編集・Notebook）とクラウド必須のものは外す。

## まず手を付けるなら

1. **未コミットの変更を別のワークツリーへ移す・写す**（Emacs 31.1）——ワークツリー＝部屋なので、部屋どうしの素材の流れがそのまま増える。
2. **このファイルを、ほかのワークツリーの同じ場所で開く・比べる**（Emacs 31.1）——同じく部屋どうしの行き来。
3. **プッシュ済みのコミットを書き換える前に、そうだと言う**（Emacs 31.1）——確認文はあるが、共有済みかを見ていない。
4. **検索結果をその場で書き換えて戻す**（Emacs 31.1）——検索ペイン→ファイルの素材の流れ。

## 推す

### 1. 未コミットの変更を別のワークツリーへ移す・写す（元：GNU Emacs 31.1・2026-08-24）
- 出典：Emacs 31.1 の `etc/NEWS`（`C-x v w a`「Copy or move fileset changes to another working tree」・`C-x v w A`「Copy or move all changes」）
  https://raw.githubusercontent.com/emacs-mirror/emacs/emacs-31/etc/NEWS ／ リリース告知 https://lwn.net/Articles/1090308/
- 元の機能：選んだファイル（または全部）の未コミットの変更を、同じリポジトリの別のワークツリーへ写す、または移す（こちらからは消す）。
- Loomo でやると：Git ペインの変更一覧の右クリックに「別のワークツリーへ移す／写す ▸（WT の一覧）」。中身は既にある
  パッチの組み立てと当て方（§24.38.1 の `GitPatchService`：素の `git apply` → 当たらなければ `--3way`）をそのまま使い、
  当てる前に対象ファイルと方式を見せる確認も同じものを出す。移すときは当たったのを確かめてからこちらを破棄する。
  行き先の部屋が開いていれば `InstanceRelay` で知らせて、差分ペインに今入った変更を出す。「main で書き始めてしまった変更を
  作業用のワークツリーへ逃がす」が、stash→切替→pop の 3 手でなく 1 手になる。部屋＝ワークツリーという Loomo の作りに
  いちばん素直に効く。
- いまの Loomo：未実装。ワークツリーへ移るときはタブを写す（`src/Loomo.App/Services/Workspace/WorktreeRoomSeed.cs:147`）、
  作るときに無視されているフォルダーを引き継ぐ（§24.48）が、**未コミットの変更を渡す口は無い**（`ShellWindow.Worktrees.cs:65` の切替も変更は運ばない）。
  変更一覧の右クリックは「このファイルをステージ／破棄」等だけ（`src/Loomo.App/Views/Git/GitPanelView.xaml`）。
- 作業の置き場：Loomo のみ。
- 規模：M（パッチの組み立て・当て方は既存。行き先の選択、移すときの「当たったら破棄」の順序、衝突時の戻し方を足す）。
- 関連：§24.17.1・§24.38.1・§24.48・§24.55

### 2. このファイルを、ほかのワークツリーの同じ場所で開く・比べる（元：GNU Emacs 31.1・2026-08-24）
- 出典：Emacs 31.1 の `etc/NEWS`（`C-x v w w`「Visit this file in another working tree」と `project-find-matching-buffer`
  「switching between projects with similar file trees (such as Git worktrees of the same repository)」）——出典 URL は 1 と同じ
- 元の機能：いま開いているファイルの、別のワークツリーにある同じ相対パスのファイルへ一手で行く。
- Loomo でやると：エディタのタブ（TABS）の右クリックに「ほかのワークツリーで ▸」——各ワークツリーの行に「その部屋で開く」と
  「差分で比べる」。開くは §24.17.1 の切替＋`OtherRoomOpen`（起動中の部屋へ中継）に乗せ、比べるは既存のファイル同士の比較
  （`DiffComparison`）に 2 つのパスを渡すだけ。相手のワークツリーに同じファイルが無ければ行を薄くして「無い」と書く。
  「本線ではこの関数はどうなっていたか」を、ブランチの比較を組まずに 1 ファイルで確かめられる。
- いまの Loomo：部分的にある。相対パスの読み替えは部屋の写しのときだけ使っている（`WorktreeRoomSeed.cs:147`）。
  別の部屋へファイルを渡す口はある（`src/Loomo.App/Services/Startup/OtherRoomOpen.cs`）が、行き先は「起動中の別の Loomo」で、
  **同じファイルの別ワークツリー版**を引く入口は無い。
- 作業の置き場：Loomo のみ。
- 規模：S（ワークツリー一覧・相対パスの読み替え・比較・中継はすべて既存。メニューを足して繋ぐ）。
- 関連：§24.17.1・§24.5・§24.55

### 3. プッシュ済みのコミットを書き換える前に、そうだと言う（元：GNU Emacs 31.1・2026-08-24）
- 出典：Emacs 31.1 の `etc/NEWS`（`vc-allow-rewriting-published-history`「Some VCS commands can change your copy of published change
  history without warning. In VC we try to detect before that happens, and stop.」）——出典 URL は 1 と同じ
- 元の機能：公開済みの履歴を書き換える操作を、実行前に検出して止める。
- Loomo でやると：「コミットから外す」「メッセージを修正」「スカッシュ」「インタラクティブリベース」「リベース」と amend の確認で、
  対象のコミットが上流（または任意のリモートブランチ）に既に含まれているかを `git branch -r --contains` で見て、含まれていれば
  確認文の先頭に「このコミットは origin/main にプッシュ済みです。書き換えると強制プッシュが要り、他の人の手元とずれます」と
  書き、確認の重さを `GitConfirmationSeverity.Warning` へ上げる。止めはしない（強制プッシュの口は既にある）。
- いまの Loomo：部分的にある。確認文は「履歴が書き換わります」と一律に言うだけで、**共有済みかどうかを見ていない**
  （`src/Loomo.App/Services/Git/GitSessionOperationController.cs:267` の「このコミットから外す」、同ファイル 305〜360 行のメッセージ修正・
  インタラクティブリベース・スカッシュ）。
- 作業の置き場：Loomo のみ。
- 規模：S（含まれるかの問い合わせ 1 本と、確認文の分岐）。
- 関連：§24.62・§24.64

### 4. 検索結果をその場で書き換えて戻す（元：GNU Emacs 31.1・2026-08-24）
- 出典：Emacs 31.1 の `etc/NEWS`（「Grep results can be edited to reflect changes in the originating file. … typing 'e' in the "*grep*"
  buffer will now make the 'grep' results editable.」と `xref-change-to-xref-edit-mode`）——出典 URL は 1 と同じ
- 元の機能：grep・参照一覧の結果の行を直接書き換え、確定するとそれぞれの元ファイルの該当行に反映される。
- Loomo でやると：検索ペインの結果に「行を編集」モード。各一致行がその場で書ける欄になり、確定で書き換えた行だけを元ファイルへ戻す。
  一律の置換（既存）では書けない「行ごとに違う直し」（呼び出しごとに引数を足す等）を、ファイルを 1 つずつ開かずに済ませる。
  開いているタブのファイルは既存の置換と同じ経路（エディタのバッファへ当てて 1 回の取り消し）、開いていないファイルは
  ディスクへ書き `FilesReplacedOnDisk` で知らせる。書く前に元の行が検索時から変わっていないかを確かめ、変わっていたら
  その行は飛ばして件数で言う。
- いまの Loomo：部分的にある。置換はある（`src/Loomo.App/ViewModels/Search/SearchPanelViewModel.cs:416` `ReplaceInFile`、
  置換前のプレビューに取り消し線 `SearchPanelView.xaml:671`）が、**行ごとに違う書き換えはできない**。
  残した検索タブは写しなので置換させない（`docs/設計/04-機能詳細.md` §23.3.1）。
- 作業の置き場：Loomo のみ（結果行の欄は Loomo の WPF。エディタのバッファへ当てる口は置換で使っているもの）。
- 規模：M（結果ツリーの行を編集可能にする UI、変わった行だけ集めて元へ戻す処理、検索後に元が変わった行の検出）。
- 関連：§23.3・§23.3.1

### 5. 選んだ変更をパッチにして運ぶ（元：WinMerge 2.16.58・2026-07-27）
- 出典：https://github.com/WinMerge/winmerge/releases/tag/v2.16.58（「Add a file list with checkboxes to the Generate Patch dialog」PR #3470）
- 元の機能：パッチを作るときにファイル一覧のチェックで含めるものを選ぶ。
- Loomo でやると：Git ペインの変更一覧で選んだファイル（複数可）の右クリックに「パッチとして ▸ クリップボードへ／保存…／エディタで開く」。
  §24.38.1 の「クリップボードのパッチを当てる」の相方で、別の PC・別のリポジトリ・ノート・チャットへ「この変更だけ」を運ぶ素材になる。
- いまの Loomo：部分的にある。パッチを出せるのはコミット（`src/Loomo.App/Views/Git/GitSessionView.xaml:1158`「パッチ ▸ エディタで開く／
  クリップボードへコピー」）とスタッシュ（`GitPanelView.xaml:690`「パッチとして保存…」）だけで、**作業ツリーの未コミットの変更からは出せない**。
- 作業の置き場：Loomo のみ。
- 規模：S（`git diff`（未追跡は `--no-index` か一時的な intent-to-add）を選んだパスで切るだけ。スタッシュの保存の作りを流用）。
- 関連：§24.31・§24.38.1

### 6. フォルダーの比較で、動かしただけのファイルを 1 件にまとめる（元：WinMerge 2.16.56・2026-04-27）
- 出典：https://github.com/WinMerge/winmerge/releases/tag/v2.16.56（「Extend "Merge renamed items" option to support moved items」PR #3181）、
  PR 本文 https://github.com/WinMerge/winmerge/pull/3181
- 元の機能：フォルダー比較で、名前を変えた・別のフォルダーへ動かしただけのファイルを「追加＋削除」の 2 行でなく 1 行に組み、
  中身が同じなら「同一（パスが違う）」と出す。
- Loomo でやると：§24.5.3 のフォルダー比較で、片側にしか無いファイルどうしを中身（サイズ→バイト列。既にある比べ方）で突き合わせ、
  同じなら「移動」として 1 件にし数だけトーストへ、少し違うなら「移動＋変更」の 1 件の比較（`DiffComparison`、題は `旧パス → 新パス`）に
  する。配布物の版比べでフォルダーを組み替えただけのときに、追加・削除の山で本当の違いが埋もれなくなる。
- いまの Loomo：部分的にある。フォルダー比較はあり、同一ファイルは並べない工夫もある（`docs/設計/03-UIとレイアウト.md` §24.5.3）が、
  片側にしか無いものは「追加」「削除」の印で別々に並ぶ（`src/Loomo.App/Services/Diff/FolderComparison.cs`）。
- 作業の置き場：Loomo のみ。
- 規模：S（同一判定は既存。片側だけのものどうしの突き合わせと、題・印を足す。似ている判定まで入れるなら M）。
- 関連：§24.5.3

### 7. ペインの並びを回す・縦横を入れ替える（元：GNU Emacs 31.1・2026-08-24）
- 出典：Emacs 31.1 の `etc/NEWS`（「New commands to modify window layouts of frames」：`window-layout-rotate-clockwise`・
  `window-layout-flip-leftright`／`-topdown`・`window-layout-transpose`「every horizontal split becomes a vertical split and vice versa」・
  `rotate-windows`）——出典 URL は 1 と同じ
- 元の機能：分割の配置全体を回す、左右／上下に裏返す、縦横を入れ替える、中身だけを巡回させる。
- Loomo でやると：コマンドパレットと `Ctrl+W` 系に「ペインの配置を時計回りに回す」「左右を入れ替える」「縦横を入れ替える」
  「中身を巡回させる」。`PaneNodeSnapshot` の木を書き換えて既存の読み込み口で組み直すだけで、比率と各ペインの中身・状態はそのまま。
  横長モニターで左右に置いていたものをノート PC で上下にしたい、を手で解体し直さずに済む。保存レイアウトの巡回（`Ctrl+T`）とも噛み合う。
- いまの Loomo：部分的にある。ドラッグで入れ替える口はある（`src/Loomo.App/Layout/PaneLayout.cs:112`）が、キーボード／パレットからの
  回転・裏返し・転置は無い（`src/Loomo.App/Input/CommandCatalog.cs:126-140` の `pane.*` は移動・リサイズ・分割・ズーム・閉じるだけ）。
- 作業の置き場：Loomo のみ。
- 規模：M（木の変換 4 種は純関数でテストしやすい。ステージモード・切り離し中のペインとの兼ね合いを確かめる）。
- 関連：§26（ペインのレイアウト）

### 8. タブをキーボードで並べ替える（元：Visual Studio 2026 18.11・2026-10-13／GNU Emacs 31.1・2026-08-24）
- 出典：https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes（18.11「Move documents with the keyboard」
  Ctrl+Shift+PgUp/PgDn）、Emacs 31.1 の `etc/NEWS`（`tab-line-move-tab-forward`）
- 元の機能：アクティブなタブをキーで左右へ動かす。
- Loomo でやると：`tab.moveLeft`／`tab.moveRight`（Ctrl+Shift+PgUp/PgDn）を `CommandCatalog` に足す。§24.47 で番号（Ctrl+1〜9）で
  タブを選べるようにした以上、並びは「よく使うものを左に寄せる」という意味を持ち、それをマウスのドラッグ無しで整えられる。
- いまの Loomo：未実装（`src/Loomo.App/Input/CommandCatalog.cs:269-279` の `tab.*` は新規・最近使った順・番号だけ）。
- 作業の置き場：Loomo のみ（エディタ・ブラウザのタブ帯は Loomo 側。ターミナルのタブ帯の並べ替え API が無ければ Terminal に
  `MoveTab(index)` を足す——要確認）。
- 規模：S
- 関連：§24.47・§24.65

### 9. 過去のコミットの 1 か所だけを打ち消す（元：GNU Emacs 31.1・2026-08-24）
- 出典：Emacs 31.1 の `etc/NEWS`（`diff-revert-and-kill-hunk`「reverts the hunk at point … useful to undo or revert changes, committed and
  uncommitted」）——出典 URL は 1 と同じ
- 元の機能：差分の中のハンク 1 つを逆に当てて元に戻す。コミット済みの差分でも使える。
- Loomo でやると：コミットの差分を見ているときの行選択・ハンクの右クリックに「この変更を作業ツリーで打ち消す」。選んだ範囲の逆パッチを
  §24.38.1 の当て方で作業ツリーへ当てる（コミットは作らない）。「あのコミットのこの 1 行だけ戻したい」を、コミット全体の
  打ち消し→要らない分を破棄、の 2 手でなく 1 手にする。
- いまの Loomo：部分的にある。打ち消しはコミット単位（`src/Loomo.Services/Git/GitMergeService.cs:50` `RevertNoCommitAsync`）で、
  行単位の適用・破棄は作業ツリーの差分だけ（`src/Loomo.App/ViewModels/Diff/DiffSessionModels.cs:102`）。
- 作業の置き場：Loomo のみ。
- 規模：S〜M（行範囲からのパッチ組み立ては行単位ステージにあるので、それを逆向き・コミット差分に広げる）。
- 関連：§24.38・§24.62

### 10. 差分の表示を、語を含む変更だけに絞る（元：WinMerge 2.16.57・2026-07-05（プレリリース）／2.16.58 安定版・2026-07-27）
- 出典：https://github.com/WinMerge/winmerge/releases/tag/v2.16.57（「Add display line filter bar for file compare window」）、
  PR 本文 https://github.com/WinMerge/winmerge/pull/3374（「control which lines are visible without affecting the comparison result」）
- 元の機能：比較結果は変えずに、表示する行だけを式で絞る帯（Ctrl+Shift+L）。選んだ文字から絞り込みを作れる。
- Loomo でやると：差分ペインの上に絞り込み欄。語（正規表現可）を入れると、その語を含む変更ブロックだけを残して他を畳む。
  §24.50 の通し読みと組むと、「`Logger` に触った変更だけを全ファイル分読む」ができる。比較そのもの（`DiffOptions`）には触れない。
- いまの Loomo：未実装。比べ方の切替は空白の無視だけ（`src/Loomo.App/ViewModels/Diff/DiffSessionViewModel.cs:74`）。
- 作業の置き場：Loomo のみ（差分本体の描画が Loomo 側か Editor の `DiffDecorations` 側かで変わる。畳む口が Editor に無ければ足す）。
- 規模：M
- 関連：§24.5・§24.50

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| ファイルツリーで開いたフォルダー名を上に貼り付ける | Zettlr 4.4.0・2026-04-24 | 期間外（1 日前） |
| ノートの AI チャット・埋め込み API | Joplin 3.7.8・2026-07-13 | AI が主役で部屋の性質に効かない |
| 意味検索で Issue を探す（`--search-type`） | GitHub CLI 2.98.0・2026-08-20 | GitHub のサーバー側機能（クラウド必須） |
| タブの URL をその場で書き換え・ホバーで再読み込み | Floorp 12.17.0・2026-08-21 | アドレス欄で足りる。部屋に効かない |
| 選んだノートを Markdown へ書き出す | Floorp 12.20.0・2026-10-08 | ブラウザ内蔵ノートの話で Loomo に相当物が無い |
| 複数の検索を組み合わせる | Everything 1.5 1421b・2026-08-13 | 検索ペインは既に種類・フォルダーで絞れ、組み合わせの需要が薄い |
| 3 方向マージの結果ペイン | WinMerge 2.16.59（プレリリース）・2026-10-04 | 下の「既にあった」 |
| 未フォーカスの面へのクリックで選択を変えない | Directory Opus 13.24・2026-06-28 | 好みの設定で、部屋のどの性質も良くしない |
| 端末のマウスを既定で有効・補完の書き直し | Emacs 31.1 | Emacs 固有 |
| 8.3 の新機能（連絡先の取り込み・テーマ追従） | Vivaldi 8.3 スナップショット | 未リリース（日付のある正式版でない） |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当 |
|---|---|---|
| コミットを作業ツリーと比べる | Visual Studio 2026 18.11 | `GitSessionViewModel.Worktrees.cs:259` `CompareCommitWithWorkingTree`、reflog にも「作業ツリーと比較」 |
| 衝突をまとめて「こちら／あちら」で解く | Visual Studio 2026 18.11 | `DiffConflictViewModel.cs:352-355` ファイル全体を Ours／Theirs、ブロックごとの Ours／Theirs／Both |
| マージ結果を見ながら衝突を解く | WinMerge 2.16.59 | `DiffConflictViewModel.ApplyResultTextAsync`（結果の文字を書いて当てる） |
| クリップボードと比べる | WinMerge 2.16.57 | `DiffSessionViewModel.cs:733` `CompareFileWithClipboardAsync`／「Diff へ送る（クリップボードと比較）」 |
| 置換の結果を当てる前に見る | Emacs 31.1（query-replace の `d`） | 検索ペインの置換プレビュー（取り消し線、`SearchPanelView.xaml:671`） |
| PR をワークツリーでチェックアウト | GitHub CLI 2.98.0 | `ShellWindow.GitHub.cs:94`／`ShellWindow.AzurePullRequests.cs:79`「ワークツリーで開く（別の部屋）」 |
| 課題からワークツリーを作る（`gh issue develop`） | GitHub CLI 2.99.0 | §24.56 課題から作業を始める |
| 動いている裏の仕事を印で出す | Zettlr 4.5.0・2026-05-08 | §24.54 動いているもの面 |
| フォルダーのタブに番号 | Directory Opus 13.24 | §24.47 番号でタブを選ぶ |
| 削除の取り消し | Directory Opus 13.24（ごみ箱からの復元の取り消し） | `FileOperationHistory`（削除・移動の Undo／Redo） |

## 調べた範囲

- 一次情報を読んだもの：GNU Emacs 31.1 の `etc/NEWS`（emacs-31 ブランチ、全 4341 行のうち Windows・Tab Bar/Tab Line・Project・Grep・Xref・Diff・VC の節。
  リリース日は LWN の告知 2026-08-24）、WinMerge の GitHub Releases（v2.16.55〜v2.16.59。日付は GitHub API で照合）と PR #3181・#3374 の本文、
  Visual Studio 2026 のリリースノート（18.10・18.11・18.11.1。18.11 の項目のうち前回までに採っていないもの）、GitHub CLI の GitHub Releases（2.97.0〜2.103.2）、
  Zettlr の GitHub Releases（v4.4.0〜v4.9.0）、Floorp の GitHub Releases（12.17.0〜12.20.2）、Everything 1.5 の changelog（1408a〜1424b）、
  Directory Opus 13.24 の公式フォーラムの告知、Joplin の GitHub Releases（v3.6.16〜v3.7.21）、Microsoft Edit 2.0.0、PowerShell の GitHub Releases（v7.6.5〜v7.6.8・v7.7.0-preview.4/5）、
  Vivaldi のデスクトップブログ（2026-09-25〜10-23）。
- 部屋に関わる項目が無かったもの：PowerShell（設定・修正だけ）、Microsoft Edit 2.0（Loomo のエディタに既にある編集機能）、Everything 1.5（翻訳と内部の設定が大半）。
- 期間内のリリースが無かった・見つからなかったもの：Total Commander（11.56・2025-08 が最新）、Sourcetree for Windows（3.4.30・2026-03-07 が最新）、
  Kate（公式ニュースが 2025-10 で止まっている）、Lapce（v0.4.6・2026-01-21 が最新）、Visual Studio 2026 18.12（未公開）、IntelliJ IDEA 2026.3 EAP（記事が見つからず）、
  Chrome の Split View（Chrome 145・2026-02 で期間外）。
- 一次情報を読めなかったもの：Directory Opus 13.25（2026-08-21。告知は検索の要約だけ）、Firefox 159 以降（リリース一覧が 158.0.1 まで）、
  Notepad++ 8.9.x（ニュースページに日付と本文が無い）、Flow Launcher（検索で同名の別製品しか出ず）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）——前回までと同じ理由。
