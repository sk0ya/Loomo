# 他ツールの新機能からの取り込み候補（2026-10-18・焦点：全領域・期間：2024-10〜2026-10）

同じ日の 2 本（`feature-scout-2026-10-18.md`＝直近 6 か月、`feature-scout-2026-10-18-未調査製品.md`＝その補足）に続けて、
期間を 2 年に広げた調査。直近 6 か月は前の 2 本で見ているので、**主に 2024-10〜2026-04 を読み**、前の 2 本に出た候補は繰り返さない。
物差しは同じ（主役は人間の部屋。§19.9 の「やらない」とクラウド必須の機能は外す）。「いまの Loomo」はコードを grep して確かめた結果。

この版から、スキルの改訂（件数で切らない／Editor・Terminal パッケージの改修を減点しない）に合わせて、条件を満たしたものは
全部「推す」に入れた。初版で「パッケージ改修が要る」ことを理由に見送っていたもの（カーソル行の blame）も戻した。

リリース日は GitHub API（`published_at`）か公式ページで照合した。WebFetch の要約は年を取り違えることがあった
（Windows Terminal 1.25 を「2024-10」、GitHub Desktop 3.6 を「2024」と返した）ので、要約の日付はそのまま使っていない。
VS Code は「1.97（2025 年 1 月版）」のように、公式の版の月で書く（実際の公開は翌月の初め）。

## まず手を付けるなら

| # | 候補 | 置き場 | 規模 | 理由 |
|---|---|---|---|---|
| 1 | ターミナルの合図（進捗・ベル）を袖へ | Loomo のみ | S | 口がパッケージに既にあり、購読するだけ |
| 3 | コミットメッセージの下書きを残す | Loomo のみ | S | 別のワークスペースへ持ち越す不具合も一緒に直る |
| 2 | 問題の印をファイルツリーとタブに | Loomo のみ | S〜M | 周辺視野にいちばん直接効く |
| 5 | パッチを当てる | Loomo のみ | S〜M | §24.31（書き出す側）と対になる |
| 6 | 閉じたターミナルを戻す | Loomo（Pty.Host） | M | 前回の「閉じたタブを開き直す」の欠けを埋める |

## 推す（効き目の大きい順）

### 1. ターミナルの合図（進捗・ベル）を袖に出す（元：VS Code 1.97（2025 年 1 月版）／Ghostty 1.2.0・2025-09-15／zellij v0.44.0・2026-03-23）
- 出典：https://code.visualstudio.com/updates/v1_97 （ターミナルの「ConEmu progress escape sequence support」）、
  https://ghostty.org/docs/install/release-notes/1-2-0 （「Graphical Progress Bars」OSC 9;4）、
  https://github.com/zellij-org/zellij/releases/tag/v0.44.0 （「Terminal BEL forwarding from unfocused tabs and panes with visual indication」）
- 元の機能：コマンドが出す進捗（OSC 9;4）をタブやタスクバーの進捗として描く。見えていないタブで鳴ったベルに印を付ける。
- Loomo でやると：袖のターミナルのバッジ（§24.1）に、進捗（％と、エラー・一時停止の色）と「ベルが鳴った」を足す。舞台に別のペインを
  立てていても、裏のターミナルのダウンロードやビルドの進み具合と、呼ばれたことが周辺視野に入る。いまのバッジは OSC 133 の
  「始まった・終わった」だけで、その間の様子は見えない。Claude Code の作業中・入力待ちは既にタイトルとフックで出している（§24.1.1）ので、
  これはそれ以外の CLI のため。
- いまの Loomo：**部分的にある**。Terminal パッケージ（1.0.45・いまのピン）は `TerminalTabView.TaskbarProgressChanged` と `BellRang` を
  公開していて（`C:\Projects\Terminal\src\Terminal.Controls\Tabs\TerminalTabView.xaml.cs:194,206`）、ベルはパッケージ内のタブ帯に印も出す。
  Loomo はどちらも購読していない（`src/` に `TaskbarProgress`・`Bell` の参照なし）。
- 作業の置き場：Loomo のみ（`ShellWindow.PaneActivity.cs` のバッジに状態を 2 つ足す）
- 規模：S
- 関連：§24.1、§24.22（留守中のまとめにベルを載せるかは別に判断）

