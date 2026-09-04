# §33 Rider相当のC#編集ロードマップ

> 作成日: 2026-08-30  
> 状態: 実装継続
> 対象: `C:\Projects\Loomo` / `C:\Projects\Editor`  
> 関連する正本: §28 デバッグ、§30 LSP／Formatter、§31 IDE体感品質、§32 リファクタリング

## §33.1 目的

Loomoを「C#ファイルを編集できるLSPエディタ」から、日常のC#開発を閉じられるIDEへ近づける。
対象はRiderの全機能ではなく、次の編集ループを信頼できる状態にすることである。

```text
プロジェクトを開く
  → 正しいTargetFramework／Analyzer／EditorConfigで解析される
  → 補完・診断・ナビゲーションが出る
  → Quick Fix／リファクタリング／コード生成を選ぶ
  → 複数ファイルを安全に適用する
  → Build／Testで確認する
```

Rider相当を「コマンドの数」として追わない。プロジェクト文脈、意味解析、編集トランザクション、
診断表示、キーボード操作が一つの旅程として成立することを完了条件にする。

## §33.2 現在地と判定

| 領域 | 現在地 | 判定 |
|---|---|---|
| 基本テキスト編集 | Vim操作、複数カーソル、分割、検索・置換、Undo | 強い |
| LSP基礎 | 補完、hover、定義、参照、rename、format、diagnostics、code action、階層 | 実装済み。ただし汎用層 |
| C#構文表示 | `Loomo.CSharp`のC#字句／属性／raw・interpolated string表示をAppのEditor registryへ登録 | 部分実装（Roslyn semantic tokenの範囲・字句保護と共通modifier描画は実機比較済み。複雑なsemantic modifier差分は残件） |
| C#プロジェクト理解 | `Loomo.CSharp`で`.sln`／`.slnx`／`.csproj`をMSBuild評価し、参照・TFM・Analyzer・設定をモデル化 | 実装済み。fixture／Loomo実機と24プロジェクトsolution graphで確認 |
| 補完適用 | `textEdit`、`additionalTextEdits`、resolve後のcommandをEditor汎用層で適用 | 実装済み。Editor全テストとRoslyn実機Smokeで確認 |
| Quick Fix | Alt+Enter相当、Problems起点、ファイル／project／solution範囲Fix all | 実装済み。ただしサーバー依存機能の実機網羅は残件 |
| リファクタリング | rename、safe delete、Roslyn code action、独自Change Signature、構文fallbackのextract method／interface／variable／constant／field、inline method／variable、introduce parameter、encapsulate field、move type/file、pull up／push down member | 範囲と実機検証が不足 |
| Code generation | C#専用DLLからコンストラクター／プロパティ／Equals・GetHashCode／ToString／Deconstruct／引数null guard／interface・override／delegating member／Dispose・IAsyncDisposable生成をpreview付きWorkspaceEditで適用し、プロジェクトnullableと.editorconfig命名規則を反映 | 主要機能を実装（意味モデル依存・サーバー固有範囲は残件） |
| Code cleanup | C#専用DLLのcleanup profileでRoslyn C# format、using整理、行末空白・改行・末尾改行をpreview付きWorkspaceEditへまとめ、生成コードを除外 | 安全な構文／レイアウト範囲を実装（未使用判定・完全なfile layoutはRoslynへ委譲） |
| StyleCop | Analyzer導入・設定状態の表示、LSP／Build診断の共通Problems基盤、同一診断の重複排除、LSP欠落時の公式Analyzerフォールバック、severity変更UI | 実装済み。ただしLSP全診断の実機網羅は残件 |
| Test／Build／Debug | Testペインの実行／失敗再実行／公式test adapter再検出、Solution ExplorerのBuild／Test／Run／Debug導線、DAP、solution構成選択、XPlat coverageの行・分岐要約・ファイル詳細・行marker | launch profileはProject／ExecutableとIIS ExpressのRun・DAP attachに対応（IIS Expressの複雑なhost構成は実機確認が残件） |

既存の補完・Problems・LSP状態・キーボードの体感課題は§31、リファクタリングの未消化項目は§32を引き継ぐ。
§32の実機確認（抽出、ファイル作成、複数ファイルUndo）は、このロードマップでも完了扱いにしない。

### 2026-08-30 実装チェックポイント

- `Loomo.CSharp` DLLへ、MSBuild評価、プロジェクト／ソリューションモデル、`.editorconfig`、StyleCop設定の読取、
  C#リファクタリング、テスト検出を集約した。AppはUIアダプター、Coreは共有契約に限定する。
- `Loomo.CSharp`のC# syntax languageとsmart indentをEditorの差し替え可能なregistryへ登録し、raw／interpolated string、
  preprocessor、attribute、波括弧内EnterをC#固有DLLから提供するようにした。
- C# fallback lexerのraw／verbatim stringで行頭に現れる終端と、`@class`のようなescaped identifierを正しく扱い、
  後続コードの色分けを壊さない回帰テストを追加した。
- Solution Explorer、TargetFramework選択、プロジェクト外／解析中／失敗の状態表示、設定ファイル変更時の再評価を実装した。
- Editor汎用層へ補完追加編集、Alt+Enter Quick Fix、`Fix All`、semantic tokens full/delta、document links、CodeLensを接続した。
- `Loomo.CSharp`の`CSharpSemanticTokenVerifier`でRoslyn semantic tokenの範囲外応答と、文字列／コメント／preprocessorを壊す上書きを検出し、
  C# fallback lexerとの分類互換性を確認する。fixtureテストと`LOOMO_RUN_REAL_LSP=1`のRoslyn実機Smokeへ接続した。
- Editor描画側でもsemantic tokenを字句tokenへ重ねる際、文字列／コメント／preprocessorは互換するsemantic種別だけを許可し、
  C# fallbackの保護契約と実描画の優先順位を一致させた。
- CodeLensはEditor側で行番号インデックスを構築し、可視行の描画時に全件走査しないようにした。
- C#専用DLLからフィールド起点のコンストラクター／プロパティ／Equals・GetHashCode、メソッド引数のnull guard、JSONからのC#型生成、
  使用箇所からのローカル／thisメソッド生成を追加し、通常のWorkspaceEdit preview／rollback／Undoへ接続した。
- C#専用DLLに、同じブロックの連続文を対象にしたextract method、選択式のlocal variable導入、定数リテラルのextract constantを追加した。
  スコープ漏れ、部分文選択、条件式、型推論不能な式、名前衝突を事前に拒否し、いずれも通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、単一宣言の`var`ローカル変数を同じブロック内の参照へインライン化する操作を追加した。
  書き換え、入れ子スコープ、複数回評価される副作用式、複数宣言を事前に拒否し、通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、非公開フィールドから読み取り／読み書きプロパティを生成するencapsulate fieldを追加した。
  公開済み・const・同名メンバー・単独行でない宣言を拒否し、既存コードを変更しない追加専用のWorkspaceEditとして適用する。
- C#専用DLLに、リテラル・object creation・cast等の型を構文から推測できる式をreadonly fieldへ抽出する操作を追加した。
  メソッド引数／ローカル変数の捕捉、ラムダ／ローカル関数、型名衝突、曖昧な挿入位置を拒否する。
- C#専用DLLに、単一トップレベル型をusing／名前空間付きの新規`.cs`へ移動する操作を追加した。
  partial型、入れ子型、既存の移動先を拒否し、元ファイルの削除・移動先作成を1つのWorkspaceEditにまとめる。
- C#専用DLLに、単一の非公開メソッドを唯一の呼び出し箇所へインライン化する操作を追加した。
  overload、複数呼び出し、method group／nameof、ref系引数、複数回評価される副作用引数を拒否し、通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、キャレット位置のフィールドが実装する同一ワークスペース内のinterfaceから、メソッド／プロパティ／イベントの委譲メンバーを生成する操作を追加した。
  ジェネリックな委譲先、明示的実装や解決不能な型、既存メンバーは安全側で候補外にし、通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、ワークスペース全体の構文参照を確認してからprivateメンバーを削除するsafe deleteを追加した。
  公開API、トップレベル型、overload、interface契約、参照が残る対象は削除せず、通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、publicインスタンスメンバーからinterfaceを抽出して新規`.cs`を作成し、元クラスへ実装を追加する操作を追加した。
  generic／partial／入れ子クラス、既存移動先、抽出対象のないクラスは拒否し、ファイル作成を含む通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、派生クラスのpublic／protectedメンバーを同一ワークスペース内の直接の基底クラスへpull upする操作を追加した。
  private／static／override、派生側メンバーへの依存、重複定義、partial／generic／sealed型は拒否し、2ファイル変更を一括WorkspaceEditで通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、基底クラスのpublic／protectedメンバーを同一ワークスペース内の一意な直接の派生クラスへpush downする操作を追加した。
  private／static／override／abstract、基底側メンバーへの依存、複数派生、重複定義、partial／generic型は拒否し、2ファイル変更を一括WorkspaceEditで通常のpreview／rollback／Undo経路へ接続した。
- C#専用DLLに、メソッドへ新しいパラメーターを追加し、構文上確実に解決できる同一型／static型呼び出しをワークスペース横断で更新するintroduce parameterを追加した。
  overload、generic、既定値付き／params、method group／nameof、解決不能な呼び出しは拒否し、既定値または呼び出し側の式を明示した場合だけ一括WorkspaceEditで適用する。
- C#専用DLLのコード生成をrecord宣言にも拡張し、プロパティ生成は利用可能にしつつ、recordの自動値等価性へEquals／GetHashCodeを重複生成しないよう拒否する。
- C#専用DLLにトップレベルusingの重複排除・System優先ソートを追加し、コメント／プリプロセッサ付きのusingは安全のため変更しない。右クリックのC#コード生成メニューから既存WorkspaceEdit経路へ接続した。
- C#専用DLLから選択TFMのCompile項目を読み取り、他ファイルのinterfaceの未実装メンバーと基底クラスのabstract／virtual／overrideメンバーも構文で解決して、イベントのstubを含め同じpreview／Undo経路へ接続した。
- `IDisposable`フィールドを持つクラスへ、契約追加を含むDisposeパターンを複数WorkspaceEditとして生成できるようにした。
- Disposeパターンの対象判定を、意味コンパイルが利用できる場合は`System.IDisposable`の型シンボルidentityへ拡張した。
  `MemoryStream`など名前が`IDisposable`でないframework／NuGet型と、別名・継承経由の実装を見落とさず、意味コンパイルが無い場合は既存の安全な構文fallbackを使う。
- null guard生成も選択TFMの共有Compilationを受け取れるようにし、Roslynのparameter symbolで参照型／nullable value typeを判定するようにした。
  ユーザー定義value typeを誤って`ThrowIfNull`へ渡さず、意味モデルが無い場合は既存の構文fallbackへ戻る。
- Extract Classの依存判定も共有Compilationへ対応付け、ローカル変数／引数の同名識別子をクラスメンバー依存と誤認しないようにした。
  field／property／method／event symbolが同一クラスの未選択メンバーを指す場合だけ安全側で拒否し、Appの抽出メニューからも選択TFMのCompilationを渡す。
- StyleCop設定読取は中央管理 `Directory.Packages.props`、`.editorconfig` のSA／Analyzer severity、rulesetのSAルールを対象にし、設定元と値を状態モデルへ保持する。LSPがStyleCop診断を返さない場合は、`Loomo.CSharp` DLL内の `StyleCopDiagnosticService` がMSBuild評価済みの公式 `StyleCop.Analyzers.dll` をRoslyn `CompilationWithAnalyzers`で実行し、未保存本文を含むSA診断をエディタ／Problemsへ反映する（LSPがSAを返したらLSPを正本にする）。
- StyleCopの公式 `StyleCop.Analyzers.CodeFixes.dll` も同じ `Loomo.CSharp` DLLからRoslyn Workspaceへ接続し、個別Code Fixとproject／solution Fix allをWorkspaceEditへ変換する。EditorのAlt+EnterとProblemsのQuick Fixは、このホストCode Action経路へフォールバックする。
- StyleCop設定画面にプロジェクトごとのAnalyzer／stylecop.json／ruleset／severity状態を表示し、明示操作でSAルールのseverityをプロジェクト直下`.editorconfig`へ安全に書き込めるようにした。既存設定の保持、atomic write、不正入力拒否をテストした。
- project／solution Fix allのWorkspaceEdit統合で、完全一致重複に加えて重複範囲も適用前に検出し、部分適用を防ぐようにした。
- C#の右クリックからproject／solution範囲の`source.fixAll`をCompile項目へ問い合わせ、重複WorkspaceEditを統合して既存のpreview／Undo経路へ渡すようにした。
- Solution Explorerのsolution／projectノードからBuild／Testを選べるようにし、選択対象・選択TFMを可視ターミナルで実行してBuild診断をProblemsへ流すようにした。
- Solution Explorerのprojectノードから、同じ選択TFMで実行（可視ターミナル）／デバッグ（DAP起動）も選べるようにした。
- C#テスト検出をRoslyn構文木ベースへ移し、入れ子型・修飾属性・Theory／TestCase／DataRow、skip理由、trait／categoryをCSharp DLLからテストペインへ渡すようにした。
- テストペインに失敗テストの再実行を追加し、Theoryのケース重複を避けたメソッド単位ORフィルターで同じTRX／Problems／ガター経路へ戻すようにした。
- テストペインの明示「公式検出」で`dotnet test --list-tests`を可視ターミナルから実行し、adapter出力をCSharp DLLで解析して既存のソース検出行へ補完するようにした。
- 公式検出が返すTheory／TestCaseのケース名をメソッド行へ集約し、ケース数と実名をツールチップへ表示するようにした。
- `tests/Fixtures/CSharpIde` にxUnit実テストを追加し、`dotnet test`／`--list-tests`をSDKの実出力で確認した。日本語／英語のadapter見出しを同じCSharpパーサで扱う。
- StyleCopはAnalyzerを再実装せず、MSBuild評価と設定状態を表示し、LSP／Build診断を既存のProblemsモデルへ流す基盤と同一診断の重複排除を実装した。
- 選択TargetFrameworkの変更は、C# LSPセッションを再初期化して旧診断・プロジェクトキャッシュを破棄し、開いている文書を再送する。
  LSP標準にないサーバー固有のTFM initialization optionは、対象サーバーの仕様確認後に追加する。
- solutionの`SolutionConfigurationPlatforms`を読み取り、Solution Explorer／C#文脈バーで選択したDebug／Release等の構成をMSBuild評価、
  Build／Test／Run／Debugへ同じ値で渡す。構成変更時はC# LSPセッションも再初期化する。
- プロジェクト読込中にRoslynが返す`ServerCancelled`／`ContentModified`相当の診断pullを、既存診断を消さずに
  短い初回再試行＋段階的バックオフで再取得する。文書版・サーバー世代が変わった応答は適用しない。
- Solution Explorerの通常実行でも、Debugペインで選んだProject／Executableのlaunch profileを`dotnet run --launch-profile`へ渡し、
  未対応形式や別プロジェクトのプロファイルは`--no-launch-profile`へ安全にフォールバックする。
- `LOOMO_RUN_REAL_LSP=1` の実機Smoke Testで、Roslyn Language Serverのsolution自動読込、document symbols、member completion、
  構文エラーのdocument diagnostics pullを確認した。通常のテスト実行では外部サーバーを起動しない。
- Testペインにcoverletの`XPlat Code Coverage`実行を追加し、C#専用DLLのCobertura／OpenCoverパーサーで行数・分岐数要約と
  ファイル／行別の詳細を保持・折りたたみ表示する。レポートが無い場合はcollector未導入などの状態を表示し、Editor汎用のテスト列へmarkerも同期する。
- C#専用DLLに`launchSettings.json`のProject／Executableプロファイル読取を追加し、Debug構成へ引数・環境変数・作業ディレクトリを適用できるようにした。
  `launchBrowser`／`applicationUrl`／`launchUrl`から安全なHTTP(S)実効URLを算出し、dotnet起動後に可視ブラウザペインへ表示する。
  選択したlaunchSettingsプロファイル名もワークスペースのDebugプロファイルへ永続化し、再読込後に再適用する。
  プロファイル切替時の候補再読込と選択復元もテストし、IIS Expressは`iisexpress.exe`の通常Runと起動プロセスへのDAP attachへ変換する。その他の未対応commandNameは実行せず表示だけに留める。
- `tests/Fixtures/CSharpIde` に、ProjectReference、multi-targeting、WPF、linked file、条件付きコンパイル、
  StyleCop、Source Generator、テストプロジェクトを含むPhase 0 fixtureを追加した。通常ビルドは意図したSA1101 errorで失敗し、
  `-p:NoWarn=SA1101`で成功することを実機確認済み。
- 同じfixtureのBuildでは`StyleCop.Analyzers 1.2.0-beta.556`のSA1101をerrorとして検出した。一方、Roslyn 5.9のdocument
  diagnostics pullはこの実機条件ではSA1101を返さなかったため、MSBuildのNuGet依存Analyzerを`project.assets.json`から補完し、LSP欠落時は公式Analyzer DLLを直接実行するフォールバックを追加した。IDE側の未保存本文とBuildのSA1101（error・位置）の一致、`none`抑制、公式CodeFix／project Fix allは実コードテストで確認済み。Roslyn LSPが返す全SAの実機一致は引き続き確認対象とする。
- WorkspaceEditは全対象検証、適用前preview、複数ファイルのsnapshot rollback、Editor上の単一Undo／Redo経路を実装した。
- 実機起動したLoomoでC# Solution Explorerのプロジェクト／TFM／参照／Analyzer表示とC# CodeLens描画を確認し、Editor側に20,000件のCodeLens行インデックス回帰テストを追加した。
- C#専用DLLへ選択式のプロパティ導入を追加し、型プロパティの生成と選択式の置換を一つのWorkspaceEditへまとめた。
  ローカル変数・引数の捕捉、匿名関数、複数行式はコンパイル不能な生成を避けるため拒否する。
