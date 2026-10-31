# §36 Rider相当の使い勝手（2026-10-11）

> 状態: 実装済み（Editor 側は sk0ya.Editor.Controls 1.0.100 として公開し、Loomo のピンも更新済み）
> 関連: §31（体感品質）、§33（C#編集）、§19.9（VS Codeとの機能差の台帳）

## §36.1 目的と範囲

§33 で C# の機能数は Rider にかなり近づいた。残る差は**機能の有無ではなく入口**——Rider の手癖のまま
触ったときに「キーが効かない」「Ctrl+クリックで飛ばない」「パッケージを足す画面が無い」「壊したファイルを
時点で戻せない」で止まる箇所である。2026-10-11 にコードと実サーバーで棚卸しした結果を、ここで一度に埋める。
マイナーな機能（データベース、HTTP クライアント、設定同期など）は対象外。

## §36.2 棚卸しの結果（実測）

| # | 差 | 実測・根拠 |
|---|---|---|
| 1 | Vim を切ると、エディタが Ctrl+Z/Y/C/X/V しか効かない素のテキストボックスになる | `VimEngineRuntime` の plain モードは「Any other Ctrl combo is inert」。Ctrl+A／Ctrl+←→／Ctrl+Home/End／Ctrl+Backspace／Ctrl+D／Ctrl+/／Alt+Shift+↑↓ が無反応 |
| 2 | 定義・参照・名前変更の Rider キー（F12／Ctrl+B／Alt+F7／Shift+F6…）が **C# だけ** | `CSharpEditorCommandCatalog` に Rider 既定キーがあるが、`CanExecuteKeyboardCommand` が `.cs` 以外を素通しする。TS／Python 等では `gd`／`gr` と右クリックしか無い |
| 3 | Ctrl+クリックがシンボルの定義へ飛ばない | `EditorCanvas.OnMouseLeftButtonDown` の Ctrl 分岐は URL・パス・documentLink だけ |
| 4 | Roslyn の設定を一切渡していない | `LspProcess.CreateServerRequestResult` が `workspace/configuration` に全項目 null。実サーバーは 158 項目を問い合わせてくる（`csharp\|inlay_hints.…`、`csharp\|completion.dotnet_show_completion_items_from_unimported_namespaces`、`navigation.dotnet_navigate_to_decompiled_sources` 等）。値を返すと、型のインレイヒント・未 using 型の補完（2 回目の要求で `StringBuilder (System.Text)` が出る）が効くことを実サーバーで確認 |
| 5 | BCL／NuGet の定義へ飛んだ先が、編集できる素のタブになる | Roslyn は `%TEMP%\MetadataAsSource\…\Console.cs` の file URI を返す（実測）。開けるが、逆コンパイル結果を書き換え・保存できてしまい、出所も分からない |
| 6 | NuGet パッケージの管理画面が無い | 検索・追加・更新・削除・統合の UI が皆無（コード上ゼロ件） |
| 7 | ローカル履歴が無い | 軌跡（§27）は「どこへ行ったか」で、ファイルの中身の時系列ではない |
| 8 | Live Template／Postfix Template が無い | §33.17 の計画のみ。Editor のスニペットは拡張子別の素朴な展開だけ |

## §36.3 実施計画

### Phase 1 — Editor（`C:\Projects\Editor`）

1. **plain モードの IDE 標準キー**：Ctrl+A、Ctrl+←→（Shift で選択）、Ctrl+Home/End、Ctrl+Backspace/Delete
   （単語削除）、Ctrl+D（行／選択の複製）、Ctrl+/（行コメント切替）、Alt+Shift+↑↓（行の移動）、
   Ctrl+Shift+Z（やり直し）、選択なし Ctrl+X（行の切り取り）、Shift+Enter／Ctrl+Alt+Enter（下／上へ新しい行）、
   Home（行頭の非空白とのトグル）、PageUp/PageDown。Vim 有効時の挙動は一切変えない。
2. **Ctrl+クリックで定義へ**：リンクが無い位置ならキャレットを移して定義へ移動する。
3. **`workspace/configuration` をホストが答えられるようにする**：`LspClient.ConfigurationProvider`
   （section→値）と、設定変更を知らせる `NotifyConfigurationChanged()`（`workspace/didChangeConfiguration`）。
   capability `workspace.configuration` を宣言する。