### 2. 問題の印をファイルツリーとタブに出す（元：Zed v0.166.1・2024-12-18／v0.226.4・2026-03-04）
- 出典：https://github.com/zed-industries/zed/releases/tag/v0.166.1 （「display error and warning indicators in tabs」「`[ d` & `] d` to select prev/next file with diagnostics … within the project panel」）、
  https://github.com/zed-industries/zed/releases/tag/v0.226.4 （「diagnostic count badges to the project panel, displaying error and warning counts next to file names」）
- 元の機能：エラー・警告のあるファイルに、ツリーとタブで件数の印を付ける。ツリーの中で「次の問題のあるファイル」へ移れる。
- Loomo でやると：Problems（§31.5）が全ソース（Build／Compiler／LSP／StyleCop／Task）の診断を持っているので、それをパスで引いて
  ファイルツリーの行（親フォルダーにも集計）とエディタのタブに、エラー・警告の数を Git の色と並べて出す。どこが壊れているかを、
  Problems を開かずに周辺視野で見られる（§24.1 の考え方をツリーとタブへ）。
- いまの Loomo：**未実装**。ツリーの行は Git の状態だけを色で出している（`src/Loomo.App/Views/Files/FolderTreeView.xaml:524-536`・
  `FileNodeViewModel.cs:121`）。タブにも診断の印は無い。
- 作業の置き場：Loomo のみ
- 規模：S〜M（パス→件数の索引を `ProblemsViewModel` の更新に合わせて作り直す。ツリーは仮想化されているので行の更新を絞る）
- 関連：§31.5、§24.1、§26.10（Files ペインにも同じ列を）

### 3. コミットメッセージの下書きをワークスペースごとに残す（元：lazygit v0.46.0・2025-02-15）
- 出典：https://github.com/jesseduffield/lazygit/releases/tag/v0.46.0 （「Preserve pending commit message when closing/re-opening」）
- 元の機能：書きかけのコミットメッセージを、画面を閉じて開き直しても残す。
- Loomo でやると：Git ペインのメッセージ欄の中身を `WorkspaceSnapshot` に持たせ（コンポーザの `ComposerText` と同じ扱い）、
  ワークスペースの切り替えと再起動で戻す。コミットしたら消す。書きかけのまま離れても、戻ったときに続きから書ける（§24.4 復元の完全性）。
- いまの Loomo：**未実装で、切り替えのときに別のワークスペースへ持ち越される**。メッセージは `GitPanelViewModel._commitMessage`
  （`src/Loomo.App/ViewModels/Git/GitPanelViewModel.cs:211`）にしか無く、この VM はシングルトン
  （`src/Loomo.App/DependencyInjection/LoomoServiceCollectionExtensions.cs:121`）。ワークスペース側で触っている箇所が無いので、
  A で書きかけたメッセージが B のリポジトリの欄に残り、再起動では消える。`WorkspaceSnapshot` に該当する項目は無い
  （`src/Loomo.App/Services/Workspace/WorkspaceStateStore.cs:374-496`）。
- 作業の置き場：Loomo のみ
- 規模：S（保存項目 1 つ。amend の候補を選んだときにメッセージを上書きする処理（同 `:346`）と、下書きを取り違えない順番だけ注意）
- 関連：§24.4

### 4. 選んだ行の履歴をたどる（元：Fork for Windows 2.14・2025-11-21）
- 出典：https://fork.dev/releasenoteswin （2.14「File History for selected code (accessible via diff context menu)」）
- 元の機能：選んだ行の範囲だけについて、それを変えたコミットを一覧にする（`git log -L` 相当）。
- Loomo でやると：エディタで行を選び、右クリックの Git のサブメニュー（「ファイルの履歴」「Blame」の並び）に
  「選んだ行の履歴」を足す。Git ペインの履歴一覧に、その範囲を変えたコミットだけを出し、選ぶと既存の差分で開く。
  「この数行はいつ・なぜこうなったか」を、ファイル全体の履歴をめくらずに読める（§24.15 Git ペインを読む面に、§24.3 素材の流れ：エディタ → Git）。
- いまの Loomo：**未実装**。履歴はファイル単位だけ（`SelectionActionMenuBuilder.BuildGitMenuItem`、`src/Loomo.App/Services/Editor/SelectionActionMenuBuilder.cs:44`）。
  `-L` を渡している箇所はどこにも無い。
