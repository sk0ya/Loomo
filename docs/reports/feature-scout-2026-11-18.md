# 他ツールの新機能からの取り込み候補（2026-11-18・焦点：全領域・期間：直近 6 か月）

前回（`feature-scout-2026-11-14.md`）までで期間内の主要製品は読み終えているので、今回は **11-13 以降に出た版** と、
前回まで「告知が無い／確かめていない」としていた **Visual Studio 2026 18.12（11-10）・Rider 2026.3** を読んだ。

## 推す

### 1. ブラウザペインに見えている画面を、印を付けて Claude Code へ渡す（元：VS Code 1.147「Capture and mark up images」「Attach screenshots of other apps」・2026-11-18）
- 出典：https://code.visualstudio.com/updates/v1_147 （Chat の節）
- 元の機能：チャットに画像を添える前に、切り抜いたり矢印・枠・文字を書き込んだりできる。ほかのアプリの窓（直前まで見ていた窓）を撮って添えることもできる。
- Loomo でやると：ブラウザペイン（作っている Web アプリ・ローカルの HTML・面）の見出しか右クリックに「この画面を Claude Code へ渡す（送信はしない）」。
  見えている範囲を PNG にして、範囲を囲む・矢印・短い文字を書き込む小さな重ね画面を出す。確定したら §24.3.1 と同じ道、つまり
  `%TEMP%\Loomo\handoff\` に書いて `@参照` をプロンプトへ入力する（Enter は押さない）。いまは「ここの崩れ」を言葉で説明するしかない。
  人間が見ているものをそのまま指差して渡せるので、ペインから Claude へ素材が流れる（§24.3）。
- いまの Loomo：部分的にある。撮る口 `IBrowserService.CaptureScreenshotAsync`（`src/Loomo.Core/Abstractions/IBrowserService.cs:18`・
  `src/Loomo.App/Services/Browser/BrowserService.cs:196`）はあるが、呼ぶところが無い。ブラウザから Claude へ渡せるのは PR／Issue の文だけ
  （`src/Loomo.App/Services/Browser/BrowserSupport.cs:105,122,125`）。一時ファイルと `@参照` の道は `ClaudeHandoffFiles` にある。
- 作業の置き場：Loomo のみ。
- 規模：M。撮って渡すだけなら S。書き込みの重ね画面（WPF の InkCanvas 程度）で M。ui-usability の台本が要る。
- 関連：§24.3.1、§45（人間と Claude Code のあいだの線）、§21.5.1

### 2. 失敗したテストの「期待値」と「実際」を、差分ペインで比べる（元：Visual Studio 2026 18.12「Assertion diffs in Test Explorer」・2026-11-10）
- 出典：https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes （18.12.0 の Testing）
- 元の機能：Test Explorer で、期待値と実際の文字列を左右に並べ、違うところを色付けする。1 文字だけ違う場合も分かる。
- Loomo でやると：テスト一覧で失敗した行の右クリック、または詳細の欄に「期待値と実際を比べる」を置く。xUnit／NUnit／MSTest／Jest の
  失敗メッセージから `Expected:`／`Actual:` の組を切り出し、§24.5 のアドホック比較（`DiffComparison`：左＝期待値・右＝実際）として
  差分ペインに積む。長い JSON や複数行の文字列が 1 文字違うとき、メッセージを目で比べなくて済む。テストの結果を差分へ運ぶ素材の流れになる。
- いまの Loomo：部分的にある。テスト一覧は失敗の `Message` を文字のまま持って出すだけ
  （`src/Loomo.App/ViewModels/Debugging/TestItemViewModels.cs:99,154`・`DotnetTestRunner.cs:92`）。比較の受け口（`DiffComparison`・§24.5）はある。
- 作業の置き場：Loomo のみ。
- 規模：S〜M。テストランナーごとのメッセージの読み分けとテストで M 寄り。
- 関連：§24.5、§28（デバッグ・テスト）

### 3. Markdown を書いているエディタへファイルを落とすと、相対リンクか画像の参照を入れる（元：Visual Studio 2026 18.12「Markdown drag-and-drop links」・2026-11-10）
- 出典：https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes （18.12.0 の Editor and Text）
- 元の機能：ファイルを Markdown 文書へドラッグすると相対リンクが入る。対応する画像なら画像の参照が入る。
- Loomo でやると：`.md` のタブへ、エクスプローラー・Git パネル・差分の一覧・ターミナルのリンク（§24.52・§24.102）から落とすと、
  `[名前](相対パス)` を入れる。画像なら `![](相対パス)`。相対パスはその文書のフォルダーから作る（ワークスペースの外なら絶対パス）。
  ファイルを開くいまの振る舞いは、`.md` 以外への drop と修飾キー付きの drop に残す。設計書やメモを書きながら、部屋の中の素材をそのまま本文へ運べる。
- いまの Loomo：未実装。エディタへの drop は「開く」だけ（`src/Loomo.App/Services/FileSystem/FileDropPolicy.cs:25`・
  `FileDropInteractionController.cs:171`）。クリップボードの画像の貼り付けは Editor の `ImagePasteHandler` にあるが、ファイルの drop とは別の経路。
- 作業の置き場：キャレットの位置に入れるだけなら Loomo のみ。落とした位置に入れるなら、Editor に「画面上の点から文書の位置へ」の
  公開 API を足す（いまは `GutterHitTester` が内部で行だけ解く。NuGet 公開が要る）。
- 規模：M。drop の振り分け・相対パス・修飾キーの決め・実機でのドラッグの確認。
- 関連：§24.3、§24.102

### 4. 別の Markdown の見出しへのリンク（`other.md#見出し`）で、その見出しに着地する（元：Visual Studio 2026 18.12「Markdown heading links」・2026-11-10）
- 出典：https://learn.microsoft.com/en-us/visualstudio/releases/2026/release-notes （18.12.0 の Editor and Text）
- 元の機能：`[Installation](setup.md#installation)` のようなリンクで、相手のファイルを開いてその見出しまでスクロールする。エディタでもプレビューでも効く。
- Loomo でやると：プレビュー（EditorSupport）とエディタのリンクで `#…` を切り離し、ファイルを開いて見出しの行へ移る。プレビューも
  その見出しへスクロールする。見出しの id はプレビューの `HeadingId`（`MarkdownRenderer.cs:181`）と同じ作り方で照合する。
  プレビューの見出しの `#` を押すと `#見出しid` がコピーされるのに（`MarkdownPage.cs:883-893`）、ほかのファイルからそこへは飛べない。
  人間が文書の間を移るときの着地点が合うようになる。
