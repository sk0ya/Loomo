using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Editor.Core.Syntax;

namespace sk0ya.Loomo.App.Services;

/// <summary>プレビューの1行（行番号は1始まり）。<see cref="IsTarget"/> がジャンプ先の行。
/// <see cref="Tokens"/> はエディタと同じ字句解析の結果で、null なら色を付けない（言語が決まらない・
/// 解析が転んだ）。色そのものは描くとき（UI スレッド）に <see cref="EditorSyntaxColors"/> から引く。</summary>
public sealed record PalettePreviewLine(int Number, string Text, bool IsTarget, IReadOnlyList<SyntaxToken>? Tokens = null);

/// <summary>
/// プレビュー欄の中身。<see cref="Message"/> が入っているときは本文を出さずその一言だけを出す
/// （バイナリ・消えたファイル・読めなかった等）。
/// </summary>
public sealed record PalettePreviewContent(
    string Header,
    string SubHeader,
    string? Message,
    IReadOnlyList<PalettePreviewLine> Lines,
    string? Highlight)
{
    public static readonly PalettePreviewContent Empty =
        new("", "", null, Array.Empty<PalettePreviewLine>(), null);
}

/// <summary>選択中のコマンドを説明するプレビュー内容を組み立てる。</summary>
public static class PaletteCommandPreview
{
    public static PalettePreviewContent Create(PaletteCommand command)
    {
        var shortcut = string.IsNullOrEmpty(command.Shortcut)
            ? "ショートカット: 未割当"
            : $"ショートカット: {command.Shortcut}";
        return new PalettePreviewContent(
            command.Title,
            command.Category,
            $"{shortcut}{Environment.NewLine}{Environment.NewLine}Enter で実行・Esc で閉じる",
            Array.Empty<PalettePreviewLine>(),
            null);
    }
}

/// <summary>プレビューに出す範囲の切り出し（純ロジック・テスト対象）。</summary>
public static class PalettePreviewSlice
{
    /// <summary>ジャンプ先の上に何行残すか（文脈が見えて、かつ目的の行が上寄りに来る量）。</summary>
    public const int LinesBefore = 6;

    /// <summary>一度に出す行数（スクロールできるので、読める量だけ作る）。</summary>
    public const int LineCount = 200;

    /// <summary>
    /// <paramref name="targetLine"/>（1始まり・0以下＝指定なし）を上寄りに含む連続した行を返す。
    /// 指定なしのときは先頭から。範囲外の行番号はファイル内へ丸める。
    /// </summary>
    public static IReadOnlyList<PalettePreviewLine> Slice(
        IReadOnlyList<string> lines, int targetLine, int before = LinesBefore, int count = LineCount)
    {
        if (lines.Count == 0)
            return Array.Empty<PalettePreviewLine>();

        var target = targetLine <= 0 ? 0 : Math.Min(targetLine, lines.Count);
        var start = target <= 0 ? 1 : Math.Max(1, target - before);
        var end = Math.Min(lines.Count, start + count - 1);

        var result = new List<PalettePreviewLine>(end - start + 1);
        for (var number = start; number <= end; number++)
            result.Add(new PalettePreviewLine(number, Display(lines[number - 1]), number == target));
        return result;
    }

    /// <summary>表示用に整える（タブは4桁・制御文字は落とす）。折り返さないので長い行は欄内で切れる。
    /// 構文解析もこの整形後の行に対して行う（タブを桁に直す前後で色の位置がずれないように）。</summary>
    internal static string Display(string text)
    {
        var expanded = text.Replace("\t", "    ").TrimEnd();
        if (expanded.IndexOf('\0') < 0)
            return expanded;

        var sb = new StringBuilder(expanded.Length);
        foreach (var c in expanded)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }
}