- コード生成の不足項目だったfieldを、コンストラクターパラメーター起点のprivate readonlyフィールド＋代入としてC#専用DLLへ追加した。
  ref／out、既存フィールド衝突、単一行本文は安全性のため拒否する。
- C#専用EditAssistを`Loomo.CSharp.dll`へ登録し、コード位置の括弧／引用符自動補完と既存閉じ文字のovertypeを追加した。
  C# lexerで文字列・コメント・プリプロセッサ内を保護し、入力補助がコメントや文字列を壊さないことをテストした。
- C#専用EditAssistへ字句保護付きの構造インデントを追加し、閉じ波括弧直前のbody行、`case`／`default`の暗黙body、`o/O`の行挿入を
  C#の波括弧深度へ合わせた。文字列・コメント・preprocessor内の波括弧は深度へ影響させない。
- Editorの言語非依存EditAssist hookに明示statement completionを追加し、C#専用DLLから`Ctrl+Shift+Enter`を接続した。
  Roslyn構文で式文／ローカル宣言／return等だけを判定し、if・メソッド宣言・既存`;`・保護された文字列／コメントは変更しない。
- Editor汎用層でsemantic tokenのModifiersを保持し、readonly／abstract／documentationを斜体、deprecatedを取り消し線として描画する。
  IME合成によるsemantic範囲の移動にもmodifierを引き継ぎ、C#の修飾子付き実機tokenを捨てないようにした。
- C#専用DLLへcleanup profileを追加し、意味モデルなしで安全なusing整理、行末空白・改行コード・末尾改行の統一を
  一つのpreview対象WorkspaceEditへまとめた。`<auto-generated>` マーカー付き生成コードは既定で除外し、変更行数を表示する。
- cleanup profileからRoslyn C# formatterを呼び出し、`.editorconfig`のindent size／tab設定を適用するようにした。構文エラー時は
  formatterだけをスキップし、行末空白や末尾改行の安全なcleanupは継続する。
- C#専用リファクタリング／コード生成の現在ファイル編集を、適用直前に同じRoslyn formatterへ通す共通アダプターを追加した。
  生成側へindent実装を重複させず、既存の複数ファイルWorkspaceEdit・preview・Undo経路を維持する。
- C#専用コード生成へ、MSBuild評価済みTargetFrameworkのNullable設定と`.editorconfig`の.NET naming ruleを渡す経路を追加した。
  field／property／JSON型／使用箇所からのメソッド生成が命名規則とnullable無効設定を尊重することを単体テストで確認した。
- C#専用DLLに、連続した単純なfield／auto-property／instance methodを新規クラスへ抽出する操作を追加した。
  元クラスには委譲ラッパーを残し、ファイル作成を含むWorkspaceEditとしてpreview／Undo経路へ接続した。意味を確認できない
  public／protected field、非連続選択、外部メンバー依存は安全側で拒否し、generic型は意味モデルで型引数とconstraintを保持する。
  partial型も全宣言を意味モデルで検査し、選択した宣言からの抽出だけを許可する。
- Change Signatureの構文fallbackへ、generic呼び出しの型引数保持、変更後の同一型overload衝突検出、明白なdynamic受け手・
  reflection文字列参照の安全確認を追加した。危険箇所を見つけた場合は宣言だけを変更せず、計画全体を中止する。
- StyleCopのRoslynフォールバック解析と公式CodeFixへ、選択TargetFrameworkのLangVersion／DefineConstants／Nullableを共有する
  コンパイル設定を追加した。Fix allは診断を1件適用するたびに再解析し、古い範囲のずれによる部分修正を避ける。
- C#のproject／solution Fix allは開いているEditorの未保存本文をLSPへ同期してから問い合わせ、StyleCop fallbackの
  複数プロジェクト・linked fileで同じURIへ異なる編集が返る場合は適用を中止する。C#コード生成の構文fallbackも、
  選択プロジェクトから推移的なProjectReferenceのCompile項目を読み、選択TFMのDefineConstants／LangVersionで解析する。
- Fix allをEditor右クリックだけでなくSolution Explorerのproject／solutionノードにも追加し、同じpreview／atomic rollback／Undo経路へ接続した。
  安全削除・pull／push・parameter導入はSolution範囲のソーススナップショットを使い、逆方向のProjectReferenceも参照確認する。
- ソーススナップショットはファイルごとの参照プロジェクトのParseOptionsも保持し、異なるProjectReference先の条件付きコンパイルを
  アクティブプロジェクトの記号で誤解析しないようにした。
- C#専用DLLに、MSBuild評価済みの参照とソーススナップショットから共有`CSharpCompilation`を構築する境界を追加した。
  interface／override／委譲のコード生成は、aliasや別namespaceの同名型を名前検索で混ぜず、意味モデルで解決した宣言だけを候補にする。
  ワークスペース外のBCL／NuGet interfaceについても、ソース宣言が無い場合はsymbolから安全なstubを生成する。
- C# cleanup profileにCompilationのCS8019を接続し、意味モデルを渡した明示的cleanupでは未使用usingも除去する。
  global using、コメント付きusing、生成コードは従来どおり保護し、using整理をformatterより先に行って診断位置を一致させる。
- 使用箇所からのメソッド生成はSemanticModelで引数型を優先推測し、構文fallbackを残した。metadata基底classのoverride判定は
  引数のref kind・型まで比較して、同名・同引数数の別overloadを誤って除外しないようにした。
- コンストラクター生成をCSharp専用DLL内で拡張し、instance auto-property、`required` の
  `SetsRequiredMembers`、意味モデルで一意に解決できる基底コンストラクターの引数転送を生成するようにした。
  複数候補や呼び出し不能な基底コンストラクターは安全側で拒否し、既存フィールドとの重複代入も避ける。
- 使用箇所からのメソッド生成で明示的なgeneric型引数のarityを保持し、同じarityの既存overloadだけを衝突判定するようにした。
  CSharp専用DLLのコード生成メニューへToString生成を追加し、field／未実装auto-propertyの`nameof`付き値表示を生成する。
  record、既存parameterless ToString、対象メンバーなしは自動生成との重複や不完全な出力を避けるため拒否する。
- StyleCopのRoslynフォールバックもSyntaxTree／AdditionalTextごとに対象ファイルの`.editorconfig`を解決し、
  複数プロジェクト・サブディレクトリのseverity、generated_code、Analyzer固有設定をアクティブファイルの設定で上書きしないようにした。
- C#コード生成のinterface解決を継承階層まで拡張し、別Compileファイルの親interfaceメンバーもImplementInterface／委譲生成へ含める。
- C#のテスト検出メタデータに保持していたTrait／Category／TestCategoryをテストペインのタグフィルターへ接続し、
  成功／失敗／未実施・名前フィルターと組み合わせて表示対象を絞り込めるようにした。
  共有テストビューを使うTypeScript側にも同じ表示契約を追加し、言語切替でバインディングが欠けないようにした。
- C#テストエクスプローラーのクラス行へファイル単位実行を追加し、同一ファイル内の複数クラスを
  `FullyQualifiedName=` のORフィルターで安全にまとめて実行できるようにした。TypeScript側は既存のファイル実行へ委譲し、
  共有UIのコマンド契約を一致させた。
- C#テストの単体／クラス／ファイル／プロジェクト／ソリューションのデバッグを、CSharp DLLのtesthost起動アダプター
  （`VSTEST_HOST_DEBUG=1`でPID検出、複数テストアセンブリ対応、process tree後始末）と既存DAP attachへ接続した。
  Test Explorerの葉・グループ、Solution Explorerのproject／solutionから同じ経路を使う。
- 起動プロジェクト候補の`.sln`／`.slnx`／`.csproj`探索とテストプロジェクト判定も
  `Loomo.CSharp.Projects.CSharpProjectDiscovery`へ移し、App側はTypeScriptと共有するUI表示型への写像だけにした。
- C#テストの`dotnet test`／XPlat Code Coverageのコマンド構築、PowerShell引数の引用、TRX／coverage成果物ディレクトリ管理を
  `Loomo.CSharp.Testing.CSharpTestExecutionService`へ移し、App側は出力・Problems・ViewModel反映のアダプターに限定した。
- テスト単体・ファイル・失敗再実行・testhostデバッグで使う`FullyQualifiedName`のORフィルター生成も同サービスへ移し、
  AppのテストViewModelからC#実行条件の組み立てを取り除いた。
- C#／.NETのビルドコマンド生成・実行を`Loomo.CSharp.Build.CSharpBuildService`へ、sln／csproj／bin出力の探索を
  `Loomo.CSharp.Debug.CSharpDebugTargetResolver`へ移し、通常ビルド・IIS Expressビルド・テストデバッグビルドも同じDLL経路に統一した。
- Editor側に既存のLSP inlay hint取得・描画を、Loomoの設定画面／永続化／エディタ適用へ接続した。C# Roslynのparameter name hint等を
  `ShowInlayHints`で有効化でき、設定保存を含む関連テスト25件で確認した。
- 上記移設に対する関連テスト15件とソリューションBuild（警告0／エラー0）を確認した。全体テストは既存の2,763件合格・1件スキップ・失敗0の基準実績に加えた再実行で、
  環境の共有コンパイラー／外部プロセス資源不足によるOOM・ハングが発生したため、全体の新規確定値には算入していない。
- IIS ExpressのlaunchSettings profileをCSharp DLL内のシェル非依存起動仕様へ変換し、Debugでは`iisexpress.exe`を起動して
  既存のnetcoredbg `attach`へ接続する経路を追加した。通常Runの可視ターミナル経路、ブラウザURL、停止／再起動／アプリ終了時のプロセス後始末も同じprofileから扱う。
- Windows Shellを実際に呼ぶサムネイルとごみ箱操作のテストは、プロセス外の共有資源を使うため専用xUnitコレクションへ入れ、
  全体実行時の競合を直列化した。個別実装を除外せず、全体テストでも再検証できる状態にした。
- 残件は、StyleCopを含むRoslyn LSP全診断の実機一致、専用コード生成の対象拡張、solution／project範囲Fix allの実機UI確認、
  テストデバッグの実機DAP接続を含むデバッグUI確認、サーバー固有のsemantic modifier／大規模構文での色差分、
  IIS Expressの複雑なhost構成を含むlaunch profileの完全実機統合である。
- 2026-08-31時点の追加分は、`CSharpCodeGenerationTests` 85件合格、Roslyn実機Smoke 1件合格、Build警告0／エラー0で確認した。
- C#モデルを専用DLLへ移した後のRoslyn実機Smokeでも、inlay hint capabilityと有効な文書範囲での要求を通過させた。
  現行Roslyn headless構成はproviderを広告しても空配列を返すため、返却時のlabel検証までを実施し、hint描画自体はEditor側の既存テストで担保する。
- 同日の全体テストは`.NET SDK 10.0.302`で2,763件合格・1件スキップ・失敗0（合計2,764件）となり、上記の共有資源直列化を含めて完走した。
- C#プロジェクトモデル、`ISolutionModelService`、`IProjectEvaluator`、評価結果契約を`Loomo.Core`から
  `Loomo.CSharp.Projects`へ移し、ServicesのLSP実装も専用DLLを参照する境界へ統一した。CoreにC#型を戻さない境界テストを含め、
  プロジェクト／ソースローダー36件、StyleCop17件、LSPワークスペース28件、Build／Debug／Test連携30件を分割実行して合格した。
- C#テスト検出の`DiscoveredTest`／`ITestDiscoveryService`も`Loomo.Core`から`Loomo.CSharp.Testing`へ移し、
  Coreには.NET／Node.js双方で共有するDAP起動・attach契約だけを残した。
- テスト契約移設後の`FullyQualifiedName~CSharp`フィルターは200件合格、Buildは警告0／エラー0となった。
  全体テストは実Roslynをskipした後もWindows外部プロセス／共有資源で無出力停止したため、全体の新規確定値には算入していない。
- 契約移設後の最終再検証では、C#関連テスト200件、C#専用DLL境界テスト、`LOOMO_RUN_REAL_LSP=1`のRoslyn実機Smoke
  （inlay hint、実装先／型定義／宣言を含むLSP要求）を合格、Buildを警告0／エラー0で確認した。全体テストの新しい確定値は、
  同じ環境で再び無出力停止したため更新していない。
- 実装先／型定義／宣言が複数のローカル候補を返した場合に先頭だけへ移動していたEditor汎用経路を修正し、
  既存の結果ポップアップへ候補を渡すようにした。URIのローカル解決、非ナビゲーション位置の除外、重複排除を共通resolverへ分離し、
  Editor回帰テストを追加した。LSPが1候補だけ返す場合の従来の直接ジャンプは維持する。
- Solution ExplorerのC# Build／Test／RunでApp側に残っていた`dotnet`コマンド構築とPowerShell引用を、
  `Loomo.CSharp.Build.CSharpRunService`および既存のC# Build／Testサービスへ移した。選択configuration、TFM、launch profile、
  `--no-launch-profile`のフォールバックを同じC#実行層で扱い、Runサービスのコマンド／可視ターミナル委譲テスト3件を追加した。
- Test Explorerの通常実行・公式adapter検出・coverageにも選択TargetFrameworkを渡し、C#の実行コマンドがconfigurationだけでなく
  TFMも失わないようにした。CSharp Test実行サービスの既存テストを含む関連8件で確認した。
- 実行対象がCompileファイルではなく`.csproj`自身の場合にも選択TFMを解決できるよう、C# `SolutionModel`へ
  `ProjectForTarget`を追加した。Solution Explorer／Test Explorerのプロジェクト実行で、プロジェクトパスから選択TFMを引き継ぐ。
  プロジェクトモデル・実行サービス関連28件で確認した。
- C#デバッグのBuild／出力DLL探索にも選択TFMを渡し、`bin\Debug`配下の別TFM DLLを誤って起動しないようにした。
  TFM指定時は対象ディレクトリへ探索範囲を限定し、デバッグ関連9件で確認した。
- C#デバッグのAutos候補からC#キーワードを除外する規則を`Loomo.CSharp.Debug.CSharpAutosExtractor`へ移し、
  Coreには言語非依存の候補抽出だけを残した。TypeScript／JavaScriptの除外規則はApp側アダプターへ分離し、
  C#専用DLL境界とAutos回帰を含む16件、C#関連205件で確認した。
- Equals／GetHashCode生成がインスタンスauto-propertyだけの型にも対応し、fieldとpropertyを同じ比較・hash対象へまとめた。
  indexer・static・abstractは従来どおり対象外で、生成回帰86件、C#関連206件、Build警告0／エラー0で確認した。
- C# fallback lexerの属性解析を複数行・入れ子角括弧・属性内文字列へ対応させ、属性終了後の宣言キーワードへ正しく復帰するようにした。
  C# Editor統合テスト28件とソリューションBuild（警告0／エラー0）で確認した。
- Roslyn Language Serverのバージョン・起動引数・インストールコマンドと旧C#サーバー設定の移行判定を
  `Loomo.CSharp.LanguageServer.CSharpLanguageServerCatalog`へ移し、Services側は多言語カタログへの写像に限定した。
  LSP管理／境界テスト24件、C#関連210件、Build警告0／エラー0で確認した。
- ナビゲーション結果をワークスペース相対パス・所属C#プロジェクト・外部ソース付きで表示する変換をAppへ追加し、
  実装先／型定義／宣言の複数候補でも出どころを区別できるようにした。表示変換テスト2件とBuild警告0／エラー0で確認した。
- ナビゲーション候補のホバー時に、対象行を示すマーカー付きの前後ソースをpeek表示するようにした。
  欠損／外部ソースや長大な行もUIを壊さず扱い、周辺ソース抽出テスト4件とBuild警告0／エラー0で確認した。
- 大規模solutionの初期評価を、同時数4の制限付きプロジェクト並列化へ変更した。MSBuildプロセスのCPU／メモリ競合を抑えつつ、
  `Task.WhenAll`の入力順でSolution Explorerの表示順を維持し、プロジェクト単位の失敗状態とキャンセルも従来どおり保持する。
  SolutionModelService関連21件とBuild警告0／エラー0で確認した。
- C#固有の名前変更、整形、Quick Fix、using整理、cleanup、メソッド抽出、主要コード生成を
  `Loomo.CSharp.Editor.CSharpEditorCommandCatalog`の安定Command IDへ集約した。右クリック、コマンドパレット、キーボードは
  App側の同一アダプターを経由し、C#操作の語彙と実行実装を分離する。コンストラクター、フィールド、プロパティ、Equals／GetHashCode、
  ToString、使用箇所からのメソッド、interface、override、委譲、Dispose、null guard、JSON型生成までパレット／キーから呼び出せる。
  C#アセンブリ境界／キーバインド関連34件とBuild警告0／エラー0で確認した。
- 定義・実装・型定義・宣言・参照のナビゲーションをEditor DLLの公開APIへ追加し、CSharp Command IDから既存のLSP位置解決・複数候補表示へ
  接続した。キーイベントを合成せず、Editorの既存ナビゲーション実装をそのまま再利用する。Editor Controls 155件、C#関連211件、Build警告0／エラー0で確認した。
- CSharp DLL内で重複していた起動候補の`.sln`／`.slnx`解析と`.csproj`走査をSolutionModelServiceの共通発見器へ統合し、
  ソリューションとデバッグ対象で同じプロジェクト集合を使うようにした。フォールバック走査の深さも8へ統一し、深いネストと不正solutionからの復帰を含む検出テスト11件で確認した。
- 主要なC#コード生成もCommand IDから呼び出せるようにし、コマンドパレット・キーバインドと右クリックを同じCSharpアダプターへ接続した。
  全体ビルドは警告0／エラー0、C#関連テストは211件合格を維持した。
