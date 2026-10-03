using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace sk0ya.Loomo.Core.Files;

/// <summary>
/// エクスプローラーの「関連ファイルのまとめ表示」（VS Code の Explorer File Nesting 相当）の判定。
/// 同じフォルダーにあるファイル名の集合から、「どのファイルをどのファイルの子として畳むか」を決める純関数。
///
/// <para><b>ルールの書き方</b>は VS Code の <c>explorer.fileNesting.patterns</c> と同じ：
/// キー＝親のパターン（完全一致の名前か、<c>*</c> を1つ含むパターン）、値＝子のパターンのカンマ区切り。
/// 子のパターンでは <c>$(capture)</c>（親の <c>*</c> に当たった部分）・<c>$(basename)</c>（親の拡張子を除いた名前）・
/// <c>$(extname)</c>（親の拡張子、ドットなし）が使え、<c>*</c> は任意の文字列に当たる。
/// VS Code の <c>${capture}</c> 表記もそのまま受け付ける（設定を写してきても動くように）。</para>
///
/// <para><b>入れ子は1段だけ</b>。子が別のルールでさらに子を持つ（<c>a.ts</c>→<c>a.js</c>→<c>a.js.map</c>）ときは、
/// 孫を一番上の親の直下へ寄せる——階層を深くしても畳む意味が薄れ、開く手間だけが増えるため。
/// 1つのファイルが複数の親に当たるときは、<b>ルールの並び順で先のもの</b>（同じルール内なら名前順で先の親）を採る。
/// 名前の比較は Windows のファイルシステムに合わせて大文字小文字を区別しない。</para>
/// </summary>
public sealed class FileNesting
{
    /// <summary>既定のルール（並び順に意味がある：先のルールが優先）。設定に何も保存されていないときに使う。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DefaultPatterns { get; } = new KeyValuePair<string, string>[]
    {
        new("*.xaml", "$(capture).xaml.cs"),
        new("*.resx", "$(capture).Designer.cs, $(capture).*.resx"),
        new("*.cs", "$(capture).Designer.cs, $(capture).g.cs, $(capture).g.i.cs"),
        new("*.razor", "$(capture).razor.cs, $(capture).razor.css, $(capture).razor.js"),
        new("*.cshtml", "$(capture).cshtml.cs, $(capture).cshtml.css"),
        new("*.csproj", "$(capture).csproj.user"),
        new("Directory.Build.props", "Directory.Build.targets, Directory.Packages.props, Directory.Build.rsp"),
        new("appsettings.json", "appsettings.*.json"),
        new("package.json", "package-lock.json, yarn.lock, pnpm-lock.yaml, pnpm-workspace.yaml, bun.lockb, bun.lock, .npmrc, .yarnrc, .yarnrc.yml, .nvmrc, .node-version"),
        new("tsconfig.json", "tsconfig.*.json, jsconfig.json"),
        new("*.ts", "$(capture).js, $(capture).d.ts, $(capture).js.map, $(capture).d.ts.map"),
        new("*.tsx", "$(capture).js, $(capture).jsx, $(capture).d.ts, $(capture).js.map"),
        new("*.js", "$(capture).js.map, $(capture).min.js, $(capture).d.ts"),
        new("*.jsx", "$(capture).js"),
        new("*.css", "$(capture).css.map, $(capture).min.css"),
        new("*.scss", "$(capture).css, $(capture).css.map"),
        new(".gitignore", ".gitattributes, .gitmodules, .mailmap, .git-blame-ignore-revs"),
        new("README.md", "README.*.md"),
    };

    /// <summary>規則が1つも無いときの空インスタンス（何もまとめない）。</summary>
    public static FileNesting Empty { get; } = new(Array.Empty<Rule>());

    private readonly IReadOnlyList<Rule> _rules;

    private FileNesting(IReadOnlyList<Rule> rules) => _rules = rules;

