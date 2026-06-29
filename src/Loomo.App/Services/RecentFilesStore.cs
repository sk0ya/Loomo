using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

/// <summary>エディタで開いたファイルのフルパスを新しい→古い順で %APPDATA%/Loomo/recent-files.json に
/// 永続化する MRU 履歴。コマンドパレットの「最近開いたファイル」候補の供給元。
/// 同一パスは大文字小文字を無視して1件にまとめ、上限を超えたら古い方から捨てる。</summary>
public sealed class RecentFilesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly List<string> _entries; // 新しい→古い順

    /// <summary>保持する履歴の最大件数（メモリ・ファイル共通の上限）。</summary>
    public int MaxEntries { get; }

    public RecentFilesStore() : this(DefaultPath()) { }

    public RecentFilesStore(string filePath, int maxEntries = 100)
    {
        _filePath = filePath;
        MaxEntries = maxEntries;
        _entries = LoadFromDisk();
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "recent-files.json");

    /// <summary>現在の履歴（新しい→古い順）のスナップショット。</summary>
    public IReadOnlyList<string> Entries => _entries.ToArray();

    /// <summary>1ファイルを履歴の先頭へ。既にあれば先頭へ繰り上げる。空・不正パスは無視。永続化まで行う。</summary>
    public void Add(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        string normalized;
        try { normalized = Path.GetFullPath(fullPath); }
        catch { return; }

        _entries.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, normalized);
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    private List<string> LoadFromDisk()
    {
        if (!File.Exists(_filePath))
            return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(
                File.ReadAllText(_filePath), JsonOptions) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch
        {
            // 履歴の永続化失敗は機能本体を妨げない（次回起動で取りこぼすだけ）。
        }
    }
}