- C#リファクタリングのextract interface／class、pull／push、introduce、inline、safe delete、encapsulate、move typeも
  同じCommand IDカタログとAppアダプターへ統合し、右クリック・パレット・キーバインドの経路差をなくした。全操作は既存のpreview／
  atomic rollback／Undo経路を維持し、Loomo C#関連211件とEditor全体1,478件を合格させた。
- Change Signatureも`editor.csharp.changeSignature`として同じカタログへ追加し、動的に生成されるリファクタリングメニュー、
  コマンドパレット、キーバインドから実行時の最新キャレットで計画するようにした。既存の入力検証・複数ファイルWorkspaceEdit・Undoを再利用し、
  C#＋キーバインド関連241件とBuild警告0／エラー0で確認した。
- Editorの補完要求中表示が候補採用後・空応答・キャンセル・古い応答の到着後に残らないよう、要求世代を導入して
  成功／失敗／破棄を同じ後始末へ統一した。さらに明示的にポップアップを閉じた時点で世代を無効化し、サーバーの遅延応答を待たず表示を解除する。
  Vim／plain両モードの回帰6件、Editor Controls 156件、C#＋キーバインド関連241件、
  Loomo Build警告0／エラー0で確認した。
- C#テストデバッグで起動したVSTest待機プロセスとattach先のDAPセッションを関連付け、DAP終了・再実行・ViewModel破棄の
  いずれでもtesthostのプロセスツリーとイベント購読を解放するようにした。C#関連217件とソリューションBuild警告0／エラー0で確認した。
- SolutionModelの発見処理を、壊れた／書き込み途中の`.slnx`とワークスペース変更直後に消えたフォルダーからも例外で止めず、
  共通の深さ制限付き`.csproj`走査へ復帰するようにした。SolutionModel関連23件とBuild警告0／エラー0で確認した。
- C#のマルチターゲットプロジェクトで、選択TFMごとの`ProjectReference`をモデルへ保持し、Roslyn構文スナップショットの
  参照グラフとSolution Explorerの参照表示へ適用した。条件付き参照が別TFMへ混入しない回帰を含む関連34件、Build警告0／エラー0で確認した。
- C# MSBuild評価のキャンセル時に標準出力の読み取りだけを止めず、`dotnet msbuild`のプロセスツリーを終了してから再throwするようにした。
  solution構成の読み取りも書き込み中／削除直前はDebug／Releaseへ安全に戻す。テストデバッグのattach直後終了も成功中表示へ遷移させない。
- Roslyn Language Serverが未接続・診断未対応の間も、選択TFMのParseOptions／参照／未保存本文からC# compiler診断を生成し、
  StyleCopやLSPと別発生源として波線・Problemsへ反映するフォールバックを`Loomo.CSharp`へ追加した。Compiler source filter、
  未保存本文・正常系・キャンセル・LSP優先の重複排除を含む関連テストを追加し、Build警告0／エラー0で確認した。
- compiler fallbackにも対象ファイルの`.editorconfig`にある`dotnet_diagnostic.CSxxxx.severity`をRoslynの
  `SpecificDiagnosticOptions`として反映し、severityのerror化とnone抑制を実コードテストで確認した。C#／Problems関連239件が合格した。
- `tests/Fixtures/CSharpIde`で、StyleCopのSA1101を含む通常Buildが失敗し、`-p:NoWarn=SA1101`でsolution Build後に実テストまで完了する
  Build gate／Test旅程を自動テスト化した。fixture関連3件と実Roslyn LSP smoke 1件が合格した。
- solution／project Fix Allの対象Compileファイル列挙、選択TFM、linked fileの異なるWorkspaceEdit検出を
  `Loomo.CSharp.Refactoring.CSharpFixAllPlanner`／`CSharpFixAllEditMerger`へ移し、App側をUI・LSP呼出し・preview／適用のアダプターへ整理した。
  対象範囲・欠損／未読込プロジェクト・同一／競合編集の回帰9件、C#関連242件、Build警告0／エラー0で確認した。
- fixture Build／Test、testhostデバッグ、実Roslyn LSPが同時実行時に外部プロセス資源を競合しないよう、C#外部プロセス用の
  xUnit collectionを追加した。C#関連242件を直列実行して失敗0、実Roslyn smoke 1件も再確認した。
- 同じ `CSharpIde` fixtureで、未保存本文の編集→選択TFMのcompiler診断→公式StyleCop CodeFix→再診断を
  実コードで通すテストを追加した。compilerの未定義識別子とSA1101をそれぞれ検出し、修正後に両方が消えることを確認する。
- 2026-08-31の追加検証は、fixture関連4件、C#／Problems関連243件の合格、Loomo Build警告0／エラー0で確認した。
- コード生成のnullable／命名規則／ParseOptionsをプロジェクト文脈から組み立てる処理を
  `Loomo.CSharp.Refactoring.CSharpGenerationOptionsFactory`へ移し、App側のC#意味判定をUIアダプターから除去した。
  `.editorconfig`のfield命名規則と選択TFMのnullable／言語バージョンを検証する回帰テストを追加した。
- 未保存本文を正本にしたソーススナップショット、選択TFMの参照解決、Roslyn Compilationの生成を
  `Loomo.CSharp.Projects.CSharpWorkspaceOperationContext`へ集約した。cleanup、抽出、safe delete、pull／push、
  parameter導入、null guard、意味依存コード生成のApp実装から重複したRoslyn構築を除去し、構文専用／意味付きの
  生成モードを2件の境界テストで確認した。C#／Problems関連246件、Build警告0／エラー0。
- `.editorconfig`のcleanup項目（indent／改行／using順／末尾改行）を
  `Loomo.CSharp.Refactoring.CSharpCleanupOptionsFactory`へ集約し、Appへ登録済みの共有設定サービスから
  cleanup／生成編集へ同じ設定スナップショットを渡すようにした。C#／Problems関連247件、Build警告0／エラー0。
- 追加後の実Roslyn Language Server smoke（補完、diagnostics、semantic tokens、定義／参照、rename、
  document highlight等）を `LOOMO_RUN_REAL_LSP=1` で再実行し、1件合格した。通常のテストでは外部サーバーを起動しない。
- C# compiler fallback診断も共有Workspace Operation Contextを利用し、選択TFMのCompilationOptions
  （`.editorconfig`のseverityを含む）とassembly名を保ったまま、ソース探索／参照解決／Compilation生成の重複を除去した。
  C#／Problems関連247件、Build警告0／エラー0で再確認した。
- `ProjectReference` の `OutputItemType=Analyzer` をMSBuild評価から拾い、参照先の選択構成 `TargetPath` を
  `Loomo.CSharp` のAnalyzer集合へ追加する経路を実装した。Source Generatorは元DLLをロックしないcollectible
  load contextからRoslynへ渡し、MSBuildの `AdditionalFiles` と共有 `.editorconfig` の
  `AnalyzerConfigOptionsProvider` もGeneratorDriverへ渡して、生成された `FixtureGenerated.g.cs` を
  共有Compilationへ統合するfixture回帰を追加した。StyleCopのAnalyzer設定取得も同じProviderへ統合した。
  C#／Problems関連248件、fixture Build／Test journey、Loomo Build警告0／エラー0、実Roslyn smoke 1件で確認した。
- Roslyn意味モデルからC#呼び出しのoverload、active parameter、XML documentationを
  `Loomo.CSharp.Editor.CSharpSignatureHelpService` で `LspSignatureHelp` へ変換し、LSPが空応答／未接続のときだけ
  Editorの汎用signature popupへfallbackする経路を追加した。C#関連250件、Build警告0／エラー0で確認した。
- Roslyn Operation APIの引数バインディングから、呼び出し／object creationのparameter name inlay hintを
  `Loomo.CSharp.Editor.CSharpParameterNameHintService` で生成し、明示named argumentの重複を避けつつ、LSPが空応答／未接続のときだけ
  Editorの汎用inlay hint描画へfallbackする経路を追加した。入力変更のデバウンス、文書切替・キャンセル時の古い応答破棄、C#関連252件で確認した。
- C# fallback lexerの補間文字列処理を拡張し、通常／verbatim／raw補間文字列の`{式}`部分をC#字句として描画できるようにした。
  `{{`／`}}`のエスケープは文字列として保持し、式内部のidentifier／methodもfallback分類する回帰テストを追加した。
- 定義PeekのLSP応答では`file:`以外のURIも捨てず、既存の参照結果popupへ「外部ソース」として表示できるようにした。
  ローカル定義ジャンプの契約は維持し、外部URIのクリックだけブラウザ経路へ渡す。外部URI表示テストを含めて確認した。
- §33.7のpeek表示として、Editor汎用の`PeekDefinitionAsync`が定義位置を既存の参照結果popupへ渡し、未保存の同一バッファ行を
  ディスク上の内容より優先してプレビューする経路を追加した。C#専用Command ID、右クリック、コマンドパレットから利用でき、
  定義を開く前にプロジェクト／ファイル位置とソース行を確認できる。Build警告0／エラー0で確認した。
- 大規模solutionのRoslyn用ソーススナップショットに、1ファイル上限に加えて全体4,096ファイル／64MiBの上限を設けた。
  上限到達時もアクティブ文書の未保存本文は必ず保持し、実fixtureを`SolutionModelService`で再評価する旅程で5プロジェクト、
  multi-targeting、TFM別ProjectReference、linked file、テストプロジェクト判定、TFM選択の再読込保持を確認した。
- Change Signatureへ共有CSharpCompilationを渡し、LSPのreferencesに現れないmethod group／`nameof`も対象symbol単位で安全確認する経路を追加した。
  同名の別メンバーは意味モデルで除外し、delegate代入などを検出した場合は宣言だけを変更せず計画全体を中止する。
- 実Roslyn Language Serverを一時fixtureへ接続する回帰を追加し、`LspWorkspaceService`のreferencesが解析途中に
  宣言だけ返す状態から呼び出し元を返すまで待って、2ファイルのChange Signature計画へ渡ることを確認した。
- C#専用DLLのWorkspace source snapshotが開いている全C#タブの未保存本文を受け取れるようにし、
  Change Signature／cleanup／抽出／safe delete／pull／push／introduce／コード生成で、別ファイルの古いディスク内容を
  意味解析へ混ぜないようにした。別Compileファイルの未保存本文を優先する回帰を追加した。
- compiler／StyleCop fallback診断にも同じ複数未保存バッファのスナップショットを渡し、アクティブ文書が参照する
  開いている別Compileファイルの本文をRoslyn Compilationへ反映する経路を追加した。242件のC#関連テストで確認した。
- `Loomo.CSharp.Refactoring.CSharpRenameService`を追加し、Roslyn `SymbolFinder`で宣言・参照をsymbol identity単位に
  renameするフォールバックを実装した。Editor汎用RenameはLSP空応答／未接続時にこのホストproviderへ戻り、同名ローカルを
  誤変更しない別Compileファイル回帰を含むC#関連248件のテストで確認した。Editor全体の1,480件も合格した。
- C#専用interface抽出をfixtureコピーで実行し、新規`.cs`作成を含むWorkspaceEditを適用した後、solution Build／Testまで完了する旅程を追加した。
- 同じ5プロジェクトfixtureへRoslyn semantic renameを接続し、`FeatureService`のメソッド宣言から別プロジェクトの
  test callerまでWorkspaceEditを適用してsolution Build／Testを完了する回帰を追加した。
- `CSharpSemanticWorkspace`／`CSharpSemanticSymbolResolver`をC#専用DLL内の共有基盤として追加し、renameと定義／参照検索で
  同じRoslyn Workspace／Compilationを利用するようにした。EditorのLSPが未接続・空応答のときは、Appから`Loomo.CSharp.dll`
  の定義／参照／rename providerへフォールバックし、未保存の複数C#タブも意味解析へ渡す。
- `CSharpIde`の5プロジェクトfixtureで、別プロジェクトの呼び出し位置から`FeatureService`の定義へ移動し、宣言と呼び出し元の
  参照一覧を取得する回帰を追加した。fixtureのBuild／Test journeyと、別プロジェクトの定義／参照解決を確認した。
- 最新検証は`.NET SDK 10.0.302`で実施し、LoomoはBuild警告0／エラー0、C#関連254件合格、Roslyn実機Smoke 2件合格、
  EditorはCore 1,323件＋Controls 158件の計1,481件を合格とした。Loomo全体テストは再実行で2,842合格・2スキップ・失敗0（合計2,844）
  となった。初回全体実行で一過性のUIテストタイムアウトが1件発生したが、単独および再実行では合格した。
- C#専用DLLのRoslynナビゲーションへ実装先・型定義・宣言検索を追加し、interface memberの実装クラス、呼び出し先の型、
  宣言位置をsymbol identityで解決するようにした。LSPのcapability非対応／空応答時はEditorの汎用結果popupへ返すホストproviderへ
  フォールバックし、LSP未接続時もC#コマンドを利用できる。関連C#テスト253件、Editor host fallback 2件、Editor全体1,481件を合格とした。
- C#専用DLLにRoslyn symbolのシグネチャとXML summaryを返す`CSharpHoverService`を追加し、EditorのHover要求へ接続した。
  LSPが未接続・空応答でもC#の`K`／Hover Infoから同じ表示を使える。関連C#テスト253件で確認した。
- Signature Help／parameter name hint／HoverのRoslyn Compilationへ、開いている他C#タブの未保存本文を渡す経路を追加した。
  参照先のシグネチャ・XML summary・引数名がディスク上の古い本文へ戻らないことを回帰テストで確認した。C#関連254件、
  Loomo全体2,842合格・2スキップ、Build警告0／エラー0で確認した。
- C#専用DLLへ`CSharpCompletionService`を追加し、Roslyn Completion APIを第一候補として、Features providerが未ロード／空応答でも
  意味モデルからメンバー・スコープ候補を返すフォールバックを実装した。Roslynの未import型補完で返る`using`追加編集も保持し、
  Editor汎用補完はLSP未接続・空応答時にhost providerへ戻る。Appはsolutionと複数の未保存C#バッファをDLLへ渡すだけの薄い
  アダプターへ整理した。C#関連256件、Editor host fallback 2件、
  Editor全体1,481件を合格とした。
- 最新検証は`.NET SDK 10.0.302`で実施し、LoomoはBuild警告0／エラー0、全体テスト2,844合格・2スキップ・失敗0（合計2,846）、
  EditorはCore 1,323件＋Controls 158件の計1,481件を合格とした。
- `LOOMO_RUN_REAL_LSP=1`でRoslyn Language ServerのCompletion／diagnostics Smoke 2件も合格した。Roslyn Featuresの実行時依存は
  App／テスト出力へ明示配置し、C#専用DLLのMEF Completion providerが実機経路でもロードされることを確認した。
- C#編集の意味処理を`sk0ya.Loomo.CSharp.dll`へ閉じる境界を検証した。Roslyn NuGetは`PrivateAssets=all`でAppの
  コンパイル参照へ伝播させず、AppはC#専用DLLの公開DTO／ファサードだけを使用し、Roslyn／MEFの実行時依存だけを
  CSharp出力から同梱する。`CSharpAssemblyBoundaryTests`でAppのRoslyn AssemblyRefが0件、実行時DLL配置を確認し、
  C#関連257件、Loomo全体2,845合格・2スキップ・失敗0（合計2,847）、Build警告0／エラー0となった。
- C#専用DLLへRoslyn symbol identityによる同一文書の`DocumentHighlight`（read／write／宣言分類）と
  `prepareRename`範囲検証を追加し、LSP未接続・非対応・空応答時のEditor host fallbackへ接続した。タブ切替後の
  古いURIのハイライト適用も破棄し、未保存本文をそのまま意味解析へ渡す。C#関連259件、Editor全体1,323＋159＝1,482件、
  Loomo全体2,847合格・2スキップ・失敗0（合計2,849）、Build警告0／エラー0で確認した。
- C#専用DLLのコード生成へ、インスタンスフィールド／読み取り可能プロパティからの`Deconstruct`生成を追加した。
  record・indexer・staticメンバーを除外し、既存の同一arityも拒否する。Command ID、右クリック、キーバインドまで接続し、
  生成回帰90件、C#関連265件、Loomo全体2,849合格・2スキップ（合計2,851）、Build警告0／エラー0で確認した。
- C#専用DLLのfallback lexerへ、`partial`、accessorの`add/remove`、`allows`、`extension`、
  field-backed propertyの`field`など、現行C#のcontextual keywordを追加した。代表構文のEditor回帰31件で確認した。
- Change Signatureの安全確認条件を見直し、呼び出し側テキストを変更しない引数型／修飾子／戻り値の変更でも、
  delegateのmethod group・dynamic・reflection参照を走査するようにした。型契約変更と呼び出し編集の要否を分離し、
  C#シグネチャ構文回帰23件で確認した。
- C#専用DLLのコード生成で、`.editorconfig`のproperty namingをconstructor／Equalsの重複判定にも適用した。
  `_name`と対応する`name`／`Name`を二重に生成・比較しないことを回帰テストで確認し、C#関連268件を合格とした。
  あわせて、Deconstructの式本体プロパティ／`init` getterを拾い、setter-only propertyを除外し、semantic model上の
  非nullable値型`IDisposable`フィールドにはnull条件演算子ではなく直接`Dispose()`を生成するよう補強した。
- `Equals`／`ToString`生成も読み取り可能なpropertyだけを対象に統一し、setter-only propertyを読み取るため
  コンパイル不能になる生成結果を防止した。CSharpコード生成テスト94件、fixture上のDeconstruct生成→Build→Test、
  Editor全テスト1,482件が合格した。
