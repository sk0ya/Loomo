# IDE体感品質チェックリスト

対象ロードマップ: §31。リリース間で同じ操作と測定点を再現するための正本。

## 実行記録

| 項目 | 記録値 |
|---|---|
| 日時 | |
| Loomo commit / 構成 | |
| Editor package / commit | |
| Terminal package / commit | |
| OS / .NET SDK | |
| C# server / version | |
| TypeScript server / version | |
| 診断ログ | `%TEMP%\editor-lsp-debug.log` / `%TEMP%\loomo-ide-quality.log` |

詳細ログが必要な実行だけ、Loomo起動前に`SK0YA_EDITOR_IDE_DIAG=1`と
`LOOMO_IDE_QUALITY_DIAG=1`を設定する。通常起動ではログI/Oを
発生させない。測定後は環境変数を解除する。

## 共通準備

1. C#、TypeScript、LSP未構成の空テキスト用に、別々のワークスペースを用意する。
2. 各ワークスペースを開き直し、開始前の対象ファイルSHA-256を記録する。
3. ProblemsのフィルターをError/Warning、Build/LSP、ワークスペース全体へ戻す。
4. ログの時計と画面録画またはストップウォッチの時計を対応付ける。

## C# / TypeScript 共通編集旅程

- [ ] ファイルを開き、LSPの起動開始、initialize完了、解析可能になった時刻を記録する。
- [ ] `.`を入力し、補完要求開始、最初の候補表示、選択、Tab採用の時刻を記録する。
- [ ] 同じ操作をEnterとcommit characterでも行い、確定文字が1回だけ入ることを確認する。
- [ ] 3文字を高速入力し、古い応答へ候補が巻き戻らないことを確認する。
- [ ] snippet候補を採用し、Tabで同番号placeholderと`$0`を移動する。
- [ ] Undo 1回で採用前の本文・キャレット・dirty状態へ戻る。
- [ ] 構文エラーを作り、波線とProblemsに同じ問題が出る。
- [ ] F8/Shift+F8で移動し、Quick Fixまたは手修正後に項目が消える。
- [ ] LSPプロセスを強制終了し、再接続開始/完了時刻とdidOpen再送後の補完・診断復帰を記録する。

## LSPなしワークスペース

- [ ] 未構成が「起動中」「失敗」と区別して表示される。
- [ ] 補完要求がクラッシュせず、導入または設定への次の行動が表示される。
- [ ] 編集、保存、Undo、ProblemsのBuild項目がLSPなしでも成立する。

## デバッグ4旅程

- [ ] 起動→BP停止→Variables/DataTip/Watch→Step Over→Continue→正常終了→再実行。
- [ ] 例外停止→例外概要→先頭Call Stack→再開または停止。
- [ ] 条件付きBP/ログポイント→条件エラーまたはログ→セッション継続。
- [ ] 起動失敗、adapter終了、対象非0終了で理由・終了コード・所要時間・再試行導線を確認する。

各旅程で開始要求、adapter ready、最初の停止、終了時刻、終了分類を記録する。

## キーボード・Automation

- [ ] Editor→Problems→該当箇所→Terminal→Editorをマウスなしで移動する。
- [ ] 舞台切替、デタッチ復帰、設定オーバーレイ後に最後の内部フォーカスへ戻る。
- [ ] Escapeが補完→署名→検索→モーダル→コマンド待ちの内側から閉じる。
- [ ] UI AutomationでEditor CanvasとTerminal surfaceがDocumentとして見え、内部Proxyが列挙されない。

## 終了確認

1. 全操作をUndoし、対象ファイルのSHA-256が開始時と一致することを確認する。
2. ログから要求開始、候補到着、破棄理由、採用、LSP起動/initialize/ready/再接続の時刻を転記する。
3. 前リリースとの差分、回帰、実機固有の例外を記録する。

## 2026-08-02 ローカル統合検証記録

