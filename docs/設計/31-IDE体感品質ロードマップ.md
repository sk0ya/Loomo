# §31 IDE体感品質ロードマップ

> 作成日: 2026-08-02  
> 対象: 補完の体感品質、Problems、デバッグ、LSPの耐障害性、キーボード／フォーカス  
> 関連する正本: UIは§26、デバッグは§28/§29、LSP所有境界は§30。本章はそれらを横断する実装順序と完了条件の正本。

## §31.1 目標と現在地

目標は「機能が存在する」から「連続した操作を迷わず完遂できる」へ移すこと。VS CodeやRiderの機能総数を
追うのではなく、Loomoの部屋・舞台・袖という構造を保ったまま、日常の編集ループをIDEとして信頼できる状態にする。

2026-08-02時点の基準線：

- Editor `1.0.70`でRoslynのpull diagnostics（`textDocument/diagnostic`）に対応。
- Loomoは変更後300msで文書診断を取得し、古い版の応答を破棄する。波線とIDEのProblemsへ同じURI単位スナップショットを配る。
- ProblemsはBuild/LSPを統合し、発生源、重大度、ファイル、位置を表示する。Undoで空診断になれば古い項目を消す。
- Undoで保存地点へ戻るとdirty表示が消える。Editor/Terminalの内部入力ProxyはAutomationのControlビューへ露出しない。
- デバッグ開始・終了は動作し、終了コードと所要時間を出力する。条件付きBP、DataTip、Watch、例外停止等の個別機能は§28のとおり実装済み。
- 公開NuGetだけのクリーン復元でビルド警告0・エラー0、Loomo 1367件、Editor Core 1228件、
  Editor Controls 51件が成功。実機で「構文エラー→波線→Problems→Undoで消去」まで確認済み。

ここからの不足は主に、補完の選択品質、Problemsから修正までの導線、デバッグの一連体験、LSP状態の説明、
ペイン間フォーカスの予測可能性である。

## §31.2 実装原則と所有境界

1. **操作の完了を単位にする。** APIやボタン単体ではなく、「入力→候補採用→次の入力」「停止→原因確認→再開」のような旅程で受け入れる。
2. **新しいLSPセッションを作らない。** プロセス、文書、診断は§30の`ILspWorkspace`/`ILspDocument`が正本。ビューは表示状態だけを持つ。
3. **Editorに属するものをLoomoへ複製しない。** 補完ポップアップ、commit character、snippet、波線、CanvasのAutomationはEditor。
   ワークスペース集約、Problems、導入／再接続UX、ペイン移動はLoomo。
4. **失敗と待機を区別する。** `接続中`、`解析中`、`再接続中`、`未導入`、`失敗`を「LSP: ready/なし」の二値へ潰さない。
5. **非同期結果は世代管理する。** 補完、診断、署名、Code Actionは文書URI・版・CancellationTokenを持ち、古い結果でUIを戻さない。
6. **マウスとキーボードを同格にする。** 主要操作はコマンドIDを持ち、パレット／設定可能なキー／UIから同じ実装へ到達させる。
7. **公開パッケージは各コントロールリポジトリで1つにまとめる。** EditorはCore/Controls/Defaultsを
   `sk0ya.Editor.Controls` 1個へ、TerminalもCore/Controlsを`sk0ya.Terminal.Controls` 1個へ同梱する。
   内部プロジェクトを個別publishしない。変更は全内部プロジェクトのテスト→統合パッケージの次の正規版を
   pack/publish→Loomoを公開NuGetだけで復元→実機確認の順に閉じる。

## §31.3 Phase 0 — 再現可能な品質ベースライン

後続Phaseの「良くなった」を主観だけにしないため、最初に操作シナリオと計測点を固定する。

実装：

- `docs/検証/IDE体感品質チェックリスト.md`を作り、C#、TypeScript、LSPなしの3ワークスペースで同じ手順を実行できるようにする。
- 補完は要求開始／最初の候補／破棄理由／採用まで、LSPは起動／initialize／解析可能／再接続までを既存ログへ時刻付きで記録する。
- デバッグは開始要求／adapter ready／最初の停止／終了までを記録し、終了理由と終了コードをセッション要約へ残す。
- UI操作テストで扱いにくいCanvas部分は、Editorの純ロジック／ViewModelテストと実機チェックを明確に分ける。

完了条件：

- 同じ操作を別リリースで再実行でき、各Phaseの回帰判定に使える。
- 通常操作では追加ログI/Oを発生させず、診断フラグ有効時だけ詳細記録する。

## §31.4 Phase 1 — 補完を「出る」から「選べる」へ

優先度は最上位。短い編集のたびに触るため、1回の違和感が全体評価へ直結する。

実装（主に`C:\Projects\Editor`）：

- LSPの`sortText`、`filterText`、`preselect`、`textEdit`、`insertTextFormat`を保持し、表示順と挿入範囲へ反映する。
- prefix一致を先頭一致→単語境界→部分一致の安定順で再ランキングし、サーバー順位を同点時の順序として残す。
- `triggerCharacters`、再トリガー、削除時の再絞り込み、0件、ロード中、失敗を別状態にする。
- `commitCharacters`で候補を確定した後、その文字を一度だけ入力する。Enter/Tab/Ctrl+N/Ctrl+P/Escapeの競合を整理する。
- snippetの最低限（`$0`、`${1:name}`、同番号プレースホルダー）を実装し、未対応構文はプレーンテキストへ安全に劣化する。
- 補完表示中もsignature helpを独立更新し、ポップアップ同士がキャレットやキー入力を奪い合わないようにする。
- 要求ごとに文書版とCancellationTokenを持ち、入力後に届いた旧候補を表示しない。
- 候補の種類、詳細、非推奨、選択位置を視認でき、スクロールしても選択項目を可視範囲へ保つ。

完了条件：

- C#とTypeScriptで「`.`入力→候補移動→Tab/Enter/commit character採用→続けて入力」がマウスなしで成立する。
- 高速に3文字入力しても、1文字目の遅い応答へ候補が巻き戻らない。
- Undo 1回で補完採用前へ戻る。snippetを含め本文・キャレット・dirty状態が一貫する。
- Editorに候補順位、置換範囲、commit character、snippet、キャンセル競合の自動テストを追加する。

## §31.5 Phase 2 — Problemsを「一覧」から「修正の入口」へ

実装（主にLoomo、Code Actionの文書操作はEditor APIを再利用）：

- Error/Warning、Build/LSP、現在ファイル／ワークスペース全体のフィルターと件数を追加する。
  発生源は Build・Compiler・LSP・StyleCop に、tasks.json のタスク出力を problem matcher で変換した
  **Task**（§31.17）を加えた。
- URI＋range＋source＋message＋codeを安定キーにし、Build/LSPの同一内容を必要に応じて重複排除する。
- `F8`/`Shift+F8`相当の次／前の問題コマンドを追加し、ファイルを開いて該当rangeを選択・中央表示する。
- 行の主操作をジャンプ、副操作をCode Action（Quick Fix）にする。複数候補、適用失敗、文書版不一致を表示する。
- LSP再接続、文書close、ワークスペース切替、空のfull reportで古い診断を確実に消す。`unchanged`と要求失敗では維持する。
- 大量診断はUI仮想化し、更新のたびに選択・スクロールを不必要に失わない。
- （追加 2026-10-10）問題の箇所を1枚の抜粋タブ（§35.3）に並べて、その場でまとめて直せる：件数の行の「▤ 抜粋タブ」
  （表示中すべて）、診断行の右クリック「CS8618 をすべて抜粋タブで開く」（同じコードを全ファイルから）、
  見出し／行の右クリック「このファイルの問題を抜粋タブで開く」。詳細は §35.3「問題一覧から」。

完了条件：

- 構文エラー作成→Problems表示→キーボードで移動→Quick Fixまたは手修正→項目消去が一続きで成立する。
- Build中もLSP項目が消えず、Build結果更新でも別ソースの項目を壊さない。
- 1000件の診断更新でUIスレッドを長時間占有しない。

## §31.6 Phase 3 — デバッグの主要旅程を閉じる

§28の個別機能を、次の4旅程として統合検証・補修する。

1. 起動→ブレークポイント停止→Variables/DataTip/Watch→Step Over→Continue→正常終了。
2. 例外停止→例外名・message・throw位置→Call Stack→再開／停止。
3. 条件付きBP／ログポイント→条件エラーまたはログ出力→セッション継続。
4. 起動失敗／adapter終了／対象の非0終了→原因、終了コード、所要時間、再試行導線。

実装：

- 停止理由を正規化し、ヘッダーと出力の先頭で「どこで、なぜ止まったか」を同じ文言で示す。
- 例外停止時はVariablesへ機械的に切り替えず、例外概要とCall Stackの先頭フレームを優先表示する。
- フレーム切替時にEditorの実行行、Variables、Autos、Watch、DataTipの評価コンテキストを同じframeIdへ揃える。
- session endを`正常終了`、`終了コード N`、`ユーザー停止`、`adapter切断`、`起動失敗`へ分類する。
- 操作不可の理由をtooltipだけに置かず、ステータスへ短く表示する。
- C#とTypeScriptで共通化できる旅程テストを`DebugManagerViewModelBase`周辺へ置き、adapter差は契約テストに分ける。

