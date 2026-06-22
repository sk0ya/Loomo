# Qwen3-4B：llama.cpp(GGUF) vs ONNX Runtime GenAI 性能比較

同一の Qwen3-4B を **3 構成**で、速度と正確さの両面から比較した。バックエンドは modelPath で振り分かる
（`.gguf` → llama.cpp / `genai_config.json` を持つフォルダ → ONNX）。プロンプト・ツール定義・ツール呼び出し
記法・履歴トリム・反復暴走ガードは全構成で共通（Loomo 自前の Qwen3 ChatML フォーマッタ）なので、純粋に
**重みの量子化 × 推論ランタイム**の差を測れる。

- 機材: CPU 実行（`GpuLayerCount=0`／ONNX も CPU int4）。本マシン1台・同一セッション・**逐次実行**（CPU 競合で
  速度がぶれないよう3構成を直列に走らせた）。
- 計測日: 2026-06-22。
- 正確さ: `AgentCapabilityHarness`（25 エージェントタスク・地上真実オラクル・R=1）。
- 速度: `LocalEngineSpeedBench`（固定プロンプト in=1054 tok を直接生成・出力予算 300 tok）＋ハーネスの自己計測集計。

| 構成 | フォルダ/ファイル | サイズ |
|---|---|---|
| ONNX int4 | `qwen3-4b-cpu-int4/`（ORT-GenAI） | ~4.1 GB |
| GGUF Q4_K_M | `qwen3-4b-q4_k_m/…Q4_K_M.gguf`（llama.cpp） | 2.50 GB |
| GGUF Q5_K_M | `qwen3-4b-q5_k_m/…Q5_K_M.gguf`（llama.cpp） | 2.89 GB |

---

## 速度

### 素の throughput（マイクロベンチ・in=1054 tok を1回生成）

| 構成 | モデルロード(初回) | prefill tok/s（cold＝再利用なし） | decode tok/s |
|---|---:|---:|---:|
| ONNX int4   | 17.1 s | 24.6 | **7.8** |
| GGUF Q4_K_M | **6.9 s** | **29.7** | **8.0** |
| GGUF Q5_K_M | 5.9 s | 14.4 | 7.1 |

- **decode（ユーザー体感の文字が出る速さ）は ONNX int4 ≒ Q4_K_M（約 7.8–8.0 tok/s）でほぼ互角**。
  Q5_K_M は約 9% 遅い（7.1）。
- **prefill（プロンプト評価）は Q4_K_M が最速（29.7）で ONNX int4（24.6）を上回る**。Q5_K_M は重みが大きい分
  prefill が約 2 倍重い（14.4）。
- **モデルロードは GGUF が圧倒的に速い（~6–7 s vs ONNX 17 s）**。ファイルが小さく mmap が効くため。
- KV プレフィックス再利用は両エンジンで実装済み。温まった2回目は安定プレフィックス（system+tools の ~1054 tok）の
  prefill が ~120 ms に落ちる（＝再利用で末尾1トークンのみ再評価）。実運用では novel な差分だけを上記 prefill 速度で払う。

### 実エージェント実行（ハーネス25タスク・KV再利用あり・所要合計）

| 構成 | 壁時計(25タスク) | prefill 合計 | decode 合計 | AI反復回数 |
|---|---:|---:|---:|---:|
| ONNX int4   | **9.5 分** | 268.5 s | 276.7 s | 39 |
| GGUF Q4_K_M | 10.0 分 | 316.0 s | 268.4 s | 44 |
| GGUF Q5_K_M | 14.3 分 | 517.4 s | 330.9 s | 42 |

- ONNX と Q4 は実運用でも僅差（Q4 は反復が 44 と多めだったぶん壁時計でやや負けた／decode 合計は Q4 が最小）。
- **Q5 は prefill が重く、25タスクで明確に最遅（+50% 弱）**。

## 正確さ（PASS / 25・R=1）

| 構成 | PASS |
|---|---:|
| ONNX int4   | **21 / 25** |
| GGUF Q4_K_M | 20 / 25 |
| GGUF Q5_K_M | 20 / 25 |

16 タスクは3構成とも ✅。差が出たタスクのみ:

| タスク | ONNX | Q4 | Q5 |
|---|:--:|:--:|:--:|
| search-bug | ✅ | ✅ | ❌ |
| append-file | ✅ | ✅ | ❌ |
| multi-file-bump | ✅ | ❌ | ✅ |
| copy-file | ✅ | ❌ | ✅ |
| max-number | ❌ | ✅ | ❌ |
| count-files | ❌ | ❌ | ✅ |
| find-text | ❌ | ❌ | ❌ |
| replace-all | ❌ | ❌ | ✅ |
| insert-json-key | ✅ | ✅ | ❌ |

- **3 構成の PASS 差（21 vs 20 vs 20）は R=1・温度0.7サンプリングのばらつき範囲内**で、量子化間の優劣は判定できない
  （失敗タスクの内訳もばらばらで、量子化精度に相関した一貫した劣化は見られない）。確度の高い精度比較には R≥3 が要る。
- **`find-text` は3構成とも失敗**＝量子化やランタイムではなくタスク/プロンプト側の課題（既知の難所）。

## 総括・推奨

- **decode 速度は ONNX int4 と GGUF Q4_K_M がほぼ同等**。llama.cpp に替えても「遅くなる」ことはなく、**Q4_K_M は
  prefill とモデルロードでむしろ速い**。正確さも R=1 では実質同点。
- **GGUF Q4_K_M を推奨**。理由: (1) decode は ONNX と互角・prefill とロードは上、(2) 精度は同点圏内、
  (3) GGUF はエコシステムが広くモデル入手が容易、(4) ファイルが小さい（2.5 GB）。
- **Q5_K_M は CPU では割に合わない**。prefill ~2倍・decode ~9%遅で、本ワークロードでは精度の上積みも無し（20/25）。
  精度を取りに行くなら量子化を上げるより R を増やした評価かモデルサイズ/プロンプト側で。
- 実装は `ILocalInferenceEngine` の継ぎ目に `LlamaCppEngine` を足し、`LocalInferenceRouter` が modelPath で
  振り分ける形（設定UIや新フィールドの追加なし。`.gguf` を選ぶだけで llama.cpp になる）。

### 注意・限界
- R=1・CPU・本マシン1台限定。サンプリング非決定なので精度は試行ごとに揺れる。
- 速度の代表値は「warm（KV再利用後）の decode」と「cold の prefill」。warm の prefill 値（~8700 tok/s）は
  末尾1トークン再評価の見かけ値で throughput ではない。
- ウォームアップ（起動時の KV 事前 prefill）は現状 ONNX 専用。GGUF は初回ターンでロード＋prefill する。
