using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// <see cref="Views.CodeOutlineView"/>（LSP コード構造アウトライン＋②呼び出し解析のネイティブ WPF 表示）の
/// ビューモデル。<see cref="Views.ShellWindow"/> が LSP から組んだ内部モデル（<see cref="OutlineNode"/> ツリー・
/// <see cref="CallPanels"/>・案内）を受け取り、バインド可能な表示ツリーへ写す。構造（アウトライン）は編集でしか
/// 変わらないので <see cref="ShowOutline"/> で作り直し、キャレット追従（②の差し替え・current 付替え）は
/// <see cref="SetCurrentAndPanels"/> で<b>ツリーを作り直さず</b>行う（折りたたみ状態を保つ）。
/// </summary>
public sealed partial class CodeOutlineViewModel : ObservableObject
{
    /// <summary>案内（未接続／未導入）を表示中か。false ならアウトライン＋②パネルを表示。</summary>
    [ObservableProperty] private bool _isNotice;

    /// <summary><see cref="IsNotice"/> の反転（アウトライン領域の表示条件・XAML から使う）。</summary>
    public bool IsOutline => !IsNotice;
    partial void OnIsNoticeChanged(bool value) => OnPropertyChanged(nameof(IsOutline));

    // ---- アウトライン ----
    public ObservableCollection<CodeOutlineItem> Roots { get; } = new();

    /// <summary>アウトラインが空（シンボル無し）か。プレースホルダ表示に使う。</summary>
    [ObservableProperty] private bool _isOutlineEmpty;

    // current ハイライトの付替え用に全ノードを平坦保持する（ツリーを辿らず flip できる）。
    private readonly List<CodeOutlineItem> _allItems = new();

    // ---- ②呼び出し解析 ----
    /// <summary>②の見出し（「◯◯ の呼び出し関係」）。対象シンボル未解決なら null。</summary>
    [ObservableProperty] private string? _callTitle;
    /// <summary><see cref="CallTitle"/> が非空か（見出し／区切り線の表示条件）。</summary>
    public bool HasCallTitle => !string.IsNullOrEmpty(CallTitle);
    partial void OnCallTitleChanged(string? value) => OnPropertyChanged(nameof(HasCallTitle));

    public ObservableCollection<CodeCallSection> Sections { get; } = new();

    // ---- 案内（未接続／未導入） ----
    [ObservableProperty] private string _noticeMessage = "";
    [ObservableProperty] private string? _serverLine;
    /// <summary><see cref="ServerLine"/> が非空か。</summary>
    public bool HasServerLine => !string.IsNullOrEmpty(ServerLine);
    partial void OnServerLineChanged(string? value) => OnPropertyChanged(nameof(HasServerLine));
    [ObservableProperty] private string? _commandText;
    /// <summary><see cref="CommandText"/> が非空か。</summary>
    public bool HasCommandText => !string.IsNullOrEmpty(CommandText);
    partial void OnCommandTextChanged(string? value) => OnPropertyChanged(nameof(HasCommandText));
    [ObservableProperty] private bool _showInstall;
    [ObservableProperty] private bool _showDocs;
    [ObservableProperty] private bool _showSettings;

    /// <summary>「インストール」ボタンの再判定ヒント（現在ファイルの拡張子）。実際の対象は ShellWindow が再評価する。</summary>
    public string? NoticeExtension { get; private set; }
    /// <summary>「導入手順」ボタンで開く URL。</summary>
    public string? NoticeDocsUrl { get; private set; }

    /// <summary>アウトライン＋②パネルを（作り直して）表示する。構造が変わったとき（初回・編集後）に呼ぶ。</summary>
    internal void ShowOutline(IReadOnlyList<OutlineNode> roots, int currentLine1, CallPanels panels)
    {
        IsNotice = false;

        Roots.Clear();
        _allItems.Clear();
        foreach (var node in roots)
            Roots.Add(BuildItem(node));
        IsOutlineEmpty = Roots.Count == 0;

        ApplyCurrent(currentLine1);
        ApplyPanels(panels);
    }

    /// <summary>
    /// キャレット追従：ツリーは作り直さず、current ハイライトの付替えと②パネルの差し替えだけ行う
    /// （折りたたみ状態・スクロールを保つ）。<paramref name="currentLine1"/> は 1 始まり（0＝current なし）。
    /// </summary>
    internal void SetCurrentAndPanels(int currentLine1, CallPanels panels)
    {
        ApplyCurrent(currentLine1);
        ApplyPanels(panels);
    }

