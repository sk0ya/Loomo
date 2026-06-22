using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.ViewModels;

public enum EntryKind { User, Assistant, Tool, Approval, Error, Info, Thinking, Activity }

/// <summary>AIバーの会話トランスクリプト1項目。</summary>
public sealed partial class TranscriptEntry : ObservableObject
{
    public EntryKind Kind { get; init; }

    [ObservableProperty] private string _header = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isCollapsed;
    public string CollapseGlyph => IsCollapsed ? "▶" : "▼";

    /// <summary>展開して見せる中身があるか（本文・差分・承認のいずれか）。
    /// 無い行（診断の1行など）は折りたたみグリフを出さない。</summary>
    public bool HasBody => !string.IsNullOrEmpty(Text) || HasDiff || IsPending;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(HasBody));

    // 承認カード用
    [ObservableProperty] private bool _isPending;
    private TaskCompletionSource<bool>? _completion;

    // 差分表示用（propose_edit の承認カード）
    public ObservableCollection<DiffLineVm> DiffLines { get; } = new();
    public bool HasDiff => DiffLines.Count > 0;

    // /model の一覧（クリックで切替できるモデル候補）
    public ObservableCollection<ModelChoiceVm> ModelChoices { get; } = new();
    public bool HasModelChoices => ModelChoices.Count > 0;

    // 「進行状況」（Activity）の構造化タイムライン。1イベント＝1段（種別アイコン・本文・経過時刻）。
    // 本文（Text）は永続化用の「[時刻] メッセージ」連なりを保ち、表示はこの Steps を使う。
    public ObservableCollection<ActivityStep> Steps { get; } = new();

    /// <summary>永続化済みの進行状況本文（[時刻] 行の連なり）から段階タイムラインを復元する。
    /// セッション復元時に、保存テキストしか無くてもタイムライン表示を組み直せるようにする。</summary>
    public void HydrateActivitySteps()
    {
        Steps.Clear();
        if (string.IsNullOrEmpty(Text)) return;
        foreach (var line in Text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (ActivityStep.FromLogLine(line) is { } step) Steps.Add(step);
        }
    }

    /// <summary>クリックで選べるモデル候補を設定する。クリック時は選択行の ● を移してから
    /// <paramref name="onSelect"/>（実際の切替）を呼ぶ。</summary>
    public void SetModelChoices(IEnumerable<string> names, string current, Action<string> onSelect)
    {
        ModelChoices.Clear();
        foreach (var name in names)
        {
            ModelChoices.Add(new ModelChoiceVm(
                name,
                string.Equals(name, current, StringComparison.OrdinalIgnoreCase),
                chosen =>
                {
                    foreach (var c in ModelChoices) c.IsCurrent = ReferenceEquals(c, chosen);
                    onSelect(chosen.Name);
                }));
        }
        OnPropertyChanged(nameof(HasModelChoices));
    }

    public void AppendText(string chunk) => Text += chunk;

    /// <summary>クリップボードへコピーする際の内容（差分エントリは接頭辞付きで復元）。</summary>
    public string CopyText => HasDiff
        ? string.Join(Environment.NewLine, DiffLines.Select(DiffPrefix))
        : Text;

    private static string DiffPrefix(DiffLineVm line) => line.Kind switch
    {
        DiffLineKind.Added => "+" + line.Text,
        DiffLineKind.Removed => "-" + line.Text,
        DiffLineKind.Gap => "⋯" + line.Text,
        _ => " " + line.Text,
    };

    partial void OnIsCollapsedChanged(bool value) => OnPropertyChanged(nameof(CollapseGlyph));

    partial void OnIsPendingChanged(bool value) => OnPropertyChanged(nameof(HasBody));

    public void BindApproval(TaskCompletionSource<bool> completion)
    {
        _completion = completion;
        IsPending = true;
    }

    /// <summary>+/-/空白/… 接頭辞付きの統合差分テキストを色付き行へ展開する。</summary>
    public void SetDiff(string unified)
    {
        foreach (var raw in unified.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0)
            {
                DiffLines.Add(new DiffLineVm(DiffLineKind.Context, ""));
                continue;
            }

            var (kind, text) = raw[0] switch
            {
                '+' => (DiffLineKind.Added, raw[1..]),
                '-' => (DiffLineKind.Removed, raw[1..]),
                '⋯' => (DiffLineKind.Gap, raw[1..]),
                ' ' => (DiffLineKind.Context, raw[1..]),
                _ => (DiffLineKind.Context, raw)
            };
            DiffLines.Add(new DiffLineVm(kind, text));
        }

        Text = ""; // 差分表示へ切替えるため生サマリは隠す
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasBody));
    }

    [RelayCommand]
    private void ToggleCollapse() => IsCollapsed = !IsCollapsed;

    /// <summary>このエントリの本文（または差分）をクリップボードへコピーする。</summary>
    [RelayCommand]
    private void Copy()
    {
        var content = CopyText;
        if (string.IsNullOrEmpty(content)) return;
        try { Clipboard.SetText(content); }
        catch { /* クリップボードが他プロセスにロックされている場合は無視 */ }
    }

    [RelayCommand]
    private void Approve()
    {
        IsPending = false;
        _completion?.TrySetResult(true);
        Header = "✅ 承認済み: " + Header;
    }

    [RelayCommand]
    private void Reject()
    {
        IsPending = false;
        _completion?.TrySetResult(false);
        Header = "⛔ 拒否: " + Header;
    }
}