| 項目 | 結果 |
|---|---|
| Loomo / Editor | `44fd1fb`を基点とする作業ツリー / `0d5827f`を基点とする作業ツリー |
| Editor package | `sk0ya.Editor.Controls 1.0.73`（nuget.org公開版） |
| SDK | .NET SDK `10.0.302` |
| 自動テスト | Loomo 1382、Editor Core 1242、Editor Controls 57、すべて成功 |
| Loomo build | 警告0、エラー0 |
| 実機 | Roslyn `5.9.0-1.26303.1`で再試行後に`プロジェクト読込中`から`ready`へ復帰し、didOpen再送・pull diagnostics復帰を診断ログで確認 |
| Automation | Editor Canvas / Terminal surfaceがDocumentとして見え、内部入力Proxyが列挙されないことを確認 |

### LSP最小マトリクス

| server | ローカル状態 | 診断方式 / 補完トリガー / 再接続 |
|---|---|---|
| Roslyn | 実機確認済み | pull diagnostics / capability値 / 強制再試行後にdidOpen再送・診断復帰 |
| typescript-language-server | 未導入 | 未導入表示を確認対象とし、暗黙に別機能へ置換しない |
| pylsp | 未導入 | 未導入表示を確認対象とし、暗黙に別機能へ置換しない |
| marksman `2026-02-08` | 導入済み | capability値を使用。Markdown formatting非対応時もFormatterへ暗黙置換しない |

Editor `1.0.73`の公開はユーザーが実施した。初回検証では同版の古いローカルNuGetキャッシュを参照していたため、
Flat Containerから取得した公開nupkgのSHA-512を確認してキャッシュを更新した。Loomoからの公開、インストール、外部送信は行っていない。未導入2サーバーの実プロセス差異と、
デバッグ4旅程の全手動走行は、該当環境を用意した公開前受け入れで追記する。

### 2026-08-02 20:05 再実測

| 項目 | 実測結果 |
|---|---|
| Loomo | `6049a88`、作業ツリー既存変更なしで開始 |
| 構成 | Windows `10.0.22631` / .NET SDK `10.0.302` / Editor `1.0.73` / Terminal `1.0.30` |
| C# LSP | Roslyn `5.9.0-1.26303.1`。既存C#文書で `LSP: ready`、CanvasがUI AutomationのDocumentとして公開されることを再確認 |
| TypeScript LSP | `typescript-language-server` はグローバル未導入（`npm list -g ...` がempty）。実プロセス旅程は実施不能のため未完了 |
| デバッグadapter | `netcoredbg 3.2.0-1 (9744e1f, Release)` |
| C#デバッグ実測 | `UnitsNetDemo` の既定構成で開始。事前ビルド後に対象WPFアプリが起動し、Loomoは `実行中` へ遷移。停止操作後は `待機中` に戻り、開始ボタンが再度有効化された |
| デバッグ終了表示 | **不一致**: 出力タブには `デバッグ起動: ...MarsThrusterDemo.dll` のみ残り、期待する `ユーザー停止`・終了コード・所要時間のセッション要約を確認できなかった |
| build / test | `dotnet build sk0ya.Loomo.sln --no-restore`: 警告0・エラー0。`dotnet test sk0ya.Loomo.sln --no-build`: 1382件成功 |

今回完遂できたのはデバッグ旅程の「起動・実行中表示・ユーザー停止・即時再実行可能」まで。
ブレークポイント停止を起点とするVariables/DataTip/Watch、例外停止、条件付きBP/ログポイント、
起動失敗・adapter異常終了・対象非0終了の分類は未完了であり、チェックを完了扱いにしない。
また、終了要約が出力へ残らない現象は§31.6の完了条件に対する回帰候補として追跡する。

