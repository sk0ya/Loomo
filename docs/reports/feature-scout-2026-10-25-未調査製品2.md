# 他ツールの新機能からの取り込み候補（2026-10-25・焦点：前回までに見ていない製品・期間：2026-04-25〜2026-10-25）

同じ日の `feature-scout-2026-10-25.md` までの 7 本で、主要製品の一次情報はほぼ読み切っている。今回は **まだ一度も読んでいない製品**
（atuin の本文・iTerm2 3.7・Tower for Windows・SmartGit 26.1／26.2・Beyond Compare 5.2・delta・difftastic・Notepad++）に絞った。
前回までに見ていた製品で、10-24 以降に新しく機能を出した版は無かった（Claude Code は 2.1.310 のまま、Zed・VS Code・Windows Terminal・
lazygit・Tabby・GitButler は前回読んだ版が最新）。前回までに推した・見送った・既にあった項目は重ねて挙げていない。

物差しは同じで、主役は人間の部屋。§19.9 の「やらない」とクラウド必須の機能は外した。「いまの Loomo」はコードを grep して確かめた結果。

## 実装済み（2026-10-25 追記）

推す 11 件はすべて同日に実装した。
- 推す 1 → §24.66（最近のコマンドには積まない・ペグボードは伏せて残す）
- 推す 2 → §24.67（表示済みのタブだけ。表示していないタブはホストに検索の口が無く未対応）
- 推す 3 → §24.68（構文色は付けない）
- 推す 4 → §24.69（スカッシュマージは「上流が消えた」で拾う。マージ済み PR の照合は、PR の口がオープンなものしか引かないので未対応）
- 推す 5 → §24.70（統合表示だけ。左右並びは Editor の `DiffDecorations` の改修が要り未対応）
- 推す 6 → §24.71
- 推す 7 → §24.72（書くのは常駐ホスト。Terminal パッケージの改修は不要だった——`HeadlessTerminal.CreatePlainTextSnapshot` が公開済み）
- 推す 8 → §24.73（端末の上の帯ではなく TABS の行の 3 行目とツールチップ）
- 推す 9 → §24.74（コミット一覧の右クリックから。blame・コミット詳細には未配置）
- 推す 10 → §24.75（部屋の付け替えまで）
- 推す 11 → §24.76（基準は比較基準ではなく既定ブランチ）

## まず手を付けるなら

1. 秘密をコマンドの記録とペグボードに残さない（ターミナル・S）——いまは `$env:TOKEN="…"` が workspaces.json にそのまま残る
2. 全部のターミナルのタブを横断して探す（検索・S〜M）——検索パネルのターミナル検索は、いま前面にあるタブ 1 枚しか見ていない
3. パッチのファイルを差分として読む（EditorSupport・S〜M）——`.patch` を開いても素の文字のまま
4. ブランチの片付け（Git・M）——ワークツリーには片付けがあるのに、ブランチには無い

## 推す（効き目の大きい順）

### 1. 秘密をコマンドの記録とペグボードに残さない（元：atuin v18.22.0・2026-09-09／v18.24.2・2026-10-22）
- 出典：https://github.com/atuinsh/atuin/releases （v18.22.0「Redact secrets from captured command output」、v18.24.2「*(secrets)* Keep HashiCorp tokens out of history」）
- 元の機能：シェル履歴とコマンドの出力の記録から、トークンや鍵らしい文字列を伏せる（あるいは、その行を記録しない）。新しい種類のトークンを版ごとに足している。
- Loomo でやると：最近のコマンド（§24.20）に積むとき、`ghp_…`・`github_pat_…`・`sk-…`・`AKIA…`・`xox[bp]-…`・`hvs.…`・JWT（`eyJ…`）、
  `Authorization: Bearer …`、`-p`／`--password` の値を含む行は**積まない**か、その部分を伏せる。ペグボードへ残すターミナルの素材（§24.26）も、
  保存の前に同じ判定で伏せ、伏せたことをカードに出す。どちらも workspaces.json に入って再起動のあとも残るので、残る前に止めるのが要。
  .http の応答の保存（§24.30.2）は `Authorization`／`Cookie` を既に伏せているので、同じ考え方をターミナルにも広げる。
