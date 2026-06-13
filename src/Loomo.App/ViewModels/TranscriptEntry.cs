using System;
using System.Collections.ObjectModel;
using System.Linq;
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

    // 承認カード用
    [ObservableProperty] private bool _isPending;
    private TaskCompletionSource<bool>? _completion;

    // 差分表示用（propose_edit の承認カード）
    public ObservableCollection<DiffLineVm> DiffLines { get; } = new();
    public bool HasDiff => DiffLines.Count > 0;

    // /model の一覧（クリックで切替できるモデル候補）
    public ObservableCollection<ModelChoiceVm> ModelChoices { get; } = new();
    public bool HasModelChoices => ModelChoices.Count > 0;

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
