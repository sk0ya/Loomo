using System;
using System.Collections.Generic;
using System.Linq;
using Editor.Core.Lsp;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// コマンドパレットの検索モード（§24.2）。入力の先頭1文字で切り替わり、コマンド以外は
/// 「部屋の中を探して飛ぶ」ナビゲーションになる。
/// </summary>
public enum PaletteMode
{
    /// <summary>検索対象を絞らない（既定・プレフィックス無し）。</summary>
    All,
    /// <summary>部屋の操作。</summary>
    Command,
    /// <summary>ファイル名（ワークスペース全フォルダー横断）。</summary>
    File,
    /// <summary>テキスト全文（grep）。</summary>
    Text,
    /// <summary>シンボル（言語サーバーのワークスペースシンボル）。</summary>
    Symbol,
    /// <summary>いま開いているファイルの行番号。</summary>
    Line,
}

/// <summary>
/// パレット入力を「モード＋素のクエリ」へ分解した結果（純ロジック・テスト対象）。
/// 先頭1文字だけを見るので、入力中にモードが揺れない。
/// </summary>
public readonly record struct PaletteQuery(PaletteMode Mode, string Text)
{
    public const char CommandPrefix = '>';
    public const char FilePrefix = '/';
    public const char TextPrefix = '#';
    public const char SymbolPrefix = '@';
    public const char LinePrefix = ':';

    /// <summary>Tab で巡回する順（ヒント行の並びとも揃える）。</summary>
    private static readonly PaletteMode[] Cycle =
    {
        PaletteMode.All, PaletteMode.File, PaletteMode.Text, PaletteMode.Symbol, PaletteMode.Line, PaletteMode.Command,
    };

    /// <summary>場所を含む検索結果を扱うモード（プレビューを出す）。</summary>
    public bool IsNavigation => Mode is PaletteMode.All or PaletteMode.File or PaletteMode.Text
        or PaletteMode.Symbol or PaletteMode.Line;

    public static PaletteQuery Parse(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return new PaletteQuery(PaletteMode.All, "");

        var mode = ModeOf(input[0]);
        return mode is null
            ? new PaletteQuery(PaletteMode.All, input.Trim())
            : new PaletteQuery(mode.Value, input[1..].Trim());
    }

    /// <summary>行モードで指定された行番号（1始まり）。数字以外・0以下は null。</summary>
    public int? LineNumber =>
        Mode == PaletteMode.Line && int.TryParse(Text, out var line) && line > 0 ? line : null;

    private static PaletteMode? ModeOf(char c) => c switch
    {
        CommandPrefix => PaletteMode.Command,
        FilePrefix => PaletteMode.File,
        TextPrefix => PaletteMode.Text,
        SymbolPrefix => PaletteMode.Symbol,
        LinePrefix => PaletteMode.Line,
        _ => null,
    };

    /// <summary>モードを表すプレフィックス（コマンドは無印）。</summary>
    public static string PrefixOf(PaletteMode mode) => mode switch
    {
        PaletteMode.Command => CommandPrefix.ToString(),
        PaletteMode.File => FilePrefix.ToString(),
        PaletteMode.Text => TextPrefix.ToString(),
        PaletteMode.Symbol => SymbolPrefix.ToString(),
        PaletteMode.Line => LinePrefix.ToString(),
        _ => "",
    };

    /// <summary>いまの入力を、素のクエリを保ったまま別モードの入力文字列へ組み替える。</summary>
    public string ToInput(PaletteMode mode) => PrefixOf(mode) + Text;

    /// <summary>Tab（<paramref name="direction"/>=+1）／Shift+Tab（-1）で次の探し方へ。</summary>
    public static PaletteMode NextMode(PaletteMode mode, int direction = 1)
        => Cycle[((Array.IndexOf(Cycle, mode) + direction) % Cycle.Length + Cycle.Length) % Cycle.Length];

    public static string LabelOf(PaletteMode mode) => mode switch
    {
        PaletteMode.All => "すべて",
        PaletteMode.Command => "コマンド",
        PaletteMode.File => "ファイル",
        PaletteMode.Text => "テキスト",
        PaletteMode.Symbol => "シンボル",
        PaletteMode.Line => "行",
        _ => "コマンド",
    };

    /// <summary>入力欄の下に出す道案内（どの文字でどこへ行けるか）。</summary>
    public const string ModesHint = "すべて    / ファイル    # テキスト    @ シンボル    : 行    > コマンド";

    /// <summary>道案内に「切替キー」を添える。キーは設定で変えられるので文言に埋めず、実効バインドを
    /// 受け取って組み立てる（<c>palette.nextScope</c> と、パレットを開くキー自身＝中では対象を次へ回す）。
    /// 未割当・重複は落とし、1つも無ければ添えない。</summary>
    public static string HintWith(params string?[] switchKeys)
    {
        var keys = switchKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return keys.Length == 0 ? ModesHint : $"{ModesHint}    ―  {string.Join(" / ", keys)} で切替";
    }
}