- いまの Loomo：未実装。`RecentTerminalCommands.Record`（`src/Loomo.App/Services/Workspace/RecentTerminalCommands.cs:25`）は行をそのまま積み、
  `TerminalPegboardMaterial`（同 `TerminalPegboardMaterial.cs:23`）は大きさしか制限しない。伏せる処理は `HttpResponseArchive.Mask`（`src/Loomo.Core/Http/HttpResponseArchive.cs:81`）だけ。
- 作業の置き場：Loomo のみ
- 規模：S（判定の表と、記録・素材の 2 か所への差し込み。テストは誤検知しない例を多めに）
- 関連：§24.20・§24.26・§24.30.2

### 2. 全部のターミナルのタブを横断して探す（元：atuin v18.23.0・2026-09-22）
- 出典：https://github.com/atuinsh/atuin/releases （v18.23.0「Full-text search over captured command output」。v18.22.0 でコマンドごとの出力の記録が入った）
- 元の機能：これまでに実行したコマンドの出力を全文検索し、どのコマンドの出力だったかから辿れる。
- Loomo でやると：検索パネルのターミナル検索の範囲を「前面のタブ」から「この部屋の全部のターミナルのタブ」へ広げる。ヒットはタブごとにまとめ、
  選ぶとそのタブへ行って一致を選択して見せる（いまの `SelectMatch` と同じ）。「さっきどこかのタブで出たエラー」を、タブを 1 枚ずつめくらずに見つけられる。
  §24.21 で切り出したコマンドの出力（前回・今回）にも当てれば、ヒットに「`dotnet test` の出力」とコマンドの名前を添えられる。
- いまの Loomo：部分的にある。検索パネルにターミナル検索はあるが、供給口が `_activeTerminalTab` 1 枚しか見ていない
  （`src/Loomo.App/Views/Shell/ShellWindow.xaml.cs:371`）。まだ表示していない（実体化していない）タブはスクロールバックがホストにしか無い。
- 作業の置き場：Loomo のみ（表示済みのタブ）。表示していないタブまで探すなら Terminal（常駐ホストへ「この語でスクロールバックを探す」要求を足す。NuGet 公開が要る）
- 規模：S（表示済みのタブだけ）／M（ホスト側の検索まで）
- 関連：§24.21・§34

### 3. パッチのファイルを差分として読む（元：Beyond Compare 5.2.2・2026-05-20）
- 出典：https://www.scootersoftware.com/download/v5changelog （5.2.2「Dropping a .patch or .diff file on any view opens a Text Patch view.」）
- 元の機能：`.patch`／`.diff` を落とすと、ファイルごとの差分として読める画面で開く。
- Loomo でやると：エディタで `.patch`／`.diff` を開いたとき、EditorSupport に Diff ペインと同じ見た目（ファイルごとの見出し・行番号・構文色・行内差分）で出す。
  中身は同じ文字で、見せ方だけを変える——〜Support の役目そのもの。PR から落としたパッチ、§24.31 で保存したスタッシュのパッチ、ターミナルに出た `git diff` を
  エディタで開いたもの（§24.21）を、当てる前に読める。当てる口（§24.38.1 の右クリック「このパッチを作業ツリーへ当てる」）は既にあるので、読む→当てるが
  同じ場所でつながる。
- いまの Loomo：部分的にある。`.patch`／`.diff` を当てる口はある（`src/Loomo.App/Views/Shell/Editor/ShellWindow.SelectionActions.cs:186`）が、
  EditorSupport の種類（`src/Loomo.App/Services/Editor/Support/*EditorSupport.cs`）にパッチを描くものが無く、素の文字で見るしかない。