- いまの Loomo：部分的にある。同じページの中の `#見出し` は自前でスクロールする（`MarkdownPage.cs:865-878`）。ほかのファイルへのリンクは
  `FileLinkResolver.TryResolve` が `#` 以下を含めたままパスを探すので見つからない（`src/Loomo.Core/Files/FileLinkResolver.cs:52-90`。
  `#` を切る処理が無い）。エディタからだと「ファイルが存在しません」になる（`ShellWindow.Links.cs:20-23`）。
  **コードを読んでの判断で、実機ではまだ押していない。**
- 作業の置き場：Loomo のみ。
- 規模：S。
- 関連：§32.10.1（リンクの解決）、§24.8

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| Send Later・下書き・Inbox の「確認済み」 | VS Code 1.147（11-18） | エージェントのセッションを管理する機能。Claude Code 側の話。Inbox の確認済みに近いものは §24.71「あとで」にある |
| Browser Use（エージェントが Chrome／Edge を操作する） | VS Code 1.147 | AI を増やす話で、Claude in Chrome が既にある |
| リモートの HTML を内蔵ブラウザでプレビューする | VS Code 1.147 | 非目標（リモート開発） |
| 内蔵ブラウザの重ね表示で暗くしない設定 | VS Code 1.147 | 部屋に効かない |
| パネルの角・間隔・つながったタブの見た目 | VS Code 1.147 | 部屋に効かない（見た目の好み） |
| Markdown の表をセル単位で編集する・`<details>` を畳める WYSIWYG エディタ | VS Code 1.147 | 部屋に効かない（Markdown を書く体験の話で、Loomo のエディタは Vim）。プレビューで `<details>` を畳める点は下の「既にあった」 |
| `.editorconfig` の rulers・CSS の色見本・TOML の色付け | VS 18.12（11-10） | 部屋に効かない（エディタ単体の機能） |
| Output ウィンドウで ANSI の色を出す | VS 18.12 | 部屋に効かない。Loomo のコマンドの出力はターミナルにあり、もともと色が付く |
| 右側のタブを閉じる・ツールウィンドウのタブの幅 | VS 18.12 | 部屋に効かない（タブの操作の話） |
| ドック中の窓のドラッグを Esc で取り消す | VS 18.12 | 部屋への効き目が小さい。参考：別窓のタブのドラッグは `DragDrop.DoDragDrop`（`DetachedPaneWindow.TabDrag.cs:274`）で、これは既定で Esc で取り消される。ドックの面のドラッグが Esc で取り消せるかは確かめていない |
| PR の画面のコメントの絞り込み・レビュアーの管理 | VS 18.12（Preview） | PR は Azure DevOps と GitHub のページをブラウザペインで見る形（§42・§24.27）。画面を作り直す話になり、部屋への効き目が小さい |
| NuGet の公開からの待ち時間（cooldown） | VS 18.12 | 部屋に効かない |
| 同じホストで複数の GitHub アカウントを使うとき、リポジトリをアカウントで分ける | GitHub Desktop 3.6.12-beta2（11-16） | 部屋に効かない |
| OSC 8 のリンクの URI をそのまま読む・OSC の長さの上限 | Rio v0.5.31（11-13） | 不具合を直す話。Loomo の Terminal パッケージで同じ穴があるかは別に確かめる価値がある（`a;b` を含む URI） |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| コマンドが見ていない間に終わったら、時間で絞って知らせる（`notify-on-command-finish`） | Rio v0.5.32（11-15） | §24.97（`src/Loomo.App/Services/Terminal/LongCommandNotice.cs`・`ShellWindow.PaneActivity.cs:180`） |
| Windows Terminal 1.25 の ConPTY（`OpenConsole.exe`）を同梱する | Rio v0.5.32 | Loomo も同梱の ConPTY（`x64\OpenConsole.exe`）で動く（AGENTS.md） |
| OSC 7501 の申告をホストへ渡す（`Action::ProgramStatus`） | Rio v0.5.31 | §24.61（Terminal の `ProgramStatusChanged`）。袖と動いているもの面に出る |
| OSC 7501 にバックグラウンドのコマンド・モニター・MCP のタスクも並べる | Claude Code 2.1.326（11-16） | Loomo は子の id ごとのレコードを動いているもの面に 1 行ずつ出す（`src/Loomo.App/Services/Diagnostics/RunningEntries.cs:61-72`）。Claude Code が送り始めれば、そのまま並ぶはず（実機では未確認） |
| localhost 以外のリンクも内蔵ブラウザで開く | VS Code 1.147 | ターミナルのリンクの URL はブラウザペインで開く（`ShellWindow.Links.cs:37`）。Loomo を既定のブラウザとして登録することもできる（`DefaultAppRegistration.cs:42`） |
| 入力を待っているセッションの印を強くする | VS Code 1.147 | 動いているもの面の「あなたの番」（`RunningEntryTone.Attention`・§24.54） |
| プレビューの `<details>`／`<summary>` | VS Code 1.147 | Loomo の Markdown の変換は、段落の地の文を逃がさず生の HTML として出す（`MarkdownRenderer.cs:502-550` の `Inline` は地の文を `Encode` しない）ので、WebView2 で畳める（コードを読んでの判断） |
| Solution Explorer でディスクの通りに並べる（File System view） | VS 18.12 | フォルダーツリーがディスクの通りの表示 |
| Git の履歴でコミットのハッシュを写す | VS 18.12 | コミット一覧と reflog の右クリック「ハッシュ」「短縮ハッシュ」（`GitSessionView.xaml:1167`・`GitReflogView.xaml:246`） |

