using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Editor.Core.Lsp;

namespace sk0ya.Loomo.Services.Lsp;

/// <summary>
/// 拡張子→言語サーバーの対応表。**この表の所有者は Loomo ひとり**（旧 <c>Editor.Core.Lsp.LspServerRegistry</c>
/// からの移管。エディタ側は <see cref="ILspServerAdmin"/> 越しに触るだけになった）。
///
/// 組み込み既定は <see cref="LspServerCatalog"/> から導出する。「どの実行ファイルか」と
/// 「どう入れるか・どう見せるか」が別々の場所にあると片方だけ更新されて食い違うので、1レコードに寄せてある。
/// ユーザー変更（追加・置換・組み込みの無効化）はそれに重ねて
/// <c>%APPDATA%/Loomo/lsp-servers.json</c>（<c>{ Overrides, Removed }</c>）へ永続化する。
/// </summary>
public sealed class LspServerTable : ILspServerAdmin
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LspServerDef> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _storePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>組み込み既定表（カタログから導出）。ユーザーは上書き・非表示にできるが失うことはない。</summary>
    public static IReadOnlyDictionary<string, LspServerDef> Builtins { get; } = BuildBuiltins();

    private static Dictionary<string, LspServerDef> BuildBuiltins()
    {
        var map = new Dictionary<string, LspServerDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in LspServerCatalog.Servers)
            foreach (var target in server.Targets)
                map[target.Extension] = new LspServerDef(server.Executable, server.Args, target.LanguageId);
        return map;
    }

    /// <summary>既定の永続化先: <c>%APPDATA%/Loomo/lsp-servers.json</c>。</summary>
    public static string DefaultStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Loomo", "lsp-servers.json");

    /// <summary>拡張子の対応が変わった（Set/Remove/Reset）。ワークスペースがその場で開き直すために購読する。</summary>
    public event Action<string>? Changed;

    /// <param name="storePath">JSON の保存先。null ならメモリ内のみ（テスト用）。</param>
    public LspServerTable(string? storePath)
    {
        _storePath = storePath;
        Load();
    }

    /// <summary>拡張子（".cs" 等）に対応する言語サーバー。未設定なら null。</summary>
    public LspServerDef? GetForExtension(string extension)
    {
        var ext = LspExtensions.NormalizeExt(extension);
        if (ext.Length == 0) return null;
        lock (_gate)
        {
            if (_overrides.TryGetValue(ext, out var def)) return def;   // ユーザー指定が最優先
            if (_removed.Contains(ext)) return null;                    // 組み込みをユーザーが無効化
            return Builtins.GetValueOrDefault(ext);
        }
    }

    /// <summary>組み込み＋ユーザー変更をマージした実効表（無効化された組み込みも含む）を拡張子順で返す。</summary>
    public IReadOnlyList<LspServerEntry> List()
    {
        lock (_gate)
        {
            var rows = new Dictionary<string, LspServerEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ext, def) in Builtins)
                rows[ext] = new LspServerEntry(ext, def,
                    _removed.Contains(ext) ? LspServerOrigin.Removed : LspServerOrigin.BuiltIn);
            foreach (var (ext, def) in _overrides)
                rows[ext] = new LspServerEntry(ext, def, LspServerOrigin.Custom);

            return rows.Values
                .OrderBy(e => e.Extension, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>拡張子にサーバーを割り当てる（組み込み・カスタムを問わず置換）。</summary>
    public void Set(string extension, LspServerDef def)
    {
        var ext = LspExtensions.NormalizeExt(extension);
        if (ext.Length == 0) throw new ArgumentException("Extension must not be empty.", nameof(extension));
        lock (_gate)
        {
            _overrides[ext] = def;
            _removed.Remove(ext);
            Save();
        }
        Changed?.Invoke(ext);
    }

    /// <summary>カスタムは削除、組み込みは無効化（再起動後も保持）。実際に変わったら true。</summary>
    public bool Remove(string extension)
    {
        var ext = LspExtensions.NormalizeExt(extension);
        if (ext.Length == 0) return false;
        bool changed;
        lock (_gate)
        {
            changed = false;
            if (_overrides.Remove(ext)) changed = true;
            if (Builtins.ContainsKey(ext) && _removed.Add(ext)) changed = true;
            if (changed) Save();
        }
        if (changed) Changed?.Invoke(ext);
        return changed;
    }

    /// <summary>ユーザー変更を捨てて組み込み既定へ戻す。実際に変わったら true。</summary>
    public bool Reset(string extension)
    {
        var ext = LspExtensions.NormalizeExt(extension);
        if (ext.Length == 0) return false;
        bool changed;
        lock (_gate)
        {
            changed = _overrides.Remove(ext) | _removed.Remove(ext);
            if (changed) Save();
        }
        if (changed) Changed?.Invoke(ext);
        return changed;
    }

    private static bool SameAsBuiltin(string extension, LspServerDef def) =>
        Builtins.TryGetValue(extension, out var builtin)
        && string.Equals(builtin.Executable, def.Executable, StringComparison.OrdinalIgnoreCase)
        && string.Equals(builtin.LanguageId, def.LanguageId, StringComparison.Ordinal)
        && builtin.Args.SequenceEqual(def.Args, StringComparer.Ordinal);

    // ── 永続化 ──────────────────────────────────────────────────────────────

    private sealed class StoreDto
    {
        public Dictionary<string, LspServerDef> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Removed { get; set; } = [];
    }

    private void Load()
    {
        if (_storePath is null || !File.Exists(_storePath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(_storePath), JsonOptions);
            if (dto is null) return;
            bool migrated = false;
            lock (_gate)
            {
                _overrides.Clear();
                _removed.Clear();
                foreach (var (ext, def) in dto.Overrides)
                {
                    var key = LspExtensions.NormalizeExt(ext);
                    if (key.Length == 0 || def is null || string.IsNullOrWhiteSpace(def.Executable)) continue;
                    // 旧 C# サーバー（csharp-ls / roslyn-language-server シム / Loomo 配下の dotnet 起動）を
                    // ユーザー設定として抱えたままだと、組み込みが Roslyn になっても古い方が勝ち続ける。
                    if (LspServerCatalog.IsSupersededCSharpServer(key, def)) { migrated = true; continue; }

                    var normalized = new LspServerDef(def.Executable, def.Args ?? [], def.LanguageId ?? "");
                    // 組み込みと同じ内容の上書きは捨てる。以前は組み込みに無かった割り当てが組み込みへ
                    // 昇格したときの残骸で、持ち続けても効果は無いのに設定画面では「custom」に見えるうえ、
                    // 以後この拡張子だけ組み込みの更新が届かなくなる。
                    if (SameAsBuiltin(key, normalized)) { migrated = true; continue; }
                    _overrides[key] = normalized;
                }
                foreach (var ext in dto.Removed)
                {
                    var key = LspExtensions.NormalizeExt(ext);
                    if (key.Length > 0) _removed.Add(key);
                }
                if (migrated) Save();
            }
        }
        catch
        {
            // 壊れた設定でエディタが起動しなくなるのは困る → 組み込みだけで続行。
        }
    }

    private void Save()
    {
        if (_storePath is null) return;   // メモリ内（テスト）
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dto = new StoreDto
            {
                Overrides = new Dictionary<string, LspServerDef>(_overrides, StringComparer.OrdinalIgnoreCase),
                Removed = _removed.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList(),
            };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch
        {
            // 保存失敗（ロック・ディスク不足）でコマンドを落とさない。
        }
    }
}