- 作業の置き場：Loomo のみ（統合表示の描画 `DiffFlowDocumentRenderer` とパッチの読み取り `UnifiedPatchEditor` を流用）
- 規模：S〜M（パッチを複数ファイルに割る・EditorSupport の 1 種類を足す・編集に追従する）
- 関連：§24.31・§24.38.1・§26

### 4. ブランチの片付け——取り込み済み・古いブランチを、理由つきで（元：Tower for Windows 13.0・2026-07-13／14.0・2026-09-15）
- 出典：https://www.git-tower.com/release-notes/windows （13.0「Automatic Branch Management: You can now archive fully merged and stale branches manually」、
  14.0「Fully Merged Detection: Tower now detects branches merged through Git ancestry and merge commits」「Fully Merged Hints: Branch hints now explain why Tower considers a branch merged」）
- 元の機能：取り込み済みのブランチと長く触っていないブランチに印を付け、なぜ取り込み済みと判断したかを添えて、まとめて片付けられる。
- Loomo でやると：§24.55 のワークツリーの片付けと同じ形の窓を、ローカルブランチにも作る。候補の理由は「既定ブランチに取り込み済み（`branch --merged`）」
  「上流が消えた（`UpstreamGone`）」「N 日触っていない（`LastCommit`）」の 3 つで、行にそれぞれの理由を出し、選んでまとめて消す。ワークツリーに
  ぶら下がっているブランチは候補から外す（消せない）。スカッシュマージで入ったものは `--merged` では拾えないので、PR の印（§24.39）がマージ済みなら
  それも理由に数える。戻ってきたときにブランチツリーが終わった枝で埋まっていない、という意味で部屋の見通しを良くする。
- いまの Loomo：部分的にある。取り込み済みの判定（`GitWorktreeCleanup.ParseMergedBranches`・`src/Loomo.Services/Git/GitWorktreeCleanup.cs:157`）、
  `UpstreamGone`・`LastCommit`（`src/Loomo.Services/Git/GitReferenceModels.cs:6`）は揃っているが、どれもワークツリーの片付けか行の表示にしか使っておらず、
  ブランチ自体の片付けは 1 本ずつ右クリックで消すしかない。
- 作業の置き場：Loomo のみ
- 規模：M（候補の判定・窓・まとめて消す。窓は §24.55 の `WorktreeCleanupViewModel` を下敷きにできる）
- 関連：§24.39・§24.55

### 5. 差分で、移動しただけのブロックを見分ける（元：SmartGit 26.2 Preview（26.2.007・2026-09-08〜26.2.015・2026-10-22））
- 出典：https://www.smartgit.dev/changelogs/changelog-26.2.txt （26.2.007「Changes view: detects moved code within and across files」、26.2.010「arrows connect moved blocks; a pinnable preview shows the counterpart」）
- 元の機能：差分の中で、削除と追加が実は同じブロックの移動であるものを見分け、移動先と矢印でつなぐ。ファイルをまたぐ移動も拾う。
- Loomo でやると：Diff ペインで、削除のかたまりと追加のかたまりが（空白を除いて）3 行以上一致するとき、赤／緑ではなく「移動」の色にし、行の横の印から
  相手の位置へ飛べるようにする。メソッドの並べ替えやクラスへの抽出（§32）を読むとき、「全部消して全部書いた」に見えて本当の変更が埋もれるのを防ぐ。
  通し表示（§24.50）ならファイルをまたぐ移動も同じ判定で拾える。git の `--color-moved` と同じ考え方で、計算は手元の Myers の結果の上でやる。
- いまの Loomo：未実装。§24.25 で行内差分・空白無視・似た行の対応までは入っているが、移動の判定は無い（`Loomo.App/Services/Diff`・`Loomo.Core/Diff` に該当なし）。
- 作業の置き場：Loomo のみ（統合表示）。左右並びにも色を出すなら Editor（`DiffDecorations` に「移動」の行種別を足す。NuGet 公開が要る）
- 規模：M（判定・統合表示の色と飛び先。左右並びは Editor の改修込みでもう一段）
- 関連：§24.25・§24.50
- 注：SmartGit 側はまだ Preview の機能

