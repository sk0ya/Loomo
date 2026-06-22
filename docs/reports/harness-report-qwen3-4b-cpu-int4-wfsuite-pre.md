# Loomo エージェント能力ハーネス結果
- 実行日時: 2026-06-22 08:23:50
- モデル: qwen3-4b-cpu-int4
- スイート: workflow
- 追加プロンプト(モード): workflow
- 構成タグ: wfsuite-pre
- ワークスペース: C:\Users\koya\AppData\Local\Temp\loomo-harness-38609158

## サマリ: 8/8 PASS

| タスク | 判定 | 成功率 | 所要 | 反復 | 詳細 |
|---|---|---:|---:|---:|---|
| wf-translate-en | ✅ | 1/1 | 86937ms | 0 | 英語で出力（日本語混入なし） |
| wf-summary-3 | ✅ | 1/1 | 14003ms | 0 | 箇条書き 3 行（- 始まり） |
| wf-bullets | ✅ | 1/1 | 20893ms | 0 | 箇条書き 5 行（- 始まり） |
| wf-keywords | ✅ | 1/1 | 10307ms | 0 | カンマ区切り（区切り 8 個） |
| wf-comment-code | ✅ | 1/1 | 7552ms | 0 | コメント付きコードを出力 |
| wf-regex | ✅ | 1/1 | 34867ms | 0 | 正規表現らしさ 5/6 |
| wf-table | ✅ | 1/1 | 7377ms | 0 | Markdown 表を出力 |
| wf-polite | ✅ | 1/1 | 4015ms | 0 | 丁寧な日本語へ |

## [wf-translate-en] 次の日本語を自然な英語に翻訳してください。訳文だけを出力し、説明は付けないでください。

対象:
私たちは新しい機能を来週リリースする予定です。
- 判定: ✅ PASS — 英語で出力（日本語混入なし）
- 所要: 86937ms（load 18170 / prefill 65881 / decode 1337）
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
- 所要: 14003ms（load 0 / prefill 6353 / decode 7645）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomoはローカルで動作するAIエージェント - テーラー・エディタ・フォルダツリーを操作可能 - 小型モデルでも動作し、ツールは4つに絞られているられている

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
- 所要: 20893ms（load 0 / prefill 5890 / decode 15000）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomo はローカルで動く AI エージェントです。 - ターミナル・エディタ・フォルダツリーを操作できます。 - 小型のモデルでも動くよう、ツールは4つに絞っています。 - 会話履歴はコンテキスト窓に収まるよう自動で切り詰めます。 - 起動時にモデルを暖機して初回応答を速くしています。。

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
- 判定: ✅ PASS — カンマ区切り（区切り 8 個）
- 所要: 10307ms（load 0 / prefill 4761 / decode 5544）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: ONNX Runtime GenAI, Phi-4-mini, Qwen3, CPU, ローカル実行, JSON, パーサ, KV プレフィックス, prefillfill

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
- 所要: 7552ms（load 0 / prefill 2769 / decode 4781）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: ```python # この関数は2つの数値aとbを加算して返します def add(a, b):     return a + b ``````

- 実ファイル状態:
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
- 所要: 34867ms（load 0 / prefill 1922 / decode 32943）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 正規表現: ^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$  説明: - ^: 文字列の開始を示します。 - [a-zA-Z0-9._%+-]+: ユーザー名部分を表します。大文字小文字両方の文字、数字、ドット、アンダーライン、パーセント、プラス、ハイフンが許可されています。 - @: メールアドレスのアットマークを表します。 - [a-zA-Z0-9.-]+: ドメイン名の前の部分を表します。大文字小文字両方の文字、数字、ハイフン、ドットが許可されています。 - \.: ドットを表します。これはドメイン名の区切りです。 - [a-zA-Z]{2,}: ドメインの拡張子を表します。英文字2文字以上が許可されています。 - $: 文字列の終わりを示します。。

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
- 所要: 7377ms（load 0 / prefill 2192 / decode 5183）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: | みかん | 120円 | |------|--------| | バナナ | 80円 | | ぶどう | 300円 | |

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
- 所要: 4015ms（load 0 / prefill 2267 / decode 1746）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 明日までにこの件を送っていただけますか。。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

