using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// 書きかけのコミットメッセージを、リポジトリ（Git の対象フォルダー）ごとに持つ帳面（§24.32）。
/// ワークスペースは複数のフォルダーを持ちうる（マルチルート）ので、鍵はワークスペースではなく対象フォルダー。
/// 空の下書きは持たない——「書いていない」と「空を書いた」を区別する意味が無く、保存が膨らむだけ。
/// </summary>
public sealed class CommitDraftBook
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><paramref name="root"/> の下書きを書く。空白だけなら消す。root が無ければ何もしない。</summary>
    public void Put(string? root, string? text)
    {
        if (Key(root) is not { } key) return;
        if (string.IsNullOrWhiteSpace(text))
            _drafts.Remove(key);
        else
            _drafts[key] = text;
    }

    /// <summary><paramref name="root"/> の下書き。無ければ空文字。</summary>
    public string Get(string? root)
        => Key(root) is { } key && _drafts.TryGetValue(key, out var text) ? text : "";

    /// <summary>保存された帳面で置き換える（ワークスペースの復元）。null は空の帳面。</summary>
    public void Replace(IReadOnlyDictionary<string, string>? drafts)
    {
        _drafts.Clear();
        if (drafts is null) return;
        foreach (var (root, text) in drafts)
            Put(root, text);
    }

    /// <summary>保存用の写し。空なら null（スナップショットに空の欄を書かない）。</summary>
    public Dictionary<string, string>? Export()
        => _drafts.Count == 0
            ? null
            : _drafts.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

    // 末尾の区切りの有無で別の鍵にならないようにそろえる（`C:\work\app` と `C:\work\app\`）。
    private static string? Key(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return root.TrimEnd('\\', '/');
        }
    }
}