4. **補完へのホスト追加候補**：`HostSupplementalCompletionProvider`。LSP の結果があっても**常に**合流する
   （既存の `HostCompletionProvider` は「LSP が 0 件のとき」の代替で、意味が違う）。あわせて、補完要求後に
   打ち足した文字を TextEdit の置換範囲へ含める（VS Code と同じ。範囲がキャレット手前で止まっていると、
   打ち足した分が残って二重になる）。

### Phase 2 — Loomo

5. **Roslyn 設定**（`RoslynClientConfiguration`）：インレイヒント（引数名・var の型・ラムダ引数型）を
   `EditorSettings.ShowInlayHints` に連動、未 using 型の補完、逆コンパイル／SourceLink での定義移動を ON。
   設定変更時は `didChangeConfiguration` で即時反映する。
6. **言語共通のナビゲーションキー**：定義／Peek／実装／型定義／宣言／参照／名前変更／整形／Quick Fix を、
   C# 以外でも LSP が付いたエディタにフォーカスがあれば実行する。ただし Ctrl+B（宣言）は Vim 有効時の
   非 C# ファイルでは Vim のページ送りへ素通しする（C# は従来どおり）。
7. **メタデータ（逆コンパイル）タブ**：`MetadataAsSource` 配下のファイルは読み取り専用で開き、状態行で出所を示す。
8. **Live Template／Postfix**（`Loomo.CSharp/Editor/CSharpTemplateCompletion`）：
   - Live：`prop` `propg` `ctor` `cw` `foreach` `for` `forr` `if` `else` `while` `do` `try` `tryf` `using`
     `lock` `switch` `class` `interface` `record` `enum` `svm` `sim` `test`。`ctor` はキャレットを囲む型名を使う。
   - Postfix：`.if` `.not` `.null` `.notnull` `.foreach` `.for` `.forr` `.var` `.return` `.await` `.throw`
     `.par` `.while` `.using` `.cw` `.nameof`。式の範囲は Roslyn 構文木で決める（`.xxx` を外した本文を
     パースし、ドットの直前で終わる最大の式）。
   - どちらも「文を書ける位置」だけで出す（式の途中・文字列・コメント内では出さない）。
9. **ローカル履歴**（`LocalHistoryStore`）：`%APPDATA%/Loomo/history/` にファイルごと gzip で保存。
   記録点は「エディタで開いた時点」「保存」「履歴から戻す直前」。同一内容は積まない。14 日・1 ファイル
   200 版で刈る。書き込みは `DeferredWriteQueue`（UI スレッドで行わない、§31.15）。
   入口はコマンド「ローカル履歴を表示」（差分ペインで版↔現在）と「ローカル履歴から戻す」（1 回の Undo で
   戻る編集として適用）、「最近の変更（ローカル履歴）」（ファイル横断・削除済みファイルの復元）。
10. **NuGet パッケージ管理**（`NuGetPackageService` ＋ `NuGetPackagesWindow`）：導入済み一覧は
    `dotnet list package --format json`（中央管理 CPM も SDK が解決する）、検索とバージョン一覧は
    構成済みの v3 フィード（`dotnet nuget list source`）。追加／更新／削除は `dotnet add|remove package`、
    プロジェクト間でバージョンが違うものは「統合」で揃える。入口はソリューションパネルの右クリックと
    コマンドパレット。`packages.config` の旧形式は対象外と明示する。

### Phase 3 — 検証

- 各ロジック（plain キー、Roslyn 設定値、テンプレート、ローカル履歴の刈り込み・重複排除、NuGet の JSON 解析）
  は単体テスト。Roslyn 設定は実サーバーへの問い合わせ（`workspace/configuration` の section 名）で確認済み。
- Editor の変更は `UseLocalEditor=true` で Loomo から通しで確認する。公開パッケージ化は別途の明示指示で行う。

## §36.4 実装記録（2026-10-11）

### Editor（`C:\Projects\Editor`）