- interface実装生成は、static／privateメンバーに加えてdefault interface member（本体付きmethod／property／event）も
  実装対象から除外し、宣言のみの契約だけを生成するよう補強した。`.editorconfig`のproperty namingを`ToString`の
  field／property重複判定にも適用し、CSharpコード生成テスト97件で確認した。
- Safe DeleteにCSharpのsemantic compilation経路を追加し、Roslyn symbol identityで対象メンバーへの参照だけを判定するようにした。
  別クラスの同名メンバーを誤って参照扱いにせず、対象symbol自身の参照は安全側で拒否する回帰を追加した。
  あわせてfixture上のDeconstruct生成結果を実ファイルへ適用し、Build／Testまで通す統合テストを追加した。
- メソッド抽出を`CSharpSemanticOperations`へ接続し、`var`の型推定、外側blockのローカル、shadowing、
  書き換え対象の`ref`引数化、staticメソッドからのinstance member参照を意味モデルで検証するようにした。
  async／iteratorは意味を壊す編集を避けるため明示的に対象外とし、fixture上の抽出→Build→Testを確認した。
- Pull Up／Push DownにもRoslyn symbol identityを接続し、同名ローカルをメンバー依存と誤認しないようにした。
  Push Downは継承可能なprotected／public基底メンバーを許可し、private依存だけを拒否する。パラメーター導入も
  overloadごとの呼び出しを意味モデルで限定し、interface実装メソッドの契約破壊を拒否するようにした。
- Inline Methodを`CSharpSemanticOperations`へ接続し、呼び出し側または宣言側の選択からRoslynが解決した
  overloadだけを対象にするようにした。別overloadの呼び出しを残す回帰テストを追加し、AppからCSharp専用DLLの
  semantic経路へ入ることを確認した。
- Inline Variableも`CSharpSemanticOperations`へ接続し、選択した宣言または使用箇所からRoslynの`ILocalSymbol`
  を解決して、ネストしたblockの参照を正しく置換するようにした。同名のshadowing local、initializer自身の参照、
  lambda／local function内の参照、書き換え対象をsymbol identityと安全条件で除外する回帰を追加した。
- Extract Fieldを`CSharpSemanticOperations`へ接続し、Roslynの式型を使ってbinary expressionなど構文fallbackで
  型を推測できなかった式にも対応した。ローカル／引数の捕捉、staticメソッドからのinstance member参照、`this`を
  フィールド初期化子へ移すケースを拒否し、生成結果がコンパイル不能になる経路を回帰テストで塞いだ。
- Introduce Propertyもsemantic facadeへ接続し、式内のlocal／parameter／member symbolを識別するようにした。
  staticメソッドのstatic expressionはstatic propertyとして生成し、instance参照を誤って持ち込まない。
- Introduce Variableも`CSharpSemanticOperations`へ接続し、Roslynの式型を確認してvoid式や型を解決できない式を
  `var`宣言へ誤変換しないようにした。Appからの実行も未保存本文を含むsemantic workspace snapshot経由に統一した。
- Extract Constantもsemantic facadeへ接続し、Roslynのconstant valueと型を検証して、リテラル以外でも`1 + 2`や
  その他のコンパイル時定数を安全に抽出できるようにした。非const式は拒否し、既存の構文fallbackの制約とエラー表示を維持する。
- Encapsulate Fieldもsemantic facadeへ接続し、選択した`IFieldSymbol`の型、static／readonly属性、所属型のメンバー衝突を
  Roslynで検証するようにした。generic型を含むフィールドのプロパティ生成を回帰テストで確認し、Appの実行経路も統一した。
- 直近の全体テストは2,856合格・2スキップ・失敗0（合計2,858）、`sk0ya.Loomo.sln`のBuildは警告0／エラー0となった。
- 追加実装後の再検証でも全体テストは2,858合格・2スキップ・失敗0（合計2,860）を維持した。
- 最新の全体テストは2,862合格・2スキップ・失敗0（合計2,864）、C#関連274件、`sk0ya.Loomo.sln`のBuildは警告0／エラー0、
  Editor全テストは1,482件合格となった。
- 最新検証はLoomo全体2,871合格・2スキップ・失敗0（合計2,873）、C#関連283件、コード生成／リファクタリング回帰105件、
  `sk0ya.Loomo.sln`のBuild警告0／エラー0で確認した。fixtureのsemantic抽出・パラメーター導入、継承移動の単体検証も合格した。
- Inline Method追加後の最新検証はLoomo全体2,872合格・2スキップ・失敗0（合計2,874）、C#関連284件、
  コード生成／リファクタリング回帰106件、`sk0ya.Loomo.sln`のBuild警告0／エラー0。Editor全テストは1,482件合格を維持した。
- Inline Variableのsemantic経路とnested／shadowing回帰追加後、最新の全体テストは2,874合格・2スキップ・失敗0
  （合計2,876）、C#関連286件、コード生成／リファクタリング回帰108件、`sk0ya.Loomo.sln`のBuild警告0／エラー0となった。
- Extract Field／Introduce Propertyのsemantic経路追加後、最新の全体テストは2,878合格・2スキップ・失敗0
  （合計2,880）、C#関連290件、コード生成／リファクタリング回帰112件、`sk0ya.Loomo.sln`のBuild警告0／エラー0となった。
- Introduce Variableのsemantic経路追加後、最新の全体テストは2,879合格・2スキップ・失敗0
  （合計2,881）、C#関連291件、コード生成／リファクタリング回帰113件、`sk0ya.Loomo.sln`のBuild警告0／エラー0となった。
- Extract Constantのsemantic経路追加後、最新の全体テストは2,880合格・2スキップ・失敗0
  （合計2,882）、C#関連292件、コード生成／リファクタリング回帰114件、`sk0ya.Loomo.sln`のBuild警告0／エラー0となった。
- Encapsulate Fieldのsemantic経路追加後、最新の全体テストは2,881合格・2スキップ・失敗0
  （合計2,883）、C#関連293件、コード生成／リファクタリング回帰115件、`sk0ya.Loomo.sln`のBuild警告0／エラー0となった。
- Extract Interfaceも`CSharpSemanticOperations`へ接続し、対象class、同名interface、public instance memberをRoslyn symbolで確認するようにした。
  static／privateメンバーを除外し、expression-bodied propertyも`get;`へ変換して、新規interfaceファイル作成と元クラスへの実装追加を一つのWorkspaceEditへまとめる。
- Move Type to Fileも`CSharpSemanticOperations`へ接続し、Roslynでトップレベル型の宣言symbolと宣言位置を確認してから、元ファイルの削除と移動先作成を一つのWorkspaceEditへ返すようにした。Appから未保存C#バッファを渡す経路も統一した。
- Extract Interfaceのsemantic経路は同一ファイル内のpartial宣言もRoslynの同一type symbolから集約し、複数ファイルpartialはusing／型エイリアスの再構成が必要なため安全側で拒否する。
- 最新の全体テストは2,882合格・2スキップ・失敗0（合計2,884）、C#関連294件、コード生成／リファクタリング回帰116件、
  `sk0ya.Loomo.sln`のBuild警告0／エラー0。App／Services／CoreにRoslyn参照がない境界監査も再確認した。
- Move Type to File追加後の最新の全体テストは2,883合格・2スキップ・失敗0（合計2,885）、C#関連295件、コード生成／リファクタリング回帰117件、
  `sk0ya.Loomo.sln`のBuild警告0／エラー0。`LOOMO_RUN_REAL_LSP=1`のRoslyn Language Server Smoke 2件も合格した。
- partial抽出対応後の最新の全体テストは2,884合格・2スキップ・失敗0（合計2,886）、C#関連296件、コード生成／リファクタリング回帰118件、
  `sk0ya.Loomo.sln`のBuild警告0／エラー0。通常設定のRoslyn実機Smoke 2件は外部サーバー未指定のためskipされた。
- Extract Interfaceのsemantic経路で対象classのaccessibilityをinterfaceへ引き継ぎ、同名interface判定を同一namespaceへ限定した。
  file-local classは別ファイルへ契約を出せないため拒否し、構文fallbackの従来のpublic生成は維持した。
- using整理を`CSharpSemanticOperations`へ接続し、右クリック操作も選択TFMのCompilationと開いている未保存C#バッファを利用してCS8019を反映するようにした。
- 可視性／using整理の追加後の最新の全体テストは2,886合格・2スキップ・失敗0（合計2,888）、C#関連298件、コード生成／リファクタリング回帰119件、
  `sk0ya.Loomo.sln`のBuild警告0／エラー0。対象回帰テスト4件とcleanup回帰12件も合格した。
- 大規模solutionのソース取り込みは、ファイル数／容量上限で読み飛ばした非アクティブファイル数を
  `CSharpWorkspaceSourceSnapshot.SkippedFileCount` と `IsComplete` で公開するようにした。上限に達しても、
  アクティブ文書と開いている未保存本文は最後に必ず正本として保持する。
- Extract Interface／型移動／安全な削除／継承メンバー移動／パラメーター導入のように参照全体の検査が必要な操作は、
  スナップショットが不完全な場合に `SourceSnapshotWarning` を既存の `result.Error` 経路へ返し、候補なしや安全成功と誤認しないようにした。
  ローカル文書で完結するコード生成・抽出操作は引き続き利用できる。Buildは警告0／エラー0、全体テストは2,886合格・2スキップ・失敗0を維持した。
- LSPがcompiler Code Actionを返さない場合に備え、`Loomo.CSharp`の`CSharpCompilerCodeFixService`を追加した。
  `CS0246`／`CS0103`は参照DLLを含むnamespace探索後、修正後Compilationで診断が消える`using`候補だけを提示し、
  `CS8019`は対象行だけを削除する。さらに副作用のない単一の`CS0168`ローカル宣言だけを削除する。
  AppのC# HostCodeActionProviderへStyleCop修正と統合し、compiler Quick Fixの回帰3件を追加した。
  C#関連テスト302件とBuildは警告0／エラー0で合格した。全体テストの再実行では既存WorkflowViewModel 3件が
  並列実行時に失敗したが、クラス単独は14件合格し、今回のC#変更に起因する失敗はない。
- semantic tokenのLSP未接続／空応答フォールバックを追加した。C#固有のRoslyn `SemanticModel`走査と
  型・namespace・method・property・field・parameter・local・attributeの分類、`readonly`／`abstract`／
  `deprecated` modifier生成は`Loomo.CSharp.Editor.CSharpSemanticTokenService`へ置き、Editor側には
  `HostSemanticTokensProvider`という汎用フックだけを追加した。LSP応答を優先し、未接続・空応答・例外時だけ
  host tokenを描画する。C#関連304件、Editor.Controls 160件、両ソリューションのBuildは警告0／エラー0で確認した。
- LSPの`source.fixAll`がcompiler診断を返さない場合にも、`CSharpCompilerCodeFixService.ApplyAllAsync`で
  未使用using／未使用local／検証済みusing追加をプロジェクト／solution範囲へ繰り返し適用できるようにした。
  修正後に再解析し、途中結果をファイルへ書かず全文WorkspaceEditとして既存のpreview／atomic apply／Undoへ渡す。
  複数ファイルのcompiler Fix All回帰を追加し、C#関連305件とBuildは警告0／エラー0で確認した。
- Editorのキーボード`Fix All`／`Code Action`も、LSP接続中はLSPを優先し、空応答または未接続時はHostCodeActionへ
  フォールバックするようにした。C# Appは同じ専用DLLへsource.fixAllを委譲し、StyleCopとcompilerの順次修正を
  元本文基準の一つのWorkspaceEditへ合成するため、同一ファイルで片方だけが失われない。C# Fix Allのプレビューと
  atomic apply／Undo経路は既存実装を継続利用し、Editor.ControlsのHost回帰4件と両ソリューションのBuildを確認した。
- StyleCopとcompilerのFix All合成処理をAppから`Loomo.CSharp.Refactoring.CSharpFixAllService`へ移し、
  StyleCop未導入プロジェクトでもcompiler Fix Allを実行できるようにした。順次適用後の最終本文を元本文基準の
  全文WorkspaceEditへ正規化し、キーボード・コンテキストメニュー・Solution ExplorerのFix Allで同じ結果を使う。
  C#関連306件、Editor.Controls 160件、Buildは警告0／エラー0で再確認した。
- semantic tokenのRoslyn Compilation範囲をsolution全体からアクティブ文書のProjectReferenceグラフへ絞り、
  大規模solutionで無関係なプロジェクトを毎回読み込む負荷を避けた。未保存本文優先、型解決、LSP応答優先の
  フォールバック契約は維持し、C#関連306件、Editor.Controls 160件、両ソリューションのBuild警告0／エラー0を再確認した。
- C#専用DLLへcompiler／StyleCop診断を診断行単位の`#pragma warning disable/restore`で抑制するQuick Fixを追加した。
  `#if`等のpreprocessor行、既存pragma、非C#ファイルは対象外とし、プロジェクト設定を書き換えずWorkspaceEditの
  preview／atomic apply／Undoへ渡す。専用回帰4件（Roslyn再解析で抑制確認を含む）を追加し、C#関連310件の合格を確認する。
- using整理、cleanup、抽出、inline、単一ファイルのコード生成などプロジェクト内で完結する意味操作のCompilation範囲を
  solution全体から選択プロジェクトのProjectReferenceグラフへ絞った。rename／safe delete／Change Signature／pull・pushなど
  参照全体を検査する操作はsolution範囲を維持し、大規模solutionで機能範囲を変えずに不要なRoslyn読み込みを減らした。
- compilerの文書Quick Fixと、その対象ファイルごとのFix All再解析も同じProjectReferenceグラフを使うようにし、
  using候補探索や未使用using／localの修正でsolution全体をCompilationへ積まないようにした。C#関連309件とBuild警告0／エラー0を再確認した。
- C# fallback診断は本文変更時に前の本文の結果を即時破棄し、解析中の古い波線・Problems・Quick Fixが残らないようにした。
  最新LSP診断は保持し、fallbackだけを再解析結果まで空にすることで、解析中と候補なしを混同しない状態を維持する。
- LSPが同一種類の診断を部分的に返す場合も、C#のStyleCop／compiler fallbackを診断ID・範囲単位で統合し、
  同じ診断だけを重複排除するようにした。LSP診断からもpragma抑制Quick Fixを生成し、Problems起点のStyleCop操作を
  エディタの共通Code Action経路へ揃えた。SourceFixAllのkindフィルターも修正し、C#関連310件、Problems 20件、Build警告0／エラー0で確認した。
- 実Roslyn Language Serverのsemantic token legend（`static`／`ReassignedVariable`／`deprecated`）を確認し、
  `Loomo.CSharp`のRoslyn fallbackにもstatic宣言・代入／増減／ref／out位置のmodifierを追加した。既存の
  readonly／abstract／deprecated描画契約を保ちつつ、実応答と同じmodifier名の回帰を追加した。C#関連311件と
  Roslyn実機Smoke 2件で確認した。
- C# fallback lexerの複数行raw／verbatim補間文字列で補間式の状態と波括弧深度を行間保持し、式内のidentifier／数値を
  String tokenから分離した。不完全入力は安全側で文字列扱いに戻し、32件のC# Editor回帰とC#関連312件、Build警告0／エラー0で確認した。
- 直近の全体テストは2,851合格・2スキップ・2失敗だったが、失敗した既存のGit履歴／比較UI待機テストは
  クラス単独で再実行して各7件／15件が合格した。C#関連265件、Build警告0／エラー0も再確認した。
- 開発中のEditor commitは`f425695`、Loomo commitは`b9c4918`。両リポジトリは意図した変更を含むdirty状態で、公開NuGetだけを使うクリーンrestoreは未実施のため、§33の状態は「実装継続」とする。
- C#コード生成の意味モデル経路をpartial型の別宣言まで拡張し、別ファイル側に既に存在するinterface実装／overrideを
  active fileへ重複生成しないようRoslyn symbol identityで判定する。event fieldの複数宣言も個別に判定し、
  回帰121件、C#関連314件、`sk0ya.Loomo.sln`のBuild警告0／エラー0で確認した。
- コード生成のpartial型対応をconstructor／property／Equals・GetHashCode／ToString／Deconstruct／Disposeへ拡張した。
  別宣言のフィールド・auto-property・式本体propertyをRoslyn symbolから収集し、remote using aliasに依存しない型表示、
  compiler生成backing field除外、`IDisposable`の型identity判定を追加した。基底requiredメンバーは代入可能な場合だけ
  parameter化し、private／readonlyなど安全に満たせない契約は生成を拒否する。コード生成回帰128件、Build警告0／エラー0で確認した。
- C# semantic tokenの`ReassignedVariable`判定を、member accessのreceiverではなく実際の代入対象identifierだけへ限定した。
  複合代入・増減・ref/out・tuple・declaration expressionを対象にし、`holder.Value = value`の`holder`を誤って書き換え扱いに
  しない回帰を追加した。C#関連322件、Build警告0／エラー0で確認した。
- 基底型の`required`メンバーをconstructor生成で明示的に`base.Member`へ代入するようにし、派生型側の名前解決に依存せず
  基底契約を満たす生成結果にした。既存のローカルrequired生成（`this.Member`）は維持し、コード生成回帰128件と
  `sk0ya.Loomo.sln`のBuild警告0／エラー0で確認した。
- C# semantic token fallbackへ`async`、`const`由来の`readonly`、metadata由来記号の`defaultLibrary` modifierを追加した。
  BCL型・非同期メソッド・定数の分類を回帰5件で確認し、実Roslyn Language Server Smoke 1件とC#関連323件、Build警告0／
  エラー0を通過した。宣言／定義modifierは実応答との差分を確定するまで追加せず、既存の描画互換性を維持する。
- C# fallback lexerでXML documentationの`<summary>`／`<see>`等のタグをAttributeとして分離し、本文・不完全タグ・属性値内の`>`は
  Commentとして保護する。query式／patternの代表keywordと合わせてEditor統合回帰34件、C#関連325件、Build警告0／エラー0で確認した。