/// <summary>ナビゲーション1件分の場所（供給元＝LSP シンボル等を、パレットの語彙へ写した中間形）。</summary>
public readonly record struct PaletteLocation(
    string FullPath, string DisplayPath, int Line, int Column, string Title, string? Note = null);

/// <summary>
/// 検索結果をパレットの行（<see cref="PaletteCommand"/>）へ写す（純ロジック・テスト対象）。
/// 並び順は検索側のランキングをそのまま使う（<see cref="PaletteFilter"/> は通さない）——
/// ファイル名の飛び石一致も grep の順も、検索サービス側が既に決めているため。
/// </summary>
public static class PaletteNavigationItems
{
    /// <summary>1モードあたりの表示上限（一覧はキーボードで辿る前提なので深追いしない）。</summary>
    public const int MaxResults = 50;

    /// <summary>行のタイトルに使う最大文字数（長大な1行でレイアウトを壊さない）。</summary>
    private const int MaxTitleChars = 200;

    public static IReadOnlyList<PaletteCommand> ForFiles(
        IReadOnlyList<FileSearchHit> hits, Func<PaletteTarget, Action> jump)
        => hits.Select(h =>
        {
            var target = new PaletteTarget(h.FullPath);
            return new PaletteCommand(FolderOf(h.RelativePath), NameOf(h.RelativePath), jump(target))
            {
                Target = target,
            };
        }).ToList();

    public static IReadOnlyList<PaletteCommand> ForText(
        IReadOnlyList<ContentSearchHit> hits, string term, Func<PaletteTarget, Action> jump)
        => hits.Select(h =>
        {
            var target = new PaletteTarget(h.FullPath, h.Line, h.Column, term);
            return new PaletteCommand($"{h.RelativePath}:{h.Line}", Truncate(h.LineText.Trim()), jump(target))
            {
                Target = target,
            };
        }).ToList();

    public static IReadOnlyList<PaletteCommand> ForLocations(
        IReadOnlyList<PaletteLocation> locations, Func<PaletteTarget, Action> jump)
        => locations.Select(l =>
        {
            var target = new PaletteTarget(l.FullPath, l.Line, l.Column);
            return new PaletteCommand($"{l.DisplayPath}:{l.Line}", Truncate(l.Title), jump(target), l.Note)
            {
                Target = target,
            };
        }).ToList();

    /// <summary>ワークスペースシンボルをパレットの語彙へ写す。ローカルパスへ解決できないものは落とす
    /// （検索ペインの ToSymbolMatch と同じ判断）。表示パスの綴りはマルチルート対応の
    /// <c>IWorkspaceService.ToDisplayPath</c> を渡してもらう。</summary>
    public static IReadOnlyList<PaletteLocation> FromSymbols(
        IReadOnlyList<LspSymbolInformation> symbols, Func<string, string> toDisplayPath)
    {
        var result = new List<PaletteLocation>();
        foreach (var symbol in symbols)
        {
            if (CodeEditorSupport.TryUriToLocalPath(symbol.Location?.Uri) is not { } path)
                continue;
            result.Add(new PaletteLocation(path, toDisplayPath(path),
                (symbol.Location?.Range?.Start?.Line ?? 0) + 1,
                (symbol.Location?.Range?.Start?.Character ?? 0) + 1,
                symbol.Name, symbol.ContainerName));
            if (result.Count >= MaxResults)
                break;
        }
        return result;
    }

    /// <summary>「いまのファイルの N 行目へ」の1件。開いているファイルが無ければ空。</summary>
    public static IReadOnlyList<PaletteCommand> ForLine(
        string? activeFilePath, string displayPath, int line, Func<PaletteTarget, Action> jump)
    {
        if (string.IsNullOrEmpty(activeFilePath))
            return Array.Empty<PaletteCommand>();

        var target = new PaletteTarget(activeFilePath, line, 1);
        return new[] { new PaletteCommand(displayPath, $"{line} 行目へ", jump(target)) { Target = target } };
    }

    private static string NameOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? relativePath : relativePath[(index + 1)..];
    }

    private static string FolderOf(string relativePath)
    {
        var index = relativePath.LastIndexOf('/');
        return index < 0 ? "" : relativePath[..index];
    }

    private static string Truncate(string text)
        => text.Length <= MaxTitleChars ? text : text[..MaxTitleChars] + "…";
}