完了条件：

- 上記4旅程を実機で完遂し、終了後すぐ同じ構成を再実行できる。
- 停止中に表示するソース位置と評価対象frameが食い違わない。
- 失敗時にログを開かなくても次の行動（設定修正、再試行、導入）が判断できる。

## §31.7 Phase 4 — LSPの状態・再接続・差異を説明する

実装（Editorプロトコル部品＋Loomoワークスペース所有層）：

- 状態を`未構成 / 起動中 / 初期化中 / プロジェクト読込中 / ready / 再接続中 / 停止 / 失敗`としてモデル化する。
- ステータスバーは現在文書、設定画面はサーバー単位で状態・実行ファイル・最後のエラー・再試行を表示する。
- pull diagnosticsは現行300msデバウンスを維持し、`full`、`unchanged`、`null（失敗）`の意味をテストで固定する。
- ContentModified/ServerCancelledは静かな再試行対象、プロセス終了は§30のバックオフ再接続対象として分ける。
- capabilityごとに非対応を正常なdegradeとして扱い、formatterやBuildへ勝手に置換しない。
- Roslyn、typescript-language-server、pylsp、marksmanで最小マトリクスを回し、診断方式・完了トリガー・再接続差を記録する。

完了条件：

- 起動が遅い状態と失敗が見分けられ、ユーザーが再起動／設定／導入のどれを選ぶべきか分かる。
- サーバー強制終了後、開いている文書が再送され、補完・診断・参照が復帰する。
- 別ワークスペースの古い診断や応答が現在のProblemsへ混入しない。

## §31.8 Phase 5 — キーボードとフォーカスをIDE品質へ揃える

実装：

- Editor、Terminal、Problems、Debug、Sidebar、AI入力をフォーカス領域として列挙し、領域ごとの「戻り先」を保持する。
- 既存`Ctrl+W → h/j/k/l`を全ペインで検証し、非表示・縮小表示・デタッチ・モーダル中の規則を固定する。
- ペインを舞台へ出したときは最後の内部フォーカス（Editor Canvas、Terminal surface、一覧の選択行等）を復元する。
- Escapeの優先順位を、補完／署名／検索／モーダル／コマンド待ちの内側から外側へ統一する。
- custom-drawn surfaceはAutomationでDocumentとして公開し、内部ProxyをControl/Contentビューへ出さない。
- キーボードのみのスモーク手順と、主要AutomationPeerの回帰テストをEditor/Terminal/Loomoへ置く。

完了条件：

- マウスなしでEditor→Problems→該当箇所→Terminal→Editorを移動でき、文字入力が別ペインへ漏れない。
- 舞台切替、デタッチ復帰、設定オーバーレイを挟んでもキャレット／選択／入力先が予測可能である。
- Narrator/UI AutomationからEditor CanvasとTerminal surfaceを識別でき、内部Proxyは列挙されない。

## §31.9 実施順序、リリース、判定

実施順序は **P0 → P1 → P2 → P3 → P4 → P5**。P1でEditorの補完APIを確定してからP2のCode Action導線を作り、
P3までで日常の編集・修正・デバッグを閉じる。P4/P5は横断的だが、先に行うと状態モデルやフォーカス規則を
未確定UIへ二度実装するため後段とする。ただし各Phaseで見つかったデータ損失、入力先誤り、クラッシュは順序に関係なく優先修正する。

各Phaseのリリース判定：

1. Core/ViewModelの自動テスト。
2. 変更したコントロールリポジトリ内の全プロジェクトをテストする。
3. Loomoの全テストと`dotnet build sk0ya.Loomo.sln`（警告0・エラー0）。
4. 変更したリポジトリから統合パッケージを**1個だけ**publishする
   （Editor=`sk0ya.Editor.Controls`、Terminal=`sk0ya.Terminal.Controls`）。
5. 空のNuGetキャッシュ＋nuget.orgだけでrestore/build/test。
6. §31.3の実機シナリオを公開版で実行し、一時変更をUndo、対象ファイルのハッシュ一致を確認。

Phase完了はコードのmergeではなく、上記6段階と各節の完了条件を満たした時点とする。

## §31.12 打鍵の応答（2026-09-12 実施）

「`.cs` を編集していると入力が引っかかる」への対処。原因は**どれも UI スレッドで打鍵ごとに走る
O(ファイル全体) の処理**で、実測（7065行・300KB の `.cs`、`VimEditorControl.xaml.cs` 自身）は次のとおり。
どれか 1 つではなく、合計 11ms/打鍵 が毎回かかっていた。

| 打鍵ごとに走っていたもの | 実測 | 対処 |
|---|---|---|
| パンくずのフォールバック見出し抽出（`DocumentSymbolExtractor.Extract` 全文） | **8.08ms** | 250ms のデバウンス＋背景スレッド。ファイル切替直後の 1 回だけ同期 |
| `didChange` の本文組み立て＋JSON 化＋パイプ書き込み | **1.6〜4ms** | 送信専用スレッドへ。未送信分は最新版で置き換える |
| `CreateContentChange` の `Replace`×2＋LINQ `Count` | **2.36ms**（上の内数） | 割り当てなしの 1 パスへ |
| 空白の目印（`WhitespaceIssueDetector.Detect` 全行） | **1.26ms** | 可視行だけ（スクロール／サイズ変更で再計算） |

`.cs` で目立つのは、①C# のファイルが大きいこと、②Roslyn の起動に数十秒かかり、その間ずっと
パンくずがフォールバック経路（＝全文抽出）に落ちること、③Roslyn は `didChange` のたびに再解析するので
stdin を読み遅れやすく、**書き込みそのものがブロックする**こと、の 3 つが重なるため。

### §31.12.1 送信を UI スレッドから外す（`C:\Projects\Editor`）

`LspProcess` は送信専用スレッド 1 本（`LspStdin`）を持ち、呼び出し側は `OutgoingMessageQueue` へ積むだけで返る。
JSON 化もパイプ書き込みもそのスレッドでしか起きない。1 本なので**積んだ順＝サーバーへ届く順**が保たれる。

`didChange` は本文の組み立てを**送信直前まで遅らせる**（`SendNotification(method, Func<object?>, coalesceKey)`）。
未送信の同じ文書の `didChange` は最新版で置き換えられ、追い越された版は本文を作られもしない——速く打つほど仕事が減る。
差分の基準は「最後に実際に送った本文」で、その更新も送信直前に起きるので、間の版を飛ばしてもサーバーの内容とずれない。

置き換えのとき**キュー内の位置は動かさない**。動かすと、あとから積まれた補完要求が古い本文のまま先に届く
（サーバーは受け取った順に処理するので、文書の更新はそれを使う要求より前になければならない）。

### §31.12.2 検証

- Editor: `OutgoingMessageQueueTests` 9件（FIFO・置き換えの位置保持・キー解放・遅延生成）と
  `Incremental_range_matches_the_old_normalizing_computation` 14ケース（書き換え前の式との一致）を追加。
  Editor 全体 1559件成功。
- Loomo: 3185件成功。実 Roslyn との統合テストに
  `Rapid_successive_changes_leave_the_server_holding_the_last_version` を追加
  （40版を連投してから最後の版だけで診断が変わることを、壊れた本文・直した本文の両方向で確認）。
- 実機起動確認済み。

## §31.13 入力の先読み（2026-09-12 実装）

キャレットの先に「この行はこう続く」を薄く出し、**Tab** で丸ごと・**Ctrl+→** で 1 語ずつ受け入れる。
IntelliJ / Visual Studio が見せているものと同じ形。`:set noinlinesuggest` で切れる（既定は有効）。

**IntelliJ のモデルは持ってこられない**（IDE にバンドルされたプラグインで、単体配布が無く JetBrains 製品ライセンス下）。
一方で作り方は論文（*Full Line Code Completion: Bringing AI to Desktop*, arXiv 2405.08704）で公開されていて、
その構成が Loomo の手持ちとほぼ一致していた——100M パラメータ、context 384 トークン、INT8 で約 100MB、
推論は **ONNX Runtime か llama.cpp**、レイテンシ 50〜150ms、グレー表示で Tab 受け入れ、
そして **IDE の検査で構文的に正しくない候補を捨てる**。Loomo には llama.cpp バックエンドも KV 再利用も
LSP 診断もある。

### §31.13.1 二段構え

| 段 | 出どころ | 応答 | 要るもの |
|---|---|---|---|
| 内蔵 | エディタの `BufferLinePredictor`（同じバッファの既出行） | 即時 | 無し |
| 上乗せ | Loomo の `FimCompletionEngine`（Qwen2.5-Coder 0.5B・FIM） | 約 280ms | モデル 506MB |

内蔵の提案が**先に**出て、モデルの答えが届いたら置き換わる。遅い供給元を待つあいだ何も出ない時間を作らない。
モデルを入れていなければ内蔵だけで動く（既定はこの状態）。

### §31.13.2 実測と、そこから決めたこと

Ryzen 5 3500（6 コア）・CPU のみ・Qwen2.5-Coder 0.5B。