### 6. 動いているもの面の行を「あとで」に回す（元：iTerm2 3.7.0・2026-09-08）
- 出典：https://iterm2.com/news.html （3.7 の告知・2026-09-08）、https://iterm2.com/downloads/stable/iTerm2-3_7_0.changelog （「Right-click a row in the Session Status toolbelt tool and choose Snooze to move it below a divider at the bottom of the list and dim it. A snoozed entry un-snoozes automatically the next time its status changes.」）
- 元の機能：状態の一覧の行を「あとで」にすると、区切りの下へ移って薄くなる。その行の状態が次に変わったら自動で戻る。
- Loomo でやると：動いているもの面（§24.54）の行の右クリックに「あとで」。分かっていて放ってある入力待ちの Claude Code や、止めてあるデバッグを「あなたの番」から
  下へ退け、ActivityBar の数からも外す。状態が変わったら（入力待ち→作業中→また入力待ち等）自動で戻るので、見落としにはならない。周辺視野に
  「本当にいま自分の番のもの」だけが残る。
- いまの Loomo：未実装。行は 3 つのまとまりに並ぶだけで、行ごとに退ける手段は無い（`src/Loomo.App/Services/Diagnostics/RunningEntries.cs:42`）。
- 作業の置き場：Loomo のみ
- 規模：S（行の鍵ごとに「あとで」にした時点の状態を覚え、変わったら外す。保存はしない）
- 関連：§24.1.1・§24.54

### 7. 閉じたターミナルの画面を書庫に残す（元：iTerm2 3.7.0・2026-09-08）
- 出典：https://iterm2.com/downloads/stable/iTerm2-3_7_0.changelog （「Session archives were added. … A per-profile setting can automatically archive a session to a configured directory when it closes. Restoring an archive reopens the session's scrollback in a new window.」）
- 元の機能：閉じたセッションのスクロールバックを自動で書庫に保存し、あとで開き直して読める（直近 10 件を覚える）。
- Loomo でやると：§24.41 の「3 分預ける」が切れたあと、そのタブの画面は消える。預かりが切れる（あるいは常駐でないタブを閉じる）ときにスクロールバックを
  `%APPDATA%/Loomo/terminal-archive/` へ文字で書き、パレットの「閉じたターミナルの画面を読む」から、タブ名・場所・閉じた時刻つきで選んで読み取り専用で
  エディタに開く。「昨日のあのタブで流れたログ」を、閉じたあとでも取り戻せる。状態ファイルには入れず別ファイルにし、件数と大きさに上限を持つ
  （§23.3.1 の 72MB 事故の形にしない）。秘密の扱いは推す 1 と同じ判定を通す。
- いまの Loomo：部分的にある。Terminal パッケージにはセッションを文字で書き出す仕組み（`EnableSessionLogging`・`C:\Projects\Terminal\src\Terminal.Controls\Tabs\TerminalTabView.xaml.cs:1136`、
  手動の `SaveTranscript`）があるが、Loomo はどちらも使っておらず、閉じたタブの画面は 3 分を過ぎると戻らない。
- 作業の置き場：Loomo（書庫と一覧）。常駐ホスト側で、預かりが切れて殺す直前に画面を書き出すなら Terminal（Pty.Host の `ParkWatch` に書き出しを足す。NuGet 公開が要る）
- 規模：M（書き出す時点が「Loomo が閉じたとき」と「ホストが預かりを切ったとき」の 2 つある）
- 関連：§24.41・§34