/// <summary>
/// パレットの選択行が指すファイルを読んでプレビューを組み立てる。ファイルは開かず
/// （タブも履歴も作らず）、先頭 <see cref="MaxBytes"/> までしか読まないので、一覧を
/// ↑↓で流していても重くならない。呼び出しはバックグラウンド前提。
/// </summary>
public static class PalettePreviewLoader
{
    /// <summary>読み込む上限。これを超えるファイルは先頭だけを見せる。</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>バイナリ判定に見る先頭バイト数（NUL が出たらテキストではない）。</summary>
    private const int BinaryProbeBytes = 8192;

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Windows の旧来コードページ（cp932）。検索サービスと同じ順で退避する（UTF-8 → cp932）。
    private static readonly Lazy<Encoding> JapaneseWindows = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });

    public static async Task<PalettePreviewContent> LoadAsync(
        PaletteTarget target, string displayPath, CancellationToken ct)
    {
        var header = Path.GetFileName(target.FullPath);
        var sub = target.Line > 0 ? $"{displayPath}:{target.Line}" : displayPath;

        FileInfo info;
        try { info = new FileInfo(target.FullPath); }
        catch (Exception ex) { return Failed(header, sub, ex.Message); }

        if (!info.Exists)
            return Failed(header, sub, "ファイルが見つかりません。");
        if (info.Length == 0)
            return Failed(header, sub, "空のファイルです。");

        byte[] buffer;
        try
        {
            await using var stream = new FileStream(target.FullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, useAsync: true);
            buffer = new byte[(int)Math.Min(stream.Length, MaxBytes)];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failed(header, sub, $"読み込めませんでした: {ex.Message}"); }

        if (IsBinary(buffer))
            return Failed(header, $"{sub}  ·  {FormatSize(info.Length)}", "バイナリのためプレビューできません。");

        var text = Decode(buffer);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = PalettePreviewSlice.Display(lines[i].TrimEnd('\r'));

        var truncated = info.Length > MaxBytes;
        if (truncated)
            sub += $"  ·  先頭 {FormatSize(MaxBytes)} のみ（全体 {FormatSize(info.Length)}）";

        var slice = PalettePreviewSlice.Slice(lines, target.Line);
        return new PalettePreviewContent(header, sub, null,
            PalettePreviewSyntax.Attach(target.FullPath, lines, slice), target.Highlight);
    }

    private static PalettePreviewContent Failed(string header, string sub, string message)
        => new(header, sub, message, Array.Empty<PalettePreviewLine>(), null);

    private static bool IsBinary(byte[] buffer)
        => Array.IndexOf(buffer, (byte)0, 0, Math.Min(buffer.Length, BinaryProbeBytes)) >= 0;

    /// <summary>UTF-8（BOM は除去）→ cp932 → Latin-1 の順に試す。最後は必ず読めるので例外にしない。</summary>
    private static string Decode(byte[] buffer)
    {
        var span = buffer.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];

        try { return StrictUtf8.GetString(span); } catch (DecoderFallbackException) { }
        try { return JapaneseWindows.Value.GetString(span); } catch (DecoderFallbackException) { }
        return Encoding.Latin1.GetString(span);
    }

    private static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024d:0.#} KB",
        < 1024 * 1024 * 1024 => $"{size / (1024d * 1024):0.#} MB",
        _ => $"{size / (1024d * 1024 * 1024):0.#} GB",
    };
}

/// <summary>
/// プレビューの行にエディタと同じ構文色を付けるためのトークンを配る。字句解析器も配色も差分本体
/// （<see cref="DiffSyntaxHighlighter"/>）と同じ <see cref="EditorSyntaxColors"/> 経由なので、同じファイルを
/// エディタ・差分・パレットのどこで読んでも色が一致する。
/// </summary>
internal static class PalettePreviewSyntax
{
    /// <summary>ファイル先頭からここまでは通しで解析する。途中から解析を始めると、複数行コメントや
    /// 文字列の内側に入っている行の色が崩れるため。これを超える位置の一致はその窓だけを解析する
    /// （まれな上に、1回のプレビューで巨大ファイル全体を舐めるほうが害が大きい）。</summary>
    internal const int ContextLines = 5_000;

    internal static IReadOnlyList<PalettePreviewLine> Attach(
        string filePath, IReadOnlyList<string> lines, IReadOnlyList<PalettePreviewLine> slice)
    {
        if (slice.Count == 0 || EditorSyntaxColors.CreateEngine(filePath) is not { } engine)
            return slice;

        var last = slice[^1].Number;
        var fromTop = last <= ContextLines;
        var first = fromTop ? 1 : slice[0].Number;
        var window = new string[last - first + 1];
        for (var i = 0; i < window.Length; i++)
            window[i] = lines[first - 1 + i];

        SyntaxToken[]?[] tokens;
        try
        {
            tokens = new SyntaxToken[]?[window.Length];
            foreach (var line in engine.Tokenize(window))
                if (line.Line >= 0 && line.Line < tokens.Length)
                    tokens[line.Line] = line.Tokens;
        }
        catch
        {
            // 色付けは読みやすさの補助でしかない。どの言語の解析が転んでもプレビュー自体は出す。
            return slice;
        }

        var result = new PalettePreviewLine[slice.Count];
        for (var i = 0; i < slice.Count; i++)
        {
            var at = slice[i].Number - first;
            result[i] = at >= 0 && at < tokens.Length ? slice[i] with { Tokens = tokens[at] } : slice[i];
        }
        return result;
    }
}

/// <summary>ナビゲーション候補の周辺ソースを peek 表示用に切り出す。</summary>
public static class NavigationSourceReader
{
    /// <summary>peek一覧で使う、指定行だけの簡易プレビュー。</summary>
    public static string ReadLine(string filePath, int line)
    {
        if (string.IsNullOrWhiteSpace(filePath) || line < 0) return "";
        try { return File.ReadLines(filePath).Skip(line).FirstOrDefault()?.Trim() ?? ""; }
        catch { return ""; }
    }

    public static string Read(string filePath, int line, int radius = 2, int maxLineCharacters = 240)
    {
        if (string.IsNullOrWhiteSpace(filePath) || line < 0) return "";
        radius = Math.Clamp(radius, 0, 20);
        maxLineCharacters = Math.Clamp(maxLineCharacters, 40, 2000);

        try
        {
            var start = Math.Max(0, line - radius);
            var lines = File.ReadLines(filePath).Skip(start).Take(radius * 2 + 1).ToArray();
            if (lines.Length == 0) return "";

            var builder = new StringBuilder();
            for (var index = 0; index < lines.Length; index++)
            {
                var actualLine = start + index;
                var text = lines[index].TrimEnd('\r', '\n');
                if (text.Length > maxLineCharacters)
                    text = text[..maxLineCharacters] + "…";
                builder.Append(actualLine == line ? "▶" : " ")
                    .Append($" {actualLine + 1,4}  ")
                    .Append(text)
                    .Append('\n');
            }
            return builder.ToString().TrimEnd();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