- 作業の置き場：Loomo のみ
- 規模：S〜M（`git log -L<開始>,<終了>:<path>` の出力をコミットとその範囲の差分に分ける読み取りが要る。
  行番号は保存前の編集で動くので、保存済みの内容で引くか、未保存なら断る）
- 関連：§24.15、§24.3、§24.11（比較基準）

### 5. パッチを当てる（元：Fork for Windows 2.17・2026-03-13）
- 出典：https://fork.dev/releasenoteswin （2.17「Ctrl+V to apply patch from clipboard」）
- 元の機能：クリップボードにある差分（パッチ）を、そのまま作業ツリーへ当てる。
- Loomo でやると：Git ペインに「パッチを当てる」を足す。入力はクリップボード／エディタで開いている `.patch`・`.diff`／ファイル選択。
  当てる前に Diff ペインで中身を見せ（§24.5 アドホック比較の取り込み口に乗せる）、`git apply --3way` で当て、当たらなかった部分は
  そのファイル名と理由を出す。PR のコメントやブラウザ・ターミナルに出てきた差分、§24.31 で保存したパッチを、部屋の中で作業ツリーへ戻せる
  （§24.3 素材の流れ：外 → Git）。
- いまの Loomo：**部分的にある**。書き出す側（スタッシュをパッチとして保存・`src/Loomo.App/ViewModels/Git/GitPanelViewModel.cs:678`）はあるが、
  当てる側が無い。`git apply` は差分の中でのステージと行単位の取り消しに内部で使っているだけ
  （`src/Loomo.Core/Diff/UnifiedPatchEditor.cs:21`、`src/Loomo.Services/Git/GitCommitService.cs:78`）。
- 作業の置き場：Loomo のみ
- 規模：S〜M（`git apply --check` で事前に確かめる処理と、プレビューの面）
- 関連：§24.31、§24.5、§24.3

### 6. 閉じたターミナルを戻す（元：Ghostty 1.2.0・2025-09-15／Warp 2025.10.15）
- 出典：https://ghostty.org/docs/install/release-notes/1-2-0 （macOS の「undo/redo for terminal closure」）、
  https://docs.warp.dev/changelog/2025 （2025.10.15「pane restoration with `CMD-SHIFT-T`」）
- 元の機能：うっかり閉じたターミナルを、元に戻す操作で中身ごと戻す。
- Loomo でやると：ターミナルのタブを手で閉じたとき、すぐにシェルを殺さず、常駐ホスト（§34）の中で数分だけ残す。
  その間に「閉じたタブを開き直す」で同じタブ ID のまま付け直せば、画面とスクロールバックと動いているプロセスごと戻る。
  時間が過ぎたらホストが殺す。前回の調査（未調査製品の 1「手で閉じたタブを開き直す」）でターミナルを対象外にした理由
  （閉じるとシェルを殺している）を、常駐の仕組みで外す形になる（§24.4）。
- いまの Loomo：**未実装**。閉じると `RemotePtySession.Dispose` が `Kill` を送る（`src/Loomo.Services/Terminal/RemotePtySession.cs:74`）。
  殺さずに切り離すだけの `Abandon` は既にあるが、テスト用（同 `:87`）。
- 作業の置き場：Loomo（App ＋ `sk0ya.Loomo.Pty.Host`。Terminal パッケージは不要）
- 規模：M（ホスト側に「切り離されたセッションを期限で殺す」タイマーと、正常終了の `Leave` との兼ね合い（§34.6）が要る。
  閉じたタブの一覧は前回の候補 1 と共有する）
- 関連：§34、§24.4、前回の未調査製品 1

### 7. 分岐元とぶつかりそうなら先に知らせる（元：GitKraken Desktop 10.6.0・2024-12-17）
- 出典：https://help.gitkraken.com/gitkraken-desktop/10x/ （10.6.0「Automatic Conflict Detection: Monitors base branches and alerts users via toolbar icon when conflicts are detected」）
- 元の機能：いまのブランチと分岐元（main など）を見張り、マージしたら衝突することになった時点で印を出す。
- Loomo でやると：§24.11 の比較基準（分岐点・ブランチ）が既にあるので、fetch のあとや基準が動いたときに
  `git merge-tree --write-tree <HEAD> <基準>` をローカルで回し、衝突するファイルがあれば Git ペインの見出しと袖に印を出す。
  押すと衝突しそうなファイルの一覧（それぞれ既存の差分で開ける）。並行作業（§24.17 ワークツリー）で、どれが main とぶつかり始めたかが
  マージを試す前に見える。GitKraken の版はチームメイトの作業との照合（クラウド）も含むが、それは使わない。
