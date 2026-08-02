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