- **plain モードの IDE キー**は `VimEngineRuntime.PlainIdeKeys.cs`（`TryProcessPlainIdeKey`）。Vim 有効時の経路からは
  呼ばれない。本文を変える操作はどれも単独の Undo（`BeginPlainCommand`）——連続入力の塊へ混ぜると、Ctrl+D を
  戻したつもりが直前の入力まで消える。単語の切れ目（`PlainWordNavigation`）は「語・記号・空白」の 3 種。
  コントロール側は Vim 無効時だけ Enter／Backspace／Delete に修飾キーを渡し（`PlainModifiers`）、Ctrl+/ は
  配列に依らず「その配列で '/' を打つキー」で判定する。Alt+Enter（Quick Fix）は Ctrl を伴うときは取らない
  （Ctrl+Alt+Enter＝上に行を作る）。
- **Ctrl+クリック**：`EditorCanvas.SymbolCtrlClicked`。リンク（URL・パス・documentLink）が優先、識別子の上だけ定義へ。
- **`workspace/configuration`**：`ILspClient.ConfigurationProvider`（既定実装は null＝従来どおり）と
  `NotifyConfigurationChanged`。`LspClient.AnswerConfiguration` は item と同数・同順の配列を返し、provider の例外は null。
  capability `workspace.configuration` を宣言（宣言しないと Roslyn は引きに来ない）。
- **補完への追加候補**：`VimEditorControlOptions.HostSupplementalCompletionProvider`。LSP と並行に作り
  （`CompletionMerge.Start`）、ラベル＋種類が同じものだけ重複とみなす（キーワード `foreach` と雛形 `foreach` は両方出す）。
  受け入れ時、置換範囲の終わりがキャレット手前ならキャレットまで伸ばす（`CompletionMerge.ExtendToCaret`）。
  既存の Ctrl+A の回帰テスト（「plain の Ctrl+A は何もしない」）は、「Visual に入らない」という本来の意図を残して
  「全選択になる」へ更新した。
- テスト：`PlainIdeKeyTests`（21）、`CompletionMergeTests`（6）。Editor.Core 1621・Editor.Controls 324 全件合格。

### Loomo

- **Roslyn 設定**：`Loomo.CSharp/LanguageServer/RoslynClientConfiguration`。section の言語接頭辞とグループを外した
  オプション名で引く。インレイヒントは常に有効で返し、表示の ON/OFF は従来どおり設定の「インレイヒント」が決める
  （Editor は LSP が 0 件のときだけホストの引数名ヒントへ落ちるので、二重にはならない）。列挙型のオプションへ
  `"true"` を返すと Roslyn が解析警告を出すので、知らない名前は null。`LspClientPool` の既定接続で全サーバーに付ける
  （Roslyn 以外が問う section はここに無いので従来どおり null）。
- **言語共通キー**：`CSharpEditorCommandDispatchPolicy.IsLanguageNeutral`。Id は互換のため `editor.csharp.*` のまま。
  非 C# ではエディタにフォーカスがあるときだけ実行し、Vim 有効時の Ctrl+B（宣言）は Vim へ譲る。パレットにも出す。
- **逆コンパイルタブ**：`MetadataSourceFiles`。`AfterEditorFileLoaded` で読み取り専用にし、同じエディタへ普通の
  ファイルを読み直したら戻す（他の理由で読み取り専用にしている相手には触らない）。
- **Live／Postfix**：`Loomo.CSharp/Editor/CSharpTemplateCompletion`。Postfix の式は、`.xxx` を外した本文の構文木で
  ドットの直前に終わるノードを上へ辿った最も外側の式。`)` の親は引数リストなので式でないノードも通り抜け、
  代入とラムダは越えない（`n = xs.Length.par` は右辺だけ）。
- **ローカル履歴**：`Services/History/LocalHistoryStore`（＋`LocalHistoryPresentation`）、`ShellWindow.LocalHistory`。
  コマンド `localHistory.show` / `.restore` / `.recent` / `.restoreDeleted`（既定キーなし・パレットから）。
  戻すときは戻す前の本文を `BeforeRestore` として残してから、文書全体を 1 つの編集で置き換える（Undo 1 回で戻る・保存はしない）。