| | Q8_0 (506MB) | Q4_K_M (379MB) |
|---|---|---|
| モデルロード（1 回だけ） | 471 ms | 同等 |
| キャッシュなし・335 トークン | 1.0 秒 | 1.1 秒 |
| **打鍵ごと**（KV 再利用・suffix 3 行） | **216〜320 ms** | 223〜330 ms |
| 打鍵ごと（suffix 10 行） | 495〜543 ms | 512〜593 ms |

- **KV キャッシュ再利用が成否を分ける。** 打鍵で変わるのは行末だけなので、411 トークン中 298 を再利用して
  113 だけ送り直す。無いと毎回 1 秒を超え、先読みとして成立しない。
- **suffix を短くするのが一番安い改善。** FIM のプロンプトでは後ろの本文がキャレットより後ろに並ぶため、
  打鍵のたびに丸ごと送り直しになる。10 行→3 行で半分になった。prefix は逆に頭から変わらないので長くてよい。
- **量子化は効かない。** Q4 にしても速くならず（0.5B は演算律速でメモリ帯域律速ではない）、出力だけが崩れた。
  **Q8_0 を使う。**
- **SPM 並びは使えない。** 後ろの本文を先頭へ回せば再利用率が上がる（変わるのが末尾だけになる）ので試したが、
  6 問すべて外した。Qwen2.5-Coder はその並びを学習していないと見るのが妥当で、速度のために正しさは捨てられない。
  **PSM 固定。**

### §31.13.2.1 CPU の取り分（2026-09-12 追記）

最初は全コア（6/6）で回していたが、**アプリの中では単独ベンチの 270ms が 1.5〜3.9 秒に伸びた**
（診断ログに残っている）。同じプロセスで UI スレッドと言語サーバーが動いているのに、
推論が全コアを取れば当然そうなる。**先読みが速いことより、入力が引っかからないことが優先。**

スレッド数を振って測ると、prefill と decode で性質が正反対だった（Ryzen 5 3500・6 コア）。

| スレッド | 初回prefill | 打鍵ごと | 内訳 (prefill + decode) |
|---|---|---|---|
| 1 | 4632ms | 973ms | 648 + 325 |
| 2 | 2466ms | 513ms | 339 + 174 |
| 3 | 1771ms | 401ms | 236 + 165 |
| 4 | 1417ms | 348ms | 189 + 159 |
| 6 | 1157ms | 324ms | 158 + **166** |

**prefill は素直に並列化が効き（648→158ms）、decode は 2 スレッドで頭打ち**（174ms 以降ほぼ横ばい）。
llama.cpp は両者を別々に指定できる（`Threads` / `BatchThreads`）ので、分けて割り当てる。

| decode / prefill | 打鍵ごと | 内訳 |
|---|---|---|
| 6 / 6 | 306ms | 145 + 161 |
| **2 / 4**（採用） | **357ms** | 186 + 171 |
| 2 / 3 | 429ms | 235 + 194 |
| 1 / 4 | 513ms | 185 + 328 |

全コア比で 17% 遅いだけで、**時間の長い decode 中は 2 コアしか使わない**。prefill は短いバーストなので
多めに割り当ててよい。既定は decode 2 ／ prefill「コア数 − 2」。

あわせて入れた歯止め:

- **生成の予算 1500ms。** 長引いた提案は出る頃にはキャレットが先へ進んでいてどのみち捨てられる。
  待ち続けるだけ CPU を取られる。ただし prefill は完走させる——長いのは KV が空の初回だけで、
  それは次回以降のための投資。
- **prefill のチャンクを 64 トークンに。** decode は途中で止められないので、この粒度がそのまま
  打鍵によるキャンセルの応答時間になる。
- **前の生成を待つ（捨てない）。** 「動いていたら即あきらめる」にしていたときは、生成 1 回ぶんの間に
  来た要求が全部巻き添えになり、打ち続けるほど何も出なくなっていた。
- **診断ログは専用スレッドへ。** 打鍵ごとに同期でファイルへ書いていて、診断のつもりで入力を
  重くしていた。詰まったら捨てる。

### §31.13.2.2 別プロセスへ出す（2026-09-12）

スレッド数をどう割り振っても、同じプロセスにいる限り打鍵は引っかかった。CPU コアだけの話ではなく、
**メモリ帯域も GC も UI スレッドと分け合う**からで、これは配分では解けない。

`sk0ya.Loomo.Completion.Host` を立て、推論をそこへ出した。

- **優先度 `BelowNormal`。** ここが眼目で、人の入力（Normal）より先読みが優先されることが
  <b>構造的に</b>起こらなくなる。配分の調整と違って、これは破れない。
- **やり取りは標準入出力の 1 行 1 JSON**（`Core/Completion/FimProtocol.cs`）。長さ prefix も枠組みも要らず、
  詰まったら行が届かないだけで、どちらの側も相手を待たない。送るのは組み立て済みプロンプト（数 KB）で、
  本文全体は渡さない。
- **プロンプトの組み立ても候補の選別も本体に残す。** 文脈を知っているのは本体で、ワーカーはモデルを回すだけ。
- **ワーカーは常に最新の 1 件だけ処理する。** 依頼が来たら走っている生成を即座に取り消す。読み取りと生成を
  別スレッドにしてあるのはそのため（生成中に読めないと、取り消しの指示すら届かない）。
- **落ちても本体は何も失わない。** 次の依頼で起動し直し、待っている依頼は「出せなかった」で畳む。
  5 秒で応答が無ければ諦める。3 回続けて起動に失敗したら試すのをやめる。
- **本体と一蓮托生**（Job Object）。クラッシュや強制終了で 500MB のモデルを抱えたプロセスだけが
  残らないようにする。10 分使われなければ自分から畳む。

実測: 打鍵あたり **237ms**（in-process の 259ms と同等。IPC の上乗せは数 KB の JSON ぶんで測定に出ない）。
初回はワーカー起動＋モデル読み込み＋全文 prefill で約 2.1 秒。

JetBrains の Full Line Code Completion も別プロセス（gRPC 越し）で、同じ結論に達したものと見ている。

### §31.13.3 落とす側を厚くする

素の状態で 6 問中 4 問が完全一致。外し方に特徴があり、`ClearDiagnostics(e);` に対して
**`ClearDiagnostics(e.Uri, e.Diagnostics);`** ——形は正しく引数だけが違う。モデルを大きくするより
落とす側を厚くする方が効くという論文の主張が、そのまま実測で確かめられた。

`FimCandidateFilter` が見るのは**文字列として壊れているもの**だけ:
手掛かりの書き直し（`ClearClearDiagnostics` になる）、行の後ろの重複、記号だけの断片、同じ文字・語の暴走、
候補を入れたことで<b>新たに</b>崩れる括弧・引用符の対応（元から開きっぱなしの行は責めない）、行コメント内は数えない。

意味の検査（未解決の参照、引数の数）は言語サーバーの仕事で、ここではやっていない——**残件**。
JetBrains が一番効くと言っているのはそこなので、次に手を入れるならここ。

### §31.13.4 置き場所と設定

- モデルは `%APPDATA%/Loomo/models/**completion/**` の下。チャット用のモデル一覧
  （`ModelCatalogService` が `models/` の直下だけを見る）に混ざると、会話用に選べてしまう。
- 設定は `LoomoSettings.InlineCompletion`（既定は無効）。設定の「エディタ」に入手ボタンがあり、
  押すと取得・パス設定・有効化までやる。
- `FimCompletionEngine` はチャット用 `LlamaCppEngine` とは別の DI シングルトン。同じエンジンを共有すると、
  会話の生成待ちで打鍵が数秒止まる。前の要求が動いている間に来た要求は**待たせずに捨てる**
  （並べると、手を止めた頃に何世代も前の提案が返る）。

## §31.14 C# のフォールバックが打鍵を止めていた（2026-09-12）

§31.12 で打鍵ごとの O(ファイル全体) を潰したあとも「入力が途中で固まる」が残っていた。
犯人は**先読みではなく、C# の Roslyn フォールバックが UI スレッドでソリューション全体を
組み立て直していたこと**。`%APPDATA%/Loomo/jank.log` に名前で残っていた。

```
DISPATCH dur= 958 ms  pri=Background  async CSharpDocumentHighlightService+<FindAsync>d__0
DISPATCH dur=4753 ms  pri=Send        (不明)
```

`dur=` は UI スレッド上での実行時間なので、これがそのまま入力の停止時間である。

### §31.14.1 なぜ UI スレッドに乗っていたか

1. 補完は `_completionDebounce`（**DispatcherTimer**、300ms）から走る。つまり出発点が UI スレッド。
2. `LspViewBridge.RequestCompletionAsync` に `ConfigureAwait(false)` が無いので、LSP が 0 件を返した
   時点の `await provider(...)` も UI スレッド。
3. `CSharpCompletionService.GetAsync` は `async Task` なのに `Task.Run` が無く、**最初の `await` に
   着く前の同期部分**——ソリューション全 `.cs` のディスク読み込み、全件パース、Source Generator 実行、
   `MefHostServices` の合成、980 文書の `AddDocument`——が呼び出し元スレッドで走り切る。