## 調べた範囲

- 11-13 以降に出た版を GitHub API で洗い、一次情報を読んだもの：
  - VS Code 1.147（11-18。リリースノートの全部の見出し。AI 以外は Browser integration・Editor experience・Accessibility の数項目だけ）
  - GitHub Desktop 3.6.11（11-16。Git の更新だけ）・3.6.12-beta2（11-16）
  - Rio v0.5.31（11-13）・v0.5.32（11-15）（`rioterm.com/changelog` の該当部分）
  - Claude Code 2.1.326（11-16）・2.1.327（11-17）。§38 に関わるもの（ウィンドウタイトルの印・IDE プロトコル）には変更が無かった。
    `[VSCode]` の「`/ide` で 2 つ目のセッションが繋ぐと、新しい方が勝つ」は VS Code の拡張側の振る舞い。Loomo の `ClaudeIdeServer` で、同じ状況でどうなるかは確かめていない
- 前回まで確かめていなかったもの：Visual Studio 2026 18.12.0（11-10）・18.12.1（11-17。修正だけ）。Microsoft Learn のリリースノートの 18.12 の節を読んだ
  （ページの先頭 10 万字。AI の項目は除いて読んだ）。
- 確かめられなかったもの：**Rider 2026.3**。JetBrains の公式の What's New・ブログからは本文が取れず、正式版の日付は第三者の一覧（ComponentSource、11-10）でしか
  確かめられなかった。EAP の告知（09-07）にある「データブレークポイントを右クリックから」「Files view」「Home window」は、正式版の出典が取れないので候補にしていない。
- 11-13 以降に版が出ていなかったもの：Zed・Windows Terminal・GitButler・GitLens・zellij・Ghostty・WezTerm・Helix・lazygit・kitty・Warp・Alacritty・Tabby・Files・
  Git Extensions・Yaak・jj・SourceGit・Trilium・tmux・yazi（Zen 1.24.2b・PowerToys v0.103.3151・atuin v18.24.8・gh-dash v4.26.3 は前回読了）。Logseq と Neovim は nightly だけ。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）。理由は前回までと同じ。

## 実装済み（2026-11-19）

推した 4 件をすべて入れた。設計は §24.109・§24.110。

| 推す | 設計 | 状態 |
|---|---|---|
| 1. ブラウザの画面に印を付けて Claude Code へ | §24.110 の 1 | コミット済み。実機でパレットから開き、印を付けて渡すまで確かめた。右クリックの入口は未確認 |
| 2. 失敗したテストの期待値と実際を Diff で | §24.110 の 2 | コミット済み。実機では押していない（使い捨てのワークスペースでテストの実行が止まったため） |
| 3. Markdown へ落としてリンク | §24.110 の 3 | Editor 1.0.116 で口（`TryGetInsertionPositionFromPoint`）を公開し、Loomo のピンを上げてコミット。実機はローカルの Editor で確かめた（同じコミットの中身） |
| 4. `other.md#見出し` で見出しに着地 | §24.109 | コミット済み。実機でプレビューのリンクから見出しの行に着くことを確かめた |