- **NuGet**：`Loomo.CSharp/Packages/NuGetPackageService`、`NuGetPackagesViewModel`、`NuGetPackagesWindow`。
  入口はソリューションパネルの右クリック「NuGet パッケージを管理…」とコマンド `nuget.manage`。`dotnet` は
  `MSBUILDDISABLENODEREUSE=1` で起動し、出力は `ChildProcessIo` の専用スレッドで読む（常駐ノードがパイプを握って
  固まる・プール枯渇の既知の罠）。複数プロジェクトへの操作は Directory.Packages.props／obj を共有するので直列。
- テスト：`RiderUsabilityTests`（36）。

## §36.5 追補：コード整理の対象とプロファイル・テストのデバッグをキーから（2026-10-11）

§36 の後に残った差のうち、毎日の編集ループで先に引っかかる 2 つを埋めた（§33.17 の実施順序 2 を兼ねる）。

- **コード整理のプロファイル**（`CSharpCleanupProfile`）：`Full`（using 整理＋整形＋レイアウト）と `Reformat`（整形と
  行末空白・改行・末尾改行だけ、using には触らない）。コマンドは「コードスタイルを一括適用」（Full）、
  「整形とレイアウトだけ適用」（`editor.csharp.cleanupReformat`）、「変更行だけコードスタイルを適用（Git）」
  （`editor.csharp.cleanupChangedLines`）。右クリックの「まとめて整える」とパレットに出る。
- **対象の絞り込み**（`CSharpCleanupScope`）：選択があれば選択した行だけ、「変更行だけ」は HEAD との差分で追加・変更された
  行だけ。整理そのものは**常にファイル全体**で行い（Roslyn の整形も using 整理もファイル単位でしか正しく判定できない）、
  元の本文との行差分のうち元の行が対象に入るかたまりだけを採る。削除と追加が同数のかたまりは行ごとに判定する
  （まとめると選択外の行まで書き換わる）。触らなかった行の改行コードは元のまま。
  HEAD の本文は `GitHeadText`（`git -C <ファイルのフォルダー> show HEAD:./<名前>`）で**ファイルを含むリポジトリへ直接**訊く
  ——`GitService` の対象リポジトリに依らないので、マルチルートの追加フォルダーでも正しい相手と比べる。
  未追跡・Git 管理外はファイル全体（Rider と同じ）。
- **保存時**：設定に「整形とレイアウトだけ」（`CleanCSharpOnSaveReformatOnly`）と「変更した行だけ（Git）」
  （`CleanCSharpOnSaveChangedLinesOnly`）を足した（どちらも既定 OFF＝従来どおりファイル全体のフル整理）。
- **プロジェクト／ソリューション一括**（`CSharpBulkCleanup`）：Solution Explorer の右クリックと、エディタの「C#」サブメニューの
  Fix All の隣。対象は Fix All と同じ `CSharpFixAllPlanner`（選択中 TFM の Compile 項目）、開いているタブは未保存の本文が正本。
  全ファイルを 1 つの WorkspaceEdit にまとめて既存のトランザクションへ渡す（全件検証→適用・失敗で巻き戻し・Undo 1 回）。
  プレビュー画面は無いので、書き換えるファイル数・行数を確認ダイアログで先に見せる。**意味解析は使わない**——ファイルごとに
  Compilation を作るとソリューション全体では時間に収まらないので、未使用 using の一括削除は Fix All（IDE0005）に任せる。
- **テストのデバッグをキーから**：`editor.test.debugAtCaret`「カーソル行のテストをデバッグ」（既定キーなし・パレット）。
  `ITestExplorer.DebugTestAsync` がテストペインの 🐞 と同じコマンドを通す（dotnet＝testhost を待たせて attach、
  vitest / jest＝js-debug で起動）。テストのデバッグ自体は既にあり、無かったのはキーボードの入口だけ。
- テスト：`CSharpCleanupScopeTests`（12。`GitHeadText` は一時リポジトリで実 git を叩く）。UI からの実機操作は未実施。

## §36.6 非目標

- Rider の全キーマップの再現（Ctrl+E 等、Vim・部屋の操作と衝突するものは採らない。§31.18 の判断を踏襲）。
- 認証付き NuGet フィードの資格情報管理、`packages.config` 形式の管理。
- ローカル履歴のディレクトリ単位の差分表示、ラベル付け（「Put Label」）。

## §36.7 追補：Emmet（2026-10-18）

