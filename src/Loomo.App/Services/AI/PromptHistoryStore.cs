using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

/// <summary>AI入力欄の送信済みプロンプト履歴を %APPDATA%/Loomo/history.json に永続化する。
/// 起動間で ↑/↓ の履歴呼び出しを引き継ぐためのもの。</summary>
public sealed class PromptHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    /// <summary>保持する履歴の最大件数（メモリ・ファイル共通の上限）。</summary>
    public int MaxEntries { get; }

    public PromptHistoryStore() : this(DefaultPath()) { }

    public PromptHistoryStore(string filePath, int maxEntries = 200)
    {
        _filePath = filePath;
        MaxEntries = maxEntries;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "history.json");

    /// <summary>保存済み履歴を古い→新しい順で読み込む。</summary>
    public List<string> Load()
    {
        if (!File.Exists(_filePath)) return new List<string>();
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

    /// <summary>履歴を保存する（古い→新しい順。上限を超えたら古い方から捨てる）。</summary>
    public void Save(IReadOnlyList<string> history)
    {
        var trimmed = history.Count > MaxEntries
            ? history.Skip(history.Count - MaxEntries).ToList()
            : history;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(trimmed, JsonOptions));
    }
}