追跡結果: 手動停止経路がadapterイベントを購読解除した後、`Exited`を通知せず`Idle`へ戻していたことが原因。
C# / TypeScript両debug serviceで、アクティブな手動停止時に`Idle`の後で終了通知を発火するよう修正し、
停止後の再実行可否と終了要約を両立した。回帰テスト追加後のLoomo全テストは1385件成功。

修正版の実機再確認（20:17）では、C#デバッグ開始要求からadapter readyまで74ms、手動停止まで29.383秒。
停止後のステータスに`ユーザー停止 — 同じ構成を再実行できます。`、出力に
`セッション終了（ユーザー停止、実行時間 29.4秒）`が残り、開始ボタンの再有効化も確認した。
診断ログにも`debug.start.requested`、`debug.adapter-ready`、`debug.ended`が同一session IDで記録されたため、
上記「デバッグ終了表示」の不一致は解消済み。

### 2026-08-02 20:23 受け入れ再走（中間記録）

この節は全項目完了前の中間記録である。未実施を成功扱いにせず、実測済みの事実と回帰候補を先に固定する。

| 項目 | 実測結果 |
|---|---|
| Loomo / 構成 | `e062cd0` / Windows `10.0.22631` / .NET SDK `10.0.302` / Editor `1.0.73` / Terminal `1.0.30` |
| 診断設定 | `SK0YA_EDITOR_IDE_DIAG=1`、`LOOMO_IDE_QUALITY_DIAG=1`で起動。ログは`%TEMP%\editor-lsp-debug.log`と`%TEMP%\loomo-ide-quality.log` |
| 専用ワークスペース | `%LOCALAPPDATA%\Temp\LoomoIdeQuality-20260802`配下にC#、TypeScript、LSPなしテキストを分離して作成 |
| 開始時SHA-256 | C#: `348A822809FBFFBB1AB7C3F5264B2DC3F7A966C7D2F7AC98DE456AB2490836E6` / TypeScript: `0E178ED4F4924EDA44AA8867575B2515ADC7F03C071B807ABF825329A4B7CD40` / text: `C5D7052E01CF05DDA48BCA471F7D371577383D97EF4E8C2C6D56339948DF0F4E` |
| 言語サーバー | Roslyn `5.9.0-1.26303.1` / typescript-language-server `5.3.0`を今回導入 / pylsp `1.15.0`を今回導入 |
| ハーネス事前確認 | C# `dotnet build`、TypeScript `tsc --noEmit`とも成功 |

#### 実測済み

- C#文書は`LSP: ready`まで到達した。構文エラー`Console.`を入力すると、波線とProblemsに
  「識別子がありません」「`;`が必要です」の2件が同時に現れ、F8で該当行へ移動できた。
  Undo後はdirty表示が消え、Problemsも「問題はありません」へ戻った。
- Roslynプロセスを20:29:33に強制終了した後、20:29:35にinitializeが成功し、didOpen再送と診断復帰を確認した。
  再接続所要時間は約1.64秒。
- C#の補完は`.`入力とCtrl+Spaceの両方を試したが候補が表示されず、診断ログにも
  `textDocument/completion`要求が記録されなかった。サーバー応答以前に要求が送出されていない回帰候補。
- ProblemsからEditorへのF8移動、および舞台切替キーによるEditor→Terminal→Browserの移動を確認した。
- UI AutomationではEditor Canvasが`Document`として公開された。内部入力Proxyは通常ツリーには列挙されず、
  フォーカス要素としてのみ現れた。Terminal surfaceの`Document`公開は直前の同版実測結果を維持する。
- 通常ブレークポイントを`Program.cs:10`と`:24`へ設定し、Breakpoints一覧への登録と赤丸表示を確認した。
  しかし新規デバッグセッションを2回開始しても停止せず正常終了した。登録済みBPが新規sessionへ反映されていない回帰候補。
- 例外構成では`Program.cs:15`の`InvalidOperationException`で停止した。ステータスの例外概要、
  Locals（`$exception`、`args`、`mode`、`counter`、`message`）、先頭Call Stack、`counter = 9`のDataTipを確認した。