### 8. ターミナルのタブにメモを付ける（元：iTerm2 3.7.0・2026-09-08）
- 出典：https://iterm2.com/downloads/stable/iTerm2-3_7_0.changelog （「Session Notes were added: floating, draggable, resizable, collapsible multiline text notes attached to a session that persist across restarts.」）
- 元の機能：セッションに複数行のメモを付け、再起動しても残る。
- Loomo でやると：TABS とターミナルの見出しの右クリックに「メモ」。名前（§24.53）と色（§24.63）の隣に `TerminalTabSnapshot` へ持ち、TABS の行のツールチップと
  ターミナルの上端に畳める帯で出す。「このタブは本番の DB にトンネルを張っている」「ここで Claude Code に〇〇を頼んでいる」を、離れて戻ったときに
  読める。名前では短すぎ、ペグボードではタブから離れてしまう中間を埋める。
- いまの Loomo：未実装。`TerminalTabSnapshot` は ID・名前・場所・色だけ（`src/Loomo.App/Services/Workspace/WorkspaceStateStore.cs:998`）。
- 作業の置き場：Loomo のみ（帯は Loomo 側でターミナルの上に重ねる）
- 規模：S〜M（保存は名前と同じ経路。帯の置き場所で幅が出る）
- 関連：§24.53・§24.63・§34

### 9. `loomo://` のリンクをコミットにも（元：SmartGit 26.2 Preview 26.2.007・2026-09-08）
- 出典：https://www.smartgit.dev/changelogs/changelog-26.2.txt （「Standard window: shareable links to commits that open SmartGit at the selected commit」）
- 元の機能：コミットへのリンクを写し、踏むとアプリがそのコミットを選んだ状態で開く。
- Loomo でやると：§24.59 の `loomo://open` に `commit=<hash>` を足し、Git ペインのコミット一覧・コミット詳細・blame（§24.42）の右クリックに
  「このコミットへのリンクをコピー」。踏むとその部屋の Git ペインでそのコミットを選び、詳細を出す。ノートや Issue に「このコミットで壊れた」と書いて、
  あとでそこへ戻れる。開くだけの口、という §24.59 の約束はそのまま。
- いまの Loomo：部分的にある。`loomo://open` はファイル・行・列だけ（`src/Loomo.Core/Files/LoomoLink.cs:16`）。
- 作業の置き場：Loomo のみ
- 規模：S（リンクの形に 1 つ足す・受け手で Git ペインへ振る）
- 関連：§24.42・§24.59

### 10. 動かしたワークツリーを繋ぎ直す（元：Tower for Windows 14.1・2026-10-06）
- 出典：https://www.git-tower.com/release-notes/windows （14.1「Worktrees: Added "Repair Worktree…" for worktrees whose folder was moved.」）
- 元の機能：フォルダーを動かして見失ったワークツリーを、新しい場所を指して繋ぎ直す（`git worktree repair`）。
- Loomo でやると：WT タブで「フォルダーが無い」（`IsPrunable`）行の右クリックに「新しい場所を指して繋ぎ直す…」。いまは片付け（§24.55）で prune の候補に
  並ぶだけなので、フォルダーを動かしただけのワークツリーを消してしまいかねない。繋ぎ直したら、そのワークツリーの部屋（§24.17）のパスも付け替える。
- いまの Loomo：未実装。`IsPrunable` は prune にしか使っていない（`src/Loomo.Services/Git/GitWorktreeModels.cs:30`、`GitWorktreeCleanup.cs:72`）。`worktree repair` を呼ぶ箇所は無い。
- 作業の置き場：Loomo のみ
- 規模：S（フォルダー選択→`git worktree repair <新しい場所>`→部屋のパスの付け替え）
- 関連：§24.17・§24.55

### 11. ブランチで入れたコミットに一覧で印を付ける（元：Tower for Windows 13.2・2026-08-18／SmartGit 26.2 Preview 26.2.012・2026-10-09）
- 出典：https://www.git-tower.com/release-notes/windows （13.2「History now clearly identifies which commits a branch introduces relative to its parent at the fork point.」）、
  https://www.smartgit.dev/changelogs/changelog-26.2.txt （26.2.012「Graph: "Select Feature Range" reveals a feature branch's commits from its base to its tip」）