- いまの Loomo：**未実装**（`merge-tree` を使っている箇所なし）。
- 作業の置き場：Loomo のみ
- 規模：M（`merge-tree` の出力の読み取り、走らせる契機、ワークツリーごとに回すかどうか）
- 関連：§24.11、§24.17、§24.13

### 8. 変更一覧の各ファイルに増減の行数を出す（元：lazygit v0.45.0・2025-01-11／Zed v0.227.1・2026-03-11）
- 出典：https://github.com/jesseduffield/lazygit/releases/tag/v0.45.0 （「Show the number of lines changed per file in working file tree view」）、
  https://github.com/zed-industries/zed/releases/tag/v0.227.1 （「Added git diff stats to git panel entries」）
- 元の機能：作業ツリーの変更一覧の各行に `+12 −3` を添える。
- Loomo でやると：Git ペインの変更一覧（ステージ済み／未ステージ）と、ワークツリーのタブの `±N` の横に増減の行数を出す。
  どの変更が大きいかを開かずに見て、レビューやステージの順を決められる。
- いまの Loomo：**未実装**。変更の行は状態の 1 文字だけ（`GitChangeItem`・`src/Loomo.App/ViewModels/Git/GitPanelViewModel.cs:20-32`、
  元の `GitChangeEntry` にも行数は無い・`src/Loomo.Services/Git/GitStatusModels.cs:7`）。`--numstat` はコミットの変更ファイルにだけ使っている
  （`src/Loomo.Services/Git/GitHistoryService.cs:73,90`）。
- 作業の置き場：Loomo のみ
- 規模：S（`git diff --numstat` と `--cached --numstat` を状態の取得に足す。未追跡のファイルは行数を数えるか空欄）
- 関連：§24.15、§24.17

### 9. コミットの前に、そのファイルの問題を確かめる（元：VS Code 1.98（2025 年 2 月版））
- 出典：https://code.visualstudio.com/updates/v1_98 （Source Control「Diagnostics commit hook (experimental): Warns before committing with unresolved issues」）
- 元の機能：コミットしようとしたファイルにエラーが残っていれば、コミットの前に知らせる。
- Loomo でやると：Git ペインのコミットで、ステージ済みのファイルに Problems のエラー（設定で警告も）があれば、件数と「Problems で見る」
  「そのままコミット」を出す。2 の索引をそのまま使える。
- いまの Loomo：**未実装**（`GitPanelViewModel.CommitAsync`・`GitPanelViewModel.cs:825` は Problems を見ていない）。
- 作業の置き場：Loomo のみ
- 規模：S（2 を先にやれば数時間）
- 関連：§31.5、2

### 10. 切り離したペインのウィンドウを常に手前に置く（元：VS Code 1.100（2025 年 4 月版））
- 出典：https://code.visualstudio.com/updates/v1_100 （「Floating window modes: Compact view and always-on-top options for detached windows」）
- 元の機能：別ウィンドウにしたエディタを、ほかのアプリより手前に固定する。余計な枠を消した小さい表示にもできる。
- Loomo でやると：切り離したウィンドウ（`src/Loomo.App/Detach/`）のタイトルバーに「常に手前」の切り替えを足し、
  `DetachedWindowSnapshot` に持たせて復元する。Loomo の外（ほかのアプリやブラウザ）で作業している間も、ターミナルやプレビューを
  手前に置いて見ておける——部屋の周辺視野を部屋の外まで伸ばす。Windows には標準の「常に手前」が無い（PowerToys が要る）。
- いまの Loomo：**未実装**（`Detach/` で `Topmost` を使っているのはドラッグ中の影 `TabDragGhost.cs:101` だけ。
  `DetachedWindowSnapshot` は位置・大きさ・最大化だけ・`WorkspaceStateStore.cs:627-633`）。
- 作業の置き場：Loomo のみ
- 規模：S
- 関連：記憶の「Detached pane windows」、§24.4
- 注意：初版では zellij の Pinned Floating Panes を「OS の機能で足りる」と見送っていたが、Windows には無いので取り消した。