- 通常終了時は出力とセッション要約が残った。前節で修正した手動停止要約も修正版で確認済み。
- 全画面一覧を開いた状態ではEscapeで閉じなかった。期待するフォーカス復帰経路を含めて再確認が必要。
- TypeScript文書を開くところまで実施したが、Loomoプロセスから`typescript-language-server`を解決できず
  20:38:35に起動失敗した。シェル上の`5.3.0`導入確認とは結果が異なるため、起動時PATHの確認から再走する。

#### 未完了（この時点ではチェックしない）

- C# / TypeScriptの補完候補採用（Tab、Enter、commit character）、高速入力時の世代管理、snippet placeholder、採用Undo。
- TypeScriptの診断、Problems移動、再接続。pylspの実プロセス確認。
- LSPなしワークスペースの未構成表示、補完失敗時導線、保存・Undo・Build Problems。
- 通常BP停止後のVariables / DataTip / Watch / Step Over / Continue / 再実行。
- 条件付きBP、ログポイント、条件エラー、起動失敗、adapter異常終了、対象非0終了。
- Problemsを含む全キーボード巡回、デタッチ復帰、設定オーバーレイ復帰、Escape階層の全組合せ。
- 終了時SHA-256一致、診断ログの全時刻転記、全自動テスト再実行。

以上のため、チェックリスト全体はこの時点では**未完了**。回帰候補を修正・再走し、未完了欄がなくなった時点で完了判定する。

#### 20:57–21:03 追試

- 補完要求が出なかった直接要因を再現条件まで切り分けた。Loomo設定でVimキーバインドが無効だと、
  Editor `1.0.73`はplain modeでLSP補完と補完サブモードを意図的に無効化する。Vimを有効化すると、
  C#で未送出だったのと同じ操作経路から`textDocument/completion`が送出された。したがって前記の
  「サーバー応答以前の回帰候補」はVim無効時の仕様差であり、Vim有効条件の実測と分けて扱う。
- 設定オーバーレイはEscapeで閉じた。ただし閉じた直後のUI AutomationフォーカスはEditor内部Proxyではなく
  BrowserのWebView Documentを示した。最後の内部フォーカス復帰は不一致として追跡する。
- npmの標準shim `typescript-language-server(.cmd)`はEditorの`UseShellExecute=false`起動では実行できなかった。
  実プロセス旅程では一時的に`.ts → node.exe <typescript-language-serverのcli.mjs> --stdio`を登録した。
- 最初に導入されたTypeScript `7.0.2`はtypescript-language-server `5.3.0`が有効なTypeScriptとして認識しなかった。
  ハーネスをTypeScript `5.9.2`へ固定すると、21:00:47.514にinitialize開始、21:00:47.681に成功、
  workspace版`5.9.2`の選択、didOpen再送、document readyを確認した。20:59台の`LSP: ready`表示は
  initialize error後にも表示されていたため、UIのready判定がinitialize失敗を成功扱いする別の回帰候補。
- TypeScript `5.9.2`で`person.`を入力すると`age`と`name`が表示され、`age`をTabで採用できた。
  確定文字は重複しなかった。通常モードでUndo 1回により採用前の`person.`、キャレット、dirty状態へ戻り、
  さらにUndoすると試験入力が消えてdirty表示も消えた。
- TypeScriptサーバーを21:00:47.005に強制終了後、21:00:47.681にinitialize成功、didOpen再送・解析復帰した。
  再接続所要時間は約0.68秒。
- TypeScriptで`const broken: string = 42;`を入力すると、21:04:09.626に
  `Type 'number' is not assignable to type 'string'.`がpublishDiagnosticsで到着し、本文の`42`に波線が出た。
  補完ポップアップ表示中のEscapeは1回目でポップアップだけを閉じ、2回目でInsertからNormalへ戻った。
  Undo後は21:05:26.570にdiagnostics空配列が到着し、dirty表示も消えた。Problemsタブの一覧表示とF8移動は
  この追試ではタブ選択が成立せず未確認のままとする。
