# Loomo エージェント能力ハーネス結果
- 実行日時: 2026-06-24 21:16:42
- モデル: qwen3-4b-q4_k_m
- スイート: workflow
- 追加プロンプト(モード): workflow
- 構成タグ: system-prompt-trim-workflow
- ワークスペース: C:\Users\koya\AppData\Local\Temp\loomo-harness-5c548e45

## サマリ: 10/10 PASS

| タスク | 判定 | 成功率 | 所要 | 反復 | 詳細 |
|---|---|---:|---:|---:|---|
| wf-translate-en | ✅ | 1/1 | 37207ms | 0 | 英語で出力（日本語混入なし） |
| wf-translate-prev | ✅ | 1/1 | 12671ms | 0 | 英語で出力（日本語混入なし） |
| wf-translate-table | ✅ | 1/1 | 17513ms | 0 | 英語で出力（日本語混入なし） |
| wf-summary-3 | ✅ | 1/1 | 18696ms | 0 | 箇条書き 3 行（- 始まり） |
| wf-bullets | ✅ | 1/1 | 19336ms | 0 | 箇条書き 5 行（- 始まり） |
| wf-keywords | ✅ | 1/1 | 9534ms | 0 | カンマ区切り（区切り 8 個） |
| wf-comment-code | ✅ | 1/1 | 5256ms | 0 | コメント付きコードを出力 |
| wf-regex | ✅ | 1/1 | 30626ms | 0 | 正規表現らしさ 5/6 |
| wf-table | ✅ | 1/1 | 8669ms | 0 | Markdown 表を出力 |
| wf-polite | ✅ | 1/1 | 4389ms | 0 | 丁寧な日本語へ |

## [wf-translate-en] Translate the following Japanese text into natural English. Output only the English translation, nothing else.

対象:
私たちは新しい機能を来週リリースする予定です。
- 判定: ✅ PASS — 英語で出力（日本語混入なし）
- 所要: 37207ms（load 2724 / prefill 32812 / decode 1577）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: The target: We plan to release a new feature next week.

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-translate-prev] Translate the previous step's output into natural English. Output only the English translation, nothing else.

Loomo はローカルで動く AI エージェントです。ターミナル・エディタ・フォルダツリーを操作できます。小型のモデルでも動くよう、ツールは4つに絞っています。会話履歴はコンテキスト窓に収まるよう自動で切り詰めます。起動時にモデルを暖機して初回応答を速くしています。
- 判定: ✅ PASS — 英語で出力（日本語混入なし）
- 所要: 12671ms（load 0 / prefill 4741 / decode 7924）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: Loomo is a local AI agent. It can operate terminal, editor, and folder tree. The tools are narrowed down to four because of the small model. The conversation history is automatically trimmed to fit within the context window. It warms up the model upon startup to speed up the first response.

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

## [wf-translate-table] Translate the following Japanese text into natural English. Output only the English translation, nothing else.

対象:
| カテゴリ | 説明 |
|---------|------|
| Loomo | Windows専用の開発ワークスペースで、エディタ・ブラウザ・ターミナルを一室に織り込む。 |
| ステージモード | 1つのペインを全面に配置し、残りのペインを右端の「袖」で表示する。 |
| ビルド | .NET 9 SDKが必要で、`dotnet build`や`dotnet run`などのコマンドを実行します。|
- 判定: ✅ PASS — 英語で出力（日本語混入なし）
- 所要: 17513ms（load 0 / prefill 5826 / decode 11682）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: Target: | Category | Description | |---------|------| | Loomo | A Windows-specific development workspace that integrates an editor, browser, and terminal into one room. | | Stage Mode | Places one pane fully across the screen and displays the remaining panes in the right-side "sleeve." | | Build | Requires the .NET 9 SDK, and executes commands such as `dotnet build` or `dotnet run`.|

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
- 所要: 18696ms（load 0 / prefill 5565 / decode 13126）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomoはローカルで動くAIエージェントで、ターミナル・エディタ・フォルダツリーを操作できます。 - 小型モデルでも動作するよう、ツールは4つに絞られています。 - 会話履歴はコンテキスト窓に収まるよう自動で切り詰められ、起動時にモデルを暖機して初回応答を速くしています。

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
- 所要: 19336ms（load 0 / prefill 5233 / decode 14098）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: - Loomo はローカルで動く AI エージェントです。 - ターミナル・エディタ・フォルダツリーを操作できます。 - 小型のモデルでも動くよう、ツールは4つに絞っています。 - 会話履歴はコンテキスト窓に収まるよう自動で切り詰めます。 - 起動時にモデルを暖機して初回応答を速くしています。

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
- 所要: 9534ms（load 0 / prefill 4277 / decode 5252）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: ONNX Runtime GenAI, Phi-4-mini, Qwen3, CPU, ローカル実行, JSON, パーサ, KV プレフィックス, prefill

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
- 所要: 5256ms（load 0 / prefill 2650 / decode 2601）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: def add(a, b):     # Adds two numbers and returns the result     return a + b

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
- 所要: 30626ms（load 0 / prefill 2437 / decode 28185）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 正規表現: `\b[\w.-]+@[\w.-]+\.\w{2,4}\b`  説明: - `\b`：単語境界を表し、メールアドレスの前後を境界としています。 - `[\w.-]+`：メールアドレスのユーザー部分を表します。これは文字、数字、点、下線、ハイフンが含まれる文字列です。 - `@`：メールアドレスの "@" 符号を表します。 - `[\w.-]+`：ドメイン名の部分を表します。これは文字、数字、点、下線、ハイフンが含まれる文字列です。 - `\.`：ドットを表し、ドメインの一部です。 - `\w{2,4}`：TLD（トップレベルドメイン）を表します。これは2文字以上4文字以下の文字から成る文字列です。 - `\b`：単語境界を表し、メールアドレスの末尾を境界としています。

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
- 所要: 8669ms（load 0 / prefill 2634 / decode 6030）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: | 商品 | 価格（円） | |------|------------| | りんご | 120        | | バナナ | 80         | | ぶどう | 300        |

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
- 所要: 4389ms（load 0 / prefill 2948 / decode 1436）
- AI呼び出し回数(=ツール反復): 0
- 最終回答: 明日までにこの物を送ってください。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

