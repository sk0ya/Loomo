# 他ツールの新機能からの取り込み候補（2026-11-09・焦点：全領域・期間：11-08 以降の版＋未調査製品は 2026-05-09 以降）

物差し：主役は人間の部屋（§23 / §24 / §26 / §27）。§19.9 の「やらない」（拡張エコシステム・リモート開発・Web 版・共同編集・Notebook）と
クラウド必須・アカウント必須の機能は外した。「いまの Loomo」はコードを grep して確かめた結果。
前回までの 14 本（`feature-scout-2026-10-18*.md`〜`feature-scout-2026-11-08.md`）で推した・見送った・既にあった項目は重ねて挙げていない。
前回から 1 日しか経っておらず、主要製品はほぼ読み切っているので、今回は新しく出た版と、まだ読んでいなかった製品（Eclipse・Qt Creator 21・Xcode 27 ほか）を見た。

## 推す

### 1. 実行ターゲットの行に「いま動いている」と「前回の結果・何分前か」を出す（元：Eclipse IDE 2026-09＝Platform 4.41・2026-09）
- 出典：https://eclipse.dev/eclipse/news/4.41/platform.php（本文は GitHub の `eclipse-platform/www.eclipse.org-eclipse` の `news/4.41/platform.md` で読んだ）、
  https://eclipseide.org/release/noteworthy/2026-09/
- 元の機能：Run／Debug の履歴メニューで、まだ終わっていない起動がある構成の横に ⚙ を出す（同じ構成を二重に起動しないように）。
  履歴の各行のツールチップに「最後に実行したのは 5 分前」を出し、起動構成の一覧は「最近起動した順」に並べ替えられる。
- Loomo でやると：タイトルバーのデバッグメニューの実行ターゲット一覧（§28.11・`DebugRunTargetItem`）の各行に、
  動いている間は印を出し、終わったら前回の結果（✓／✗ と終了コード）と「何分前」を淡色で添える。いまは結果が IDE ペインの見出しの 1 行
  （「実行成功」「実行失敗（1）」）に**最後の 1 回ぶんだけ**出るので、どの対象を最後に回して通ったのかは、メニューを開いても分からない。
  席を離れて戻ったとき「さっき回したのはどれで、通ったか」がメニューを開いた一目で読める（復元・周辺視野）。前回の結果は
  ワークスペースごとに覚え（`DebugLaunchProfileStore` の隣か `WorkspaceSnapshot`）、部屋を開き直しても残す。
  §24.104（ワークツリーの行の ▶／✓／✗）と同じ印の形に揃える。
- いまの Loomo：**未実装**。`DebugRunTargetItem.cs` が持つのは `Name`／`Detail`／`Glyph`／`IsCurrent` だけで、実行の状態も結果も持たない。
  実行の結果は `ShellWindow.CSharpSolutionActions.cs:101` の `ExecuteCSharpTargetAsync` が `SetStatus` で見出しの文字列に書くだけで、
  実行中かどうかも `DebugViewModel.IsTaskRunning` の 1 つしかない（どの対象かは持っていない）。
- 作業の置き場：Loomo のみ。
- 規模：S（対象のキー（構成 ID／プロジェクトのパス＋launchSettings のプロファイル）ごとの「実行中・最後の結果・終わった時刻」を
  `ExecuteCSharpTargetAsync` とデバッグの開始・終了で書き、行のテンプレートに 1 列足す。永続化は小さな JSON 1 つ）