- Undo後の`journey.ts` SHA-256は開始時と同じ
  `0E178ED4F4924EDA44AA8867575B2515ADC7F03C071B807ABF825329A4B7CD40`に復帰した。

## 2026-08-04 回帰候補2件の原因特定と修正

前節で「回帰候補」として残した2件を切り分け、いずれも実装の欠陥だったので直した。

| 項目 | 内容 |
|---|---|
| 構成 | Windows `10.0.22631` / .NET SDK `10.0.302` / Editor `1.0.76`（今回作成・未公開） / netcoredbg `3.2.0-1` |
| テスト | Loomo 1442件、Editor Core 1263件、Editor Controls 81件、すべて成功。`dotnet build sk0ya.Loomo.sln` 警告0・エラー0 |

### Vim無効時に補完が出ない（仕様差ではなく欠陥だった）

前節では「Vim無効時の仕様差」として分けたが、`VimSettings.Enabled` の**既定値は `false`**（`LoomoSettings.cs`）で、
Vimを有効にしていないユーザーには補完がまったく無い状態だった。原因は `VimEditorControl` 側で、
補完の駆動経路4か所が `_engine.VimEnabled` で塞がれていたこと。plain モードはエンジンが Insert を常駐状態に
するので、`Mode == Insert` の条件だけで両モードを表せる。Vim固有の補完サブモード（Ctrl+X 系）は
`VimEngineRuntime` の plain 入力ハンドラ側で既に除外済みで、この4か所は二重の門になっていた。

- 修正：入力ごとの補完駆動・`Ctrl+Space`・補完ポップアップのキー操作から `VimEnabled` 条件を外した。
  Escape でポップアップを閉じるときの `ProcessKey("Escape")` は Vim有効時だけに限定した
  （plain モードでは抜ける先のモードが無く、本文の選択を消すだけの操作になるため）。
- Editor 側に回帰テスト5件（`PlainModeCompletionTests`）。`VimEnabled` 条件を戻すと失敗することを確認済み。
- 実機（09:20–09:21、Vim無効・専用ワークスペース）：`Program.cs` を開いて `LSP: ready`、`person.` の入力で
  `textDocument/completion` が送出され、Roslynの候補（`Age` `Equals` `GetHashCode` `GetType` `Name` `ToString`）が
  ポップアップ表示。↑↓で移動し Tab で `Name` を採用、確定文字の重複なし。保存しなかったため対象ファイルの
  SHA-256 は開始時のまま。
- **未解消の別件**：採用後もステータスバーに `LSP: completion loading…` が残る（表示状態の後始末漏れ）。

### 登録済みブレークポイントが効かない（C#・条件なしBPが全滅）

原因は `setBreakpoints` の payload。任意項目 `condition` / `hitCondition` / `logMessage` を**明示的な `null`** で
送っており、netcoredbg がこれを
`can't parse: [json.exception.type_error.302] type must be string, but is null` で拒否していた。DAPの応答は
**リクエスト単位**で失敗するので、条件なしブレークポイントが1件でもあるとそのソースの登録が丸ごと通らない。
構成フェーズ（起動時の一括送信）では失敗が握りつぶされていたため、「一覧に出ているのに止まらない」に見えていた。

- 修正：payload 組み立てを `DapBreakpointPayload`（netcoredbg / js-debug 共通）へ一本化し、値の無い任意項目は
  **キーごと省略**する。構成フェーズの `catch` も無言をやめ、失敗理由を出力へ流す。
- 実測（`NetcoredbgDebugService` を直接駆動する使い捨てハーネス、対象は12行のコンソールアプリ）：
  - 修正前 … `setBreakpoints` が `success:false`、対象は停止せず終了コード0で終了。
  - 修正後 … 送信が `{"line":10}` だけになり `success:true`、`reason=breakpoint` `line=10` で停止。
