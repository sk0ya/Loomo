---
marp: true
theme: default
paginate: true
---

# Marp サンプル

Loomo の Markdown プレビューが marp-core で描画します。

- フロントマターに `marp: true` があると自動でスライド表示
- 既定は全スライドを縦並びで一覧（スクロール）
- ヘッダの発表トグルで「1枚ずつ」表示に切替（`←` `→` / `Space` 送り・`f` 全画面）
- `theme:` `paginate:` などの Marp ディレクティブが効く

---

## スライド分割

`---`（水平線）が次のスライドへの区切りです。

```csharp
Console.WriteLine("コードブロックもそのまま");
```

---

## 非 marp は通常表示

フロントマターに `marp: true` が**無い**普通の Markdown は、発表トグルに関わらず
常に通常のドキュメント表示（縦スクロール）になります。スライドになるのは marp 文書だけです。