- 関連：§28.11、§24.104

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| 選んだ起動構成をまとめて 1 つのグループとして起動（Quick Group Launch） | Eclipse 4.41（2026-09） | Loomo のデバッグは 1 セッション前提（`DebugLaunchViewModel.CanStart` が `IsTaskRunning` を見る）で、まとめて起動はデバッグの道具の作り直し。部屋の性質ではなく道具の品質 |
| 選んだスレッド以外を再開（Shift+F8） | Eclipse 4.41 | デバッガーの道具の品質 |
| Problems／Tasks の文字での絞り込み | Eclipse 4.41 | Problems は重要度・出所・範囲で絞れる（`ProblemsViewModel.cs:165-174`）。文字の絞り込みは一覧の道具の品質 |
| コンソールの文字の大小を種類ごとに覚える・Find/Replace のキー割り当て・未保存の印を閉じるボタンに重ねる | Eclipse 4.41 | 操作感・見た目の品質 |
| 差分を 1 枚（unified）でも出す | Eclipse 4.41 | Diff ペインに「通し」「全文」がある（§24 の差分ペイン・コミット 0014be9e） |
| 再起動のあと端末を開き直さない設定 | Eclipse 4.41 | `LoomoSettings.PersistentTerminalSessions`（§34）で切れる |
| 端末のタブをドラッグで並べ替え | Eclipse 4.41 | 端末の品質（Terminal 側に並べ替えがある：`TerminalAppCoordinatorTests.TabsCanBeReorderedWithoutLosingItems`） |
| 自動 fetch・リポジトリの全プロジェクトを開く／閉じる | EGit 7.7（2026-09） | 自動 fetch は §24.92 にある／Eclipse のプロジェクトの概念 |
| 「Changes」「Git Log」のナビゲーションビュー | Qt Creator 21 Beta（2026-10-01） | Git パネル・Git ペインにある |
| コメントとコミットメッセージのスペルチェック・CMake のアウトライン・Qt Profiler・C++/QML の混合デバッグ・リモート Windows／Podman | Qt Creator 21 Beta | エディタの品質／言語固有／リモート開発（非目標） |
| エージェントのセッションを作り直す・切り替える・トークン量を出す | Qt Creator 21 Beta | AI のための機能（評価軸ではない）。Claude Code の利用量はハーネスペイン（§37）で見ている |
| 「現在のブランチだけに絞る」トグル | SourceGit v2026.24（2026-11-09） | Git ペインはブランチをダブルクリックで 1 本に絞れて、見出しのチップで戻せる（`GitSessionView.xaml:918`）。トグルの有無は道具の品質 |
| ブランチを消すとき、付いているワークツリーも消す | SourceGit v2026.24 | ワークツリーの片付けは §24.55 にある。ブランチの削除の流れの品質 |
| ファイルが丸ごと追加／削除のとき差分を片側だけ出す | SourceGit v2026.24 | 差分の表示の品質（Loomo の Diff ペインがそうなっているかは確かめていない） |
| `git am` の進行中の通知・ブランチ名をキーでコピー・AI の API キーを OS の資格情報へ | SourceGit v2026.24 | 進行中の操作の検出は `rebase-apply` を見ていて am も含む（`GitStatusService.cs:64`）／道具の品質／AI の設定 |
| ノートを別のノートに埋め込んで、その場で編集・ブロックの抜粋を同期・段組み | Trilium v0.107.0（2026-11-08） | ノートアプリの文書の作り。Loomo の素材は「ファイル」で、ペグボード（§24）とリンクで運ぶ |
| AI のツール呼び出しの前に許可を聞く | Trilium v0.107.0 | Loomo の AI ペインは最初から承認カード（`RequiresApproval`） |
| Command Palette の Adaptive Cards・グラフ | PowerToys v0.103.3099（11-09・プレビュー） | ランチャーの拡張の口 |
| 打っている間はインラインの問題を控えめに出す・ツールバーの並べ替え・テーマのスライダー・ブランチ選択を下の帯へ | Xcode 27（WWDC26・2026-06-08 発表、9 月正式） | エディタ・見た目の品質。ブランチは Loomo ではタイトルバーと Git パネルに出ている |
| `git.sign-on-push` を revset に | jj v0.47.1（11-05） | jj 固有 |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当（§・コード） |
|---|---|---|
| エディタで衝突の印（`<<<<<<<`〜`>>>>>>>`）を色分けする | Qt Creator 21 Beta | Editor の `EditorCanvas.cs:2047`（`ConflictOursHeader` ほか）。解くのは Diff ペイン（`DiffConflictViewModel`） |
| 1 ファイルの差分ビューをその場で編集 | Qt Creator 21 Beta | `DiffSideEditorPresenter.cs:379`（作業ツリー側は `IsReadOnly = false`） |
| 段落・見出しへのリンクを写して、開くとその場所へ | Trilium v0.107.0（ブロック参照） | 「この場所へのリンクをコピー（loomo://）」（§24.59・`CommandCatalog.cs:228`・`LoomoLinkClipboard`）、Markdown プレビューの見出しのアンカー（`MarkdownRenderer.cs:282`） |

## 調べた範囲

- 11-08 以降に出た版を GitHub API で確かめ、一次情報を読んだもの：SourceGit v2026.24（11-09）、Trilium v0.107.0（11-08）、
  PowerToys v0.103.3099 preview（11-09）、jj v0.47.1（11-05。前回は 0.47.0 まで）、atuin v18.25.0-beta.1（11-03。本文はインストール手順だけ）。
- 11-08 以降に新しい機能の版が無かったもの：VS Code（1.145.1・1.145.2 は 11-06 の修正版）、Zed（v1.27.0／v1.28.0-pre・11-04）、Windows Terminal（10-02）、
  lazygit（v0.66.3・11-07 は修正版で前回読了）、kitty（0.50.0・11-03）、Neovim（nightly のみ）、GitButler（0.23.2）、GitHub Desktop（3.6.11-beta1）、
  Zen（1.24.1b）、zellij（v0.45.1・08-28）、Tabby（v1.0.238・10-07）、Yaak、Git Extensions（v7.2.1・08-18）、Files（v4.3・10-11）、Logseq（2.0.2・10-07）、
  Ghostty・Helix・WezTerm（期間内の正式版なし）。
- 初めて一次情報を読んだ製品：Eclipse IDE 2026-09（Platform 4.41 の New and Noteworthy を全部・EGit 7.7）、Qt Creator 21 Beta（2026-10-01 の公式ブログ。
  最終版は 11-09 時点で RC まで）。
- 検索結果の要約だけのもの：Xcode 27（WWDC26 のセッション 258 の要約と Apple のプレスリリース。リリースノートの本文は読んでいない）。
- 期間内の版が見つからなかった・期間外だったもの：Rider 2026.3（09-07 からの EAP のみで、正式版の告知なし）、Visual Studio 2026 18.12（検索に出ず）、
  GNOME 51（09-16。Ptyxis／Text Editor の変更は見つからず）、Chrome（縦のタブは 04-07・Split View は 02 月で期間外）。
- 見ていないもの：Chrome／Edge DevTools、Cursor／Windsurf（AI が主役）、Arc（開発停止）、Notion（クラウド前提）——前回までと同じ理由。

## 判断（2026-11-09）

推した 1 件（実行ターゲットの行に実行中と前回の結果を出す）は、ユーザーの判断で取り込まない。次回以降の調査で同じ案を重ねて挙げない。