### 11. 折りたたみを復元する（元：Zed v0.180.2・2025-04-02）
- 出典：https://github.com/zed-industries/zed/releases/tag/v0.180.2 （「Added persistence to editor folds to preserve them between restarts」）
- 元の機能：エディタで畳んだ範囲を、再起動のあとも畳んだまま戻す。
- Loomo でやると：タブの復元（キャレットの行・スクロールの割合）に、畳んでいる範囲も加える。読み終えた部分を畳んで残りを読む、という
  読み方が離れて戻っても続く（§24.4 「キャレット行とスクロール比率まで」の残り）。棚（§24.29）から戻したタブも同じ。
- いまの Loomo：**未実装**。`EditorTabSnapshot` はキャレットとスクロールだけ（`WorkspaceStateStore.cs:902-930`）。Editor 側も
  `FoldManager`（`C:\Projects\Editor\src\Editor.Core\Folds\FoldManager.cs:7`）はあるが、`VimEditorControl` に畳んだ範囲を読み書きする
  公開の口が無い。
- 作業の置き場：Editor（畳んだ範囲の取得と復元の公開 API・NuGet 公開が要る）＋ Loomo（保存項目 1 つ）
- 規模：M（範囲は行番号で持つので、ディスク上で変わっていたら捨てる判定が要る）
- 関連：§24.4、§24.29

### 12. コマンドパレットで最近使ったコマンドを上に出す（元：Zed v0.181.5・2025-04-09／VS Code 1.107（2025 年 11 月版））
- 出典：https://github.com/zed-industries/zed/releases/tag/v0.181.5 （「Added persistent history of command palette usages」）、
  https://code.visualstudio.com/updates/v1_107 （「"X" button removes commands from recently used list」）
- 元の機能：パレットを開いただけのとき、最近実行したコマンドが上に来る。履歴は再起動のあとも残り、要らないものは消せる。
- Loomo でやると：`>` コマンドで、打つ前の一覧の先頭に「最近使った」を出す（使った回数と最近さで並べる。§24.20 の最近のコマンドと同じ考え方）。
  よく使う部屋の操作に、キーを覚えなくても 2 打で戻れる（§24.2）。
- いまの Loomo：**未実装**。パレットの「最近」は最近のファイル・最近の場所・ターミナルの最近のコマンドで
  （`ShellWindow.CommandPalette.cs:302-313`）、実行したコマンドの履歴は持っていない（`Services/Palette/` に該当なし）。
- 作業の置き場：Loomo のみ
- 規模：S（コマンド ID は `PaletteCommand.Id` が既にある・`Services/Palette/CommandPalette.cs:13`。保存はアプリ単位で 1 つ）
- 関連：§24.2、§26.2

### 13. カーソル行の blame を行末に出す（元：VS Code 1.96（2024 年 11 月版）〜1.97）
- 出典：https://code.visualstudio.com/updates/v1_96 ・ https://code.visualstudio.com/updates/v1_97
  （「Git blame information in editor and status bar」）
- 元の機能：カーソルのある行の末尾に「誰が・いつ・どのコミットで」を薄く出し、ステータスバーにも同じものを出す。
- Loomo でやると：カーソル行の末尾に作者・日付・件名を薄く出し、押すとそのコミットの差分を開く（`:Gblame` の欄の行を押したときと同じ道）。
  §24.11 の比較基準とは別に、いまいる行の来歴が周辺視野に入る。
- いまの Loomo：**部分的にある**。blame は左の欄として全行に出す形だけ（`:Gblame`、`SelectionActionMenuBuilder.cs:50`・Editor の
  `GutterRenderer.DrawBlameMargin`）。データの口（`IEditorGitService.GetBlameLines`）はある。
- 作業の置き場：Editor（行末に注記を描く口。inlay hints の描画はあるので、それを行末に置けるかから）＋ Loomo（表示の切り替えと設定）
- 規模：M
- 関連：§24.15

