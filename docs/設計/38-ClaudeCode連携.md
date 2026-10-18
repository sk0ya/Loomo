# §38 Claude Code 連携（ターミナルの Claude Code を部屋の住人にする）

> 作成日: 2026-10-17 / 状態: **実装済み**（IDE 連携・フック・セッション再開・コンテキスト量・EDITOR の受け口）。
> 関連する正本: 袖の活動バッジ（タイトルの印・承認待ち）は §24.1.1、エディタ→Claude の @参照と「前面が埋まった
> 端末へは打ち込まない」は §24.3.1、常駐セッションは §34、ハーネスペイン（記録の場所探し）は §37。

## §38.1 目的と位置

人間は Loomo の**可視ターミナル**で Claude Code を動かして開発している。Loomo の内蔵エージェント（§2）とは別に、
部屋の中に居るもう一人の住人として扱う。ただし主役は変わらず人間で、Loomo が Claude を操るのではない。
「Claude が何をしているか・何を待っているか」が周辺視野に入り、人間の見ている場所・部屋の診断が言わなくても
Claude に伝わる。§24 の物差しで言えば次のとおり。

| 物差し | ここでの意味 |
|---|---|
| 周辺視野 | 袖のバッジ（作業中／入力待ち／承認待ち、§24.1.1）・タスクバーの点滅・見出しのコンテキスト量（§38.4） |
| 素材の流れ | 選択→Claude（selection_changed）、@参照（§24.3.1）、コンポーザ→Claude（§38.5）、提案→Diff（§38.3） |
| 復元 | セッションの再開（§38.4）——常駐セッションが切れても会話へ戻れる |

## §38.2 IDE 連携（VS Code 拡張と同じ口）

Claude Code は VS Code / JetBrains 拡張と、**IDE 側が待ち受ける WebSocket ＋ MCP（JSON-RPC）**で話す。公開仕様は
無いので、互換実装 claudecode.nvim の `PROTOCOL.md` を正本にし、**Claude Code 2.1.302 を実際に繋いで確かめた**
（initialize → tools/list → `closeAllDiffTabs` → `getDiagnostics`、`selection_changed` から選択を答えられる）。

- **発見**: IDE が `~/.claude/ide/<port>.lock`（`CLAUDE_CONFIG_DIR` を尊重）に `pid`・`workspaceFolders`・
  `ideName`・`transport:"ws"`・`authToken` を書く。ターミナルに `CLAUDE_CODE_SSE_PORT=<port>` と
  `ENABLE_IDE_INTEGRATION=true` があれば、そこで起動した claude は lock を読んで自分から繋ぐ。Loomo は
  **自分のターミナルにだけ**これを渡す（`ConfigureTerminalSession` が常駐でも自前でも同じ環境変数を足す）。
- **認証**: 握手の `x-claude-code-ide-authorization` がトークンと一致しなければ 401。待ち受けは 127.0.0.1 のみ。
- **ポートは前回と同じものを先に試す**（`%LOCALAPPDATA%\Loomo\claude-ide-port`）。端末のシェルは Loomo より
  長生きする（§34）ので、再起動後も古いシェルの環境変数が指すポートで待てるように。
- **WebSocket は自前の握手**＋`WebSocket.CreateFromStream`。HttpListener（http.sys）は URL 予約や管理者権限が
  絡むので使わない。
- **答えるツール**（`ClaudeIdeTools`）: `openFile`・`openDiff`・`getCurrentSelection`・`getLatestSelection`・
  `getOpenEditors`・`getWorkspaceFolders`・`getDiagnostics`・`checkDocumentDirty`・`saveDocument`・`close_tab`・
  `closeAllDiffTabs`。`executeCode`（Jupyter）は持たない。
  - `getDiagnostics` は**問題一覧の全件**（`ProblemsViewModel.AllProblems`、表示の絞り込みを通さない）——
    LSP・ビルド・コンパイラ・StyleCop・タスクの全発生源。人間の見え方の好みで Claude の知る範囲を狭めない。
  - 行・列は 0 始まり。URI は `file:///C:/…`（ドライブは大文字。`file:///c%3A/` も受ける）。