`CSharpDocumentHighlightService.FindAsync` は `Task.Run` が Compilation の組み立てだけを覆っていて、
続く `SymbolFinder.FindReferencesAsync`（ソリューション全体）が UI スレッドへ戻っていた。
semanticTokens／signatureHelp／inlayHint／hover は最初から `Task.Run` で包んであり、
**補完とハイライトだけが漏れていた**。

### §31.14.2 実測（Loomo 自身・980ファイル・8.5MB・Ryzen 5 3500）

| 1 回あたり | 対処 |
|---|---|
| ソース読み 81ms ／ 全件パース **545〜1786ms** ／ `MefHostServices.Create` **132〜648ms** ／ `AddDocument`×980 **522ms**（合計 **1289〜3082ms**） | 下記 3 点 |
| Roslyn の候補 **11,100 件**に `GetChangeAsync` + `GetDescriptionAsync` を全件（**約 22 秒**） | 打ったプレフィックスで絞り、上限 300 件 |

- **Compilation を差分更新する**（`CSharpWorkspaceOperationContext`）。Roslyn の Compilation は不変で
  差分が安いので、`ReplaceSyntaxTree` で変わった木だけ入れ替える——**1 回 4ms**。鍵は `SolutionModel` の
  参照同一性（record なので再評価で別インスタンスになる＝そのまま世代印）＋スコープ＋アクティブパス＋
  CompilationOptions（**値**で比較。毎回新しいインスタンスが返るため参照では永久に外れる）。
  枠は **4 つ**持つ——同じ打鍵で意味色付け（ProjectGraph）・補完（Solution）・コンパイラ診断
  （ProjectGraph＋独自オプション）が**別の鍵で**来るので、1 枠だと三者が互いを追い出し、
  キャッシュがあるのに毎回作り直すという最悪の形になる。
- **`MefHostServices` はプロセスで一度だけ**（`CSharpSemanticWorkspace`）。不変なので共有して差し支えない。
- **文書は `ProjectInfo` へ一括で渡す**。`AdhocWorkspace.AddDocument` は 1 件ごとに Solution を作り直すので、
  980 件を 1 件ずつ足すと 522ms、まとめれば 5ms。
- **候補は絞ってから展開する**。Roslyn の `CompletionService` は「その位置であり得る候補」を全部返す
  （絞り込みは IDE の仕事）。`CompletionList.Span` の本文で前方一致を取ると
  「w」223件・「wo」64件・「workspa」41件まで落ち、展開は 22 秒から 0.2〜1.5 秒になる。
  上限に当たるのはプレフィックス無しの手動呼び出しだけで、そこは 1 画面に出る数をはるかに超えている。

差分更新では **Source Generator を回し直さない**（生成結果は前回のものを引き継ぐ）。無期限に古く
ならないよう、差分更新が 64 回続いたら作り直す。

### §31.14.3 残っている費用

打鍵あたり **1.0〜1.5 秒**（背景スレッド）。UI は止まらず、追い越された要求は取り消される。
内訳の主は `GetCompletionsAsync` 自身の **600〜700ms** で、これは
`CSharpSemanticWorkspace.Create` が毎回**新しい `AdhocWorkspace`** を作るため、Roslyn 側が
文書を読み直して束縛し直していることによる（キャッシュしているのは `CSharpCompilation` で、
Workspace の内部 Compilation は別物）。次の一手はこの Workspace を保持して
文書の差分だけ適用すること。rename／参照検索と共有している型なので、分けて扱う。

### §31.14.4 計測そのものの穴を埋めた

`UiJankProfiler` は ping が 2 秒で返らないと `continue` していた。つまり
**利用者が「固まる」と呼ぶ 2 秒超の停止だけが UIWAIT から漏れていた**（4.7 秒の停止が DISPATCH 行に
しか残っていなかったのはこのため）。上限を 60 秒にして必ず記録する。あわせて
`ManualResetEventSlim` を `using` で捨てていたのを、**Set が済んでから**捨てるようにした
（破棄済みへの `Set()` は UI スレッド上の例外になる）。

GC 回数は「前回記録した時点から」の累計を出していたので、平穏な時間に回った GC まで stall の数字に
混ざっていた（`gen0=171` のような読み手を誤らせる値になる）。その ping の往復ぶんだけを数える。

`(不明)` は Loomo 型でないデリゲート名を捨てていたせい。WPF 内部（TSF/IME、入力、レイアウト）や
他パッケージ由来の停止が切り分け不能になるので、`[外部] <型>.<メソッド>` として残す。

### §31.14.5 検証

Loomo 3240件すべて成功（`CSharpWorkspaceOperationContextCacheTests` 4件を追加——同じ入力なら
同じ Compilation、1 文字の編集なら**その 1 ファイルの構文木だけ**が新しくなる、ファイルの顔ぶれが
変わったら作り直す、スコープ違いが互いを追い出さない）。

## §31.15 エディタの操作は、UI スレッドを最小限しか使わない（2026-09-20）

§31.12／§31.14 は「打鍵ごとに走る計算」を追った。残っていたのは**打鍵ごとに走るディスクと複製**で、
どれも人が何も頼んでいない裏の仕事なのに UI スレッドで払っていた。実測（456KB・7001 行の `.cs`、
エディタタブ 10 枚、Ryzen 5 3500）と対処は次のとおり。

| UI スレッドで払っていたもの | いつ | 前 | 後 |
|---|---|---|---|
| ワークスペースの定期保存（`state.json`＋下書き＋索引の書き出し） | 打鍵・タブ切替のたび（ApplicationIdle） | **9.8ms** | **0.13ms** |
| スナップショット採取の本文 join（`VimEditorControl.Text`） | 同上・開いている**全タブ**ぶん | 0.32ms×10＝**3.2ms**（毎回 LOH） | 未保存タブだけ |
| 軌跡の記録（SQLite の INSERT／UPDATE） | ペイン切替・編集の確定・ファイル移動 | **1.37ms** | **0.003ms** |
| 読み直し判定の改行正規化（`NormalizeEol`×2） | ブランチ切替・一括置換・開き直し（タブ数ぶん） | 0.51ms＋**1.4MB** | 0.46ms＋**割り当て 0** |
| ファイルを開く（`.editorconfig` 探索・読み出し・デコード・行分割） | ファイルを開くたび | 全部 UI スレッド | 背景スレッド |

### §31.15.1 書くのは背景、組み立てはその場

保存を丸ごと非同期にはしない。**いまの状態を読めるのは UI スレッドだけ**だからで、分け方は
「**計画を作るのは呼び出しスレッド、実行するのは書き出し専用スレッド**」にしてある
（`FileWritePlan` / `DeferredWriteQueue`）。計画に載るのは**文字列とパスだけ**——WPF の要素もエディタの
バッファも持ち込まないので、実行がいつ走っても組み立てた時点の内容が書かれる。競合を避けるための
ロックは要らない。

待ち行列は **1 本**。積んだ順＝ディスクへ届く順を保つため（索引と本体が入れ替わると、次の起動が
古い方を読む）。新しい計画が古い計画の対象を**全部**書き直すなら、実行前の古い方は捨てる——速く打つほど
仕事が減る形で、§31.12 の `didChange` と同じ考え方。

**「書いた直後に読み直せる」契約は捨てていない。** `Save`（同期）・`Load`・`DeleteWorkspace` は
自分で `Flush` を通す。ワークスペース切替と終了時は従来どおり同期で書き切る。捨てたのは
「**打鍵のたびに**同期で書く」ことだけ。軌跡も同じ形で、`AppendDeferred` は行への参照
（`TrailRowRef`）をその場で返し、id は書き出しスレッドが埋める。追記と更新が同じ 1 本を順に通るので、
**id が決まる前に更新が走ることはない**。

### §31.15.2 本文の複製をやめる（`C:\Projects\Editor`）

`VimEditorControl.Text` は `string.Join("\n", lines)` で毎回**全文の複製**を作る（456KB で 0.32ms・LOH 行き）。
ホストは同じ版を何度も読む——セッション採取・診断・「ディスクと同じか」・`didChange`——ので、
`TextBuffer.GetText()` を `Version` ごとに 1 回だけ計算するようにした（`IsModified` と同じ形）。
2 回目以降は**フィールドを読むだけ**（0.005ms）。

ホスト側でも、**下書きに書くとき＝未保存か名前無しのタブだけ**本文を読むようにした。保存済みのタブは
ファイルから復元できるので本文が要らない（復元側 `RestoreEditor` も同じ条件でしか使っていない）。
旧 single-editor 形式の鏡を作るために採取をもう一度呼んでいたのも、採取済みのものから引くようにした。

### §31.15.3 ファイルを開く

`LoadFile` が UI スレッドでやっていたのは、`.editorconfig` の親方向への探索（階層ごとの `File.Exists`＋
見つけたファイルの読み込みと解析）、バイト列の読み出し、エンコーディングとバイナリの判定、デコード、
行分割。**どれも UI スレッドを必要としない**ので、`PreparedFileLoad.Prepare(path)` に切り出して背景スレッドへ出し、
UI スレッドは読み終えたものを載せるところだけを払う。