### 14. ブランチ一覧に、そのブランチの PR を出す（元：lazygit v0.61.0・2026-04-06）
- 出典：https://github.com/jesseduffield/lazygit/releases/tag/v0.61.0 （「Show pull requests against branches」）
- 元の機能：ブランチの一覧で、そのブランチから出ている PR の番号と状態を並べる。
- Loomo でやると：§24.27（GitHub）・§24.27.1（Azure DevOps）が既に PR を読めるので、ブランチの一覧とワークツリーのタブに PR の番号と
  状態（下書き・レビュー待ち・承認済み・マージ済み）を添え、押すと部屋の中の PR の面で開く。どのブランチがもう片付いたか
  （マージ済みで消してよいか）が一覧で見える。
- いまの Loomo：**未実装**（`ViewModels/Git` のブランチ側で PR を参照している箇所なし）。
- 作業の置き場：Loomo のみ（gh／Azure DevOps の既存の連携に乗る）
- 規模：S〜M（`gh pr list --json headRefName,number,state` を 1 回引いてブランチ名で突き合わせる。引く頻度は fetch に合わせる）
- 関連：§24.27、§24.27.1、§24.17

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| 保存したコマンドの一覧（Snippets pane） | Windows Terminal 1.22（安定版 2024-10-22） | ペグボード（§23）・`tasks.json`（§31.17）・最近のコマンドの回数（`RecentTerminalCommandSnapshot.RunCount`）で足りる |
| fzf 型のあいまい検索をコマンドパレットに | Windows Terminal 1.24（2025-08-26） | パレットの並べ方の調整で、部屋の機能ではない |
| ターミナル内の補完（シェルの補完候補） | VS Code 1.98〜1.107・IntelliJ IDEA 2026.1 | 道具としてのターミナルの品質で、部屋の性質（周辺視野・素材の流れ・復元）には効かない |
| fetch のあとに main を早送りする | lazygit v0.50.0（2025-05-03） | Git 操作の網羅で、部屋の性質に効かない |
| リポジトリの活動を時間で見るツリーマップ | Fork 2.8（2025-05-09） | 作業場としての使い道が薄い |
| 実行時間を行ごとにガターへ出す | IntelliJ IDEA 2024.3（2024-11） | DAP が実行時間を返さないので、計測の仕組みから作ることになる。部屋への効き目も小さい |
| Vim のマークを再起動のあとも残す | Zed v0.179.2（2025-03-26） | 軌跡のしおり（`trail.bookmark`・§27）が部屋の単位で同じ役をしている。マークまで残すかは要望が出てから |
| ローカルの画面をブラウザから使う Web Client／遠隔アタッチ | zellij v0.43.0・v0.44.0 | 非目標（リモート開発・Web 版） |
| Dashboard（メール・カレンダー・RSS を 1 面に） | Vivaldi 7.0（2024-10-24） | 部屋の外の道具。ブラウザペインの役目ではない |
| Live Folders（GitHub の Issue／PR を自動で並べる） | Zen Browser 1.19b（2026-02） | §24.27 の GitHub の面で足りる |
| 2 つのタブを 1 つのウィンドウで並べる（Split screen） | Edge（安定版 2025-08 頃） | Loomo はペインを並べる仕組みそのもの。日付は記事で確認しただけ |
| Command Palette（拡張できるランチャー） | PowerToys 0.90.0（2025-03-31） | Loomo のパレット（§24.2）がある。拡張の口は非目標 |
| チームメイトの作業との衝突の予告 | GitKraken 10.8.0（2025-03-11） | クラウド必須。ローカルの分岐元との照合は推す 7 に入れた |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| ワークツリーの一覧・作成・削除・開く | VS Code 1.103 / Fork 2.17 / IntelliJ 2026.1 / GitKraken 10.5 | §24.17 |
| スタッシュの一覧・中身の差分 | VS Code 1.107 / Zed v0.210.4 | §24.31・`GitStashService` |
| Problems を発生源で絞る | VS Code 1.109 | `ProblemsViewModel.IsVisible`（`src/Loomo.App/ViewModels/Shell/ProblemsViewModel.cs:525-529`）の Build／Compiler／LSP／StyleCop／Task の切り替え |
| エディタのガターから変更の塊をステージ | IntelliJ IDEA 2024.3 / Zed v0.173.8 | Editor パッケージの `IEditorGitService.StageHunk`／`UnstageHunk`（`C:\Projects\Editor\src\Editor.Controls\Git\IEditorGitService.cs:30`）。Loomo はガターの比較基準だけ差し替えている（`EditorGutterGitService`） |
| Markdown プレビューの中で検索 | IntelliJ IDEA 2025.1 | エディタの検索語をプレビューにも塗る（`EditorSupportWebViewController.PushSearchHighlight`、PDF は WebView2 の Find API） |
| 選んだ範囲とクリップボードを比べる | Zed v0.197.3 | §24.5（`DiffComparisonSourceResolver` の `SelectionWithClipboard`） |
| 2 つのファイルを選んで比べる | Zed v0.200.4 / v0.191.4（`zed --diff A B`） | §24.5 |
| 分岐点からのブランチの差分 | Zed v0.211.4 | §24.11 |
| 任意のコミットの版にファイルを戻す | GitKraken 11.10.0 | `GitDiffService` の `checkout <rev> -- <path>`（`src/Loomo.Services/Git/GitDiffService.cs:144`、名前の変更をまたぐ場合も扱う） |
| リベースの取り消し | GitKraken 11.7.0 | 操作ログ（reflog）の面（§24.19） |
| ファイルをターミナルへ落としてパスを入れる | Warp 2025.12.03 | `FileDropPolicy` の `FileDropSurface.Terminal`（`src/Loomo.App/Services/FileSystem/FileDropPolicy.cs:12,27`） |
| ターミナルのタブの名前を変える | Zed v0.222.2 | タブ名はスナップショットに保存（§34） |
| 文字コードを指定して開き直す・Shift_JIS | Zed v0.219.4 / v0.222.2 | §23.3.3〜§23.3.4 |
| ターミナルの最近のコマンドとスティッキースクロール | VS Code 1.96・1.97・1.104 | §24.20 |
| 長いコマンド・タスクの終了を知らせる | VS Code 1.105 / Ghostty 1.3.0 | 袖のバッジとタスクバーの点滅（§24.1・`ShellWindow.PaneActivity.cs:74`）、留守中のまとめ（§24.22） |
| 差分の行からエディタのその行へ | Fork 2.5（2025-02-07） | §24.16（左右がエディタそのもの）・§24.7 |
| 縦に並んだタブ | Fork 2.2（2024-10-30） | TABS（サイドバーのタブ一覧） |
| ahead / behind の印 | Fork 2.16（2026-02-20） | `GitPanelViewModel.Ahead/Behind`（`GitPanelViewModel.cs:206-208`） |
| 連鎖キーの続きを出す（which-key） | Zed v0.219.4 | §24.2.2（今日の前回分で実装済み） |