- **選択の通知**: アクティブなエディタの選択（無ければキャレットの空の選択）を、落ち着いてから（200ms）
  `selection_changed` で送る。**誰も繋いでいなければ何もしない**——打鍵の経路に仕事を足さない（§31.15）。
- 層: 通信とツールは `Loomo.Services/ClaudeCode/`（`ClaudeIdeServer`・`ClaudeIdeTools`）、エディタ・診断・差分への
  問い合わせは App が `IClaudeIdeHost` を実装（`ShellWindow.ClaudeIde.cs`）。サーバーのスレッドから来るので
  答えはすべて Dispatcher を通す。
- 設定: `LoomoSettings.ClaudeCodeIdeIntegration`（既定 ON、ターミナル設定のチェック）。再起動後に効く。

## §38.3 編集の提案（openDiff）

Claude が承認の要る編集をするとき、IDE が繋がっていれば `openDiff` で提案を送り、**人間が決めるまで返事を待つ**。

- 差分は Diff（ペインか別ウィンドウ、§24.5.2 の振り分け）に「いま ↔ Claude の提案」の比較として出す。
- 決めるのは**ターミナルペイン上端のバー**（`ClaudeProposalViewModel`）——「受け入れる／却下／差分を見る」。
  Claude はターミナルにも同じ提案の Yes/No を出している。そちらで答えれば Claude が `close_tab` を送ってくるので、
  バーからも消える（却下として閉じる＝claudecode.nvim と同じ）。どちらで答えても同じ結論。
- 受け入れの返事は `FILE_SAVED` ＋最終本文、却下は `DIFF_REJECTED` ＋タブ名。**ファイルを書くのは Claude**で、
  Loomo は書かない。受け入れの1秒後に開いているタブを読み直す。
- 提案は重なりうる（並行するエージェント）ので列に積み、先頭から1件ずつ。同じタブ名で重ねて来たら前のを畳む。

## §38.4 フック・セッション・コンテキスト量

`Notification` と `SessionStart` の2つのフックを Loomo へ届ける（`ClaudeHookBridge`）。

- **経路**: フックのスクリプト（`%LOCALAPPDATA%\Loomo\claude\loomo-claude-hook.ps1`）が、Loomo のターミナルが
  渡す `LOOMO_TERMINAL_TAB`（どのタブか）と `LOOMO_CLAUDE_HOOK_PIPE`（宛先、ユーザーごとに固定名）を読み、
  フックの入力 JSON をタブ ID と一緒に1行で名前付きパイプへ送る。**どちらも無ければ即終了**——設定はユーザー全体に
  入るので、Loomo の外の claude では無害であることが前提。標準入力は WinPS 5.1 の OEM コードページを避けて
  バイトのまま UTF-8 で読む。
- **設定は明示的に**: パレット「Claude Code のフックを設定」で `~/.claude/settings.json` に足す（他の設定・
  他のフックには触らず、Loomo のスクリプトを指す項目だけ。初回だけ `settings.json.loomo-backup` を残す）。
  「フックを外す」で Loomo の項目だけ外す（`ClaudeHookInstaller`）。
- **承認待ち**: `notification_type: "permission_prompt"`（古い版は文言の "permission"）で袖のバッジを
  「● Claude 承認待ち」にする（§24.1.1）。実測で、承認ダイアログの間もタイトルの印は ✳ ＝タイトルだけでは
  ターンの終わりと区別できない。
- **セッション**: `SessionStart` の `session_id`・`transcript_path` でタブとセッションを結ぶ。
  - ターミナル見出しに**コンテキスト量**（`⧉ 123k`。最後の応答の usage の入力＋キャッシュ読み書き）を出す
    ——Claude Code の statusline 相当を部屋の見出しに。ターンの終わりとタブの切り替えで、記録を裏で読み直す。
  - パレット「Claude Code のセッションを再開…」: ワークスペースのセッション（§37 の記録の場所探しを流用）を、
    Claude が付けた題名（`ai-title`）・最後の頼みごと・コンテキスト量つきで新しい順に並べ、選んだものを
    新しいタブで `claude --resume <id>`（シェルがプロンプトを出してから打つ。5 秒で諦めて打つ）。

## §38.5 コンポーザと EDITOR

