# Loomo エージェント能力ハーネス結果
- 実行日時: 2026-06-11 09:34:37
- モデル: qwen3-4b-cpu-int4
- 構成タグ: utf8fix2-findtext
- ワークスペース: C:\Users\koya\AppData\Local\Temp\loomo-harness-9e27cbe0

- 各タスク試行回数: 3

## サマリ: 全試行PASS 1/1（1回以上PASS 1/1）

| タスク | 判定 | 成功率 | 所要 | 反復 | 詳細 |
|---|---|---:|---:|---:|---|
| find-text | ✅ | 3/3 | 20251ms | 2 | util.txt のみ特定・ファイル不変 |

## [find-text] 「gamma」という単語を含むファイルはどれ？ファイル名を教えて。
- 判定: ✅ PASS — util.txt のみ特定・ファイル不変
- 所要: 20251ms（load 0 / prefill 11955 / decode 7840）
- AI呼び出し回数(=ツール反復): 2
- ツール呼び出し:
    - `run_powershell {"command":"rg --files \u0022gamma\u0022 ."}`
    - `run_powershell {"command":"rg \u0022gamma\u0022 ."}`
- ツール結果:
    - [ERR] exit_code: 1 cwd: C:\Users\koya\AppData\Local\Temp\loomo-harness-9e27cbe0 --- output --- rg: gamma: 指定されたファイルが見つかりません。 (os error 2) .\todo.md .\src\util.txt .\README.md .\numbers.txt .\config.json .\app.py  ヒント: rg の --files は検索パ…
    - [ok] exit_code: 0 cwd: C:\Users\koya\AppData\Local\Temp\loomo-harness-9e27cbe0 --- output --- .\src\util.txt:gamma 
- 最終回答: gammaという単語を含むファイルは、src\util.txtです。。

- 実ファイル状態:
    - app.py: `def add(a, b):\n    return a - b   # bug: should be a + b\n\nprint(add(2, 3))\n`
    - config.json: `{\n  "name": "loomo",\n  "version": "1.2.3"\n}\n`
    - numbers.txt: `3\n7\n5\n`
    - README.md: `# Sample Project\n\nThis is a demo workspace for Loomo.\nVersion: 1.2.3\n`
    - src/util.txt: `alpha\nbeta\ngamma\n`
    - todo.md: `TODO: write docs\nTODO: add tests\nDone: setup project\n`

