# 他ツールの新機能からの取り込み候補（2026-10-18・焦点：全領域・期間：2026-04〜2026-10）

物差し：主役は人間の部屋（§23 / §24 / §26 / §27）。§19.9 の「やらない」（拡張エコシステム・リモート開発・Web 版・
共同編集・Notebook）とクラウド必須の機能は外した。「いまの Loomo」はコードを grep して確かめた結果。

## 推す

> 2026-10-18 追記：1〜4 を実装した（§24.2.2・§24.17.2・§24.2.3・§24.5.3）。

### 1. 連鎖キーの続きを出す（元：Zed 1.23.2・2026-10-07／1.22.0・2026-09-30）
- 出典：https://zed.dev/releases/stable/1.24.2 （同ページ一覧の 1.23.2「Multi-keystroke binding completion hints」、1.22.0「pending keystroke indicator」）
- 元の機能：連鎖キーの 1 打目を押したところで、続けて押せるキーとコマンドを一覧で出す（which-key 型）。
- Loomo でやると：`Ctrl+W P` のような連鎖が既にある。1 打目のあと、袖か画面下に「次のキー → コマンド名」の小さな一覧を出す。
  覚えていない操作もキーボードから離れずに見つけられる（§24.2 の操作統一）。
- いまの Loomo：**部分的にある**。連鎖待ちの状態は `KeyboardResolver.Pending`（`src/Loomo.App/Input/KeyboardResolver.cs:28`）が持っているが、
  画面に出す処理はどこからも参照されていない。
- 規模：S（Loomo 内だけ。続きのキーの表は `_prefixed` から引ける）
- 関連：§24.2、§26.2（ショートカット設定）

### 2. ワークツリーごとの未コミットの変更を一覧に出す（元：GitKraken Desktop 12.3.0・2026-07-07）
- 出典：https://help.gitkraken.com/release-notes/current/
- 元の機能：「未コミットの変更か動いているエージェントがあるワークツリーごとに、コミットグラフへ WIP ノードを出す」。
- Loomo でやると：Git ペインのワークツリー一覧の各行に、変更件数と、そのフォルダーのターミナルで Claude Code が作業中かどうか（§38 の ◐/✳）を添える。
  並行作業のどれが手つかずで、どれが途中なのかを周辺視野で見られる。
- いまの Loomo：**部分的にある**。未コミットの件数（`±N`）は WT タブに既に出ている（§24.17・`GitWorktreeInfo.ChangeCount`）。
  無いのはエージェント側だけ。※初版では `GitWorktreeInfo` の先頭だけ読んで件数も無いと誤記していた
- 規模：S〜M（ワークツリーごとに `git status --porcelain` を回し、Claude Code のタブ状態と cwd で突き合わせる）
- 関連：§24.17、§24.1.1、§38

### 3. 選んでいる文字列でファイル検索・テキスト検索を始める（元：Zed 1.24.2・2026-10-14）
- 出典：https://zed.dev/releases/stable/1.24.2
- 元の機能：「File Finder が、エディタやターミナルで選んでいる文字列を検索語に入れた状態で開く」。
- Loomo でやると：`Ctrl+P` / 「テキストを検索して開く」を開くとき、エディタまたはターミナルで選んでいる文字列を検索語として入れておく（全選択状態にして、打てば置き換わるようにする）。
  ターミナルに出たパスやエラー文字列からそのまま検索へつなげられる（§24.3 素材の流れ）。
- いまの Loomo：**未実装**。`OpenCommandPalette` はモードのプレフィックスしか入れない（`src/Loomo.App/Views/Shell/Navigation/ShellWindow.CommandPalette.cs:55`）。
- 規模：S（ターミナルの選択文字列の取得口があるかは要確認。無ければ Terminal パッケージ側の作業になる）
- 関連：§24.2、§24.3、§24.26

### 4. フォルダー同士を比べる（元：Zed 1.24.2・2026-10-14）
- 出典：https://zed.dev/releases/stable/1.24.2
- 元の機能：「`zed --diff` で開いたディレクトリの差分を左右に並べて表示する」。
- Loomo でやると：Diff ペインのアドホック比較（§24.5）に「フォルダー × フォルダー」を足す。片方にしかないファイル・中身が違うファイルを一覧にし、選ぶと既存の左右差分（§24.16）で開く。
  Git 管理外の 2 つの版（展開した配布物、バックアップ、別 PC からのコピー）を部屋の中で比べられる。
- いまの Loomo：**未実装**（設計書・`Services/Diff` ともにファイル単位の比較だけ）。
- 規模：M（一覧の面を 1 つ足す。各行の差分表示は既存のものを使う）
- 関連：§24.5、§24.16

## 検討したが見送り