載せる直前に**タブがまだ生きているか確かめる**（`LoadEditorFileAsync`）。待っている間に閉じられた
タブのコントロールは `Dispose` 済みで、そこへ読み込ませると死んだ相手を叩く。開く経路が
`OpenFileInNewEditorTabAsync` / `OpenFileInPreviewTabAsync` / `ReloadExistingTabIfChangedAsync` の 3 本
あるので、後始末は `AfterEditorFileLoaded` 1 本に集めてある（§31 の他と同じく、漏斗を増やさない）。

分割・切り離し・セッション復元は同期の `LoadFile` のまま。await を持てない呼び出し元があるので、
2 つの入口は同じ本体（`LoadFileCore`）を共有する。

### §31.15.4 ついでに直した：テーマの静的プリセットが固まっていなかった

`EditorTheme.Dracula` などの静的プリセットは `SolidColorBrush` を**凍らせずに**持っていた。凍っていない
`Brush` は作ったスレッドのもので、静的初期化子は「最初にそのクラスに触れたスレッド」で走る。
つまり UI スレッドが 2 本ある構成（切り離しウィンドウ、テストホスト）では、片方で作られたプリセットを
もう片方が描こうとした瞬間に「呼び出しスレッドはこのオブジェクトにアクセスできません」で落ちる。
プリセットは共有物なので `Freeze()` する（描画時のロックも減る）。

### §31.15.5 計測でひとつ間違えた

改行非依存の比較を最初は**1 文字ずつ回す**ループで書いた。割り当ては 0 になったが 456KB で **4.97ms**——
作って比べる（0.51ms）より **10 倍遅い**。`string.Equals` も `SequenceEqual` も SIMD で一度に多文字を見るので、
素朴なループはそれに敵わない。行ごとに区切って中身は span の一括比較に任せる形へ直して 0.46ms。
**速さの勝ちは僅かで、効いているのは打鍵中に 1.4MB を LOH へ捨てなくなったこと。**

### §31.15.6 残っている費用

- `:w`（保存）は同期のまま。456KB で約 7ms だが、**人が頼んだ操作**で、失敗をその場で言えることの方が大事。
- `ApplyLspWorkspaceEdit`（リファクタリングの適用）は対象ファイルを UI スレッドで読み書きする。
  取り消し・巻き戻しを持つ取引なので、動かすなら取引ごと動かす必要がある（§32）。
- 打鍵ごとの `ScheduleStyleCopAnalysis` は本文を 1 回読む。§31.15.2 のキャッシュで 2 回目以降は
  ただになったが、**編集のたびの最初の 1 回**（0.32ms）は残る。

### §31.15.7 検証

- Editor: 1714 件成功（`TextBufferTextCacheTests` 16 件＝同じ版なら同じ文字列・あらゆる変更で作り直す、
  `PreparedFileLoadTests` 6 件＝先読みした読み込みが普通の読み込みと同じ状態になる／別ファイル向けの
  先読みは使わない／先読み後に消えたファイルの内容を蘇らせない／開いているバッファの未保存編集を壊さない）。
- Loomo: 3423 件成功（`DeferredWriteQueueTests` 8 件、`WorkspaceStateStoreDeferredSaveTests` 5 件＝
  遅延保存と同期保存が**同じファイルを残す**こと、`EolInsensitiveTextTests`、`EditorTabCaptureTests` 3 件）。

## §31.16 子プロセスの読み取りは、スレッドプールに積まない（2026-09-24）

起動直後、窓もペインも 2 秒前後で出ているのに**ターミナルのプロンプトだけが 5〜9 秒出ない**。
端末（`sk0ya.Terminal.Controls`）は悪くなく、真因は部屋の側の構造だった。

### §31.16.1 何が起きていたか

実測（Ryzen 5 3500・6 コア・`LOOMO_STARTUP_PROFILE=1`）:

| 時点 | 直前 | 直後 |
|---|---|---|
| 初フレーム | 1.9 秒 | 1.9 秒 |
| 端末 View の `Loaded`（シェル起動の合図） | 2.4 秒 | 2.4 秒 |
| **pwsh が実際に生成される** | **5.6〜9.0 秒** | **2.4〜2.9 秒** |

端末は `Loaded` を合図に ConPTY を `Task.Run` で起こす。その `Task.Run` が**行列に 5.2 秒**並んでいた
（`Loaded` の時点でプールの待ち行列は 42 件）。`dotnet-stack` で覗くと、プールのワーカー 6 本が
**全部 `Kernel32.ReadFile` で待機**していた。

理由は `Process.StandardOutput` のパイプが**同期ハンドル**で開かれること。`ReadToEndAsync` も
`ReadLineAsync` も `BeginOutputReadLine` も async-over-sync に落ち、**子プロセスが終わるまで
プールのワーカーを 1 本占有する**。起動直後は C# プロジェクト評価の `dotnet msbuild` が 4 並列
（stdout+stderr で 8 本）、加えて git が何本も走る。コア数ぶんしかないプールは丸ごと埋まり、
自動増員は飢餓検知で毎秒 1〜2 本しか進まない。

つまり**人間のシェルが、背景のインデックス作業に負けていた**。速度の問題ではなく順序の問題で、
`ThreadPool.SetMinThreads` で行列を消すのは対症療法にすぎない（実測では効くが、
「プールに積んではいけないものを積んでいる」ことは変わらない）。

### §31.16.2 あるべき形

先例は既にあった——補完ワーカー（§31.13.2.2）は**別プロセス＋専用スレッド＋`BelowNormal`**で、
人間の入力より構造的に下に置いてある。これを子プロセス全体の作法にする。

- `Loomo.Core/Processes/ChildProcessIo.cs` が唯一の入口。`ReadToEndAsync` / `PumpLinesAsync` /
  `RunOffPoolAsync` はいずれも**名前つきの専用スレッド**（`IsBackground`）で走り、
  `RunContinuationsAsynchronously` で待っていた側の続きをそのスレッドへ引き込まない。
  境目は「**待つだけの仕事はプールの椅子を占めない**」——CPU を回す計算は今までどおり `Task.Run`。
- パイプが消えても失敗にしない。kill や破棄で途中終了したときは**そこまで読めたぶんを返す**
  （打ち切りの作法が「殺して閉じる」である以上それは異常ではなく、見捨てられた読み取りが
  未観測の例外として残るのも避けたい）。
- 打ち切りは**殺してパイプを閉じる**。ブロッキング読みは途中で降りられないので、読み取り側に
  `CancellationToken` を渡す口はわざと無い。各呼び出し側が持っている kill 経路がそのまま効く。
- 背景と分かっている子プロセス（MSBuild 評価）は `TrySetBackgroundPriority` で `BelowNormal`。
  CPU でも人間の道具の下に置く。

移した先：`MsBuildProjectEvaluator`（2 か所）・`GitCommandRunner`・`GitTreeState`・
`WorkspaceSearchService`（rg）・`TerminalService`（AI のコマンド）・`CSharpTestDebugRunner`（2 か所）・
`JsDebugServerProcess`、および `FolderTreeViewModel.Git`（git を何本も起こして待つ `Task.Run` ごと）。
`DapConnection` と `FimCompletionClient` は元から専用スレッドで、変更なし。

ひとつ副作用がある。打ち切りを「読み取りに渡したトークン」の副作用として表現していた箇所
（`GitTreeState.RunGit`）は、読みがトークンを持たなくなった以上**明示的に見る**必要がある
（起こす前と、待ったあとの `ThrowIfCancellationRequested`）。副作用に頼った打ち切りは、
そもそも読めば分かる形ではなかった。

スレッドは数本増えるが、どれも I/O 待ちで CPU を食わない。プールの椅子を占めるのとは意味が違う。

### §31.16.3 気をつける点（レビューで出た三つ）

`BeginOutputReadLine` をやめると `WaitForExitAsync` は**プロセスの終了しか待たない**
（従来は出力の読み切りまで中で待ち、しかもそれは打ち切りトークンで縛られていた）。ここから三つ。

1. **読み切りを無期限に待たない。** 切り離された孫（`npm run dev`、残った node）が継承した stdout
   ハンドルを握っていると、親が終わってもパイプは閉じず EOF が来ない。`TerminalService` は読み切りも
   安全網（`FallbackTimeout`／`ct`）の内側に置き、駄目なら木ごと殺して<b>「完了していない」</b>として返す
   ——途中で降りた実行を、親の終了コードだけ見て成功と呼ばない。回帰ガードは
   `TerminalServiceDetachedPipeTests`（孫にハンドルを継承させて実際に再現する。直す前の
   `await pumps` では 21 秒かかって落ちることを確認済み）。
2. **捨てる前に読み切る。** 読み手が `ReadLine()` の中に居るままプロセスを `Dispose` すると、
   まだ出していない行（テスト実行の締めくくりなど、一番読みたい行）が消える。`CSharpTestDebugRunner`
   は `DisposeAsync` で読み切りを待つ（待ちきれなければ諦める）。
3. **stderr も必ず誰かが読む。** 読まないとパイプが詰まって相手が止まり、stdout も来なくなる
   （rg の警告、git のエラー）。`WorkspaceSearchService`・`GitTreeState` は読み捨てを立てる。
   `check-ignore --stdin` のように<b>入力を食いながら出力する</b>相手では、読み手を立ててから
   stdin を書く（逆にすると自分が `Write` で止まる）。