/// <summary>進行状況タイムラインの1段が表すイベント種別。種別ごとにノードのアイコンと配色（トーン）が決まる。</summary>
public enum ActivityKind
{
    Info, Config, Send, Think, Response, LiveResponse,
    ToolPrepare, ToolRun, ToolDone, ToolError,
    Usage, Approval, Complete, Warn, Cancel, Error,
    Step
}

/// <summary>進行状況ノードの配色トーン（成否・注意を控えめに色分けする）。</summary>
public enum ActivityTone { Neutral, Accent, Good, Bad, Warn }

/// <summary>進行状況タイムラインの1段。左レールの状態ノード（アイコン＋トーン色）、本文、経過時刻を持つ。
/// ライブ段（生成中などの揮発プレビュー）は <see cref="IsLive"/> を立て、点滅＋等幅で「いま動いている」ことを示す。</summary>
public sealed partial class ActivityStep : ObservableObject
{
    public string Glyph { get; init; } = "•";
    [ObservableProperty] private string _message = "";
    public string TimeLabel { get; init; } = "";
    public ActivityTone Tone { get; init; }
    [ObservableProperty] private bool _isLive;
    public bool HasTime => !string.IsNullOrEmpty(TimeLabel);

    /// <summary>ステップ境界などの見出し段か（タイムライン上で強調表示する）。</summary>
    public bool IsHeader { get; init; }

    // 復元時に先頭絵文字から種別を推定するための、既知アイコン→トーン表。
    private static readonly (string Glyph, ActivityTone Tone)[] Known =
    {
        ("⚙️", ActivityTone.Neutral), ("📤", ActivityTone.Neutral), ("💭", ActivityTone.Neutral),
        ("✍️", ActivityTone.Accent), ("💬", ActivityTone.Accent), ("🔧", ActivityTone.Accent),
        ("⚡", ActivityTone.Accent), ("⏳", ActivityTone.Accent),
        ("✅", ActivityTone.Good), ("🏁", ActivityTone.Good),
        ("❌", ActivityTone.Bad), ("⛔", ActivityTone.Bad),
        ("⚠️", ActivityTone.Warn), ("⏹", ActivityTone.Warn),
        ("📊", ActivityTone.Neutral),
        ("▶", ActivityTone.Accent),
    };