- C# fallback補完の候補詳細へRoslynの`CompletionDescription`からXML documentationのsummaryを渡し、LSPが未接続／空応答でも
  documentation popupを失わないようにした。Roslyn補完の説明表示回帰を含むC#関連326件とBuild警告0／エラー0で確認した。
- Disposeコード生成の意味モデル経路を継承型にも拡張し、基底型が提供する安全な`Dispose(bool)`を`override`して基底へ委譲する
  パターンを生成するようにした。拡張不能な継承契約は不完全な隠蔽メソッドを生成せず拒否し、コード生成回帰130件、C#関連328件、
  Build警告0／エラー0で確認した。primary constructorを持つ型への通常constructor重複生成も拒否する。
- solution範囲のソーススナップショットでlinked fileが複数projectに属する場合、active documentの担当projectを先に評価し、
  条件付きコンパイル記号／ParseOptionsが別projectに上書きされないようにした。Fix Allのlinked file編集マージも全URIを
  事前検証してから反映する原子処理へ改め、競合時の部分WorkspaceEditを防止した。回帰330件、Build警告0／エラー0で確認した。
- C#のRoslyn semantic tokenでevent field宣言を通常のfieldと区別し、`event` tokenとして表示するようにした。
  複数ファイルWorkspaceEditの新規作成文書も各パスの.editorconfig整形を通してからpreview／適用へ渡す共通経路を追加し、
  C#関連332件、Build警告0／エラー0で確認した。
- Change Signatureの参照位置収集をRoslyn Compilationの意味解決へ接続し、LSPが空応答／解析中でも呼び出し側を編集できるようにした。
  partial型の別宣言にまたがる過負荷衝突も検出し、ソーススナップショットが不足する場合は安全に適用を拒否する。
  コード生成・cleanup・Change Signatureに加え、Roslyn Rename／Fix AllのWorkspaceEditにも期待本文を付与し、Editor共通のWorkspaceEditイベントを経由しても
  App側でpreview前に外部変更／stale editを検証できるようにした。C#関連339件、実Roslyn Language Server Smoke 2件、Editor.Core 1,323件、
  全体テスト2,928合格・2スキップ・失敗0、Build警告0／エラー0で確認した。
- overrideコード生成のvirtual／override method・property・eventを、abstract契約だけthrowし、それ以外は`base`実装へ転送する出力へ改善した。
  構文fallbackとmetadata／Roslyn semantic経路の双方で生成結果を回帰し、既存のC#関連339件とBuild警告0／エラー0を維持した。
- raw interpolated stringの複数ドル形式（`$$"""...{{expr}}..."""`）をfallback lexerで正しく扱い、補間に必要なブレース数と
  リテラルブレースを区別する回帰を追加した。さらに24プロジェクトの実MSBuild solution graphを作る統合テストで、プロジェクト順序・
  ProjectReference・Ready状態・TFM保持を確認した。C#関連341件、Build警告0／エラー0で確認した。
- compilerのFix Allで不足usingの追加を未使用usingの削除より先に適用し、標準`System` namespaceを優先する候補順へ改めた。
  StyleCop CodeFixとの変更を同じworking本文へ合成し、参照アセンブリ由来の不適切なnamespace候補や、using directiveを壊すStyleCop編集を
  採用しない検証を追加した。同じCSharpIde fixtureで未保存編集→compiler／StyleCop Fix All→再診断→Buildを完了し、C#関連343件で確認した。
- Editorの`Fix all (file)` fallbackがproject全体を変更しないよう、`CSharpFixAllPlanner.CreateForDocument`で選択TFMのCompile対象と
  現在の文書だけを検証・計画する経路を追加した。兄弟Compileファイルを変更しない回帰をC#専用DLL／App境界へ追加し、C#関連346件で確認した。
- 最終回帰としてLoomo全体を2,936件合格・2件スキップ・失敗0、Editor.Coreを1,323件合格・スキップ0で確認した。
  変更対象の全Buildも警告0／エラー0で、外部Editorの共通WorkspaceEdit expected text境界を含む。
- C#テストのTRX XML解析と実行結果型をAppのViewModelから`Loomo.CSharp.Testing.CSharpTrxResultParser`へ移し、Appは
  C#結果を表示用`TestStatus`へ写像するだけにした。C#固有のテスト成果物解釈がUI層へ逆流しないAssembly境界を回帰へ追加した。
- C#テストのTRX出力を実行ごとの一意な一時フォルダーへ分離し、並行実行や遅延した前回結果の混入を防いだ。反映後はその実行分だけを
  後始末する。併せてC# BuildコマンドのPowerShell引数を単一引用符で統一し、`$`や単一引用符を含むパス／構成名を保持する回帰を追加した。
- 上記のテスト実行隔離とBuild引用修正を含め、C#関連テスト348件、Build警告0／エラー0を確認した。生成済み
  `src/Loomo.App/bin/Debug/sk0ya.Loomo.App.exe`の起動smokeも8秒間の常駐後に後始末まで確認した（WPF画面の操作確認は別記の環境制約で未実施）。
- Terminal側のキャンセル／例外で実行結果を返せない場合も、CSharp DLLが払い出したTRX一時フォルダーを自身で回収するようにし、
  部分実行の成果物が次回のテスト結果へ混入しない回帰を追加した。
- Build／Testの実行ごとの成果物隔離・PowerShell引用・キャンセル後始末・所有ルート外削除拒否を含む最終回帰で、Loomo全体は
  2,940件合格・2件スキップ・失敗0（合計2,942件）となった（C#関連349件）。実Roslynテストは環境変数未設定のため従来どおり
  2件をスキップしている。
- C#テスト探索を`SolutionModel`の選択中TFM・`Compile`項目へ接続し、readyなテストプロジェクトだけを対象にした。別TFM、
  非テストプロジェクト、MSBuildで除外されたソースを一覧へ混ぜず、構成／TFM変更のloading・failed中は空結果にして古い候補を
  返さないようにした。解決済みsolutionの探索結果は権威的な集合として扱い、実行済みでも消えたテストを除去する回帰を追加した。
  C#関連367件、Build警告0／エラー0、全体テスト2,942合格・2スキップ・失敗0で確認した。
- `LOOMO_RUN_REAL_LSP=1` を有効にしたRoslyn Language Serverの実通信Smokeを再実行し、solution初期化、completion、diagnostics、
  semantic tokens、定義／参照／rename、document highlight、inlay hint、型定義・宣言・実装のcapability境界、Change Signature連携を
  2件合格で確認した。Editor側の全テストも`Editor.Core` 1,323件、`Editor.Controls` 160件を合格した。
- multi-targetingのプロジェクト所属判定も選択中TFMの`Compile`だけに限定した。別TFMだけに存在するファイルをプロジェクト文脈、
  意味Compilation、診断／Fix対象として扱わない回帰を追加し、TFM切替後に古いTFMの診断が混ざらない入口を統一した。
- 上記の別TFM所属判定と文脈バー切替回帰を含む最新の全体テストは2,944件合格・2件スキップ・失敗0（合計2,946件）となった。
- 別TFMのCompile項目にだけ存在するファイルを、単なるプロジェクト外ではなく`NotInSelectedTargetFramework`としてC#専用モデルから
  UIへ公開した。現在の意味モデル・診断対象外である理由を文脈バーに表示し、対象TFMへ切り替えれば復帰できる状態と区別する回帰を追加した。
- `NotInSelectedTargetFramework`でも表示用の所属プロジェクトとTFM選択肢を保持し、文脈バーから対象TFMへ切り替えられるようにした。
  切替後は同じファイルが即座に選択TFMのCompile／意味モデル対象へ戻るViewModel回帰を追加した。
 - Solution Explorerから実行したC#テストのTRXも、Test Explorerの行状態・ケース集約・集計・ツリー・ガター通知へ同じ反映経路で戻すようにした。
   実行場所によって結果表示だけが分離しないことを回帰で確認した。
 - カバレッジ実行の成果物も実行ごとの一意なフォルダーへ隔離し、反映後の成功・結果なし・解析失敗を問わずApp側で回収するようにした。
   Terminalの例外／キャンセル時はC#機能DLL自身が回収し、所有ルート外のフォルダーを削除しない回帰を追加した。
   追加回帰を含む全体テストは2,948件合格・2件スキップ・失敗0（合計2,950件）、Buildは警告0／エラー0で確認した。
  - §33の自動検証記録を`docs/検証/IDE体感品質チェックリスト.md`へ追記し、`LOOMO_RUN_REAL_LSP=1`のRoslyn実通信Smoke 2件合格と、
    C#専用アセンブリ境界テスト5件合格を記録した。WPF実機操作、公開パッケージだけのclean restore、外部レビューは未完了のまま保持した。
   - `.sln`の`ProjectConfigurationPlatforms`を読み取り、solutionで選択した構成をプロジェクトごとの実構成へマッピングしてMSBuild評価へ渡すようにした。
     `.slnx`／csproj単独やマッピングのない形式は選択構成へ安全にフォールバックし、構成差分の回帰を追加した。全体テストは2,949件合格・2件スキップ・失敗0（合計2,951件）。
   - プロジェクト単位のBuild／Test／Run／Debugも、solution対象とは分けてマッピング済みの実構成を使うようにした。
     LSP／compiler／StyleCopの同一診断判定も`Loomo.CSharp`の`CSharpDiagnosticMerger`へ移し、Appを表示・配線アダプターへ寄せた。
     回帰を含む全体テストは2,952件合格・2件スキップ・失敗0（合計2,954件）、Buildは警告0／エラー0で確認した。
   - C# Command ID一覧に対するApp側のキーボード結線を点検し、一覧・右クリック・パレットには存在していた定義Peekを
     キーボード実行表にも追加した。solution構成とプロジェクト実構成が異なる場合は、文脈バーのツールチップにも
     `solution → project`の実効構成を表示するようにした。回帰を含む全体テストは2,953件合格・2件スキップ・失敗0
     （合計2,955件）、Buildは警告0／エラー0で確認した。
   - Quick Fix＝`Alt+Enter`、定義＝`F12`などRider相当の主要C#操作の既定ジェスチャを
     `Loomo.CSharp`のCommand Catalogへ移し、標準キーマップから利用できるようにした。C#以外の文書では
     そのキーをAppのグローバル結線が消費しないコンテキスト判定も追加し、キー割り当てとDLL境界の回帰を確認した。
    - `Extract Interface`のgeneric class対応を意味モデル経路へ追加した。型引数・constraintを新interfaceへ写し、
      元クラスには`IName<T...>`を実装させるWorkspaceEditを生成する。意味モデルなしのfallbackは安全のため拒否し、
      生成後の元クラス＋interfaceをRoslyn Compilationで再検証する回帰を追加した。全体テストは2,954件合格・
      2件スキップ・失敗0（合計2,956件）、Buildは警告0／エラー0で確認した。
     - `Extract Interface`のsemantic経路を複数ファイルpartial classへ拡張した。全partial宣言のpublic instance memberと
        usingを集約し、別partial側の型aliasも生成interfaceへ反映する。異なる対象を指す同名aliasは安全側で拒否し、
        元クラス・全partial・生成interfaceをRoslyn Compilationで再検証する回帰を追加した。Loomo全体2,956件合格・
        2件スキップ・失敗0（合計2,958件）、Editor全体1,483件合格、Build警告0／エラー0で確認した。
      - `IAsyncDisposable`用のコード生成をCSharp専用DLLへ追加した。意味モデルで契約とフィールド型を同定し、
        `ValueTask DisposeAsync()`、nullable参照型のnull guard、`ConfigureAwait(false)`、契約追加を一つのWorkspaceEditへ
        まとめる。基底型のasync Disposeを隠すケースと意味モデルなしは安全側で拒否し、生成後のRoslyn再コンパイル回帰を追加した。
        選択TFMのCSharpIde fixtureでも実ファイルへ適用後にsolution Build／Testまで通した。継承型の`DisposeAsyncCore` override、
        フィールドなしの`ValueTask.CompletedTask`、別partial側の既存メソッド検出も回帰へ加え、Loomo全体2,963件合格・
        2件スキップ・失敗0（合計2,965件）、Editor全体1,483件合格、Build警告0／エラー0、Roslyn実通信Smoke 2件合格で確認した。
      - `Extract Class`のsemantic経路でgeneric classの型引数／`where`制約を抽出先へ保持し、partial classでは全宣言の
        メンバーを依存判定へ含めて選択中の宣言だけを抽出できるようにした。元partial・別partial・生成先をRoslyn Compilationで
        再検証する回帰を追加した。さらにCSharpIde fixtureでgeneric／partialの抽出結果を実ファイルへ適用し、solution Build／Test
        まで通す統合テストを追加した。
      - `Move Type/File`についても、CSharpIde fixtureの実ファイルから型を削除し、新規`.cs`を作成するWorkspaceEditを適用して
        solution Build／Testまで通す統合テストを追加した。Windows共有台帳の既存テスト間競合も専用コレクションへ統合して解消し、
        Loomo全体2,972件合格・2件スキップ・失敗0（合計2,974件）、Build警告0／エラー0で確認した。
      - 同じfixtureのコピー上で、意味モデル経由の`Pull Up`／`Push Down`をそれぞれ2ファイルWorkspaceEditとして適用し、
        移動元／移動先の本文確認後にsolution Buildまで通す統合回帰を追加した。対象テスト単独で合格、Build警告0／エラー0を確認した。
      - `Safe Delete`も同じfixtureで、参照中のprivateフィールドは削除を拒否し、未使用privateメソッドだけを削除して
        solution Build／Testまで通す意味モデル回帰を追加した。
      - `Inline Method`／`Inline Variable`もfixtureの意味モデル経路で適用し、対象メンバー／ローカルの除去と置換後の
        solution Build／Testまで通す統合回帰を追加した。
      - `Encapsulate Field`の同名判定がフィールド使用箇所を宣言衝突と誤認していたため、型直下の宣言名だけを検査するよう修正し、
        semantic単体回帰とfixtureのproperty生成→solution Build／Testを通過させた。
      - semantic tokenの属性判定が属性引数まで`attribute`扱いにしていたため、属性名の構文範囲だけを分類するよう修正し、
        `[Obsolete(DiagnosticId = "...")]`の名前付き引数を誤着色しない回帰を追加した。C# semantic token関連7件、Build警告0／エラー0、
        全体テスト2,973件合格・2件スキップ・失敗0（合計2,975件）で確認した。
      - `CSharpSolutionExplorerView`をSTA上の実WPF Windowへ載せ、Solution／Project／TargetFramework／FileのTreeView生成と
        file itemの実体化を確認するビュー回帰を追加した。さらにAutomationPeerでSolution／fileノード名を辿る回帰を追加し、
        全体テストは2,975件合格・2件スキップ・失敗0（合計2,977件）で完走した。
      - CSharpIde fixtureの編集→compiler／StyleCop診断→Code Fix→再診断を、一時コピーへ実際に反映してからsolution Build／Testまで
        つなぐ統合旅程へ拡張した。元fixtureを変更しない後始末も含め、対象テストを合格させた。
      - WPF実機確認の代替としてWindows UI AutomationでLoomo本体Windowの表示・応答と実行プロセスを同定したが、WPF子要素の
        安定列挙／操作はできなかった。ただしC# Solution Explorerへ追加した`CSharpSolutionTree` AutomationIdは実アプリ外部から
        取得できることを確認した。computer-use Native pipeの利用不能と合わせ、実機の編集→診断→Fix／refactor→Build→Test旅程は
        未確認のまま保持する。
      - `CSharpIde` fixtureを`--workspace`で指定した実アプリでは、UI Automationで`FeatureService.cs` TreeItemをSelectionItemとして
        選択しEnterを送信できた。結果として実際の`FeatureService.cs`タブ（`EntryTitle`／`TabTitle`）の表示を確認した。編集→診断→
        Fix／refactor→Build→Test全旅程は未確認として残す。
      - TreeItemのSelectionItem選択後にEnterでファイルを開くキー経路を`CSharpSolutionExplorerView`へ明示追加し、WPF AutomationPeer
        回帰2件を合格させた。変更後の全体再実行では既存の`GitComparePanelTests`が一過性の10秒待機で1件失敗して長時間化したため
        中断したが、同テストクラス単独は15件合格、C#変更対象のBuildとビュー回帰は合格している。
      - 変更後に`FullyQualifiedName~CSharp`でC#関連テスト379件を単独実行し、失敗0で完走した。全体再実行の既存Gitテスト失敗とは
        分離して、C#専用DLLとC# UI結線の回帰が合格することを確認した。
      - Solution Explorerの動的なBuild／Test／Run／DebugメニューへUI Automation IDを付与し、WPF回帰で
        `ContextMenu`の実体化、4操作の識別、Build操作から`ActionRequested`への結線を確認した。ビュー回帰は3件、
        C#関連テストは381件を再ビルド後に実行し、失敗0で完走した。
      - C#エディタの右クリックにも、リファクタリング／コード生成／Fix AllのCommand IDをUI Automationへ公開し、
        動的メニューの項目を実機検証で安定して識別できるようにした。キーボード／コマンドパレット／右クリックが
        同じ`CSharpEditorCommandCatalog`のIDを共有する境界を維持する。
      - StyleCop設定モデルに、PackageReference／設定ファイルは存在するがMSBuild評価結果からAnalyzer DLLを
        解決できない`AnalyzerNotLoaded`状態を追加した。設定不正・未導入・Analyzer未読込・正常導入をUI表示で区別し、
        未読込をStyleCop違反の偽診断として表示しない経路も追加した。設定解析回帰7件・診断回帰9件と
        ソリューションBuild（警告0／エラー0）で確認した。
      - C# Fix All fallbackのStyleCop各projectを同一working snapshotから評価し、linked fileのURI編集は
        完全一致だけを統合してから一度だけ反映するよう修正した。projectごとの異なる修正を順次合成せず、
        部分WorkspaceEditを返さない回帰を追加し、C#関連テスト381件とBuild（警告0／エラー0）で確認した。
      - コンストラクターパラメーターからのフィールド生成で、予約語パラメーター（`@class`など）の代入側も
        識別子エスケープを通すよう修正した。生成回帰143件、C#関連テスト382件、Build警告0／エラー0で確認した。
      - 専用一時workspaceの実Loomo WPFで`FeatureService.cs`を開き、Vim挿入→dirty表示→`ZZ`保存と対象ファイルの
        SHA-256変化を確認した。保存後のfixture solution Build（警告3・エラー0）／Test（1合格）まで実行したが、
        診断表示およびUI上のFix／refactorは未確認として残す。
      - 同じ実Loomo WPFで意図的なStyleCop SA1101を表示し、`Alt+Enter`から`Prefix reference with 'this.'`を選択、
        WorkspaceEdit preview→適用→再診断→`ZZ`保存まで確認した。SHA-256変化後のfixture solution Build（警告63・エラー0）／
        Test（1合格）も通過したが、UI上のrefactor、Undo、外部変更は未確認として残す。
      - 実Loomo WPFのCSharpIde fixtureで`GetValue`を右クリック→リファクタリング→名前の変更し、WorkspaceEdit previewから
        `FeatureService.cs`・`Contract.cs`・`FeatureTests.cs`の3ファイルへ適用した。Untitledタブの空パスを通常ファイルとして
        正規化していたホスト例外と、Source Generatorの仮想文書をRename結果へ含めていたCSharp DLL側を修正し、`ZZ`保存後の
        fixture solution Build（警告63・エラー0）／Test（1合格）まで実機確認した。続く実機追試で、同じ3ファイル変更を
        Ctrl+Z 1回で復元し、preview中の`FeatureTests.cs`外部変更時は適用を拒否して外部追記を保持することも確認した。
      - CSharp DLLのProject範囲Fix Allを実fixtureのSolutionModelで実行し、選択ProjectのCompile対象を公式StyleCop
        CodeFixで再解析して1つのWorkspaceEditへ統合した。`this.value`を含む結果を適用後、対象ProjectのBuild成功まで確認した。
        さらにSolution Explorerの`Feature` Projectを実WPFで選択し、ContextMenuの`CSharpSolutionAction.FixAllProject`を起動した。
        LSP `source.fixAll`の15秒timeout後に`Loomo.CSharp`の公式StyleCop CodeFix fallbackへ進み、3ファイルのWorkspaceEdit
        preview→適用と`Fix all: 6 件の修正を適用しました。`を確認した。診断プルのCTS終了競合を修正後、実行中クラッシュは再発しなかった。
      - 同じ実Loomo WPFでSA1101の`Prefix reference with 'this.'`を`Alt+Enter`から適用し、`Ctrl+Z`で
        `LSP／Roslyn WorkspaceEdit`を1回の操作として復元できることを確認した。外部変更競合はRename旅程で確認済み。
      - Project／Solution Fix AllのLSP `source.fixAll`要求をUIスレッド外へ移し、15秒のハードタイムアウト後も
        `Loomo.CSharp`の公式StyleCop CodeFix fallbackへ進むようにした。LSPプロバイダーが応答しない場合でも、
        WPF UIを待ち状態にせず既存のpreview／WorkspaceEdit／Undo経路を利用できる。Loomo全体テストは
        2,982合格・2スキップ・失敗0（合計2,984）、Editor全体はCore 1,323＋Controls 160合格、Build警告0／エラー0。
      - 公開 `sk0ya.Editor.Controls 1.0.79`は`Editor.Core`／`Editor.Controls`／`Editor.Controls.Defaults`をbundleして
        いることをNuGet package metadataで確認した。restoreは成功するが、現行ローカルEditorの追加API（補完追加編集、
        WorkspaceEdit expected snapshot、statement completion hook等）を含まないため、公開版だけのBuildは未達として記録した。