- **コンポーザ→Claude**: 前に出ているタブで Claude が動いていれば、▶ は PowerShell の1行に組み直さず、本文を
  **bracketed paste** で包んで送り、300ms 後に Enter。複数行が1つの頼みごとのまま届く（行ごとの Enter で
  ばらばらに送信されない。実測で 3 行が1件として届いた）。
- **EDITOR の受け口**（`LoomoEditBridge`、`code --wait` 相当）: Loomo のターミナルの `EDITOR`／`VISUAL` は
  `loomo-edit.cmd`（→ `loomo-edit.ps1`）を指す。スクリプトはパスを名前付きパイプへ送り、Loomo はそのファイルを
  エディタのタブで開いて、**タブを閉じたら**返事をする。Claude Code の Ctrl+G（プロンプトを外部エディタで書く）
  も `git commit` のメッセージも部屋のエディタで書ける（Ctrl+G の往復は実物で確認）。Loomo に繋がらなければ
  メモ帳で開いて待つ。Loomo を起動した環境に `EDITOR`／`VISUAL` があれば上書きしない。

## §38.6 タイトルの読み方（Terminal 1.0.45）

§24.1.1 の印は、タブ見出し（`HeaderTitleChanged`）ではなく**生のタイトル**（`TerminalTabView.WindowTitleChanged`、
Terminal 1.0.45 で追加）から読む。見出しは `/` `\` を含む部分をパスとみなして捨てるので、「◐ Fix src/foo.cs」の
ような題が読めず状態を見失っていた。

## §38.8 動いている Claude Code の一覧（2026-10-18）

**動機**: VS Code の Agents ウィンドウは、並行して回しているエージェントのセッションを「入力待ち」「結果あり」の
印つきで並べる。Loomo の袖のバッジ（§24.1.1）はペイン単位の集約なので、ワークツリーごとに Claude を回して
いると「承認待ち」は分かっても**どのタブが**待っているかが分からず、タブを1つずつめくることになる。

- **パレット「動いている Claude Code の一覧」**（`claude.agents`）。このワークスペースのターミナルタブのうち
  Claude の印が出ているものを1行ずつ。選ぶとターミナルペインへ行き、そのタブを前に出す。
- **並びは人間の番が先**: 承認待ち（§38.4 のフック）→ 入力待ち（見ていない間にターンが終わった）→ 待機 → 作業中。
  同じ段の中は最後に印が動いた順（`PaneActivityTracker.AgentTabs`）。
- **行の中身**: 題は Claude が付けたタイトル（印を外したもの、`ClaudeCodeTitle.Subject`）、無ければ記録の
  `ai-title`、それも無ければタブ名。右端に状態とコンテキスト量（`⧉ 123k`）。詳細にタブ名・場所（cwd）・
  最後の頼みごと・セッション ID。コンテキスト量と頼みごとは記録から読むので裏で読み、読めなくても一覧は出す。
- **1つも居なければ**そう言い、「セッションを再開…」（§38.4）を隣に置く——終わったセッションへ戻る入口はそちら。
- **持たないもの**: VS Code の「PR がマージされたらセッションを完了に」は持たない。Claude のセッションは
  ターミナルのタブそのもので、閉じれば消える——別の「完了」状態を足すと、タブとの二重管理になる。
- 実装: `Services/Diagnostics/ClaudeCodeTitle.cs`（`ClaudeAgentTab`・`ClaudeAgentState`）、
  `PaneActivityTracker.AgentTabs`、`Views/Shell/Activity/ShellWindow.ClaudeAgents.cs`。
  テスト: `ClaudeCodeTerminalActivityTests`（並び順・終わったタブが消える・題の読み方）。

## §38.7 既知の穴

- 常駐セッションの復元直後、待機中の claude は次にタイトルが変わるまで検出されない（OSC133 の C も replay では
  履歴扱い）。その間は §24.3.1 の「前面が埋まった端末へは打ち込まない」も効かない。
- 既存のタブ（設定を入れる前に開いたもの・常駐で繋ぎ直したもの）には環境変数が無い。IDE 連携・フック・
  EDITOR は**新しく開いたタブ**から効く。
- `getOpenEditors` などはモデルには出ていない（Claude Code が IDE ツールのうちモデルに見せるのは診断など一部）。
