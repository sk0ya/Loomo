using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// %APPDATA%/Loomo 配下の設定一式（settings.json・workspaces.json・キーバインド・LSP/整形の設定・
/// セッション・トレース等）を ZIP へ書き出し／取り込みする。ダウンロード済みモデル（models/）と
/// WebView2 のキャッシュ（WebView2/）は巨大なのでバックアップから除外する。
/// </summary>
public sealed class ConfigBackupService
{
    /// <summary>バックアップから除外するトップレベルのフォルダ（巨大・再生成可能なキャッシュ類）。</summary>
    private static readonly HashSet<string> ExcludedTopDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "WebView2",
    };

    private readonly string _configRoot;

    public ConfigBackupService() : this(DefaultConfigRoot()) { }

    public ConfigBackupService(string configRoot) => _configRoot = configRoot;

    public static string DefaultConfigRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Loomo");

    public string ConfigRoot => _configRoot;

    /// <summary>設定一式を <paramref name="zipPath"/> へ書き出す。除外フォルダ配下と出力 ZIP 自身は含めない。
    /// 戻り値は書き出したエントリ数。</summary>
    public int Export(string zipPath)
    {
        Directory.CreateDirectory(_configRoot);
        var fullZip = Path.GetFullPath(zipPath);

        // 既存ファイルを上書きするため、いったん削除してから作る。
        if (File.Exists(fullZip))
            File.Delete(fullZip);

        var count = 0;
        using var archive = ZipFile.Open(fullZip, ZipArchiveMode.Create);
        foreach (var file in EnumerateBackupFiles())
        {
            // 出力 ZIP が設定フォルダ内にある場合は自分自身を取り込まない。
            if (string.Equals(Path.GetFullPath(file), fullZip, StringComparison.OrdinalIgnoreCase))
                continue;

            var entryName = Path.GetRelativePath(_configRoot, file).Replace('\\', '/');
            try
            {
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                count++;
            }
            catch (IOException)
            {
                // 使用中などで読めないファイルは飛ばす（バックアップ全体は止めない）。
            }
        }
        return count;
    }

    /// <summary>ZIP の内容を設定フォルダへ展開して上書きする（zip-slip 防止つき）。戻り値は展開したエントリ数。
    /// 反映には再起動を推奨（多くの設定は起動時に読み込むため）。</summary>
    public int Import(string zipPath)
    {
        Directory.CreateDirectory(_configRoot);
        var rootFull = Path.GetFullPath(_configRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var count = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // ディレクトリエントリ（末尾 '/'）は飛ばす。
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var destination = Path.GetFullPath(Path.Combine(_configRoot, entry.FullName));
            // 展開先が設定フォルダの外へ出るエントリ（../ などの細工）は拒否する。
            if (!destination.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                && !destination.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>バックアップ対象のファイルを列挙する（除外トップレベルフォルダ配下は除く）。</summary>
    private IEnumerable<string> EnumerateBackupFiles()
    {
        if (!Directory.Exists(_configRoot))
            yield break;

        // ルート直下のファイル。
        foreach (var file in Directory.EnumerateFiles(_configRoot))
            yield return file;

        // ルート直下のフォルダ（除外フォルダ以外）を再帰的に。
        foreach (var dir in Directory.EnumerateDirectories(_configRoot))
        {
            var name = Path.GetFileName(dir);
            if (ExcludedTopDirs.Contains(name))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }
}
