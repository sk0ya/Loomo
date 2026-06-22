# Loomo エージェント能力ハーネス結果
- 実行日時: 2026-06-22 08:27:13
- モデル: qwen3-4b-cpu-int4
- スイート: workflow
- 追加プロンプト(モード): none
- 構成タグ: wfsuite-none
- ワークスペース: C:\Users\koya\AppData\Local\Temp\loomo-harness-e43f09c7

## サマリ: 8/8 PASS

| タスク | 判定 | 成功率 | 所要 | 反復 | 詳細 |
|---|---|---:|---:|---:|---|
| wf-translate-en | ✅ | 1/1 | 75822ms | 0 | 英語で出力（日本語混入なし） |
| wf-summary-3 | ✅ | 1/1 | 11920ms | 0 | 箇条書き 3 行（- 始まり） |
| wf-bullets | ✅ | 1/1 | 21463ms | 0 | 箇条書き 5 行（- 始まり） |
| wf-keywords | ✅ | 1/1 | 8895ms | 0 | カンマ区切り（区切り 7 個） |
| wf-comment-code | ✅ | 1/1 | 25801ms | 1 | コメント付きコードを出力 |
| wf-regex | ✅ | 1/1 | 31630ms | 0 | 正規表現らしさ 5/6 |
| wf-table | ✅ | 1/1 | 9141ms | 0 | Markdown 表を出力 |
| wf-polite | ✅ | 1/1 | 3978ms | 0 | 丁寧な日本語へ |

## [wf-translate-en] 次の日本語を自然な英語に翻訳してください。訳文だけを出力し、説明は付けないでください。

対象:
私たちは新しい機能を来週リリースする予定です。
- 判定: ✅ PASS — 英語で出力（日本語混入なし）
- 所要: 75822ms（load 16510 / prefill 57182 / decode 1375）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: We plan to release a new feature next week..

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-summary-3] 次の文章を日本語で3行に要約してください。各行は40字以内、要点のみを箇条書き（- 始まり）で。

対象:
Loomo はローカルで動く AI エージェントです。ターミナル・エディタ・フォルダツリーを操作できます。小型のモデルでも動くよう、ツールは4つに絞っています。会話履歴はコンテキスト窓に収まるよう自動で切り詰めます。起動時にモデルを暖機して初回応答を速くしています。
- 判定: ✅ PASS — 箇条書き 3 行（- 始まり）
- 所要: 11920ms（load 0 / prefill 6324 / decode 5593）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomoはローカルで動作するAIエージェント   - ターミナルやファイル操作が可能   - 小型モデルでも動作する4つのツールール

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-bullets] 次の文章の要点を、重複を除いて箇条書き（- 始まり）に整理してください。

対象:
Loomo はローカルで動く AI エージェントです。ターミナル・エディタ・フォルダツリーを操作できます。小型のモデルでも動くよう、ツールは4つに絞っています。会話履歴はコンテキスト窓に収まるよう自動で切り詰めます。起動時にモデルを暖機して初回応答を速くしています。
- 判定: ✅ PASS — 箇条書き 5 行（- 始まり）
- 所要: 21463ms（load 0 / prefill 5884 / decode 15576）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomo はローカルで動作する AI エージェントです。   - テーラル、エディタ、フォルダツリーを操作できます。   - 小型モデルでも動作するように、ツールは4つに絞られています。   - 会話履歴はコンテキスト窓に収まることを目的として自動で切り詰めています。   - 起動時にモデルを暖機し、初回応答を速くしています。。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-keywords] 次の文章から重要なキーワードを5〜8個抽出し、カンマ区切りで1行で出力してください。

対象:
ONNX Runtime GenAI を使い、Phi-4-mini や Qwen3 のような小型モデルを CPU 上でローカル実行する。ツール呼び出しはモデルが本文に JSON で書き、パーサが復元する。KV プレフィックスの再利用で prefill を節約する。
- 判定: ✅ PASS — カンマ区切り（区切り 7 個）
- 所要: 8895ms（load 0 / prefill 4713 / decode 4180）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: ONNX Runtime, GenAI, Phi-4-mini, Qwen3, CPU, ローカル実行, JSON, パーサ

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-comment-code] 次のコードに、各処理の意図を説明する簡潔なコメントを付けて出力してください。コード自体のロジックは変えないでください。

対象:
def add(a, b):
    return a + b

- 判定: ✅ PASS — コメント付きコードを出力
- 所要: 25801ms（load 0 / prefill 10424 / decode 15337）
- AI呼び出し回数(=ツール反復): 1
- ツール呼び出し:
    - `write_file {"path":"add_function.py","content":"# \u3053\u306E\u95A2\u6570\u306F2\u3064\u306E\u6570\u5024\u3092\u52A0\u7B97\u3057\u307E\u3059\u3002\ndef add(a, b):\n    return a \u002B b"}`
- ツール結果:
    - [ok] 書き込み完了: C:\Users\koya\AppData\Local\Temp\loomo-harness-e43f09c7\add_function.py（3行 / 83 bytes）
- 最終回答: 次のコードに、各処理の意図を説明する簡潔なコメントが追加されました。コードのロジックは変更していません。  ```python # 2つの数値を足し算する関数 def add(a, b):     return a + b ``````

- 実ファイル状態:
    - add_function.py: `# この関数は2つの数値を加算します。\ndef add(a, b):\n    return a + b`
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-regex] やりたいことに合う正規表現を1つ作り、各部分の意味を簡単に説明してください。

やりたいこと:
メールアドレスにマッチさせたい
- 判定: ✅ PASS — 正規表現らしさ 5/6
- 所要: 31630ms（load 0 / prefill 1898 / decode 29730）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 正規表現: ^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$  説明: - ^: 文字列の開始を指定します。 - [a-zA-Z0-9._%+-]+: ユーザー名部分を指定します。大文字小文字両方の文字、数字、点、アンダースコア、パーセン号、プラス記号、ハイフンが含まれます。 - @: メールアドレスのホスト名を指定します。 - [a-zA-Z0-9.-]+: ドメイン名の部分を指定します。大文字小文字両方の文字、数字、点、ハイフンが含まれます。 - \.: ドットを指定します。 - [a-zA-Z]{2,}: ドメインの拡張子を指定します。2文字以上の大文字の文字が含まれます。 - $: 文字列の終わりを指定します。。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-table] 前のステップの出力を、分かりやすい Markdown の表に整理してください。

りんご 120円
バナナ 80円
ぶどう 300円
- 判定: ✅ PASS — Markdown 表を出力
- 所要: 9141ms（load 0 / prefill 2114 / decode 7025）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: | おもてなし | 価格 | |-----------|------| | りんご     | 120円 | | バナナ     | 80円  | | ぶどう     | 300円 | |

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-polite] 次の文章を、丁寧で読みやすいビジネス文面に書き換えてください。意味は変えず、書き換えた文章だけを出力してください。

対象:
明日までにこれ送って。
- 判定: ✅ PASS — 丁寧な日本語へ
- 所要: 3978ms（load 0 / prefill 2247 / decode 1729）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 明日までにこのファイルを送っていただけますか。。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