    /// <summary>案内（未接続／未導入）を表示する。</summary>
    internal void ShowNotice(LspNoticeModel.Notice notice)
    {
        IsNotice = true;
        NoticeMessage = notice.Message;
        ServerLine = notice.ServerName is null ? null : $"対応サーバー: {notice.ServerName}";
        CommandText = notice.InstallCommand;
        ShowInstall = notice.ShowInstall;
        ShowDocs = notice.ShowDocs;
        ShowSettings = notice.ShowSettings;
        NoticeExtension = notice.Extension;
        NoticeDocsUrl = notice.DocsUrl;
    }

    private void ApplyCurrent(int currentLine1)
    {
        foreach (var item in _allItems)
            item.IsCurrent = currentLine1 > 0 && item.DataLine1 == currentLine1;
    }

    private void ApplyPanels(CallPanels panels)
    {
        var result = CallPanelModel.Build(panels);
        CallTitle = string.IsNullOrEmpty(result.Target) ? null : $"{result.Target} の呼び出し関係";

        Sections.Clear();
        foreach (var s in result.Sections)
        {
            var section = new CodeCallSection(s.Title, s.TotalCount, s.Overflow);
            foreach (var r in s.Rows)
                section.Rows.Add(new CodeCallRow(r.Symbol, r.FileName, r.Line1, r.Path));
            Sections.Add(section);
        }
    }

    private CodeOutlineItem BuildItem(OutlineNode node)
    {
        var (glyph, brushKey, title) = CodeOutline.KindBadge(node.Kind);
        var item = new CodeOutlineItem
        {
            Glyph = glyph,
            GlyphBrushKey = brushKey, // パレットの Sym* キー。ビューが SetResourceReference で張る（テーマ追従）。
            KindTitle = title,
            Name = node.Name,
            Signature = node.Detail,
            DataLine1 = node.Line0 + 1,      // Range.Start（0 始まり）→ 1 始まり。current ハイライトの一致キー。
            JumpLine1 = node.NameLine0 + 1,  // SelectionRange.Start（名前の行）→ ジャンプ先（宣言行に着地）。
        };
        foreach (var child in node.Children)
            item.Children.Add(BuildItem(child));
        _allItems.Add(item);
        return item;
    }
}

/// <summary>アウトラインの 1 ノード（クラス／メソッド等）。折りたたみ状態と current ハイライトだけ可変。</summary>
public sealed partial class CodeOutlineItem : ObservableObject
{
    public string Glyph { get; init; } = "";
    /// <summary>グリフ色のパレットリソースキー（<c>Sym*</c>）。ビューが SetResourceReference でテーマ追従させる。</summary>
    public string GlyphBrushKey { get; init; } = "SymNamespace";
    public string KindTitle { get; init; } = "";
    public string Name { get; init; } = "";
    public string Signature { get; init; } = "";
    public bool HasSignature => Signature.Length > 0;

    /// <summary>current ハイライトの一致キー（1 始まり・<c>Range.Start</c> 由来）。<see cref="ApplyCurrent"/> で使う。</summary>
    public int DataLine1 { get; init; }

    /// <summary>クリックでジャンプする行（1 始まり・<c>SelectionRange.Start</c>＝名前の行。宣言に着地する）。</summary>
    public int JumpLine1 { get; init; }

    public ObservableCollection<CodeOutlineItem> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;

    /// <summary>キャレットを含む最深メンバー（左バー＋淡い背景で強調）。</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>折りたたみ状態（既定は展開）。TwoWay バインドで保持する。</summary>
    [ObservableProperty] private bool _isExpanded = true;
}

/// <summary>②呼び出し解析の 1 セクション（呼び出し元／呼び出し先／使用箇所）。</summary>
public sealed class CodeCallSection
{
    public CodeCallSection(string title, int totalCount, int overflow)
    {
        Title = title;
        TotalCount = totalCount;
        Overflow = overflow;
    }

    public string Title { get; }
    public int TotalCount { get; }
    public int Overflow { get; }
    public bool HasOverflow => Overflow > 0;
    public string OverflowText => $"… 他 {Overflow} 件";

    public ObservableCollection<CodeCallRow> Rows { get; } = new();
    public bool HasRows => Rows.Count > 0;
}

/// <summary>②呼び出し解析の 1 行（ジャンプ先付き）。</summary>
public sealed class CodeCallRow
{
    public CodeCallRow(string symbol, string fileName, int line1, string? path)
    {
        Symbol = symbol;
        FileName = fileName;
        Line1 = line1;
        Path = path;
    }

    public string Symbol { get; }
    public bool HasSymbol => Symbol.Length > 0;
    public string FileName { get; }
    public int Line1 { get; }
    /// <summary>「Foo.cs:42」の表示ラベル。</summary>
    public string Location => $"{FileName}:{Line1}";

    /// <summary>ジャンプ先ローカルパス（null＝変換できず＝ジャンプ不可）。</summary>
    public string? Path { get; }
    public bool CanJump => !string.IsNullOrEmpty(Path);
}
