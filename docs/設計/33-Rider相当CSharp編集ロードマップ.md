# §33 Rider相当のC#編集ロードマップ

> 作成日: 2026-08-30  
> 状態: 計画  
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
| C#構文表示 | Editor独自の字句解析。Roslyn構文木ではない | 部分実装 |
| C#プロジェクト理解 | `.sln`／`.csproj`の限定的な探索。完全なMSBuild評価ではない | 最重要の不足 |
| 補完適用 | `textEdit`等は対応。`additionalTextEdits`等は未接続 | Rider品質未達 |
| Quick Fix | ProblemsからCode Actionを呼べる | Alt+Enter相当・Fix all不足 |
| リファクタリング | rename、Roslyn code action、独自Change Signature | 範囲と実機検証が不足 |
| Code generation | 専用のC#生成機能は限定的 | 不足 |
| StyleCop | プロジェクトAnalyzerの診断をIDEへ統合する経路が未完成 | 本ロードマップで追加 |
| Test／Build／Debug | 基本実行とDAPはある | IDE統合は部分実装 |

既存の補完・Problems・LSP状態・キーボードの体感課題は§31、リファクタリングの未消化項目は§32を引き継ぐ。
§32の実機確認（抽出、ファイル作成、複数ファイルUndo）は、このロードマップでも完了扱いにしない。

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

- Solution Explorerから対象を選び、同じconfigurationでBuild／Run／Test／Debugできる。
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