- 元の機能：コミット一覧で、いまのブランチが分岐元から入れたコミットを見分けられるようにし、その範囲をまとめて選べる。
- Loomo でやると：Git ペインのコミット一覧で、比較基準（§24.11）の分岐点より後のコミットに印を付け、右クリックに「このブランチで入れたコミットを選ぶ」。
  選べば複数コミットの詳細（既にある）でそのぶんの変更がまとめて見える。レビューに出す前・戻ってきて「このブランチで何をしたか」を思い出すときに使う。
- いまの Loomo：部分的にある。分岐点からの変更を差分で見る比較基準（§24.11）と、複数コミットを選んだときのまとめた詳細（`GitHistoryViewModel.SetSelectedCommits`・
  `src/Loomo.App/ViewModels/Git/GitHistoryViewModel.cs:198`）はあるが、一覧の上でどれがこのブランチのコミットかは分からない。
- 作業の置き場：Loomo のみ
- 規模：S（`merge-base` から先端までのハッシュの集合を持ち、行の印と選択に使う）
- 関連：§24.11・§24.14

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| タブグループ（名前と色のチップでタブを束ねる） | iTerm2 3.7.0・2026-09-08 | Loomo ではワークスペース（部屋）とペインが束ねる役をしていて、タブの色（§24.63）も入った。もう一段の束は効き目が見えない |
| Workgroups（1 つのセッションから関連するセッション群を立ち上げる） | iTerm2 3.7.0・2026-09-08 | 課題から作業を始める（§24.56）とワークツリーの部屋（§24.17）が同じ役。AI レビューの段取りの部分は部屋の外 |
| 配置の固定（Lock Layout） | iTerm2 3.7.0・2026-09-08 | 誤操作で配置を壊したという話が Loomo に無い。復元（§24.4）で戻せる |
| 端末へのファイルのドラッグ＆ドロップ（OSC 72） | iTerm2 3.7.0・2026-09-08 | 全画面の TUI へ落とす需要が中心で、部屋の性質に効かない |
| 選択に注釈を付けるキー | iTerm2 3.7.0・2026-09-08 | ペグボードへ出典つきで残す（§24.26）が同じ役 |
| ブランチのピン留め | Tower for Windows 13.0・2026-07-13 | ブランチツリーには絞り込みがあり、部屋の性質（周辺視野・流れ・復元・軌跡）に効かない |
| すべてのブランチを基準と並べて見る作業面（Branch Review） | Tower for Windows 14.0・2026-09-15 | 推す 4 の片付けの窓に理由と ahead/behind を出せば足りる。専用の面は重い |
| コミットの重なり（選んだコミットとファイルの重なりを列で） | SmartGit 26.1・2026-05-18 | 衝突の予告（§24.40・§24.64）と選んだ行の履歴（§24.38）で主な用途は足りる。部屋の性質に直接効かない |
| エージェントの会話を履歴の横に | SmartGit 26.2 Preview・2026-08〜10 | ターミナルサポートの会話のツリー（Claude Code の記録を読む）が既に同じ場所を占めている。AI を増やすこと自体は物差しでない |
| 差分の描画を外部の delta に任せる（OSC 1717） | delta 0.20.0・2026-10-04 | Loomo は差分を自分で描いている（§24.6・§24.25）。外部の描画に乗り換える理由が無い |
| 構文を見た差分（末尾のカンマは変更扱いしない等） | difftastic 0.69.0・2026-04-30 | 言語ごとの構文差分で作業量が大きく、部屋の性質に効かない。空白無視（§24.25）までで止める |
| 条件式のどの項が結果を決めたかを出す | Visual Studio 2026 18.10・2026-09-08（記事の要約のみ） | デバッガの機能で、部屋の性質に効かない。一次情報も読めていない |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当 |
|---|---|---|
| フォルダーツリーの開閉を再起動のあとも覚える | Notepad++ 8.9.7（2026-07-14） | `WorkspaceSnapshot.TreeExpandedPaths`（`src/Loomo.App/Services/Workspace/WorkspaceStateStore.cs:410`）を `SetPendingViewState` で戻す |
| 検索で「何件中何件目」 | Notepad++ 8.9.7（2026-07-14） | Editor の検索欄が「n of m」を出す（`C:\Projects\Editor\src\Editor.Controls\VimEditorControl.FindReplace.cs:193`） |
| 複数のコミットを選んで、まとめた差分を見る | Tower for Windows 14.1（2026-10-06） | `GitHistoryViewModel.SetSelectedCommits` → `SelectedRange`（`src/Loomo.App/ViewModels/Git/GitHistoryViewModel.cs:198`） |
| 未追跡のファイルの中身を差分に出す | Tower for Windows 13.0（2026-07-13） | `GitDiffService` の未追跡の経路（`src/Loomo.Services/Git/GitDiffService.cs:27`） |
| タブの状態の印（点・副題・色）をプログラムが申告する（OSC 21337） | iTerm2 3.7.0（2026-09-08） | OSC 7501 の申告を袖と動いているもの面へ（§24.61） |
| Claude Code のどれが入力待ちかを一目で | iTerm2 3.7.0（2026-09-08） | タイトルの印と hook から袖・動いているもの面へ（§24.1.1・§24.54） |
| 一時的な文字の置き場（Clippings） | iTerm2 3.7.0（2026-09-08） | ペグボード（§24.3） |
| 出力のかたまりを畳む | iTerm2 3.7.0（2026-09-08） | Terminal の折りたたみ（`C:\Projects\Terminal\src\Terminal.Controls\Tabs\TerminalTabView.Folding.cs`） |
| ワークツリーの状態（変更あり／なし）をブランチの横に | SmartGit 26.1（2026-05-18） | WT タブの `ChangeCount`（前回の誤認例と同じ） |
| ワークツリーの取り込み済み・古いものを片付ける | Tower for Windows 13.0 | ワークツリーは §24.55 で済み（推す 4 はブランチの話） |