- 回帰テスト5件（`DapBreakpointPayloadTests`）で「null を書かない」ことを固定。

### 設定オーバーレイを閉じた後のフォーカスがブラウザへ行く

20:57–21:03 追試で「Escapeで設定を閉じた直後のUI AutomationフォーカスがEditor内部Proxyではなく
BrowserのWebView Documentを示す」として残していた件。原因は**戻す実装が無かったこと**。設定は独立ウィンドウ
（`SettingsWindow`、Owner=本体）なので、閉じると本体が再アクティブ化され、そのときのフォーカス配置は
WPFの既定復帰にゆだねられていた。ブラウザペインはWebView2で、`Microsoft.Web.WebView2.Wpf` 自身が
「親ウィンドウが非アクティブ化→再アクティブ化されるとキーボードフォーカスを受け取り得る」ケースを
明記しており、それがそのまま表に出ていた。コマンドパレットは閉じるときに `FocusPane` で戻していたが、
設定ウィンドウには同じ処理が無かった。

- 修正：**開く直前の「最後の内部フォーカス」**（ペイン／サイドバー内部に最後にあった要素と、その位置＝
  ペイン＋ビューポート）を控え、閉じたらそこへ戻す。WebView2が非アクティブ化→再アクティブ化の過程で
  非同期にフォーカスを取りにいくため、適用は `DispatcherPriority.Background` へ回して**その後**に行う。
- 戻り先が消えていたときの退避順は 要素 → 同じビューポート → 同じペイン → サイドバー → 何もしない。
  ペインが非表示なら要素が生きていても戻さない（見えない場所へ入力先を移さない）。
- 内部フォーカスの復元は各コントロールが自分で置いた要素へ戻すだけで、Loomo側に内部状態を持たない（§31.2-3）。
- 回帰テスト13件（`FocusReturnPolicyTests`）。判断は純ロジック `FocusReturnPolicy.Decide` へ切り出し、
  要素の生存判定 `FocusReturnElement.ResolveLive` はSTAで実ウィンドウを使って確認する。
- **実機未確認**。`CoreWebView2Controller.MoveFocus` はプロセスを跨ぐため、ブラウザ側の完了通知が
  `Background` 優先度の復帰より後着した場合に取り返される可能性が理屈上残る。下の未完了欄で追跡する。

### 補完の「読み込み中」表示が消えない

採用後もステータスバーに `LSP: completion loading…` が残る件。`TriggerCompletionAsync` は要求の先頭で
この表示を出すが、待機の終わりで消す経路が2つ欠けていた——成功（ポップアップが開いた）ときと、
応答待ちのうちに補完が破棄されたとき（`HideCompletion` が飛行中の要求をキャンセルするため、候補が出る前に
Escapeで閉じると起きる）。どちらも次に誰かがステータスを書くまで固定される。

- 修正（Editor `d99a469`、未公開）：待機が終わったら必ず置き換える。失敗時はその理由、それ以外は空文字。
  あわせて要求へ世代を持たせ、遅れて届いた応答が新しい要求の表示を奪わないようにした。
- 回帰テスト5件（`CompletionStatusTests`）。修正前は3件が失敗することを確認済み。

### 引き続き未完了

- 補完の世代管理、snippet placeholder、commit character の網羅、TypeScript側の同旅程。
- 通常BP停止を起点とする Variables / DataTip / Watch / Step Over / Continue / 再実行の通し。
- 条件付きBP・ログポイント・起動失敗・adapter異常終了・対象非0終了の分類。
- Problemsタブの選択とF8移動。
- 設定オーバーレイを閉じた後のフォーカス復帰の**実機確認**（ブラウザペイン表示時／分割時／サイドバー起点／
  戻り先が消えた場合、およびペイン切替・舞台切替・デタッチ復帰の非退行）。