    private static (string Glyph, ActivityTone Tone) ForKind(ActivityKind kind) => kind switch
    {
        ActivityKind.Config => ("⚙️", ActivityTone.Neutral),
        ActivityKind.Send => ("📤", ActivityTone.Neutral),
        ActivityKind.Think => ("💭", ActivityTone.Neutral),
        ActivityKind.Response => ("✍️", ActivityTone.Accent),
        ActivityKind.LiveResponse => ("💬", ActivityTone.Accent),
        ActivityKind.ToolPrepare => ("🔧", ActivityTone.Accent),
        ActivityKind.ToolRun => ("⚡", ActivityTone.Accent),
        ActivityKind.ToolDone => ("✅", ActivityTone.Good),
        ActivityKind.ToolError => ("❌", ActivityTone.Bad),
        ActivityKind.Usage => ("📊", ActivityTone.Neutral),
        ActivityKind.Approval => ("⏳", ActivityTone.Accent),
        ActivityKind.Complete => ("🏁", ActivityTone.Good),
        ActivityKind.Warn => ("⚠️", ActivityTone.Warn),
        ActivityKind.Cancel => ("⏹", ActivityTone.Warn),
        ActivityKind.Error => ("⛔", ActivityTone.Bad),
        ActivityKind.Step => ("▶", ActivityTone.Accent),
        _ => ("•", ActivityTone.Neutral),
    };

    /// <summary>種別と経過時刻・本文から1段を作る。本文が種別アイコンで始まる場合は二重表示を避けて取り除く。</summary>
    public static ActivityStep Create(ActivityKind kind, string time, string message)
    {
        var (glyph, tone) = ForKind(kind);
        return new ActivityStep
        {
            Glyph = glyph,
            Tone = tone,
            TimeLabel = time,
            Message = StripLeadingGlyph(message, glyph),
            IsHeader = kind == ActivityKind.Step,
        };
    }

    /// <summary>復元時：永続化された「[時刻] メッセージ」1行から1段を起こす（種別は先頭絵文字から推定）。</summary>
    public static ActivityStep FromLogLine(string line)
    {
        var m = Regex.Match(line, @"^\[(?<t>[^\]]*)\]\s?(?<msg>.*)$", RegexOptions.Singleline);
        var time = m.Success ? m.Groups["t"].Value : "";
        var message = m.Success ? m.Groups["msg"].Value : line;
        if (string.IsNullOrEmpty(message)) message = line;

        var glyph = "•";
        var tone = ActivityTone.Neutral;
        foreach (var (g, t) in Known)
            if (message.StartsWith(g, StringComparison.Ordinal))
            {
                glyph = g;
                tone = t;
                message = StripLeadingGlyph(message, g);
                break;
            }
        return new ActivityStep { Glyph = glyph, Tone = tone, TimeLabel = time, Message = message };
    }

    private static string StripLeadingGlyph(string message, string glyph)
        => glyph.Length > 0 && message.StartsWith(glyph, StringComparison.Ordinal)
            ? message[glyph.Length..].TrimStart()
            : message;
}

/// <summary>/model 一覧の1行（クリックで切替できるモデル候補）。● は現在のモデル。</summary>
public sealed partial class ModelChoiceVm : ObservableObject
{
    public string Name { get; }

    /// <summary>現在選択中のモデルか（クリックで移動する）。</summary>
    [ObservableProperty] private bool _isCurrent;

    private readonly Action<ModelChoiceVm> _onClick;

    public ModelChoiceVm(string name, bool isCurrent, Action<ModelChoiceVm> onClick)
    {
        Name = name;
        _isCurrent = isCurrent;
        _onClick = onClick;
    }

    [RelayCommand]
    private void Select() => _onClick(this);
}

/// <summary>差分1行の表示モデル（種別に応じた配色を持つ）。</summary>
public sealed class DiffLineVm
{
    private static readonly Brush AddFg = Freeze("#FF9CDCAA");
    private static readonly Brush AddBg = Freeze("#262EA043");
    private static readonly Brush RemFg = Freeze("#FFE3A0A0");
    private static readonly Brush RemBg = Freeze("#26E05252");
    private static readonly Brush CtxFg = Freeze("#FF9D9D9D");
    private static readonly Brush GapFg = Freeze("#FF6E6E6E");
    private static readonly Brush None = Brushes.Transparent;

    public DiffLineKind Kind { get; }
    public string Text { get; }
    public Brush Foreground { get; }
    public Brush Background { get; }

    public DiffLineVm(DiffLineKind kind, string text)
    {
        Kind = kind;
        Text = text;
        (Foreground, Background) = kind switch
        {
            DiffLineKind.Added => (AddFg, AddBg),
            DiffLineKind.Removed => (RemFg, RemBg),
            DiffLineKind.Gap => (GapFg, None),
            _ => (CtxFg, None)
        };
    }

    private static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