    /// <summary>規則があるか（空ならまとめ処理を丸ごと省ける）。</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>「親のパターン → 子のパターン（カンマ区切り）」の並びから組み立てる。
    /// 空のキー・空の値・壊れたパターンは読み捨てる（設定の書き損じでツリーが出なくなるのを避ける）。</summary>
    public static FileNesting Create(IEnumerable<KeyValuePair<string, string>>? patterns)
    {
        if (patterns is null)
            return Empty;

        var rules = new List<Rule>();
        foreach (var (key, value) in patterns)
        {
            var parent = key?.Trim();
            if (string.IsNullOrEmpty(parent) || string.IsNullOrWhiteSpace(value))
                continue;
            if (parent.Count(c => c == '*') > 1 || parent.IndexOfAny(new[] { '/', '\\' }) >= 0)
                continue;

            var children = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => c.Length > 0 && c.IndexOfAny(new[] { '/', '\\' }) < 0)
                .ToArray();
            if (children.Length == 0)
                continue;

            rules.Add(new Rule(parent, children));
        }
        return rules.Count == 0 ? Empty : new FileNesting(rules);
    }

    /// <summary>同じフォルダーにあるファイル名の集合を受け取り、「子のファイル名 → 親のファイル名」を返す。
    /// 親にならなかったファイルと、どの親にも当たらなかったファイルは結果に含まれない。</summary>
    public IReadOnlyDictionary<string, string> Resolve(IEnumerable<string> fileNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_rules.Count == 0)
            return result;

        var names = fileNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count < 2)
            return result;

        // 1. 直接の親を決める（ルール順→親の名前順で、最初に当たったものが勝つ）。
        // 子のパターンは大半が * を含まない（$(capture).xaml.cs 等）ので、名前の集合を引くだけで済ませ、
        // * を含むものだけ全件を当てる——.cs が数百あるフォルダーで親×子の総当たりにしないため。
        var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var direct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _rules)
        {
            foreach (var parent in names)
            {
                if (!rule.TryMatchParent(parent, out var capture))
                    continue;
                foreach (var child in rule.FindChildren(parent, capture, nameSet, names))
                {
                    if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)
                        || direct.ContainsKey(child))
                        continue;
                    direct[child] = parent;
                }
            }
        }

        // 2. 入れ子は1段に畳む：親自身がさらに子なら、一番上の親まで辿る。輪になったら（a→b→a）その子はまとめない。
        foreach (var child in direct.Keys)
        {
            var top = direct[child];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { child };
            var cyclic = false;
            while (direct.TryGetValue(top, out var next))
            {
                if (!seen.Add(top))
                {
                    cyclic = true;
                    break;
                }
                top = next;
            }
            if (!cyclic && !string.Equals(top, child, StringComparison.OrdinalIgnoreCase))
                result[child] = top;
        }
        return result;
    }

    /// <summary>設定画面のテキスト（1行1ルール：<c>親 = 子1, 子2</c>）を読み取る。空行と <c>#</c> で始まる行は無視する。
    /// <c>=</c> の無い行は読み捨てる。同じ親が2回出たら後の行で上書きする（JSON の辞書と同じ振る舞い）。</summary>
    public static List<KeyValuePair<string, string>> ParseText(string? text)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0)
                continue;

            var existing = result.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                result[existing] = new(key, value);
            else
                result.Add(new(key, value));
        }
        return result;
    }

    /// <summary><see cref="ParseText"/> の逆。設定画面へ出すテキストを作る。</summary>
    public static string FormatText(IEnumerable<KeyValuePair<string, string>> patterns)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in patterns)
        {
            if (sb.Length > 0) sb.Append(Environment.NewLine);
            sb.Append(key).Append(" = ").Append(value);
        }
        return sb.ToString();
    }

    private sealed class Rule
    {
        private const string CaptureMark = "\u0001";
        private const string BasenameMark = "\u0002";
        private const string ExtnameMark = "\u0003";

        private readonly Regex _parent;
        private readonly bool _hasWildcard;
        private readonly string[] _children;

        public Rule(string parentPattern, string[] children)
        {
            _hasWildcard = parentPattern.Contains('*');
            _parent = new Regex(
                "^" + Regex.Escape(parentPattern).Replace(@"\*", "(.+)") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _children = children;
        }

        public bool TryMatchParent(string name, out string capture)
        {
            var m = _parent.Match(name);
            capture = m.Success && _hasWildcard ? m.Groups[1].Value : "";
            return m.Success;
        }

        /// <summary>この親に当たる子の名前を、名前の並び順で返す。</summary>
        public IEnumerable<string> FindChildren(
            string parentName, string capture, HashSet<string> nameSet, IReadOnlyList<string> names)
        {
            var dot = parentName.LastIndexOf('.');
            var basename = dot > 0 ? parentName[..dot] : parentName;
            var extname = dot > 0 ? parentName[(dot + 1)..] : "";

            var exact = new List<string>();
            var wildcards = new List<Regex>();
            foreach (var child in _children)
            {
                // 置換語を印（制御文字）に置いた形で * の有無を見る——親の名前に * が入っていても
                // パターン側の * と取り違えない。
                var expanded = Expand(child, CaptureMark, BasenameMark, ExtnameMark);
                if (!expanded.Contains('*'))
                {
                    var name = Expand(child, capture, basename, extname);
                    if (nameSet.TryGetValue(name, out var actual))
                        exact.Add(actual);
                    continue;
                }
                // 置換語（親の名前の一部）に * や . が入っていても字義どおりに当てる。
                var pattern = Regex.Escape(expanded)
                    .Replace(@"\*", ".*")
                    .Replace(CaptureMark, Regex.Escape(capture))
                    .Replace(BasenameMark, Regex.Escape(basename))
                    .Replace(ExtnameMark, Regex.Escape(extname));
                wildcards.Add(new Regex("^" + pattern + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }

            if (wildcards.Count == 0)
                return exact.Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            var hits = new HashSet<string>(exact, StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                if (wildcards.Any(r => r.IsMatch(name)))
                    hits.Add(name);
            return hits.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }

        private static string Expand(string pattern, string capture, string basename, string extname)
            => pattern
                .Replace("$(capture)", capture, StringComparison.OrdinalIgnoreCase)
                .Replace("${capture}", capture, StringComparison.OrdinalIgnoreCase)
                .Replace("$(basename)", basename, StringComparison.OrdinalIgnoreCase)
                .Replace("${basename}", basename, StringComparison.OrdinalIgnoreCase)
                .Replace("$(extname)", extname, StringComparison.OrdinalIgnoreCase)
                .Replace("${extname}", extname, StringComparison.OrdinalIgnoreCase);
    }
}