| 機能 | 製品・版 | 見送り理由 |
|---|---|---|
| Agents ウィンドウの定期実行・エージェントによるマージ・PR 作成 | VS Code 1.138〜（2026-09） | AI とクラウドが主役で、部屋の性質に効かない |
| エージェントの Dev Container セッション | VS Code 1.138 | 非目標（リモート開発） |
| ローカルの会話をクラウドへ引き継ぐ | Warp 2026-05 | クラウド必須 |
| 作業フォルダーごとにタブの色を自動で決める | Warp（検索結果では 2026-05） | 公式 changelog の取得結果で日付を確認できなかった。条件を満たさないので保留。§24.29 の棚と合わせて再調査の価値はある |
| Kitty キーボードプロトコル | Windows Terminal 1.25（2026-10-02） | Terminal パッケージ本体の機能。部屋への効き目が小さい |
| WPF Hot Reload（デバッグ中の XAML 編集を反映） | Rider 2026.2（2026-07〜08） | 魅力はあるが、netcoredbg と DAP の範囲を大きく超える（L 以上） |
| ワークスペースルール（タブを自動で振り分ける） | Vivaldi 8.0 | Loomo のワークスペースはフォルダー単位で、ブラウザのタブを振り分ける必要が薄い |
| リンク上で F2 を押すとファイル名を変え、リンクも書き換える | VS Code 1.142 | `LspFileMoveParticipant` が `willRenameFiles` を中継しているので、Markdown の言語サーバーが対応していれば既に効く可能性がある。marksman の対応を確認してから判断する |
| 長いコマンドの終了を通知する | Ghostty 1.3（2026-03-09） | 期間外。Loomo には袖のバッジ（§24.1）と留守中のまとめ（§24.22）が既にある |

## 既にあった（誤認防止）

| 機能 | 製品 | Loomo の該当 |
|---|---|---|
| 検索中にジャンプ先のタブをプレビューする | Windows Terminal 1.25 | パレットのプレビュー（`ShellWindow.CommandPalette.cs` の `ShowPalettePreview`） |
| ウィンドウを閉じても画面とスクロールバックが戻る | Windows Terminal 1.25 | §34 端末の常駐セッション |
| 差分の基準を分岐点にする（`git.diff_base`） | Zed 1.15.0 | §24.11 / §24.11.1 共有の比較基準 |
| 呼び出し階層 | Zed 1.19.2 | LSP の EditorSupport（呼び出し元・呼び出し先） |
| CSV / TSV の表プレビュー | Zed 1.17.2 / 1.19.2 | VGrid の EditorSupport |
| JSONL / NDJSON のプレビュー | Zed 1.23.2 | `NdjsonEditorSupport` |
| Emmet の「略記で囲む」 | Zed 1.20.1 | §36.7 |
| タブのピン留め | Warp 2026-07-23 | §24.29 タブの上限と棚 |
| ブランチの ahead / behind | Warp 2026-07-03 | `GitBranchService` / `GitStatusModels.Ahead/Behind` |
| 最近のファイル・最近の場所 | Zed 1.18.0 / 1.19.2 | `nav.recentFiles` / `nav.recentLocations` |
| Markdown に画像を貼ると保存してリンクを入れる | VS Code 1.142 | Editor パッケージ（sk0ya.Editor.Controls 1.0.45 の `ImagePasteOptions`）。Loomo は `ShellAppearanceCoordinator.cs:34` で設定を渡しているだけ（`LoomoSettings.ImagePasteDirectory` など）。※初版ではパッケージ側を見落として「推す」に入れていた |
| ワークツリーごとのターミナル | GitKraken 12.0 / 12.6 | ワークツリーを部屋として開く（`WorktreeRoomSeed`）＋ §34 |

## 調べた範囲

- 一次情報を読んだもの：Zed stable releases（1.15.0〜1.24.2）、Warp changelog 2026、VS Code updates（1.122・1.130・1.138・1.142）、
  GitKraken Desktop release notes（12.0.0〜12.6.0）、Windows Terminal v1.25.2733.0 release。
- 検索結果の要約だけで、一次情報を読んでいないもの：Rider 2026.2、IntelliJ IDEA 2026.2、Vivaldi 8.0 / 8.1、
  GitHub Desktop 3.6、lazygit 0.61〜0.63、PowerToys 0.97〜0.100、Obsidian 1.12（2026-02 なので期間外）。
- 見ていないもの：Arc / Zen、Raycast、Sublime Text / Sublime Merge、Fork、WezTerm、Neovim、Cursor / Windsurf。
- VS Code は 2026 年に入って週次のリリースになり、変更の大半が AI / エージェント関連。間の版（1.123〜1.141 の大半）は読んでいない。