- Editor packageを1.0.81候補としてpackし、`net9.0`のEditor.Core assetと`net9.0-windows7.0`のWPF assetを同梱した。
  Loomoを`UseLocalEditor=false`でlocal package sourceからclean restore／Buildし、警告0・エラー0、全テスト2,982合格・
  2スキップ・失敗0を確認した。公開nuget.orgの1.0.79は未更新なので、公開版のみの受入れはリリース後に再実行する。
- 全体テスト時に既存の`FileOperationHistoryTests`が`RecycleBin.TryRestore`のゴミ箱全件走査で停止する事象をスタックから特定し、
  直前に同プロセスが削除したパスを優先検索するヒントを追加した。対象クラス15件は40秒から9秒へ短縮して合格し、
  C#関連383件とBuild（警告0／エラー0）も合格した。全体テストは別の`GitHistoryFilterTests`一過性失敗後に無出力化したため、全体成功とは扱わない。
- `GitHistoryFilterTests`の明示`ReloadAsync`とデバウンス再読込の世代競合を修正し、履歴テストを3回連続で合格させた。
  ゴミ箱走査修正と合わせた全体`dotnet test`は2,982合格・2スキップ・失敗0（合計2,984、5分7秒）で完走した。

## §33.3 設計原則

1. **意味の正本を一つにする。** Editorの独自lexer、Loomoの簡易正規表現、Roslyn LSP、専用解析エンジンが異なる判定をしない。
   表示はEditor、プロジェクト文脈と集約はLoomo、意味解析はRoslynを第一候補とする。ただし、必要性が検証された場合は
   LoomoまたはEditor向けの専用解析エンジンを用意し、同じプロジェクト状態・診断モデル・編集モデルへ接続する。
2. **別のRoslyn解析プロセスを乱立させない。** §30の`ILspWorkspace`／`ILspDocument`が所有するLSPセッションを使い、
   `MSBuildWorkspace`を機能ごとに新規起動しない。どうしても独自semantic operationが必要な場合は、同じプロジェクト状態を
   共有できる単一サービスとして設計する。
3. **プロジェクト未読込と「候補なし」を分ける。** `未構成 / 起動中 / 初期化中 / プロジェクト読込中 / ready / 再接続中 / 失敗`
   をUIとログで区別する。
4. **診断はLSPとBuildを同じモデルへ正規化する。** `source`、`code`、severity、URI、range、message、versionを持ち、
   同じ問題の重複表示と古い応答による巻き戻りを防ぐ。
5. **複数ファイル編集はpreview可能なトランザクションにする。** 全対象を検証してから適用し、失敗時は部分適用しない。
6. **Editorの機能をLoomoへ複製しない。** 補完ポップアップ、snippet、selection、Canvas描画はEditorに置き、Loomoは共有状態・集約・導線を担当する。
7. **既存プロジェクトを勝手に書き換えない。** StyleCop導入、`.editorconfig`変更、`stylecop.json`作成は提案または明示操作にする。

8. **C#固有コードは専用アセンブリへ隔離する。** `src/Loomo.CSharp/sk0ya.Loomo.CSharp.dll` に、MSBuild／Roslyn／C#のテスト検出・リファクタリング・プロジェクト構造の実装を置く。
   `Loomo.Core` はC#固有型を持たず、`Loomo.App` はView／ViewModelのUIアダプターだけを担当する。LSPの通信・補完ポップアップ・描画など言語非依存の機能は `C:\Projects\Editor` 側へ置く。
   これによりC#機能の変更でCore／ServicesへRoslyn依存が漏れず、将来の言語機能追加も同じ境界を保てる。

## §33.4 Rider相当の最小到達点

次を満たした時点を「C#編集MVP」とする。

- `.sln`を開くとプロジェクト、参照、TargetFramework、Analyzerの状態が表示される。
- プロジェクト内のC#ファイルで、using追加を含む補完採用がマウスなしで成立する。
- syntax error、compiler warning、StyleCop warningが波線とProblemsへ出る。
- `Alt+Enter`相当の操作でQuick Fixを表示・適用できる。
- `Fix all in file`または同等の範囲指定Code Actionが使える。
- 定義、参照、実装先、型定義、renameがプロジェクト文脈で動く。
- rename／extract／Change Signatureなどの複数ファイル変更をpreviewし、一括Undoできる。
- constructor、override／interface member、property等の最低限のコード生成ができる。
- format、using整理、Analyzer診断のseverityを`.editorconfig`から反映できる。
- StyleCopをBuildにもIDEにも同じルールで適用できる。
- C#サンプルソリューションで、編集→診断→Fix→Build→Testの旅程を再実行できる。

## §33.5 Phase 0 — 評価用C#ワークスペースと品質基線

### 実装

- `docs/検証/IDE体感品質チェックリスト.md`へC#専用シナリオを追加する。
- 次のfixtureソリューションを用意する。
  - 単一プロジェクト
  - 複数プロジェクト＋ProjectReference
  - 複数TargetFramework
  - WPFプロジェクト
  - Analyzer＋StyleCop
  - Source Generator
  - 条件付きコンパイル
  - linked file
  - xUnitのTheoryと複数ケース
  - プロジェクト外の`.cs`
  - 大きなソリューション
- 各操作で、LSP状態、プロジェクト読込時間、診断件数、補完表示時間、適用時間、Build結果を記録する。
- 公開Editorパッケージ版とLoomoの参照版をテスト結果へ記録する。

### 完了条件

- 同一fixtureを別リリースで再実行できる。
- C#、TypeScript、LSPなしの3種類で、誤診断・古い診断・入力先誤りが検出できる。
- 実機確認日、パッケージ版、テスト件数が設計書へ残る。

## §33.6 Phase 1 — C#プロジェクト／意味モデル

### 実装

- `SolutionModel`、`ProjectModel`、`TargetFrameworkModel`を追加する。
- `.sln`、`.slnx`、`.csproj`を入口として、ProjectReference、Compile、Analyzer、AdditionalFiles、Noneを把握する。
- MSBuildの実評価結果を使い、Configuration、TargetFramework、DefineConstants、LangVersionを取得する。
- ファイルから担当Project／TargetFrameworkへの逆引きを提供する。
- project loading、reload、failed、not-in-projectを状態として公開する。
- Roslyn LSPのworkspace rootとProjectModelのrootを一致させる。
- プロジェクト外のファイルは、限定機能（lexer、基本LSP、Build対象外）として明示する。
- Solution Explorerをファイル一覧ではなく、solution／project／folder／fileの構造表示へ拡張する。

### 完了条件

- ProjectReference越しの型・定義・参照・補完が正しい。
- multi-targetingで選択TargetFrameworkが表示され、別TFMの診断が混ざらない。
- プロジェクト読込中に「候補なし」と誤表示しない。
- `Directory.Build.props`、`Directory.Build.targets`、`.editorconfig`変更で再評価できる。

## §33.7 Phase 2 — C# LSPクライアントの完全性

### 実装

- CompletionItemへ`additionalTextEdits`、`data`、resolve、command、完全なsnippetを追加する。
- using追加、名前空間候補、重複import、採用後のcaretを一つのUndo単位で扱う。
- `textDocument/implementation`、`typeDefinition`、`declaration`、`prepareRename`を追加する。
- peek表示、結果のscope／project／external source表示を追加する。
- semantic tokensのfull／delta、document highlight、code lens、document linkをcapability駆動で追加する。
- server capabilityがない場合は非対応を明示し、別機能へ無断で置換しない。
- LSPの`source.fixAll`、`quickfix`、`refactor`をCode Actionの分類と範囲指定へ反映する。

### 完了条件

- `.`入力→候補移動→Tab／Enter／commit character→引数入力がC#で成立する。
- `System.Linq`など未import型の補完採用でusingが正しく追加される。
- 旧リクエストの遅い応答で候補・診断・signatureが巻き戻らない。
- 実装先、型定義、prepare renameの正常系・非対応・解析中をテストする。

## §33.8 Phase 3 — Inspection、Quick Fix、Code Cleanup

### 実装

- 診断を、compiler／Roslyn analyzer／StyleCop／Buildの発生源付きでProblemsへ統合する。
- エディタのgutterへseverity別markerを表示する。
- `Alt+Enter`相当のQuick Fix popupを、caret・選択・Problems行から同じCommand IDで開く。
- 個別修正、ファイル全体、プロジェクト、ソリューション範囲を区別する。
- `Fix all`、Suppress、ドキュメント版不一致、適用失敗、解析中を表示する。
- `.editorconfig`のC# formatting、naming、syntax style、diagnostic severityを読む。
- format、organize usings、redundant code削除、file layoutをcleanup profileとして提供する。
- format／cleanupはpreviewと変更件数を表示し、generated codeを除外できるようにする。

### 完了条件

- 構文エラーまたはAnalyzer違反→波線→Alt+Enter→Fix→診断消去が一続きで成立する。
- LSP診断とBuild診断が同じ問題を二重表示しない。
- Fix allが範囲を越えて編集する場合、対象ファイルと件数を事前に表示する。
- 1000件以上の診断更新でも、選択・スクロール・入力を失わない。

## §33.9 Phase 4 — StyleCop.Analyzers連携

