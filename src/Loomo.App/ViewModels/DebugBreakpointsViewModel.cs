using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>ブレークポイントの真実を持つサブ ViewModel。行・条件・有効フラグを管理し、デバッグアダプタへ反映する。
/// エディタのガター表示の元データでもある（<see cref="IDebugSession.RaiseBreakpointsRefreshed"/> でガターを同期させる）。</summary>
public sealed partial class DebugBreakpointsViewModel : ObservableObject
{
    private readonly IDebugService _debug;
    private readonly IDebugSession _session;

    /// <summary>ソースパス（絶対）→ そのファイルのブレークポイント行。行・条件・有効フラグの真実。</summary>
    private readonly Dictionary<string, List<BreakpointViewModel>> _breakpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全ブレークポイントのフラット一覧（管理パネルの表示元）。</summary>
    public ObservableCollection<BreakpointViewModel> Breakpoints { get; } = new();

    /// <summary>ブレークポイントが 1 件でもあるか（パネルの空案内の出し分け）。</summary>
    [ObservableProperty] private bool _hasBreakpoints;

    internal DebugBreakpointsViewModel(IDebugService debug, IDebugSession session)
    {
        _debug = debug;
        _session = session;
    }

    /// <summary>あるファイルのブレークポイントをトグルする（行は 0 始まり）。新しい行集合を返す。
    /// デバッグアダプタへも（1 始まりへ変換して）反映する。</summary>
    public IReadOnlyList<int> ToggleBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!_breakpoints.TryGetValue(path, out var list))
            _breakpoints[path] = list = new List<BreakpointViewModel>();

        var existing = list.FirstOrDefault(b => b.Line0 == line0);
        if (existing is not null)
        {
            list.Remove(existing);
            Breakpoints.Remove(existing);
        }
        else
        {
            var bp = NewBreakpoint(path, line0);
            list.Add(bp);
            Breakpoints.Add(bp);
        }
        if (list.Count == 0) _breakpoints.Remove(path);

        PushBreakpoints(path);
        RefreshFlags();
        return Lines(path);
    }

    /// <summary>あるファイルのブレークポイント行（0 始まり）を返す（エディタ同期用）。条件・有効に関わらず全行。</summary>
    public IReadOnlyList<int> GetBreakpoints(string sourcePath) => Lines(Path.GetFullPath(sourcePath));

    /// <summary>あるファイルのブレークポイントを、ガターのグリフ描き分けに必要なメタ（行0・条件付き・ログポイント・有効）で返す。
    /// 条件付き＝Condition か HitCondition あり、ログポイント＝LogMessage あり（ログポイント優先で描く）。</summary>
    public IReadOnlyList<BreakpointGlyphInfo> GetBreakpointGlyphs(string sourcePath)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!_breakpoints.TryGetValue(path, out var list)) return Array.Empty<BreakpointGlyphInfo>();
        return list.Select(b => new BreakpointGlyphInfo(
            b.Line0,
            HasCondition: !string.IsNullOrWhiteSpace(b.Condition) || !string.IsNullOrWhiteSpace(b.HitCondition),
            IsLogpoint: !string.IsNullOrWhiteSpace(b.LogMessage),
            Enabled: b.Enabled)).ToList();
    }

    /// <summary>あるファイルのカーソル行（0 始まり）のブレークポイントを返す（無ければ null）。ガターからの条件編集用。</summary>
    public BreakpointViewModel? FindBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        return _breakpoints.TryGetValue(path, out var list) ? list.FirstOrDefault(b => b.Line0 == line0) : null;
    }

    /// <summary>カーソル行（0 始まり）にブレークポイントを取得（無ければ作成して）返す。ガターから条件を編集する際に、
    /// その行へまだブレークポイントが無くても置けるようにする。作成時はエディタのガターも同期する。</summary>
    public BreakpointViewModel EnsureBreakpoint(string sourcePath, int line0)
    {
        var path = Path.GetFullPath(sourcePath);
        if (!_breakpoints.TryGetValue(path, out var list))
            _breakpoints[path] = list = new List<BreakpointViewModel>();
        var bp = list.FirstOrDefault(b => b.Line0 == line0);
        if (bp is null)
        {
            bp = NewBreakpoint(path, line0);
            list.Add(bp);
            Breakpoints.Add(bp);
            PushBreakpoints(path);
            RefreshFlags();
            _session.RaiseBreakpointsRefreshed(path);  // エディタのガターへ反映
        }
        return bp;
    }

    /// <summary>1 件のブレークポイントを削除する（管理パネルの ✕）。エディタのガターも同期し直す。</summary>
    [RelayCommand]
    private void RemoveBreakpoint(BreakpointViewModel? bp)
    {
        if (bp is null) return;
        if (_breakpoints.TryGetValue(bp.Path, out var list))
        {
            list.Remove(bp);
            if (list.Count == 0) _breakpoints.Remove(bp.Path);
        }
        Breakpoints.Remove(bp);
        PushBreakpoints(bp.Path);
        RefreshFlags();
        _session.RaiseBreakpointsRefreshed(bp.Path);
    }

    /// <summary>すべてのブレークポイントを削除する。</summary>
    [RelayCommand]
    private void RemoveAllBreakpoints()
    {
        var paths = _breakpoints.Keys.ToList();
        _breakpoints.Clear();
        Breakpoints.Clear();
        foreach (var p in paths) { PushBreakpoints(p); _session.RaiseBreakpointsRefreshed(p); }
        RefreshFlags();
    }

    private IReadOnlyList<int> Lines(string fullPath)
        => _breakpoints.TryGetValue(fullPath, out var list)
            ? list.Select(b => b.Line0).OrderBy(l => l).ToList()
            : Array.Empty<int>();

    /// <summary>新しいブレークポイント行 VM を作り、条件変更時の再送を配線する。</summary>
    private BreakpointViewModel NewBreakpoint(string path, int line0)
    {
        var bp = new BreakpointViewModel(path, line0);
        bp.Changed += b => PushBreakpoints(b.Path);
        return bp;
    }

    /// <summary>そのファイルのブレークポイント（条件込み）をアダプタへ送る。</summary>
    private void PushBreakpoints(string path)
    {
        var models = _breakpoints.TryGetValue(path, out var list)
            ? list.OrderBy(b => b.Line0).Select(b => b.ToModel()).ToList()
            : new List<DebugBreakpoint>();
        _ = _debug.SetBreakpointsAsync(path, models, CancellationToken.None);
    }

    private void RefreshFlags() => HasBreakpoints = Breakpoints.Count > 0;
}
