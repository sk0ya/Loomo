using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// DiffSessionViewModel の「見た」印のパート（§24.107・GitLens 19.6.0 の review sessions）。一覧のファイルに「見た」を付け、
/// そのときのパッチの指紋を覚えておく。一覧を組み直すたび・読み直すたびに、印のあるファイルだけいまのパッチと照らし、
/// 中身が変わっていたら「見たあとに変わった」にする——Claude Code が後ろで書き換えたファイルだけが未読に戻って浮かぶ。
/// 指紋は中身だけで作るので、amend・rebase でコミットが変わっても中身が同じなら印は付いたまま（<see cref="DiffViewedMarks.Fingerprint"/>）。
/// </summary>
public sealed partial class DiffSessionViewModel
{
    private readonly DiffViewedStore? _viewed;

    /// <summary>照らし合わせの世代。一覧が組み直されたら、走っている照合の結果は捨てる。</summary>
    private int _viewedGeneration;

    private string _viewedSummary = "";

    /// <summary>印を付けられるか（置き場がある）。テストや置き場の無いホストでは出さない。</summary>
    public bool SupportsViewedMarks => _viewed is not null;

    /// <summary>そのファイルの印を覚える見方。比較基準の見方は、選んでいる基準（種類＋対象）で分ける。</summary>
    private string? ViewedScope(DiffFileItem item)
        => _viewed is null ? null : DiffViewedMarks.Scope(item, CompareBase.IsWorkingTree ? null : CompareBase.Selection);

    private DiffViewedState ViewedStateOf(DiffFileItem item)
        => _fileNodes.TryGetValue(item, out var node) ? node.ViewedState : DiffViewedState.None;

    /// <summary>
    /// 組み直した一覧のノードへ、前の一覧の印をそのまま引き継ぐ（照らし合わせが終わるまで印が消えて点滅しないように）。
    /// 正しい状態は直後の <see cref="RecheckViewedAsync"/> が決める。
    /// </summary>
    private void CarryViewedStates(IReadOnlyDictionary<string, DiffViewedState> previous, IEnumerable<DiffFileTreeNode> leaves)
    {
        foreach (var leaf in leaves)
        {
            var item = leaf.File!;
            leaf.CanMarkViewed = ViewedScope(item) is not null;
            if (ViewedKey(item) is { } key && previous.TryGetValue(key, out var state))
                leaf.ViewedState = state;
        }
    }

    private string? ViewedKey(DiffFileItem item)
        => ViewedScope(item) is { } scope ? DiffViewedStore.Key(scope, item.FullPath) : null;

    /// <summary>いまの一覧の印（鍵→状態）。組み直しの前に取っておく。</summary>
    private Dictionary<string, DiffViewedState> SnapshotViewedStates()
    {
        var states = new Dictionary<string, DiffViewedState>(StringComparer.Ordinal);
        foreach (var (item, node) in _fileNodes)
            if (node.ViewedState != DiffViewedState.None && ViewedKey(item) is { } key)
                states[key] = node.ViewedState;
        return states;
    }

    /// <summary>
    /// 印のあるファイルだけ、いまのパッチの指紋と照らして状態を決め直す。印の無いファイルは git を引かない
    /// （一覧が大きくても、照らすのは見たことのあるファイルの数だけ）。
    /// </summary>
    private async Task RecheckViewedAsync()
    {
        if (_viewed is null) return;
        var generation = ++_viewedGeneration;
        foreach (var (item, node) in _fileNodes.ToList())
        {
            var scope = ViewedScope(item);
            var remembered = scope is null ? null : await _viewed.GetAsync(scope, item.FullPath);
            if (generation != _viewedGeneration) return;
            if (remembered is null)
            {
                node.ViewedState = DiffViewedState.None;
                continue;
            }
            var fingerprint = await FingerprintAsync(item);
            if (generation != _viewedGeneration) return;
            node.ViewedState = fingerprint is null ? DiffViewedState.None : DiffViewedMarks.StateOf(scope, remembered, fingerprint);
        }
        UpdateViewedSummary();
    }

    private async Task<string?> FingerprintAsync(DiffFileItem item)
    {
        try
        {
            return DiffViewedMarks.Fingerprint(await GetPatchTextAsync(item, 3));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiffViewed] パッチを引けませんでした: {ex.Message}");
            return null;
        }
    }

    private void UpdateViewedSummary()
    {
        var summary = DiffViewedMarks.Summary(Files.Select(ViewedStateOf).ToList());
        if (summary == _viewedSummary) return;
        _viewedSummary = summary;
        OnPropertyChanged(nameof(FileListHeader));
    }

    /// <summary>行の印を押した：見たものは外し、まだ・変わったものは、いまの中身を見たことにする。</summary>
    [RelayCommand]
    private async Task ToggleViewedAsync(DiffFileItem? item)
    {
        item ??= SelectedFile;
        if (item is null || _viewed is null || !_fileNodes.TryGetValue(item, out var node) || ViewedScope(item) is not { } scope)
            return;
        if (node.ViewedState == DiffViewedState.Viewed)
        {
            _viewed.Forget(scope, item.FullPath);
            node.ViewedState = DiffViewedState.None;
        }
        else
        {
            await MarkViewedAsync(item, node, scope);
        }
        UpdateViewedSummary();
    }

    private async Task<bool> MarkViewedAsync(DiffFileItem item, DiffFileTreeNode node, string scope)
    {
        if (await FingerprintAsync(item) is not { } fingerprint)
        {
            SetStatus("このファイルの差分を引けなかったので、印を付けられませんでした。", isError: true);
            return false;
        }
        _viewed!.MarkViewed(scope, item.FullPath, fingerprint);
        // 印を付けている間に一覧が組み直されていたら、新しいノードの方に付ける。
        var current = _fileNodes.FirstOrDefault(pair => ViewedKey(pair.Key) == ViewedKey(item)).Value ?? node;
        current.ViewedState = DiffViewedState.Viewed;
        return true;
    }

    /// <summary>いま選んでいるファイルを見たことにして、次の未読へ（Alt+V）。もう見ていたら印はそのままで次へ。</summary>
    [RelayCommand]
    private async Task MarkViewedAndNextAsync()
    {
        if (SelectedFile is not { } item || _viewed is null) return;
        if (_fileNodes.TryGetValue(item, out var node) && node.ViewedState != DiffViewedState.Viewed
            && ViewedScope(item) is { } scope)
        {
            if (!await MarkViewedAsync(item, node, scope)) return;
            UpdateViewedSummary();
        }
        NextUnviewed();
    }

    /// <summary>まだ見ていない（見たあとに変わったものを含む）次のファイルへ。末尾まで来たら先頭へ回る。</summary>
    [RelayCommand]
    private void NextUnviewed()
    {
        if (Files.Count == 0) return;
        var states = Files.Select(ViewedStateOf).ToList();
        var index = DiffViewedMarks.NextUnviewed(states, SelectedFile is null ? -1 : Files.IndexOf(SelectedFile));
        if (index < 0)
        {
            SetStatus("一覧のファイルはすべて見ました。", isError: false);
            return;
        }
        SelectedFile = Files[index];
    }
}