`ReadToEndAsync` は kill による EOF は普通に返すが、**途中で壊れた読みは失敗のまま返す**。
短い結果を成功として返すと、呼び出し側は終了コードだけを見て出力が揃ったと誤解する
（`git status` の取りこぼしが、そのままツリーの差分表示の欠落になる）。行を流す `PumpLinesAsync`
は「届いた行がすべて」なので、従来どおり静かに終わる。

### §31.16.4 検証

- `ChildProcessIoTests` 5 件：読み切り／行の順序と終端／**プールのスレッドで走らないこと**／
  名前つき専用スレッドで待っている側の続きを横取りしないこと／殺したら読み取りが終端で返ること。
- `TerminalServiceDetachedPipeTests` 1 件：孫がパイプを握ったままでも手番が返ること（§31.16.3-1）。
- 実機：起動 3 回とも待ち行列 0、`Loaded` から pwsh 生成まで 38〜49ms（従来は 3〜6 秒）、
  MSBuild 評価の子プロセスが `BelowNormal` で走っていること、タイトルバーのブランチ表示
  （git 経路が生きている証拠）。
- Loomo: 3496 件成功。

## §31.17 汎用タスクランナー——tasks.json と problem matcher（2026-10-10）

TS IDE ペインの npm スクリプト一覧（§29）はドメイン特化の主導線で、「任意のコマンドを定義し、その出力を
問題へ変換する」仕組みではなかった。これを Problems（§31.5）の**第3の発生源 `Task`** として足した。

### §31.17.1 定義は `.vscode/tasks.json` をそのまま読む

Loomo 独自の書式は**作らない**。既存のリポジトリにはすでに tasks.json があり、二つ目の書式を作れば
「どちらに書くか」「両方あるときどちらが勝つか」を利用者に背負わせるだけで、得るものが無い（書式の
表現力で困った場面がまだ無い）。必要になったら tasks.json の部分集合を広げる方が先。

- 読むのはワークスペースの**全フォルダー**の `.vscode/tasks.json`（マルチルート。各タスクは定義元フォルダーを
  `${workspaceFolder}` として持ち、`dependsOn` は同じフォルダーのタスクを優先して探す）。
- JSONC（コメント・末尾カンマ）を許す。部分集合：`label`・`type`（shell / process / npm）・`command`・`args`・
  `options.cwd`（最上位も）・`windows` 上書き・`group`（文字列 / `{kind,isDefault}`）・`isBackground`・
  `dependsOn`・`detail`・`problemMatcher`（組み込み名 / インライン `pattern` / `base` 付き上書き / 配列。
  `fileLocation`・`severity`・`owner`・複数行パターンと `loop`）・最上位の `inputs`（promptString / pickString、§31.17.6）。
- 組み込み matcher：`$msCompile`・`$tsc`（`$tsc-watch`）・`$eslint-stylish`・`$eslint-compact`・`$gcc`。
  VS Code の定義を下敷きに、行頭の空白（.NET の Terminal Logger の字下げ）を許し、MSBuild の末尾
  `[proj.csproj]` をメッセージから落とした（Build ソースの既存パーサーと同じ扱い＝重複排除が効く）。
- 読めない項目・未対応の指定（`options.env`、未知の type / matcher、壊れた正規表現、重複ラベル）は捨てて
  **警告に理由を積む**。1項目の誤りで全部消えると直す場所が見えない。警告はパレットの末尾に出る。
- `${command:…}` / `${config:…}` は実行を断る。空に展開すると別のコマンドが走るため。`${input:…}` は実行前に訊く（§31.17.6）。
  `${file}` 系はエディタで開いているファイルから展開し、無ければ断る。
- `options.env` は未対応：可視ターミナルのシェルへ `$env:` を残してしまう（後の人間のコマンドに効く）。

### §31.17.2 走らせる場所は人間の可視ターミナル

`ITerminalService.RunCommandInVisibleTerminalAsync`——可視ターミナルの PTY へ打ち込み、OSC 133 の C〜D で
区切られた出力と終了コードを受け取る（TS IDE の型チェックと同じ経路）。裏のプロセス（`ChildProcessIo`）で
走らせて結果だけ Problems に出す案も比べたが、それでは部屋の中で何が起きたかが人間に見えず、Ctrl+C で
止めることも出力を遡ることもできない。ビルドの出力は人間が読むものでもあるので、見える場所で走らせ、
同じ出力を機械も読む。

- 作業フォルダーは `Set-Location -LiteralPath '…'; <コマンド>` で移す。`Push-Location; try{}finally{Pop-Location}`
  で戻すと、プロンプトが見る `$?` が Pop-Location の成功で上書きされ、OSC 133 の終了コードが常に 0 になる
  （失敗が成功に見える）。人間が `cd` したのと同じ結果を受け入れる——どこにいるかは画面に出ている。
- `dependsOn` は依存を先に1つずつ順に流し、失敗したら後続を止める。`dependsOrder: "parallel"` も順に流す
  （可視ターミナルは1本で、同時に打ち込むと前のタスクの標準入力に入る）。同じ理由で同時実行は1つだけ。
- `isBackground`（watch）は終わりを待たず、出力を行ごとに追って1回のコンパイルごとに問題を差し替える（§31.17.5）。
  依存に使うと後続が打ち込めないので、実行前に断る。

**可視ターミナルの折り返し（既知の制約と緩和）。** ターミナルが返す本文は画面の1行ごとで、画面幅で
折り返された1行は2行に割れて届く（Terminal の `ShellCommandOutputEventArgs.Output` の仕様。画面幅も公開
されていない）。MSBuild の診断は絶対パス＋プロジェクトパスでほぼ必ず画面幅を超える。そこで
`ProblemMatcherEngine.RejoinSoftWraps` が、出力の**最長行の長さを画面幅とみなし**（折り返しが起きていれば
前半はちょうど画面幅まで埋まる）、その長さの行を次の行と継ぎ直す。次の行が空白で始まる・それ自体が
matcher に当たる場合は継がない。行末がちょうど空白で割れた行（末尾が削られて幅に届かない）は継げず、
メッセージが途中で切れる——位置は前半の行から取れるのでジャンプは壊れない。根本解決は Terminal 側で
折り返し行を連結した本文を出すこと（パッケージ側の宿題）。スクロールバック上限で頭が落ちた出力
（`HeadLost`）の前半も拾えない。

### §31.17.3 Problems の第3の発生源

`ProblemSource.Task`（フィルター「Task」）。鍵はタスク（フォルダー＋ラベル）で、**同じタスクを走らせ直すと
そのタスクの分だけ丸ごと差し替え**、別のタスクの結果は残す（lint を走らせただけで直前のビルドの問題が
消えない）。0件なら消す＝きれいになった。matcher を持たないタスクは問題に触れない（「0件」と取り違えて
前回の結果を消さない）。ワークスペース切替で全部捨てる。走っている間に切り替えたら、結果は流し込まない。
同じ診断が Build と重なれば Build が正本（優先度 Build→Task→LSP→Compiler）。パスは matcher の
`fileLocation` に従い、基準は `${workspaceFolder}`＝定義元フォルダー・`${cwd}`＝タスクの作業フォルダー。
出力がすでに絶対パスならどのモードでもそのまま使う。`autoDetect` は基準で見つからなければもう一方の
基準も試す。

### §31.17.4 入口と置き場所

- コマンドパレット「タスクを実行…」（既定のビルドタスク→build→test→その他の順。開いているファイルの持ち主
  フォルダーを先に）と「既定のビルドタスクを実行」（`isDefault` の build、無ければ build がちょうど1つのとき
  それ、決められなければ一覧を開く）。既定キーは無し——VS Code の Ctrl+Shift+B は「型定義へ移動」と
  ブラウザのブックマークバーが使っている。tasks.json は実行のたびに読み直す（編集してすぐ試せる方が大事）。
- 純粋部品は `Loomo.Core/Tasks/`：`TasksJsonParser`・`BuiltInProblemMatchers`・`ProblemMatcherEngine`・
  `TaskCommandBuilder`・`TaskPlanner`・`WorkspaceTaskCatalog`・`WorkspaceTaskRunner`。入口と Problems への
  流し込みは `ShellWindow.Tasks.cs`。結果はエディタのステータスバーに1行（成否・エラー/警告件数）。
- 検証：`TaskRunnerTests` 38 件（JSONC・各組み込み matcher のフィクスチャ出力・折り返しの継ぎ直し・パス解決・
  引用・変数・dependsOn・実行の直列化・Problems の差し替え・三点同期）。実機は未確認。

### §31.17.5 watch タスクの問題——出力を行ごとに追い、コンパイル1回ごとに差し替える（2026-10-11）

`isBackground` のタスク（`tsc --watch`・`vite`・`dotnet watch`）は終わらないので、OSC 133 の C〜D で全文を受け取る
従来の経路では問題にできなかった（打ち込むだけだった）。VS Code の背景 matcher と同じく、**begins〜ends の1サイクル
ごとに、そのタスクの問題を丸ごと差し替える**——watch の価値は「直したら次のコンパイルで消える」ことなので、行ごとに
足していくと直した問題がいつまでも残る。

