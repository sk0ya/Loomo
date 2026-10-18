using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>無題バッファの言語の推定（§24.28）。確かな手がかりはそれだけで決め、弱い・競り合う一致では決めない。</summary>
public class UntitledLanguageDetectorTests
{
    [Theory]
    [InlineData("{\n  \"name\": \"loomo\",\n  \"version\": 1\n}", "JSON")]
    [InlineData("[1, 2, 3]", "JSON")]
    [InlineData("{\n  \"name\": \"書きかけ\",", "JSON")]
    [InlineData("<?xml version=\"1.0\"?>\n<root/>", "XML")]
    [InlineData("<!DOCTYPE html>\n<html></html>", "XML")]
    [InlineData("#!/usr/bin/env python3\nprint(1)", "Python")]
    [InlineData("#!/bin/bash\necho hi", "Shell")]
    [InlineData("#!/usr/bin/env node\nconsole.log(1)", "JavaScript")]
    [InlineData("@echo off\nset X=1", "Batch")]
    [InlineData("---\ntitle: x\n---\n\n# 本文", "Markdown")]
    [InlineData("---\nname: build\non: push\n", "YAML")]
    public void 確かな手がかりで決める(string text, string language) => Assert.Equal(language, UntitledLanguageDetector.Detect(text));

    [Theory]
    [InlineData("using System;\nusing System.Linq;\n\nnamespace Foo;\n\npublic class Bar\n{\n}", "C#")]
    [InlineData("import os\nfrom pathlib import Path\n\ndef main():\n    print(Path.cwd())", "Python")]
    [InlineData("const x = 1;\nfunction add(a, b) {\n  return a + b;\n}\nconsole.log(add(x, 2));", "JavaScript")]
    [InlineData("interface User {\n  name: string;\n}\nconst u: User = { name: 'a' };\nexport const f = (x: number) => x;", "TypeScript")]
    [InlineData("param($Path)\nGet-ChildItem $Path | Where-Object { $_.Length -gt 0 }\nWrite-Host 'done'", "PowerShell")]
    [InlineData("SELECT id, name\nFROM users\nWHERE id = 1;", "SQL")]
    [InlineData("use std::io;\n\nfn main() {\n    let mut s = String::new();\n}", "Rust")]
    [InlineData("package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(1)\n}", "Go")]
    [InlineData("#include <stdio.h>\n\nint main(void) {\n  return 0;\n}", "C/C++")]
    [InlineData("# 見出し\n\n- 項目1\n- 項目2\n\n[リンク](https://example.com)", "Markdown")]
    [InlineData("[package]\nname = \"x\"\nversion = \"0.1.0\"\n\n[dependencies]\nserde = \"1\"", "TOML")]
    [InlineData(".box {\n  color: red;\n  margin: 0;\n}", "CSS")]
    public void 頭の行の特徴で決める(string text, string language) => Assert.Equal(language, UntitledLanguageDetector.Detect(text));

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("今日やること\n買い物に行く\n本を読む")]
    [InlineData("name: x")]
    [InlineData("- メモ")]
    [InlineData(null)]
    public void 弱い一致や普通の文章では決めない(string? text) => Assert.Null(UntitledLanguageDetector.Detect(text));
}