## 調べた範囲

- 一次情報を読んだもの：VS Code updates（1.96〜1.110 の各版。版と月は archive で照合）、
  Zed の安定版 GitHub Releases（2024-10〜2026-04 の 245 版の「Added」行を全部。AI・言語・Vim 系は除いて読んだ）、
  Ghostty release notes（1.2.0・1.3.0）、Windows Terminal Preview 1.22・1.23・1.24 の公式ブログ、
  lazygit GitHub Releases（v0.45.0〜v0.61.0 の Features 欄）、zellij GitHub Releases（v0.41.0〜v0.44.0）、
  Fork for Windows release notes（2.0〜2.19）、GitKraken Desktop release notes（10.4.0〜11.10.0）、Sublime Merge の安定版（Build 2102〜2125）、
  Warp changelog 2025、JetBrains の What's New（IntelliJ IDEA 2024.3・2025.1・2025.2・2025.3・2026.1、Rider 2025.1）、
  Vivaldi 7.0 のブログ、Zen Browser release notes、Obsidian changelog、PowerToys v0.90.0。
- 日付は GitHub API で照合：lazygit、Windows Terminal、GitHub Desktop、zellij、PowerToys、Helix、Neovim、Zed。
- 中身を確かめられなかったもの：GitHub Desktop 3.4〜3.5 の本文（Added 欄が取れなかった）、JetBrains の各ページのリリース日
  （ページに日付が無く、版の時期だけで判断）、Edge の Split screen（ニュース記事だけ）、Warp の 2024 年分と 2026 年 1〜4 月分。
- 版の一覧だけ見て本文を読んでいないもの：Helix 25.01・25.07、Neovim 0.11〜0.12、VS Code 1.111〜1.130。
- 見ていないもの：Arc、Raycast（前回 6 か月分のみ）、Sublime Text の 2024-10〜2026-03、Cursor／Windsurf（AI が主役なので除いた）。