- **出力の受け取り（Terminal 1.0.42 `TerminalTabView.SubscribeOutputLines`）。** 確定した行（カーソルが通り過ぎた行）を
  1行ずつ受け取り、購読後に始まったコマンドの D（終了コード）を1度だけ受け取る。`RunCommandAsync` で送ると終わるまで
  （既定 10 分）直列化の列を握り、型チェックやテストの実行がその後ろに並ぶので、watch は `SendTerminalInput` で
  打ち込んで購読だけする（`ITerminalService.StartWatchedInVisibleTerminal`）。行は**論理行**（画面幅の折り返しを
  Terminal 側で継いだもの）なので、§31.17.2 の「最長行＝画面幅」の推定は使わない。行は Terminal の行の印で追うので、
  watch がスクロールバックの上限を超えて出力し続けても途切れない。D は ConPTY が本文より先に流すので、出力が落ち着く
  （200ms）のを待ってから残りの行と一緒に渡る。
- **区切り（`problemMatcher.background`）。** `beginsPattern` で集め直しを始め、`endsPattern` で出そろった分を当てて
  差し替える（0件なら空＝きれい）。`activeOnStart` も読む。組み込み `$tsc-watch` は「Starting compilation in watch mode…
  ／File change detected…」〜「Found N errors. Watching for file changes.」で区切る（行頭の時刻は地域で書式が変わるので
  見ない）。実物の `tsc --watch --pretty`（5.6）の出力をそのままテストの入力にした。区切りの無い matcher を watch に
  付けた場合は、止めたときに1回だけ当てる。止めた時点で**閉じていないサイクルは捨てる**——途中までの出力で問題を
  減らすと、直っていないものまで消えて見えるため（前のサイクルの結果を残す）。
- **端末の占有。** watch が可視ターミナルを握っている間は、次のタスクを打ち込まない（watch の標準入力に入ってしまう。
  以前はそのまま打ち込んでいた）。終わり（Ctrl+C・タブを閉じた）を受け取ったら解放する。シェル統合が無く終わりを
  検知できない端末のために、パレットに「watch タスク「…」の追跡をやめる」を出す（コマンドは止めない）。
  ワークスペースを切り替えたら追跡をやめる（出力は前のワークスペースのもの）。
- 部品：`BackgroundProblemCollector`（Core、純粋）・`ProblemMatcherBackground`・`WorkspaceTaskRunner.ActiveWatch` /
  `StopWatching`。検証：`TaskRunnerTests` に 6 件（実物の tsc 出力で 2→1→0 件の差し替え・途中で止めたサイクルを捨てる・
  区切り無し matcher・インライン background の読み取りと壊れた正規表現・端末の占有と解放・追跡の解除）、Terminal 側に
  `TerminalOutputLineTapTests` 8 件と実 pwsh を ConPTY 越しに流す `TerminalOutputLineTapRealPwshTests`（折り返された
  150 桁の行が1行で届く・終了コード 4 が D から渡る）。Loomo の画面からの実機確認は未実施。

### §31.17.6 `${input:…}`——実行の直前に人間に訊く（2026-10-11）

「デプロイ先」「テスト名」「マイグレーション名」のように実行のたびに変わる値は、VS Code では tasks.json の最上位
`inputs` に定義して `${input:id}` で参照する。これまでは「空に展開すると別のコマンドが走る」ので実行を断っていた
（§31.17.1）が、そのせいで inputs を使うリポジトリの tasks.json は一部が動かなかった。

- **読む部分集合**：`promptString`（`description`・`default`・`password`）と `pickString`（`options` は文字列か
  `{label, value}`・`default`）。`command` 型は拡張機能のコマンドの戻り値なので読んでおいて、参照したタスクを
  実行するときに理由付きで断る。id の無い項目・未知の type・選択肢の無い pickString は捨てて警告に積む。
  定義は tasks.json ごと——タスクは自分の定義元ファイルの inputs だけを引く（マルチルートで別フォルダーの同名 id を拾わない）。
- **訊くのは1つも打ち込む前。** dependsOn を含めた実行順を決めたら、参照される入力を最初に現れた順に重複なく洗い出し
  （`TaskInputs.Required`）、未定義の id はそこで断る。そのうえで全部訊いてから打ち込む——ビルドが走った後で
  デプロイ側の問いを取り消されると半端に終わるため。取り消したら何も打ち込まない。
- **同じ入力は1回の実行で1回だけ。** build と deploy の両方が `${input:env}` を使っていても1回訊く（VS Code と同じ）。
  別々に訊くと、ビルドとデプロイで違う環境名を答える余地が生まれる。
- **答えの展開。** command・args・`options.cwd` の中で展開する。args の中の答えは他の引数と同じく必要なら引用される
  （`hello world` → `'hello world'`）。shell タスクの command に入った答えは書かれたとおり打ち込まれる（VS Code と同じ）。
- **訊き方。** pickString は**パレット**——タスクを選んだのと同じ場所で、同じキー操作のまま続けて選べる。`default` の
  項目を先頭に置いて「既定」と添える（Enter 1回で既定）。パレットを項目を選ばずに閉じた（Escape・外側クリック・
  一覧の差し替え）ことを知るため、パレットに `_paletteDismissed` を足した。promptString は `InputDialog`
  （空も許す。`password` なら `PasswordBox` で伏せる——値は可視ターミナルに打ち込まれるので、画面からは隠れない）。
- 部品：`TaskInput` / `TaskInputValues` / `TaskInputs`（Core、純粋）・`TaskCommandBuilder.Build(…, inputs)`・
  `WorkspaceTaskRunner.RunAsync(…, askInput)`・`ShellWindow.AskTaskInputAsync`。検証：`TaskRunnerTests` に 6 件
  （inputs の読み取りと警告・既定を先頭へ・依存をまたいだ重複排除と command 型の拒否・command/args/cwd への展開と引用・
  打ち込む前に全部訊く・取り消したら何も打ち込まず次を走らせられる）。全体 4,255 件合格、ビルド警告 0。
  Loomo の画面からの実機確認は未実施。

## §31.18 場所のナビゲーション——戻る／進む・最後の編集箇所・最近のファイル／場所・ファイル構造（2026-10-10）

C# のリファクタリング・コード生成は §33 で Rider 相当まで揃ったのに、**行き来**の操作が無かった。定義へ
飛んだら帰り道が無く（ファイル単位の EditorSupport 履歴だけ）、さっき編集した場所・さっき見ていたファイルへは
タブ一覧から探し直すしかなかった。Rider の Navigate Back/Forward・Last Edit Location・Recent Files・
Recent Locations・File Structure に当たるものを足した。

| コマンド Id | 既定キー | 動き |
|---|---|---|
| `nav.back` / `nav.forward` | Ctrl+Alt+← / → | 前にいた場所（ファイル＋行・列）へ戻る／進む。エディタの上のマウスの戻る／進むボタンも同じ |
| `nav.lastEditLocation` | Ctrl+Shift+Backspace | 最後に編集した場所へ。続けて押すと古い編集箇所へ遡る |
| `nav.recentFiles` | Ctrl+Shift+E | 最近のファイル。開いている最中にもう一度押すと最近の場所へ切り替わる |
| `nav.recentLocations` | （なし） | 最近の場所（行の文面つき） |
| `nav.fileStructure` | Ctrl+F12 | 今のファイルの宣言を宣言順・入れ子の字下げで。キャレットを含む宣言を選んだ状態で開く |

**履歴はキャレットの動きだけから作る**（`EditorLocationHistory`、純ロジック）。定義ジャンプ・問題一覧・
パレット・検索・軌跡・TODO・デバッグ停止……と「飛ぶ」経路は数十あり、経路ごとに「ここで積む」を書けば
必ず漏れる。代わりに形だけで判定する：同じファイルで 10 行未満の移動は今の項目を更新（打鍵・矢印は積まない）、
別ファイルか 10 行以上なら新しい項目を積んで進む側を捨てる。ただし直前に積んだ項目と同じファイルで 800ms
以内の大移動は置き換える——ファイルを開く経路は「タブが前回のキャレットで出る→目的の行へ飛ぶ」の二段で
動くので、置き換えないと通り道が1件余計に残る（ページ送りの連打も1件にまとまる）。
戻る／進むで飛んでいる間の動きは記録しない（`BeginNavigation`〜`EndNavigation`。後者はディスパッチャが
落ち着いてから）。タブの切替と起動時の復元はキャレットを動かさない＝イベントが出ないので、アクティブな
ファイルが変わった時点（`OnActiveEditorFileChanged`）で現在位置を入れる。これが無いと「切り替える前にいた
場所」が履歴に残らず、実機で戻る先が無かった。仮想文書（差分・抜粋タブ）と裏のタブの動きは数えない。
履歴はワークスペースごと（戻るで別の部屋のファイルが開かないように）。編集箇所（`EditLocationTrail`）は
フォーカスのあるエディタで未保存変更があるときの `BufferChanged` だけ数える（読み込み・外部変更・裏のタブへの
WorkspaceEdit を除く）。

