using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.Core.Viewers;
using sk0ya.Loomo.Services.Orders;

namespace sk0ya.Loomo.Services.Viewers;

/// <summary>作った結果。<see cref="Ok"/> でなければ <see cref="Message"/> に人間向けの理由（Claude の出力の末尾を含む）。</summary>
public sealed record ViewerGenerationResult(bool Ok, string Message);

/// <summary>
/// そのままでは出せないファイルを見せる Viewer を、裏の Claude Code（<c>claude -p</c>）に作らせる（§44.2「パスから開く」）。
/// <para><b>線引き</b>：作業フォルダーは Viewer の置き場で、渡す道具は読み書きと検索だけ（シェルは渡さない）。対象のファイルは
/// 道具では読ませず、頭の一部を頼みごとに貼る——読める範囲を広げると、受け入れ済みの書き込みも人のリポジトリへ届いてしまう。
/// 見えるターミナルには打たない。作った Viewer は拡張子ごとの id（<see cref="ViewerPath.IdFor"/>）で、次から使い回す。</para>
/// </summary>
public static class ViewerGenerator
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(6);
    private static readonly string[] Tools = ["Read", "Write", "Edit", "Glob", "Grep"];
    private const int TextSampleChars = 6000;
    private const int BinarySampleBytes = 512;

    public static async Task<ViewerGenerationResult> GenerateAsync(
        ViewerStore store, string targetPath, string id, string? note, CancellationToken cancel)
    {
        Directory.CreateDirectory(store.Root);
        var prompt = await Task.Run(() => Prompt(store, targetPath, id, note), cancel).ConfigureAwait(false);
        int exitCode;
        string output;
        try
        {
            (exitCode, output) = await OrderService.RunClaudeAsync(store.Root, prompt, Tools, Timeout, cancel).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return new ViewerGenerationResult(false, ex.Message);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ViewerGenerationResult(false, "claude を起動できません：" + ex.Message);
        }

        var entry = store.Find(id);
        if (entry.Manifest is null)
            return new ViewerGenerationResult(false, $"Viewer「{id}」の定義が読めません：{entry.Error}\n\n{Tail(output)}");
        if (!File.Exists(Path.Combine(store.FolderOf(id), "index.html")))
            return new ViewerGenerationResult(false, $"Viewer「{id}」に index.html がありません。\n\n{Tail(output)}");
        return new ViewerGenerationResult(true, exitCode == 0 ? "" : Tail(output));
    }

    internal static string Prompt(ViewerStore store, string targetPath, string id, string? note)
    {
        var isDirectory = Directory.Exists(targetPath);
        var folder = store.FolderOf(id);
        var remake = File.Exists(Path.Combine(folder, ViewerStore.ManifestFileName));
        var builder = new StringBuilder();
        builder.AppendLine($"Write a Loomo viewer that shows files like the one below. Write the files into {folder}\\ (id \"{id}\").");
        if (remake)
            builder.AppendLine("A viewer with this id already exists in that folder: improve it in place (read it first) instead of starting over.");
        builder.AppendLine("The same viewer is reused for every file of this kind, so read the file with loomo.target.* and never hard-code its path.");
        builder.AppendLine("Declare \"target\" in the api list. Prefer to do everything in the page (parse the bytes in JavaScript) so that the");
        builder.AppendLine("viewer opens without asking the user; declare \"run\" or \"net\" only if the format really cannot be read otherwise.");
        builder.AppendLine("Write the labels in the page in Japanese. Make it a viewer, not an editor: show the content clearly and densely.");
        builder.AppendLine("Do not touch any other folder. Do not call tools other than file reading and writing. Reply with one short line when done.");
        if (!string.IsNullOrWhiteSpace(note))
        {
            builder.AppendLine();
            builder.AppendLine("The user's request for this viewer:");
            builder.AppendLine(note.Trim());
        }
        builder.AppendLine();
        builder.AppendLine("## The file this viewer is being made for");
        builder.AppendLine(Describe(targetPath, isDirectory));
        builder.AppendLine();
        builder.AppendLine(ViewerGuide.Text(store));
        return builder.ToString();
    }

    private static string Describe(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                var entries = new DirectoryInfo(path).EnumerateFileSystemInfos().Take(60)
                    .Select(info => (info.Attributes & FileAttributes.Directory) != 0 ? info.Name + "\\" : info.Name);
                return $"A folder: {path}\nFirst entries:\n" + string.Join("\n", entries);
            }
            var info = new FileInfo(path);
            var head = new byte[Math.Min(info.Length, 64 * 1024)];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                head = head[..stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)];
            var header = $"Name: {info.Name}\nSize: {info.Length} bytes\n";
            if (Array.IndexOf(head, (byte)0) < 0)
            {
                var text = new UTF8Encoding(false).GetString(head);
                if (text.Length > TextSampleChars)
                    text = text[..TextSampleChars] + "\n…(truncated)";
                return header + "It looks like text. The beginning:\n```\n" + text + "\n```";
            }
            var bytes = head.Take(BinarySampleBytes).ToArray();
            var hex = new StringBuilder();
            for (var i = 0; i < bytes.Length; i += 16)
                hex.AppendLine($"{i:X4}  " + string.Join(" ", bytes.Skip(i).Take(16).Select(b => b.ToString("X2"))));
            return header + "It is binary. The first bytes:\n```\n" + hex + "```";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{path}\n(could not be read: {ex.Message})";
        }
    }

    private static string Tail(string output)
    {
        output = output.Trim();
        return output.Length > 1200 ? "…" + output[^1200..] : output;
    }
}
