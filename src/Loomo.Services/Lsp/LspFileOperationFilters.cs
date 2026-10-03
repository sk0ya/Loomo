using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>ファイル／フォルダーの移動・改名 1 件。パスはローカルの絶対パス。</summary>
public sealed record LspFileRename(string OldPath, string NewPath, bool IsDirectory)
{
    public string OldUri => LspUri.FromPath(OldPath);
    public string NewUri => LspUri.FromPath(NewPath);
}

/// <summary>
/// サーバーが <c>workspace.fileOperations.{willRename|didRename}.filters</c> で宣言した
/// 「このファイル操作なら知らせてほしい」の条件。<b>宣言の無いサーバーには送らない</b>——
/// 送ってはいけないわけではないが、宣言していないサーバーが何かを返す保証は無く、
/// 移動のたびに全サーバーへ往復を払うことになるため（VS Code も同じ判定をする）。
///
/// <para>グロブは LSP の <c>GlobPattern</c>（<c>*</c>・<c>**</c>・<c>?</c>・<c>{a,b}</c>・<c>[a-z]</c>）で、
/// VS Code と同じく URI のパス部分（<c>/c:/w/src/a.ts</c>）に当てる。</para>
/// </summary>
public sealed class LspFileOperationFilters
{
    private readonly IReadOnlyList<Filter> _filters;

    private LspFileOperationFilters(IReadOnlyList<Filter> filters) => _filters = filters;

    /// <summary>宣言された条件の数（0 ならこの操作は未対応）。</summary>
    public int Count => _filters.Count;

    /// <summary>
    /// <paramref name="capabilities"/>（<c>initialize</c> 応答の <c>capabilities</c>）から
    /// <paramref name="operation"/>（<c>willRename</c> / <c>didRename</c> 等）の条件を読む。
    /// 宣言が無い・読めない場合は null。
    /// </summary>
    public static LspFileOperationFilters? Parse(JsonElement? capabilities, string operation)
    {
        if (capabilities is not { ValueKind: JsonValueKind.Object } caps ||
            !caps.TryGetProperty("workspace", out var workspace) || workspace.ValueKind != JsonValueKind.Object ||
            !workspace.TryGetProperty("fileOperations", out var ops) || ops.ValueKind != JsonValueKind.Object ||
            !ops.TryGetProperty(operation, out var registration) || registration.ValueKind != JsonValueKind.Object ||
            !registration.TryGetProperty("filters", out var filters) || filters.ValueKind != JsonValueKind.Array)
            return null;

        var parsed = new List<Filter>();
        foreach (var item in filters.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.Object ||
                !pattern.TryGetProperty("glob", out var globEl) || globEl.GetString() is not { Length: > 0 } glob)
                continue;

            var scheme = item.TryGetProperty("scheme", out var schemeEl) ? schemeEl.GetString() : null;
            var matches = pattern.TryGetProperty("matches", out var matchesEl) ? matchesEl.GetString() : null;
            if (TryCompileGlob(glob) is { } regex)
                parsed.Add(new Filter(scheme, matches, regex));
        }
        return parsed.Count == 0 ? null : new LspFileOperationFilters(parsed);
    }

    /// <summary>この移動・改名（旧 URI で判定する）が条件のどれかに当たるか。</summary>
    public bool Matches(LspFileRename rename) => Matches(rename.OldUri, rename.IsDirectory);

    public bool Matches(string uri, bool isDirectory)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        // Uri.AbsolutePath は %XX のまま。グロブは素の文字（日本語のフォルダー名等）に当てたい。
        var path = Uri.UnescapeDataString(parsed.AbsolutePath);
        return _filters.Any(f =>
            (f.Scheme is null || string.Equals(f.Scheme, parsed.Scheme, StringComparison.OrdinalIgnoreCase)) &&
            (f.Matches is null ||
             (f.Matches == "file" && !isDirectory) ||
             (f.Matches == "folder" && isDirectory)) &&
            f.Glob.IsMatch(path));
    }

    /// <summary>LSP の GlobPattern を正規表現へ。壊れたパターンは null（その条件だけ捨てる）。</summary>
    internal static Regex? TryCompileGlob(string glob)
    {
        var sb = new StringBuilder("^");
        var braceDepth = 0;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    // "**/" は 0 個以上のフォルダー、末尾などの "**" は何でも。
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    braceDepth++;
                    sb.Append("(?:");
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    sb.Append(')');
                    break;
                case ',' when braceDepth > 0:
                    sb.Append('|');
                    break;
                case '[':
                    var end = glob.IndexOf(']', i + 1);
                    if (end < 0) { sb.Append(@"\["); break; }
                    var body = glob.Substring(i + 1, end - i - 1);
                    if (body.StartsWith('!')) body = "^" + body[1..];
                    sb.Append('[').Append(body.Replace(@"\", @"\\")).Append(']');
                    i = end;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        if (braceDepth != 0) return null;
        // パターンは「パスの末尾」に当てる（"**/*.ts" も "src/**" も、先頭の "/c:/w/" に縛られない）。
        // 絶対パスのパターン（"/c:/w/**"）は先頭からの一致になる。
        var pattern = sb.ToString();
        if (!glob.StartsWith('/') && !glob.StartsWith("**", StringComparison.Ordinal))
            pattern = "^(?:.*/)?" + pattern[1..];
        pattern += "$";
        try
        {
            // Windows のパスは大小文字を区別しないので、options.ignoreCase の宣言に関係なく区別しない
            // （"C:" と "c:" や、拡張子 ".TS" の取りこぼしを避ける）。
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private sealed record Filter(string? Scheme, string? Matches, Regex Glob);
}