**一覧はすべてコマンドパレットに載せる**（新しい窓を作らない）。項目は `PaletteTarget` を持つので、コマンド
モードでも選んでいる間は右の欄に中身が出る（以前は「探して飛ぶ」モードでしか読まなかった）。パレットを
開いている最中は `palette.*` 以外のキーを通さないが、一覧を差し替えるだけのこの3つ（最近のファイル／場所・
ファイル構造）は通す。最近のファイルは `RecentUsageService` の記録を新しい順に、今のファイルを除いて並べる
（1行目が直前のファイル＝開いて Enter だけで行き来できる）。ファイル構造は言語サーバーの documentSymbol
（EditorSupport のアウトラインと同じ取得・8 秒上限）。

**キーの選択。** Rider の既定に揃えたが、Rider の Ctrl+E（最近のファイル）は Vim のスクロールと取り合うので
採らず、Rider では最近の場所に当たる Ctrl+Shift+E を最近のファイルに充て、二度押しで最近の場所へ切り替える
（パレットを開くキーと同じ流儀で、覚えるキーを1本に保つ）。

**ついでに直した：パレットの行の等価性。** `PaletteCommand` は record で、打鍵ごとに書き換わる `TitleMatch` が
値比較のハッシュに入っていた。ファイル構造を絞り込んで Enter したとき、ListBox の選択管理が「同じキーが既に
ある」で例外を出し（未観測 Task → Debug ビルドの `Debug.Assert` で即終了）、参照等価に改めた。

検証：`EditorLocationHistoryTests` ほか 22 件（積む／更新の閾値・二段の動きとページ送りのまとめ・戻る途中の
無視・消えたファイルの読み飛ばし・最近の場所の重複除去・編集箇所の遡り・パレット項目・行の等価性）、
`MouseNavigationPolicyTests`。実機（SendInput の実キー）で、ファイル構造→絞り込み→ジャンプ、別ファイルを
開いてから戻る、最近のファイル→二度押しで最近の場所、最後の編集箇所へ、を確認（2026-10-10）。

### §31.18.1 追補：CamelHumps 一致・テスト⇔実装・構造は名前だけで絞る（2026-10-10）

- **CamelHumps**（`FuzzyMatcher.MatchHumps`）。語の頭（先頭・小文字の後の大文字・区切り文字の後・英字の後の
  数字・`HTMLParser` の `P`）から1文字以上ずつ拾う一致で、`ELH`／`EdLoHi` が `EditorLocationHistory` に当たる。
  以前は飛び石一致と同点で、`E…L…H` をどこかに含むファイルに埋もれていた。尺度は
  前方一致 0 ＞ 先頭の語からの CamelHumps 1 ＞ 部分一致 2 ＞ 途中の語からの CamelHumps 3 ＞ 飛び石 4。
  ファイル名検索（パレットの `/`・検索ペイン）と `PaletteFilter`（コマンド・最近のファイル・ファイル構造）が
  同じ尺度を使う。失敗した (語, クエリ位置) を覚えるので、語の多いパスでも組合せ爆発しない。
- **テスト⇔実装**（`nav.relatedTest`、Ctrl+Alt+T。Rider の Ctrl+Shift+T は表示モードの切替が使っている）。
  命名規約だけで相手の名前を出す（`RelatedTestFiles`：C# `Foo`⇔`FooTests`／`FooTest`／`Foo.Tests`、TS/JS
  `foo`⇔`foo.test`／`foo.spec`、Python `foo`⇔`test_foo`／`foo_test`）。テストランナーの検出結果は「どのテストが
  在るか」は知っていても「何のテストか」は知らないので使わない。全フォルダーから探し、1件なら開き、複数なら
  フォルダーの近い順（`Foo.Tests`→`Foo` のようにテストらしさを落として共通部分を数える）にパレットへ並べる。
- **ファイル構造は名前だけで絞る**（`PaletteCommand.MatchTitleOnly`）。カテゴリが種類名なので、`me` が全メソッドに
  当たっていた。

検証：`CamelHumpsMatchingTests`・`RelatedTestFilesTests` 26 件。実機で Ctrl+P `ELH` の先頭2件が
`EditorLocationHistory.cs`／`…Tests.cs`、Ctrl+Alt+T で実装→テスト→実装を確認。

## §31.19 C# の Quick Fix は言語サーバーを先に訊く（2026-10-10）

「修正候補が出るのが遅い」。Alt+Enter と電球（ガターの💡）だけが遅く、右クリックの Quick Fix は速かった。
原因は**問い合わせの順序**で、Editor の `CollectQuickFixesAsync` は「ホスト提供（`HostCodeActionProvider`）を
先に、0件のときだけ LSP」。Loomo の C# ホストは StyleCop→Roslyn コンパイル（`CSharpCompilerCodeFixService`）→
抑制を**全部計算してから**返すので、言語サーバーが答えを持っている大半のケースでも、ホストの計算
（実測 ~0.3 秒）を待ち、そのうえで LSP（~0.4 秒）を待つ直列になっていた。電球はさらに前に 250ms の落ち着き
待ちが乗る。右クリック側（`QuickFixRequestController`）は最初から「LSP を先に、無ければホスト」だったので、
同じ診断でも入口によって候補の出方が違っていた。

C# の quickfix 要求はホスト提供の入口で `QuickFixRequestController` へ回し、3つの入口（Alt+Enter・電球・右クリック）
を同じ順序に揃えた（`ShellWindow.ViewportSplit.cs` の `HostCodeActionProvider`）。`source.fixAll` は Editor 側が
LSP を先に訊いてから来るので従来どおりホストへ直接。C# 以外は従来どおり（ホストは空を返し、Editor が LSP を訊く）。
LSP も 0 件のときは Editor がもう一度 LSP を訊く（1回ぶん重複する）が、候補が無いときだけなので許容した。

実測（`LOOMO_REFACTOR_DEBUG=1` の `refactor-debug.log`、Loomo 自身・Roslyn LS）：Alt+Enter の候補取得が
**88〜147ms**（`source=lsp`）。言語サーバー起動直後の最初の1回だけはサーバー側の暖機で 7 秒かかる（Loomo の
待ちではない）。実機で「不要な using」に電球が点き、Alt+Enter で3件が出ることを確認。

### §31.19.1 候補が無いときの二重問い合わせと、19 秒の「候補なし」（2026-10-11）

上の順序替えで、候補が0件のときだけ codeAction 要求が2回飛ぶ（Loomo が LSP を訊き、ホストも0件なら
Editor がもう一度 LSP を訊く）重複が残っていた。Editor 1.0.99 に `HostCodeActionProviderQueriesLsp` を足し、
Loomo は「quickfix だけの要求（Alt+Enter・電球）はどの言語でも LSP を先に訊いてからホストへ落ちる」と宣言する。
Editor はこのとき投げ直さず、未接続・解析中・非対応の説明だけ出す。Code Action 全般と Fix all は
Editor が LSP を先に訊いてから来るので従来どおり。Editor の投げ直しが無くなる分、Loomo 側は診断
スナップショットがまだ無い／版がずれているときも版の縛り無しで一度は LSP を訊く（`askLspWithoutSnapshot`。
右クリックは適用がスナップショットに縛られるので従来どおり厳密）。

計測の途中で、本当に遅いのは**候補の無い行**だと分かった。言語サーバーが0件を返すとホストの
`CSharpCompilerCodeFixService` へ落ちるが、ここが範囲に診断が1つも無くてもプロジェクトのコンパイルを組み立て、
`compilation.GetDiagnostics()` で**プロジェクト全体**を意味解析してから自ファイル分だけ残していた。
Loomo 自身で初回 **19.5 秒**、温まっても 0.44〜0.77 秒待ってから「候補なし」。
(1) 範囲にかかる表示中の診断が無ければ組み立てる前に0件で返す（結果は従来と同じ：修正は範囲にかかる
表示中の診断にしか出さない）、(2) 診断は `SemanticModel.GetDiagnostics()`（このファイルだけ・span なしなので
CS8019 も含む）で取り、`using` 追加候補の検証も同じくファイル単位にした。診断の無い行のホスト計算は **2ms**。

検証：Editor 全 1918 件（電球が投げ直さない／宣言なしなら従来どおり LSP へ落ちる、の2件を追加）、
Loomo 全 4233 件（範囲外の早期 return を追加）、nuget.org の 1.0.99 だけでの復元。

## §31.10 非目標

- VS Code/Riderの全機能との同数競争。
- 新しいLSP/DAP実装のLoomo内への複製。
- 一般ブラウザ自動操作ツール、リモート開発、共同編集。
- 複合デバッグ構成、統合ターミナルconsole、テストのDAPデバッグ（必要性は§28.9で別途判断）。
- UI全面刷新。既存の部屋・舞台・袖・軌跡を保ち、操作の連続性を改善する。

VS Code との機能差を個別に「やる／やらない」で棚卸しした台帳は **§19.9**（05）。本章は体感品質だけを扱う。

## §31.11 追跡チェックリスト

- [ ] P0 再現可能な品質ベースライン
- [ ] P1 補完を「出る」から「選べる」へ
- [ ] P2 Problemsを「一覧」から「修正の入口」へ
- [ ] P3 デバッグの主要4旅程
- [ ] P4 LSP状態・再接続・サーバー差異
- [ ] P5 キーボードとフォーカス

各項目を完了にするときは、実測値、テスト件数、公開パッケージ版、実機確認日をその節へ追記する。