VS Code・Zed が内蔵している HTML の略記展開（`ul>li.item$*3` → 入れ子のタグ）と、Zed の「選択を略記で囲む」。

- **コマンドとして出す。補完の一覧には載せない**。エディタは一覧が出ている間の打鍵を「絞り込み直し」にし、
  記号（`*` `>` `.`）では引き直さない。一覧に略記の展開を載せると、`ul>li` の時点の候補が `ul>li.item` まで
  打った後も残り、確定すると打ちかけの形に化ける。展開は人が「ここで」と決める操作にする。
- **入口**: パレット「Emmet: 略記を展開」「Emmet: 選択を略記で囲む…」（`editor.emmet.expand` / `editor.emmet.wrap`）と、
  右クリックの1項目（選択があれば「囲む」、無ければキャレットの手前に略記があるときだけ「『ul>li』を展開」）。
  既定キーは与えない——VS Code にも無く、Tab は snippet・Vim と取り合う。キーは設定で割り当てられる。
- **対象**: HTML 系（`.html` `.htm` `.xhtml` `.vue` `.svelte` `.cshtml` `.razor` `.php` `.astro`）と `.jsx`／`.tsx`
  （`class`→`className`・`for`→`htmlFor`・空要素は `<br />`）。それ以外のファイルでは理由を出して何もしない。
- **持っている文法**: `>` `+` `^`・`*N`・`$`（`$$` 桁埋め・`@-` 逆順・`@N` 開始番号）・`#id` `.class` `[属性]` `{本文}`・
  `( )`・親で決まる暗黙のタグ（`ul>.x` は `li`、`table>.r>.c` は `tr`/`td`、インラインの親の中は `span`）・
  よく使う既定属性（`a`→`href`、`img`→`src`/`alt`、`input:checkbox` 等）・`!` の HTML5 雛形。CSS の略記は持たない。
- **素の語は展開しない**（`hello` は文章と区別がつかない）。演算子・記号を含むか、既知のタグ名・コロン付きの名前だけ。
- **囲む**: 選択は一番内側の最後の要素の中身になる。その要素が数の無い `*`（`ul>li*`）なら選択の行ごとに繰り返す。
  行選択の末尾の改行は残す。
- 置き換えは `TryApplyLspTextEdits` の1回（取り消し1回で戻る）。キャレットは最初の空の属性値か空の中身へ。
  字下げは行の字下げ＋文書で最初に見つかった字下げ1段（無ければ 2 桁）。
- 実装: `Loomo.Services/Editor/Emmet.cs`（切り出し・構文解析・出力。純粋）、`Views/Shell/Editor/ShellWindow.Emmet.cs`。
  テスト: `EmmetTests`。

## §36.8 追補：ローカル履歴に名前を付けた版（2026-10-31）

**動機**: Trilium v0.103.0（2026-05-13）の「手で作った版に説明を付ける」と、Rider／IntelliJ の Local History の Put Label。
`docs/reports/feature-scout-2026-10-31.md` の推す 5。ローカル履歴は開いた時点・保存・戻す前の自動の版だけで、14 日・1 ファイル 200 版で一律に消えた——
「大きく書き換える前」の起点を、人が自分で印して残す手が無かった。

- パレット「この時点に名前を付けて残す…」（`localHistory.label`、既定キーなし）。名前を聞き、**いまのエディタの本文**（未保存でも）を
  `LocalHistoryKind.Labeled` の版として残す。直前の版と同じ中身でも積む（名前を付けることが目的）。
- 名前は 1 行・80 文字まで（改行は空白に）。版のファイル（`…_Labeled.gz`）と同じ名前の `.label` に書く——版のファイル名は時刻と種類で読む形のままにする。
- 名前付きの版は**期限でも版数でも刈らず、版数にも数えない**。一覧（「ローカル履歴を表示」「戻す」「最近の変更」）では種類の代わりに「「名前」」で出る。
- 実装: `LocalHistoryStore.RecordLabeled`・`NormalizeLabel`・`LocalHistoryEntry.Label`、`ShellWindow.LabelLocalHistoryVersion`。
  テスト: `RiderUsabilityTests.LocalHistory_LabeledVersionsSurvivePruning_AndKeepTheirName`。
