# Loomo エージェント能力ハーネス結果
- 実行日時: 2026-06-22 08:32:09
- モデル: qwen3-4b-cpu-int4
- スイート: workflow
- 追加プロンプト(モード): none
- 構成タグ: wfsuite-legacy
- ワークスペース: C:\Users\koya\AppData\Local\Temp\loomo-harness-1eb42bb7

## サマリ: 8/8 PASS

| タスク | 判定 | 成功率 | 所要 | 反復 | 詳細 |
|---|---|---:|---:|---:|---|
| wf-translate-en | ✅ | 1/1 | 72579ms | 0 | 英語で出力（日本語混入なし） |
| wf-summary-3 | ✅ | 1/1 | 13405ms | 0 | 箇条書き 3 行（- 始まり） |
| wf-bullets | ✅ | 1/1 | 22311ms | 0 | 箇条書き 5 行（- 始まり） |
| wf-keywords | ✅ | 1/1 | 9295ms | 0 | カンマ区切り（区切り 7 個） |
| wf-comment-code | ✅ | 1/1 | 26473ms | 1 | コメント付きコードを出力 |
| wf-regex | ✅ | 1/1 | 31355ms | 0 | 正規表現らしさ 5/6 |
| wf-table | ✅ | 1/1 | 27694ms | 1 | Markdown 表を出力 |
| wf-polite | ✅ | 1/1 | 3571ms | 0 | 丁寧な日本語へ |

## [wf-translate-en] 次の日本語を自然な英語に翻訳してください。訳文だけを出力し、説明は付けないでください。

対象:
私たちは新しい機能を来週リリースする予定です。
- 判定: ✅ PASS — 英語で出力（日本語混入なし）
- 所要: 72579ms（load 12340 / prefill 58119 / decode 1376）
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
- 所要: 13405ms（load 0 / prefill 6493 / decode 6910）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomoはローカルで動作するAIエージェント - ターミナルやエディタ、フォルダツリー操作可能 - 小型モデルでも動作する4つのツールだけだけ

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
- 所要: 22311ms（load 0 / prefill 6020 / decode 16289）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomo はローカルで動作する AI エージェントです。   - テーラー・エディタ・フォルダツリーを操作できます。   - 小型のモデルでも動作するように、ツールは4つに絞られています。   - 会話履歴はコンテキスト窓に収まらせるよう自動で切り詰められます。   - 起動時にモデルを暖機し、初回応答を速くしています。。

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
- 所要: 9295ms（load 0 / prefill 4887 / decode 4406）
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
- 所要: 26473ms（load 0 / prefill 10839 / decode 15596）
- AI呼び出し回数(=ツール反復): 1
- ツール呼び出し:
    - `write_file {"path":"add_function_comment.py","content":"# \u3053\u306E\u95A2\u6570\u306F2\u3064\u306E\u6570\u5024a\u3068b\u3092\u52A0\u7B97\u3059\u308B\u3002\ndef add(a, b):\n    return a \u002B b"}`
- ツール結果:
    - [ok] 書き込み完了: C:\Users\koya\AppData\Local\Temp\loomo-harness-1eb42bb7\add_function_comment.py（3行 / 85 bytes）
- 最終回答: 以下は、各処理の意図を説明する簡潔なコメントが追加されたコードです：  ```python # 2つの数値aとbを足し算して返します。 def add(a, b):     return a + b ``````

- 実ファイル状態:
    - add_function_comment.py: `# この関数は2つの数値aとbを加算する。\ndef add(a, b):\n    return a + b`
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
- 所要: 31355ms（load 0 / prefill 1942 / decode 29411）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 正規表現: ^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$  説明: - ^: 文字列の開始を示します。 - [a-zA-Z0-9._%+-]+: ユーザー名部分を表します。大文字小文字の文字、数字、ドット、アンダーライン、パーセント、プラス、ハイフンが許可されています。 - @: メールアドレスのアットマークを表します。 - [a-zA-Z0-9.-]+: ドメイン名の前の部分を表します。大文字小文字の文字、数字、ハイフン、ドットが許可されています。 - \.: ドメイン名の区切りを表します。 - [a-zA-Z]{2,}: タイムゾーンやサブドメインを表します。大文字の文字が2文字以上含まれています。。

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
- 所要: 27694ms（load 0 / prefill 10208 / decode 17481）
- AI呼び出し回数(=ツール反復): 1
- ツール呼び出し:
    - `write_file {"path":"output.md","content":"| \u98DF\u54C1 | \u4FA1\u683C |\n|------|------|\n| \u308A\u3093\u3054 | 120\u5186 |\n| \u30D0\u30CA\u30CA | 80\u5186 |\n| \u3076\u3069\u3046 | 300\u5186 |"}`
- ツール結果:
    - [ok] 書き込み完了: C:\Users\koya\AppData\Local\Temp\loomo-harness-1eb42bb7\output.md（5行 / 103 bytes）
- 最終回答: 以下は、前のステップの出力のMarkdown表です：  | 商品 | 価格 | |------|------| | りんご | 120円 | | バナナ | 80円 | | ぶどう | 300円 | |

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - output.md: `| 食品 | 価格 |\n|------|------|\n| りんご | 120円 |\n| バナナ | 80円 |\n| ぶどう | 300円 |`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-polite] 次の文章を、丁寧で読みやすいビジネス文面に書き換えてください。意味は変えず、書き換えた文章だけを出力してください。

対象:
明日までにこれ送って。
- 判定: ✅ PASS — 丁寧な日本語へ
- 所要: 3571ms（load 0 / prefill 2250 / decode 1320）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 明日までにご提出いただけますか。。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

