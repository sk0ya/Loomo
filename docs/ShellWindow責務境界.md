# ShellWindow 責務境界

## 目的

`ShellWindow` は WPF イベントを受け取り、機能別の Coordinator／Service を呼び出し、結果を View に反映する境界とする。
判定、状態遷移、変換、検索、永続化用スナップショット生成は `ShellWindow` の外へ置く。

## 抽出済みの責務

| コンポーネント | `ShellWindow` 外で担当する処理 |
|---|---|
| `PaneLayoutCoordinator` | 分割ツリーの移動・表示・正規化判断 |
| `StageModeCoordinator` | ステージ開始・終了時の状態遷移 |
| `SpanLayoutPlanner` | 複数モニター配置の矩形計算 |
| `WorkspaceSessionCoordinator` | タブとワークスペース状態の相互変換 |
| `CommandPaletteService` | モード解析と候補フィルター |
| `PaletteSearchCoordinator` | 検索のキャンセル、デバウンス、ファイル・Grep・LSP横断検索 |
| `EditorSupportNavigationService` | EditorSupportの戻る・進む履歴 |
| `CodeEditorSupportAnalysis` | LSP結果の解析、アウトライン・参照情報の変換 |
| `ShellAppearanceCoordinator` | エディタとターミナルへの外観適用 |
| `TrailLogic` | 軌跡の分類、比較、ジャンプ可否 |
| `TrailBarController` | 軌跡バーのスクロール、ポップアップ、選択表示 |

## ShellWindow に残す処理

- XAML から呼ばれるイベントハンドラー
- WPF コントロールの表示、選択、フォーカス、サイズの更新
- WebView2、エディタ、ターミナルなど View 実体の生成と破棄の入口
- Coordinator／Serviceへ渡す現在のView状態の収集
- Coordinator／Serviceが返した判断結果のViewへの適用

## 変更時の判断基準

次の処理を追加する場合は `ShellWindow` ではなく、対応する Coordinator／Service または純粋関数へ置く。

- WPF型を引数に必要としない条件分岐
- 同じ入力に対して同じ結果を返す計算や変換
- 複数イベントにまたがる機能状態
- キャンセル、デバウンス、重複排除
- 保存モデルとViewモデルの相互変換

`ShellWindow` 内のコメントは、View固有の制約やイベント順序の注意に限定する。機能全体の責務説明はこの文書と各Coordinatorの型コメントを正とする。