StyleCop.AnalyzersはRoslyn Analyzerとしてプロジェクトのコンパイルへ組み込む。StyleCop専用の別解析エンジンを
Loomo内へ作らない。公式資料では、ルールの有効化・無効化・severityはruleset／Analyzer設定で、プロジェクト固有の文言や
細かな挙動は`stylecop.json`で設定する構成になっている。[StyleCop.Analyzers Configuration](https://github.com/DotNetAnalyzers/StyleCopAnalyzers/blob/master/documentation/Configuration.md)

### 4.1 導入と設定

- プロジェクト単位で`StyleCop.Analyzers`のPackageReferenceを扱う。
- Central Package Managementがある場合は中央管理を優先する。
- バージョンはLoomoへ固定値を埋め込まず、対象ソリューションの既存設定を尊重する。
- 導入操作は次のいずれかを明示選択できるようにする。
  - 既存プロジェクトへPackageReferenceを追加する提案
  - Directory.Build.propsへ追加する提案
  - 既存のruleset／`.editorconfig`／`stylecop.json`を利用する
- `stylecop.json`と`.stylecop.json`を認識する。
- `stylecop.json`はソース管理対象として扱い、パス・JSONエラーを診断する。
- rulesetと`.editorconfig`が同じSAルールを設定した場合の優先順位を表示する。
- `dotnet_diagnostic.SAxxxx.severity`、カテゴリseverity、`dotnet_analyzer_diagnostic.severity`を認識する。

### 4.2 IDE診断

- Roslyn Language ServerがプロジェクトのAnalyzerを読み込んだことを状態表示する。
- `SA0000`〜のdiagnostic id、message、severity、説明、help URL、range、projectを保持する。
- 文書変更後のStyleCop診断を既存のpull diagnostics経路で取得する。
- C#の波線、Problems、現在行のQuick Fixへ同じ診断を流す。
- StyleCop固有のコード修正がCode Actionとして返る場合は、個別修正とFix allを表示する。
- Analyzer未読込、設定ファイル不正、プロジェクト解析中、Analyzer例外を「ルール違反」と混同しない。
- `#pragma warning disable SAxxxx`、`SuppressMessage`、`.editorconfig`による抑制を正しく表示する。

### 4.3 Build診断

- `dotnet build`のStyleCop warning／errorを、LSP診断と同じDiagnosticModelへ変換する。
- `SAxxxx`を安定キーとして、LSPとBuildの同一診断を重複排除する。
- severityをerrorへ設定したStyleCop違反でBuildが失敗することをfixtureで確認する。
- Build時だけ検出されるAnalyzer診断、生成コード、Release構成差を記録する。
- Build結果から該当行へ移動し、Quick Fixが利用できない場合は理由を表示する。

### 4.4 StyleCop向けUI

- 設定画面にAnalyzer状態、PackageReference、設定ファイル、ルール件数を表示する。
- SAルールIDで検索し、severityをnone／silent／suggestion／warning／errorから変更できるようにする。
- 変更はプロジェクトファイル、`.editorconfig`、rulesetのどこへ書くかを明示する。
- StyleCopのルール説明と公式ドキュメントへのリンクを表示する。
- `stylecop.json`のcompanyName、copyright、documentationCulture等を直接編集できるかは別Phaseとし、まず外部ファイルを正しく読む。

### 4.5 完了条件

- SAルール違反を含むfixtureを開くと、LSPの波線とProblemsへ表示される。
- 同じfixtureをBuildすると、IDEとBuildのseverity・ID・位置が一致する。
- SAルールを`.editorconfig`でwarningからerrorへ変更すると、IDEとBuildの両方へ反映される。
- StyleCopのCode Fixを個別適用でき、対応するFix allは範囲を誤らない。
- Analyzer未導入、Analyzer未読込、設定不正、単なる違反を別々の状態として説明できる。
- C#プロジェクト、複数プロジェクト、multi-targeting、generated codeで回帰テストがある。

### 4.6 専用解析エンジンの採用判断

StyleCop.AnalyzersをRoslyn LSP／Build経由で利用する方式を第一候補とするが、これを絶対条件にはしない。
次のいずれかが解決できない場合は、StyleCop対応またはC#編集品質のための専用解析エンジンを設計・実装する。

- LSPの診断遅延・欠落・再接続で、編集中の即時診断を安定して提供できない。
- プロジェクト外のファイル、解析中のプロジェクト、multi-targetingを正しく扱えない。
- Analyzerの診断・設定・抑制・severityをLoomoのProblems／Quick Fixへ完全に伝搬できない。
- RoslynのCode Actionでは、Rider相当のStyleCop修正、Fix all、cleanup、previewを実現できない。
- 同じ診断をEditor、LSP、Buildで再現できず、ユーザーに一貫した結果を示せない。
- 大規模ソリューションでCPU、メモリ、応答時間が許容値を超える。

専用解析エンジンを採用する場合の条件：

- RoslynのCompilation／Syntax／SemanticModel、またはそれと同等のC#意味モデルを利用する。
- StyleCopのSAルールID、severity、range、message、help URL、抑制情報を既存DiagnosticModelへ変換する。
- Analyzer固有の判定を再実装する場合は、対象ルール、互換性、既知の差分をルール単位で記録する。
- Roslyn Analyzerと専用エンジンを同時に有効化して二重診断しない。発生源と優先順位を設定で明示する。
- 専用エンジンの結果も、LSP診断・Build診断と同じProblems／Quick Fix／preview／Undo経路を通す。
- 専用エンジンはプロジェクト状態を独自に再構築せず、§33.6の`SolutionModel`を共有する。

採用判断はfixtureの実測で行う。Roslyn方式が完了条件を満たす限り専用エンジンは保留し、満たせない場合にのみ不足する範囲を限定して導入する。

## §33.10 Phase 5 — C#リファクタリングとコード生成

### リファクタリング

- rename、safe delete、move type／file、extract method／class／interface／constant／field／variableを整備する。
- introduce parameter／property、inline method／variable、encapsulate field、pull up／push downを追加する。
- Change SignatureをRoslynの意味モデルと連携させ、overload、method group、generic、dynamic、reflectionの扱いを明示する。
- Roslyn code actionとLoomo独自操作を同じpreview／apply／undo経路へ統合する。
- 競合、未保存変更、外部変更、文書version不一致、ファイル作成／移動／削除を事前表示する。

### コード生成

- implement interface／override member
- constructor、property、field、delegating member
- equals／hash code、dispose pattern
- generate from usage
- null check、argument guard
- JSONからC#型生成

### 完了条件

- 変更前に全ファイルのdiffを確認できる。
- 複数ファイル変更を一括適用し、1回のUndoで元へ戻せる。
- 失敗時に部分適用されたファイルが残らない。
- 生成コードが`.editorconfig`、nullable、命名規則を尊重する。
- Roslynが「解析中で空配列」を返す場合、候補なしと断言せず状態を表示する。

## §33.11 Phase 6 — C#編集UX

- C#構文に基づくsmart indent、smart enter、statement completion、brace補完を追加する。
- raw／interpolated string、pattern、query、preprocessor、XML documentationの色分けを検証する。
- semantic tokensとlexer fallbackの優先順位を固定する。
- 標準キーマップとVimキーマップを切り替えられるようにする。
- `Alt+Enter`、定義、実装、peek、rename、format、code cleanupをCommand IDで統一する。
- parameter name hint、method signature、documentation popupを補完と統合する。
- inspection、test、coverageのgutter表示を同じ描画・操作モデルにする。

### 完了条件

- C#の代表構文fixtureで、入力中のcaret、indent、色分け、補完、診断が破綻しない。
- Vim操作を有効にしたまま、標準キーマップの主要操作を選択できる。
- Editor、Problems、Terminal、Debug間でキーボードの入力先が漏れない。

## §33.12 Phase 7 — Build／Test／Debug統合

- solution configuration、TargetFramework、Debug／Release、launch profileを選べるようにする。
- test adapterまたは公式のテスト検出結果を利用し、正規表現による推測を補助扱いにする。
- Theory／TestCaseの実データ、trait／category、skip、failed only、rerunを扱う。
- テスト単体・クラス・ファイル・プロジェクト・ソリューションの実行／デバッグを揃える。
- coverage、test profiling、hot reload／Edit and Continueは別の大きな機能として設計する。
- Build、Test、LSP、StyleCopの診断を発生源付きで一つのProblemsへ統合する。

### 完了条件

- Solution Explorerから対象とconfigurationを選び、同じ構成でBuild／Run／Test／Debugできる。Project／ExecutableとIIS Expressのlaunch profileは通常Runへ適用し、IIS Expressは起動プロセスへのDAP attachでDebugできる。
- テスト検出結果と実行結果のIDが安定し、再実行で重複しない。
- Build構成を変更してもLSP診断とTest対象が古い構成のまま残らない。

## §33.13 実施順序

実施順序は次の通りとする。

```text
P0 品質基線
 ↓
P1 プロジェクト／意味モデル
 ↓
P2 LSP完全性
 ↓
P3 Inspection／Quick Fix／StyleCop／Cleanup
 ↓
P4 Refactoring／Code Generation
 ↓
P5 C#編集UX
 ↓
P6 Build／Test／Debug統合
```

StyleCopだけを先に追加しても、Roslynがプロジェクトを正しく読み込めなければIDE診断は安定しない。
そのためStyleCopのUI作成はP3で行うが、P1でAnalyzerを含むプロジェクト評価とLSPのreadinessを先に完成させる。

## §33.14 実装時のルール

### Editor DLLを変更する場合

- `C:\Projects\Editor`の変更は、Loomoからローカルプロジェクト参照、ローカルDLL参照、またはローカルpack成果物で利用する。
- §33のロードマップが完了するまで、変更したEditor DLL／パッケージを`nuget.org`へ公開しない。
- 公開済みNuGet版と開発中のローカル版を混在させない。どのEditor commit／DLL／パッケージを使っているかをビルドログへ記録する。
- Editor側の変更は、Editorリポジトリ内の全テスト、統合パッケージの生成、Loomoのrestore／build／test、実機確認まで通してから採用する。
- ローカル参照を公開版へ戻す場合は、Editorの変更を含むリリースノート、パッケージ版、互換性確認結果を残す。

### レビュー

- 追加・変更した機能は、実装者以外によるレビューを必ず実施する。
- レビュー対象には、実装コードだけでなく、LSP／Analyzerのプロトコル処理、UI状態遷移、ファイル操作、Undo、キャンセル、失敗時の復旧、テスト fixtureを含める。
- 「動作した」だけで承認せず、プロジェクト外、解析中、複数プロジェクト、multi-targeting、外部変更、サーバー切断の挙動を確認する。
- レビュー未実施の変更は、Phase完了・公開パッケージ化・ロードマップの完了チェックへ進めない。

### テスト

- 追加した機能には、正常系だけでなく、空結果、非対応capability、timeout、キャンセル、古い応答、文書version不一致、部分失敗のテストを追加する。
- C#機能はfixtureソリューションで、編集→診断→修正／リファクタリング→Build→Testの一連の統合テストを追加する。
- 複数ファイル変更には、preview、適用順序、ファイル作成／移動／削除、外部変更、atomic failure、Undo／redoのテストを追加する。
- StyleCop連携には、Analyzer導入済み／未導入、ruleset、`.editorconfig`、`stylecop.json`、severity変更、抑制、LSPとBuildの重複排除を網羅する。
- UI操作が必要な機能は、純ロジック／ViewModelテスト、LSPプロトコルテスト、実機UI確認を分けて用意する。
- 新しいテストを追加できない機能は、テスト不能な理由と代替検証方法をレビュー記録へ残す。

## §33.15 リリース判定

各Phaseはコードが存在するだけでは完了にしない。

1. Core／Services／Editorの純ロジックテスト。
2. 変更したEditorリポジトリの全テスト。
3. Loomo全体の`dotnet test`と`dotnet build sk0ya.Loomo.sln`。
4. 公開パッケージだけでrestoreしたクリーン環境で再実行。
5. C#fixtureの編集→診断→Fix／refactor→Build→Testを実機確認。
6. 変更対象ファイルのhash、dirty状態、Undo、外部変更の結果を確認。
7. Editorパッケージ版、StyleCop Analyzer版、.NET SDK版、実機確認日を設計書へ記録。

## §33.16 非目標

- Riderの全機能数を一致させること。
- 必要性の検証なしに、Loomo内へ独自のC#コンパイラ、独自Analyzer、独自LSPサーバーを作ること。
- StyleCopのルールを、互換性・性能・UX上の不足を確認せずにLoomo側で再実装すること。
- プロジェクトファイルや`.editorconfig`をユーザーの明示操作なしに書き換えること。
- リモート開発、共同編集、完全なProfiler、完全なHot Reloadを本ロードマップのC#編集MVPへ混ぜること。

### 2026-09-01 検証追記

- PATH上のRoslyn Language Serverを実プロセスで起動する`RealRoslynLspIntegrationTests`を再実行し、solution初期化、completion／diagnostics／semantic tokens、定義・参照・rename、Change Signature連携の2件を合格させた。
- したがって、残課題は実サーバー接続の基本成立ではなく、実WPFでの未確認操作範囲、公開Editorパッケージへの反映、実装者以外のレビューである。
- 実WPFのコード生成プレビュー／適用で`Equals／GetHashCode`生成を確認した際、式形式`Equals`の末尾セミコロン欠落を発見した。CSharp専用DLL側で修正し、生成後のRoslyn構文検査を回帰へ追加した（コード生成テスト144件合格）。
- 修正後のC#関連回帰は384件合格、Loomo全体は2,983件合格・2件スキップ・失敗0（合計2,985件）。Editor 1.0.81候補のpackage-only Buildと通常Buildも警告0・エラー0で再確認した。
- CSharpIde fixtureでも`Equals／GetHashCode`生成のWorkspaceEditを実ファイルへ適用し、solution BuildとFeature.Testsまで通す統合回帰を追加した。
- 追加後の`FullyQualifiedName~CSharp`回帰は385件合格、失敗0となった。
- 追加後の全体再走は既存`GitComparePanelTests`の一過性タイムアウト1件後に5分超無出力となったため停止し、成功値へ算入しない。package-only gateは警告0・エラー0で通過し、通常参照構成へ復元した。
- 同じ全体テストを安定化確認後に再試行したが、約7分経過しても集計を返さず、testhostとそのテスト子プロセスを停止した。全体成功とは扱わない。個別のC#回帰385件、通常Build、Editor 1.0.81候補のpackage-only Buildは合格済みである。
- WPF／LSP／外部プロセスを含むテストアセンブリの並列化を無効にし、全体テストを決定的に再実行した。2,984件合格・2件スキップ・失敗0（合計2,986件、6分42秒）。固定待機で揺れていたLSP診断回帰も期待状態待機へ変更し、非C#回帰2,599件合格・2件スキップ・失敗0を確認した。
- 同じ決定的実行をEditor 1.0.81候補のpackage-only構成（`UseLocalEditor=false`）でも行い、2,984件合格・2件スキップ・失敗0（合計2,986件、6分42秒）。確認後は通常のローカルEditor参照へ復元した。
- `LOOMO_RUN_REAL_DEBUG=1`でVSTest debug hostを起動し、実`netcoredbg`のattach、Continue、testhostのexit 0、DAP／testhostの後始末までを1件の実統合テストで確認した。追加後の通常全体テストは2,984件合格・3件スキップ・失敗0（合計2,987件、6分50秒）、Editor 1.0.81候補のpackage-only全体テストも2,984件合格・3件スキップ・失敗0（合計2,987件、6分41秒）となった。
- C# semantic tokenのRoslyn宣言解決を、既知の宣言構文に加えてRoslynの汎用`GetDeclaredSymbol`へfallbackさせた。pattern変数、`foreach`／`catch`変数、分解宣言を色付け対象へ含め、宣言時の初期化を`ReassignedVariable`と誤分類しないよう修正した。C#関連回帰386件とsolution Build（警告0／エラー0）で確認した。
- `LOOMO_RUN_REAL_IIS=1`でfixtureの`launchSettings.json`からIIS Express profileを読み取り、実`iisexpress.exe`の起動・待受・停止を確認した。続けて同じ実プロセスへ`netcoredbg`をDAP attachし、Running／Stopped状態と後始末までを2件の実統合テストで確認した。複雑なapplicationhost／host構成の実機確認は残件とする。
- IIS ExpressのCSharp DLL起動仕様に、Visual Studio形式の`.vs/config/applicationhost.config`探索を追加した。対象プロジェクトの物理パスとHTTP／HTTPS bindingのポートが一致するsiteだけを`/config`＋`/site`で起動し、一致しない・壊れた構成は従来の`/path`方式へ戻す。引数生成とsite解決の回帰9件、solution Build（警告0／エラー0）、全体テスト2,986件合格・5件スキップ・失敗0（合計2,991件）で確認した。複雑hostの実構成を使った実IIS起動は引き続き残件とする。
- 既定のIIS Express configを一時コピーしてfixture用site／空きHTTP bindingを追加し、`/config`＋`/site`方式で実`iisexpress.exe`の待受・停止まで確認する実統合テストを追加した。実IIS関連3件合格、solution Build（警告0／エラー0）。認証、複数virtual directory、SSL bindingなどを含む複雑hostの実機確認は残件とする。
- applicationhost統合テスト追加後の通常全体テストを再実行し、2,986件合格・6件スキップ・失敗0（合計2,992件、6分46秒）を確認した。
- IIS Express統合追加後も`UseLocalEditor=false`／`EditorPackageVersion=1.0.81`のpackage-only restore／Buildを再確認し、警告0／エラー0だった。その後、通常のローカルEditor参照へ戻してBuild（警告0／エラー0）を確認した。
- 同じEditor 1.0.81候補のpackage-only構成で全体テストも再実行し、2,986件合格・6件スキップ・失敗0（合計2,992件、6分52秒）を確認した。確認後は通常のローカルEditor参照へ復元した。
- IIS host回帰を拡張し、HTTPS bindingの`sslport`解決を含む起動仕様テスト6件、追加virtual directoryを含む実`applicationhost.config` site起動3件を合格させた。通常のsolution Buildも警告0／エラー0で確認した。
- Build済み`sk0ya.Loomo.App.exe`を`--workspace tests/Fixtures/CSharpIde`で実プロセス起動する`RealWpfProcessIntegrationTests`を追加し、`LOOMO_RUN_REAL_WPF=1`で外部UI Automationから`Loomo`ウィンドウ、ワークスペース、`CSharpSolutionTree`、`EditorCanvas`、C#ファイルタブ、`LSP: ready`を待機・列挙：1合格。実プロセスの主要C#導線はAutomation公開済みだが、編集→保存→Fix／リファクタリング→Build→Testを一続きに操作する外部自動旅程は未実装として残す。
- 実WPFスモーク追加後の通常構成とEditor 1.0.81候補package-only構成で、各全体テストを2,987件合格・7件スキップ・失敗0（合計2,994件、各6分51秒）で再確認した。package-only確認後は通常のローカルEditor参照へrestoreし、solution Build（警告0／エラー0）を確認した。
- Editorのカスタム描画`EditorCanvas`が実WPFのUI Automationへ`TextPattern`を広告していなかったため、`GetPattern(PatternInterface.Text)`を実装した。EditorのAutomation回帰6件、およびLoomo実プロセスからのC#本文取得を合格させた。外部キーボードによる編集・保存は入力フォーカスの実機依存が残るため、リリース判定の実機確認残件として維持する。
- 上記修正を含むEditor 1.0.82候補をローカルNuGetとして再生成し、`UseLocalEditor=false`／`EditorPackageVersion=1.0.82`／ローカルfeedでLoomoをrestoreし、実WPF `TextPattern`本文取得を1件合格させた。確認後は通常のローカルEditor参照へrestoreし、solution Build（警告0／エラー0）を再確認した。公開nuget.orgへの公開は未実施。
- Editor 1.0.82候補のpackage-only全体テストも同じローカルfeedで実行し、2,987件合格・7件スキップ・失敗0（合計2,994、6分48秒）を確認した。終了後は通常参照へ戻してrestore／Build（警告0／エラー0）を確認した。
- semantic tokenのRoslyn modifier判定を`IPropertySymbol.IsReadOnly`／`ITypeSymbol.IsReadOnly`まで拡張し、getter-only propertyと`readonly struct`を`readonly`として描画側へ渡す回帰を追加した。対象クラス9件合格、通常solution Build（警告0／エラー0）を確認した。
- 変更後の代表fixture旅程`Fixture_runs_the_build_gate_and_test_journey`を単独再実行し、fixtureのBuild→Testを1件合格（終了コード0）で確認した。広いC#フィルターの一括実行はテストホストの出力回収が不安定だったため、件数には算入していない。
- `editor.save`をAppの共通CommandCatalog／KeyboardDispatcherへ追加し、Editorの`VimEditorControl.Save()`へ接続した。実WPFプロセスでCanvasへUI Automationフォーカスを移し、OSの`SendInput`でC#本文へUnicodeを入力してCtrl+Sを送り、ディスク反映まで2件の実旅程を連続合格させた。対象fixtureはテスト後に元バイト列へ復元する。
- 公開`sk0ya.Editor.Controls 1.0.79`だけを別NuGetキャッシュへrestoreするclean probeでは、`Loomo.CSharp`を`net10.0-windows`として公開WPF assetへ合わせることでrestore／solution Build／test project Buildまで通過した。旧版に存在しない追加LSP／WorkspaceEdit APIは条件付き互換層で無効化し、公開版testは2,910合格・7スキップ・失敗0（Buildは警告0・エラー0）となった。公開操作は行っていない。

### 2026-09-01 最終候補受入れ追記

- EditorのTextPattern選択をVimEngineの選択へ同期し、外部UI Automationから選択した診断範囲をQuick Fixへ渡せるようにした。Editor全テストはCore 1,323件、Controls 161件が合格した。
- Editor `sk0ya.Editor.Controls 1.0.86`候補をローカルfeedへpackし、`UseLocalEditor=false`のpackage-only restore／Buildを警告0・エラー0で確認した。
- 同package-only構成の実WPFで、`FeatureService.cs`をSolution Explorerから開く→診断箇所をTextPatternで選択→`Alt+Enter`→Quick Fix候補→編集プレビューの「適用」→本文更新→`Ctrl+S`→ディスク反映を1件合格させた。
- 通常のローカルEditor参照でも、C#編集／保存とQuick Fix／preview適用／本文更新／保存を含む実WPF旅程3件を合格させた。fixtureは各テスト後に元バイト列へ復元した。
- そのうち一続きの実WPF旅程を拡張し、Solution Explorerのプロジェクト選択→Build→TestまでUI Automationで実行した。FeatureService.csのSA1101をQuick Fixで修正・保存した後、Feature.TestsのBuild成功・Test成功を同一プロセスで確認した。
- Loomo通常全体テストは2,989合格・9スキップ・失敗0（合計2,998）、solution Buildは警告0・エラー0だった。
- 公開NuGet 1.0.79は、`net10.0-windows`ターゲットと旧版互換層を使ったclean restore／Build／test（2,910合格・8スキップ・失敗0）まで通過した。ただし1.0.86候補は未公開で、1.0.79では後発Editor機能の一部が無効になる。実装者以外のレビューも未実施なので、§33.15の完了・公開判定はまだ行わない。
- CSharp DLLのRoslyn実行時依存をAppへコピーするMSBuildターゲットが、現行の`net10.0-windows`出力ではなく旧`net10.0`出力を参照していたため修正した。さらにApp側でTFMを複製せず、CSharpプロジェクトの`GetTargetPath`から実出力を解決するようにした。solutionをcleanしてBuildし、App出力へRoslyn DLL 9個がコピーされること、`CSharpSemanticTokenServiceTests` 9件が合格することを確認した。実装者以外のレビュー、公開NuGetへの反映は未完了のまま維持する。
- Quick Fixのプレビュー中に開いている対象ファイルへ外部書込みが発生した場合、開いているEditor本文だけでなくディスク本文もtransaction snapshotで照合するよう修正した。実WPFで外部追記を保持し、`this._value`へのQuick Fixを拒否する旅程を1件合格させた。既存Quick Fix→保存旅程も単独再確認で1件合格。実装者以外のレビューと公開NuGetへの反映は未完了のまま維持する。
- C# semantic token fallbackでRoslyn宣言識別子へ`declaration` modifierを付与し、参照側へ誤付与しないようにした。class／member／parameter／local／pattern／分解宣言を含むsemantic token回帰9件とsolution Build（警告0／エラー0）で確認した。実装者以外のレビューと公開NuGetへの反映は未完了のまま維持する。
- 公開Editor 1.0.79構成ではRoslyn実行時に存在しない任意DLLをAppのコピー対象へ無条件追加していたため、公開clean Buildが失敗することを検出した。任意依存を`Exists`条件付きに修正し、公開版1.0.79のclean restore／Build（警告0／エラー0）、通常構成のrestore／Build（警告0／エラー0）を再確認した。実装者以外のレビューと公開NuGetへの反映は未完了のまま維持する。
- `CSharpAssemblyBoundaryTests`を拡張し、Roslyn参照が`sk0ya.Loomo.CSharp`に存在し、App／Services／Coreの各アセンブリへ直接参照が漏れていないことを5件で確認した。専用DLL内にWPF UI型がないこと、App出力へCSharp DLLとRoslyn実行時依存が同梱されることも静的監査で確認した。実装者以外のレビューと公開NuGetへの反映は未完了のまま維持する。
- 現行コードでC#関連フィルターを再実行し、391件中387件合格・4件スキップ・失敗0（4分14秒）を確認した。スキップは環境変数で明示有効化する実DAP／実WPF旅程であり、通常のC#回帰と専用DLL境界テストは合格している。実装者以外のレビューと公開NuGetへの反映は未完了のまま維持する。
- §33.15の受入れ識別情報として、Editor候補1.0.86のSHA-256、Editor HEADとdirty状態、fixture hash、StyleCop／.NET SDK／RIDを[`docs/検証/33.15-受入れ記録-2026-09-01.md`](../検証/33.15-受入れ記録-2026-09-01.md)へ記録した。公開NuGet反映後の最終受入れと実装者以外のレビューは未完了である。
- `LOOMO_RUN_REAL_WPF=1`で、C# Solution Explorer／EditorCanvas／LSP readyの起動スモーク、デスクトップ入力によるC#編集→Ctrl+S、診断Quick Fix→preview適用→保存→Solution Build／Testをそれぞれ単独再実行し、各1件合格させた。複数実WPFテストの同時実行は引き続き避け、公開NuGet反映と実装者以外のレビューは未完了とする。
- 実WPFのコマンドパレットから`Equals／GetHashCode`生成を起動し、型選択→編集プレビュー→適用→Ctrl+S→ディスク反映までを1件合格させた。C#のFixだけでなく、コード生成のUI導線も同一プロセスで確認した。公開NuGet反映と実装者以外のレビューは未完了とする。
- `UseLocalEditor=false`／`EditorPackageVersion=1.0.86`のcandidate package-only構成で、同じコード生成の実WPF旅程を1件合格させた（restore／Buildは警告0・エラー0）。確認後に通常のローカルEditor参照へ復元し、solution Build（警告0・エラー0）も完了した。公開NuGet反映と実装者以外のレビューは未完了とする。
- C# Solution Explorerをサイドバー（エクスプローラ）からIDEペインの「実行」タブ左列へ移し、起動対象の「プロジェクト」一覧と上下2段で並べた。境目はGridSplitterでドラッグでき、ソリューション側は見出しのトグルで見出し行だけへ畳める（畳むと行はAuto、展開で直前の高さへ戻す）。C#プロジェクトが無いワークスペースでは段ごと消える。サイドバーはフォルダーツリー専任へ戻した。
- 同ビューの配色不足も直した。既定のTreeViewItemテンプレートは選択・ホバー・開閉矢印をSystemColors（青）で描くためテーマ切替に追従しなかったので、FolderTreeViewと同じ構成へ置き換えてAccent／AccentFg／Border／FgDimから取るようにし、素のButtonだった「ビルド」「テスト」もSecondaryButtonへ、文字サイズもFs*トークンへ揃えた。実WPFの回帰（配置・折りたたみ・選択行のブラシ・見出しボタンのStyle）を5件追加した。
- 移設の実機確認で、IDE／TS IDEペインのタブ中身がUI Automationツリーへ一切出ていないことを検出した。自前のTabControlテンプレートの`ContentPresenter`に`PART_SelectedContentHost`という名前が無く、WPFの`TabItemAutomationPeer`が選択中タブの中身を繋げられていなかった（`CSharpSolutionTree`等が実機で見つからない）。両テンプレートに名前を付け、実機で`CSharpSolutionTree`／`Build`／`Test`／`構成`／折りたたみトグルがUIAから引けること、トグル起動で段が畳まれること、ツリー行の選択がAccent／AccentFgで塗られることを目視とUIA操作で確認した。
- 狭い左列（220px程度）に合わせ、見出しのビルド／テストはアイコン（🔨／🧪、意味はツールチップ）にし、既定のBringIntoViewが選択のたびに横スクロールして名前の頭を切る問題もFolderTreeViewと同じ縦のみ追従へ直した。
- C#コード生成の実装を1ファイル（`CSharpCodeGeneration.cs`／2,796行・13種の生成と共有ヘルパー全部入りの静的クラス）から、責務ごとの15ファイルへ割り直した。`CSharpCodeGenerationService`は**振り分けだけ**（キャレット位置から対象型を決め、種類ごとの生成器へ渡し、返ったメンバー本文を型の末尾へ挿入する）を残し、生成本体はコンストラクター／プロパティ／値の振る舞い（Equals・ToString・Deconstruct）／interface実装／override／委譲／使用箇所からのメソッド／Dispose／フィールド／null guardの各`CSharp*Generator`へ移した。共有部品は探索（`GenerationSyntax`）・命名（`GenerationNames`）・書式（`MemberFormat`）の3つに分け、公開API（`Generate`／`GenerateNullGuards`／`GenerateJsonTypes`）と生成結果は変えていないので呼び出し側の変更はない。生成の失敗は`CSharpCodeGenerationResult.Failed`へ集約した。最大ファイルは475行。振る舞いを変えない整理のため、既存の`CSharpCodeGenerationTests` 144件に加えて全体テスト2,918合格・9スキップ・失敗0（合計2,927）、solution Buildは警告0・エラー0で確認した。
- コードレビューで挙がった§33の不具合4件を修正した。(1) 右クリックの「名前の変更…」はLSP接続済みなら他言語でも出るのに、クリックがC#専用結線（`ExecuteCSharpEditorCommand`）へ入り、`.cs`以外では黙って返る無反応項目になっていた。C#はそのままホスト結線、他言語はコントロールのLSP renameを直接呼ぶよう分けた。(2) 定義／参照／実装／型定義／宣言のC#フォールバックは全エディタへ結線されているのに拡張子を見ておらず、`.ts`でF12して結果が無いと「C# 定義検索: C#定義検索のCompilationを作成できませんでした。」というC#のエラーが他言語で出ていた。rename／prepareと同じ`.cs`ガードを共通化して5本へ入れた。(3) `Ctrl+S`（`editor.save`）はウィンドウのPreviewKeyDownで常に実行・消費していたため、ターミナル／ブラウザ／コンポーザで押すと無関係なエディタタブが保存され、そのペインへはキーが届かなかった。`canExecute`をエディタにフォーカスがあるときだけに絞り（false ならキーは消費されず内側へ通る）、コマンドパレット経由の保存は従来どおり通す。(4) `DebouncedFolderWatcher`の`Dispatcher.CurrentDispatcher`フォールバックはメッセージポンプの無いディスパッチャを新規作成しうるため、生成スレッドにディスパッチャが無い場合にフォルダー更新が永久に走らなくなる。生成スレッド優先という元の意図は保ったまま、間に`Application.Current?.Dispatcher`を挟んだ。全体テストは2,918合格・9スキップ・失敗0（合計2,927）、solution Buildは警告0・エラー0。
- 上記(3)の実機確認に使う`App_process_accepts_csharp_edit_and_save_from_the_desktop_input_path`は、前提のassert（EditorCanvasに`class FeatureService`が出ていること）で失敗する。fixtureワークスペースの復元タブが`Contracts`側のファイルになっているためで、修正をstashした状態でも同じ位置・同じメッセージで失敗することを確認済み——今回の修正とは無関係の環境要因である。
- エディタの右クリックメニューを作り直した。**最大の不具合はサブメニューが1つも開かなかったこと**——
  `VimEditorControl` の `DarkMenuItem` テンプレートは Border＋Grid＋TextBlock だけで `Popup` も
  `ItemsPresenter` も持たないのに、ホストが足した項目へ一律で適用されていた。そのため
  「リファクタリング」「C# コード生成」「Git」「Diffへ送る」「AIワークフローへ送る」
  「特定の関数にステップ イン」が全部押しても開かない状態だった。Editor 側でテンプレートへ
  サブメニュー（Popup＋ItemsPresenter＋▸）を入れ、配色を固定の Dracula から現在の `EditorTheme`
  由来へ変え（ライトテーマでも黒いメニューが出ていた）、スタイルは追加後の一段だけへ代入するのを
  やめてメニュー全体の暗黙スタイルにした（サブメニューの子項目まで同じ見た目になる）。
  ネイティブ項目の見出しは `EditorContextMenuLabels` としてホストが差し替えられるようにし、
  Loomo は日本語表を渡す（1つのメニューに「Copy Line」と「AIへ送る」が並ぶ状態を解消）。
  長いメニューはスクロールし、先頭・末尾・連続の区切り線は落とす。
- Loomo 側の並びは4つの束（①別のペインへ送る ②コードを操作する ③このファイルを扱う ④版と実行）へ
  まとめ、束ごとに区切り線を1本だけ置く`ShellWindow.AddMenuGroup`を入れた。`.cs`の右クリックで
  トップレベル19項目＋区切り線7本だったものが、13項目＋区切り線4本になる。Git・デバッグは
  サブメニューへ畳み、デバッグ項目はデバッガの管轄拡張子のときだけ出す（`.md`でも
  「ブレークポイントの条件を編集…」が出ていた）。「Fix All in Project/Solution」「Git Blame」など
  英語混じりの見出しも日本語へ揃えた。
- 「C# コード生成」サブメニューは30項目フラット・無条件表示だったが、うち**16項目は実行側が
  `HasSelection` を必須にしており**、キャレットだけの右クリックで押すと「〜を選択してください」と
  返るだけだった（§23.3「押せるのに何も起きない項目は作らない」に反する）。並びの正本を
  `CSharpEditorMenu`（WPF に依存しない純関数）へ出し、選択が要る操作は選択があるときだけ
  入れ子の「抽出・導入・インライン化」へ出す。見出しとキー表記は
  `CSharpEditorCommandCatalog` から引くのでコマンドパレット・キーバインドと綴りがズレない
  （実効キーがあればそちらを優先）。実体と食い違っていた「C# cleanup profile（プレビュー）」は
  「コードスタイルを一括適用」へ改めた（プレビューはせず直接適用する）。
- 回帰は Editor 側に右クリックメニューのテスト5件（テンプレートがサブメニューを持つ／ホスト項目と
  その子孫へ同じスタイルが効く／区切り線の整理／配色がテーマ由来／見出しの差し替え）、Loomo 側に
  `CSharpEditorMenuTests` 6件と `EditorContextMenuGroupTests` 11件を追加した。Editor は
  Core 1,323件＋Controls 166件が合格、Loomo は3,014合格・11スキップ・失敗0（合計3,025）、
  solution Build は警告0・エラー0。
- 右クリックの見直しの続きとして、**「C# サブメニューが一覧表になっている」**を直した。C# の操作は
  40 種あるが右クリックはその目録ではないので、開いてすぐ見える段は毎日使うものだけ——
  using 整理／メソッド抽出・ローカル変数の導入・ローカル変数のインライン化／コンストラクター・
  プロパティ・インターフェース実装・override 生成の 8 項目までに絞り、残りは「書き換え」「生成」
  「まとめて整える」の 3 つの入れ子へ落とした。落としたものも同じ Command ID なので、
  コマンドパレットとキーバインドからは今までどおり 1 手で届く（並びを変えただけで、
  カタログからは何も外していない）。表の段の長さは `CSharpEditorMenuTests` で上限を持たせた。
- 「移動」も1つの入口へまとめた。エディタ側は定義／実装／型定義／宣言／参照を「移動」サブメニューに
  収め、`EditorContextMenuBuildingEventArgs.NavigateMenu` でホストへ渡す。Loomo の
  「定義をPeek表示」はそこへ入れる（以前は C# サブメニューの中にあり、移動の入口が2か所に割れていた）。
  これでネイティブ項目のトップレベルは 20 行から 11 行になった。
- 折り返しの切り替えは右クリックから外した。今の見え方の設定であってキャレットに対して何かをする
  操作ではないので、`Alt+Z` / `:set wrap` だけに置く。
- 実描画で、ホストが足した区切り線だけ既定の白い線で描かれていることを検出した。メニューの中の
  `Separator` は暗黙スタイルではなく `MenuItem.SeparatorStyleKey` で引いたスタイルが容器へ
  直接代入されるためで、そのキーもメニューの Resources へ載せて直した（回帰テスト付き）。
- ネイティブ項目のうち**押しても目的を果たせない3本**を、Loomo 側で取捨した
  （`ShellWindow.EditorNativeMenu`。ホストへ渡ってくるのは組み上がった `ContextMenu` そのものなので、
  足すだけでなく外す・差し替えることもここでできる。目印は `EditorMenuLabels` の見出し定数）。
  - 「元に戻す」「やり直す」「すべて選択」は**外した**。`u` / `Ctrl+R` / `ggVG` と
    Ctrl+Z / Ctrl+Y / Ctrl+A が常に効くうえ、右クリックは「この位置に対して何をするか」を選ぶ場所で、
    履歴と全選択はそこに要らない。
  - 「この位置で使える修正」は**「Quick Fix」サブメニューへ差し替えた**。ネイティブ側は候補を
    キャンバスに描くポップアップで見せるので、実機で確かめると候補は出るのに **j/k と Enter でしか
    選べない**——右クリック（マウス）から入った操作の続きがキーボード専用で、マウスでは1件も
    適用できなかった。§32 のリファクタリングと同じく、開いたときに候補を詰めてクリックで適用する。
    候補の出どころも適用も `Alt+Enter` と同じ（ホストの Roslyn／StyleCop が先、無ければ言語サーバーへ
    `only: ["quickfix"]`。適用は `ApplyLspWorkspaceEdit` に集約されるので編集プレビューと Undo も同じ道）。
  - 「この位置の説明を表示」は**本文を出すポップアップへ差し替えた**。ネイティブ側は hover を
    ステータスバーへ**先頭1行だけ**流しており、Markdown で返すサーバーでは実測で <code>```csharp</code> という
    コードフェンスだけが出ていた（説明が1文字も読めない）。`HoverDisplayText` でフェンスと
    Markdown エスケープ（`\_value`）を外し、右クリック位置へ読み取り専用 `TextBox` のポップアップで出す。
    候補が無いときも同じ場所に「説明はありません」と出す——黙って終わると、また
    「押しても何も起きない項目」に戻る。
  - 罠: そのポップアップの Escape は**トンネル段（`PreviewKeyDown`）で取る**。WPF の `TextBox` は
    Escape を Undo として扱い、そのコマンドがポップアップの外——最後はエディタ——まで上がるため、
    説明を閉じただけで「1 change undone」が出た（実機で検出）。
  - 回帰は `EditorNativeMenuTests` 4件と `HoverDisplayTextTests` 8件。実機は CSharpIde fixture で
    右クリック→「Quick Fix」→候補2件（`Prefix reference with 'this.'` ／ `SA1101をこの行で抑制`）→
    クリック→編集プレビューまでを確認した（`Alt+Enter` と同じ候補・同じ適用経路）。