## 調べた範囲

- 一次情報を読んだもの：atuin の GitHub Releases（v18.18.0〜v18.24.2 の Features 欄。2026-07-27〜10-22）、iTerm2 3.7.0 の changelog（`iTerm2-3_7_0.changelog`。
  ビルドは 2026-06-08、公開は news ページで 2026-09-08）と 3.7.6 までの `full_changes.txt`、Tower for Windows の release notes（12.0〜14.2・2026-04-29〜10-19）と
  Mac 版（16.0.2〜18.0）、SmartGit 26.1 の What's New（リリースは syntevo ブログで 2026-05-18）と 26.2 Preview の changelog（26.2.004〜26.2.015）、
  Beyond Compare 5.2 の changelog（5.2.0〜5.2.6）、delta（0.20.0・0.20.1）・difftastic（0.69.0〜0.71.0）の GitHub Releases、
  Notepad++ 8.9.7・8.9.8 のリリースノート。
- 10-24 以降に新しい版が出ていないことを GitHub API／npm で確かめたもの：Claude Code（2.1.310 が最新）、Zed、VS Code、Windows Terminal、lazygit、Tabby、GitButler。
- 版の一覧だけで、機能の版ではなかったもの：fzf（0.74.x）、starship 1.27.0、nushell 0.116、eza、zoxide、bat（期間内の版なし）。シェルの道具そのもので、部屋に関わる項目は無かった。
- 一次情報を読めなかったもの：Visual Studio 2026 18.10（elevenforum の要約だけ。18.11 までは前回読了）、18.12（見つからず）、Kaleidoscope（2025-07 の 6.1 より新しい
  公式のリリースノートが見つからず）、Directory Opus 13.22・13.23（日付の無いフォーラムの投稿だけ）、Notepad++ 8.9.6.x（版のページは読んでいない）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf、Arc、Notion——前回までと同じ理由。
