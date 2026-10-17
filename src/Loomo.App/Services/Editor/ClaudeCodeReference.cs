namespace sk0ya.Loomo.App.Services;

/// <summary>エディタの場所を Claude Code のプロンプトへ渡す参照（<c>@src/Foo.cs#L10-24</c>）を組む（§24.3.1）。
/// 本文ではなく<b>場所</b>を送る——Claude は自分でファイルを読めるので、貼った本文が古びる・長くて
/// プロンプトを埋めるより、どこを見てほしいかだけを渡す方がよい。</summary>
internal static class ClaudeCodeReference
{
    /// <summary>参照の文字列（末尾に空白を1つ付ける＝続けて打てる）。<paramref name="cwd"/> は Claude が
    /// 動いているターミナルの作業場所で、<c>@</c> の相対パスはそこから解決されるので、その下のファイルは
    /// 相対（区切りは /）、外なら絶対パスにする。行は 0 始まりで受け、1 始まりで書く。</summary>
    public static string Format(string filePath, string? cwd, int? startLine0, int? endLine0)
    {
        var path = RelativeTo(filePath, cwd);
        var target = path.Contains(' ') ? $"\"{path}\"" : path;
        return $"@{target}{LineSuffix(startLine0, endLine0)} ";
    }

    /// <summary>メニューに出す短い見出し（<c>Foo.cs:10-24</c>）。</summary>
    public static string Label(string filePath, int? startLine0, int? endLine0)
    {
        var name = Path.GetFileName(filePath);
        if (startLine0 is not { } start)
            return name;
        var end = endLine0 ?? start;
        return end > start ? $"{name}:{start + 1}-{end + 1}" : $"{name}:{start + 1}";
    }

    /// <summary>LSP 形式の選択（0 始まり・終端は排他）を行の範囲にする。次の行の頭で終わる選択
    /// （行選択の典型）は、その行を含めない。</summary>
    public static (int Start, int End) LinesOfSelection(int startLine, int endLine, int endCharacter)
    {
        if (endLine < startLine)
            (startLine, endLine) = (endLine, startLine);
        var last = endCharacter == 0 && endLine > startLine ? endLine - 1 : endLine;
        return (startLine, last);
    }

    private static string LineSuffix(int? startLine0, int? endLine0)
    {
        if (startLine0 is not { } start)
            return "";
        var end = endLine0 ?? start;
        return end > start ? $"#L{start + 1}-{end + 1}" : $"#L{start + 1}";
    }

    private static string RelativeTo(string filePath, string? cwd)
    {
        if (string.IsNullOrEmpty(cwd))
            return filePath;
        var relative = Path.GetRelativePath(cwd, filePath);
        // 別ドライブなら絶対パスがそのまま返る。上へ出る（..）なら外なので絶対パスで渡す。
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return filePath;
        return relative.Replace('\\', '/');
    }
}
