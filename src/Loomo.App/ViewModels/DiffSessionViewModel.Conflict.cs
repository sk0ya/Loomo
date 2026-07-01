using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Diff;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>通常行1行（コンフリクトの外側、両者で共通の内容）。クリック不可。</summary>
public sealed record ConflictOrdinaryLineVm(int LineNumber, string Text);

/// <summary>通常行のまとまり（コンフリクトとコンフリクトの間の地の文）。</summary>
public sealed record ConflictOrdinaryBlockVm(IReadOnlyList<ConflictOrdinaryLineVm> Lines);

/// <summary>コンフリクトの Ours/Theirs ペイン内の1行。<see cref="Kind"/> は <c>"Context"</c>
/// （もう一方の側にも同じ内容の行がある＝共通）か <c>"Distinct"</c>（この側にしかない＝差分）。
/// <see cref="LineNumber"/> はそのコンフリクト内でのローカルな1始まり番号（ファイル全体の行番号ではない）。</summary>
public sealed record ConflictSideLineVm(int LineNumber, string Text, string Kind);

/// <summary>
/// コンフリクト1件（採用操作の対象）。Rider の3-way マージ画面と同じ考え方で、Ours（読み取り専用）/
/// Result（自由編集）/ Theirs（読み取り専用）の3ペインとして表示する。«/» で Ours・Theirs を Result へ
/// その場で取り込めるほか、Result 欄へ直接手で書いて「適用」してもよい。Result の既定は空（未解決）。
/// </summary>
public sealed partial class ConflictRegionVm : ObservableObject
{
    public ConflictRegionVm(
        int index, string oursLabel, string theirsLabel,
        IReadOnlyList<string> oursLines, IReadOnlyList<string> theirsLines)
    {
        Index = index;
        OursLabel = oursLabel;
        TheirsLabel = theirsLabel;

        // Ours→Theirs の行diffを、Ours にしか無い行／Theirs にしか無い行のハイライトに使う
        // （通常の新旧diffではなく「この側だけの内容か」という身元の意味で Distinct を付ける）。
        var diff = DiffUtil.ComputeFull(string.Join('\n', oursLines), string.Join('\n', theirsLines));
        OursDisplayLines = BuildSideLines(diff, skip: DiffLineKind.Added);
        TheirsDisplayLines = BuildSideLines(diff, skip: DiffLineKind.Removed);
    }

    private static IReadOnlyList<ConflictSideLineVm> BuildSideLines(IReadOnlyList<DiffLine> diff, DiffLineKind skip)
    {
        var result = new List<ConflictSideLineVm>();
        var n = 0;
        foreach (var line in diff)
        {
            if (line.Kind == skip) continue;
            n++;
            result.Add(new ConflictSideLineVm(n, line.Text, line.Kind == DiffLineKind.Context ? "Context" : "Distinct"));
        }
        return result;
    }

    /// <summary><see cref="ParsedConflictFile.Regions"/> 内での位置（解決 API 呼び出し・ナビゲーションの識別子）。</summary>
    public int Index { get; }
    public string OursLabel { get; }
    public string TheirsLabel { get; }

    /// <summary>Ours ペインの表示行（そのコンフリクト内でのみの相対行番号＋差分ハイライト種別）。</summary>
    public IReadOnlyList<ConflictSideLineVm> OursDisplayLines { get; }
    /// <summary>Theirs ペインの表示行。</summary>
    public IReadOnlyList<ConflictSideLineVm> TheirsDisplayLines { get; }

    /// <summary>中央（Result）ペインの編集中テキスト。既定は空＝未解決。</summary>
    [ObservableProperty] private string _resultText = "";

    /// <summary>Result ペインの行番号ガター（"1\n2\n3" 形式。TextBlock にそのままバインドすれば改行として描画される）。</summary>
    [ObservableProperty] private string _resultLineNumberText = "1";

    partial void OnResultTextChanged(string value)
    {
        var count = value.Length == 0 ? 1 : value.Replace("\r\n", "\n").Split('\n').Length;
        ResultLineNumberText = string.Join('\n', Enumerable.Range(1, count));
    }

    /// <summary>前へ/次へナビゲーションの現在地か（枠を強調表示する）。</summary>
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// DiffSessionViewModel のコンフリクト解消パート。作業ツリーにマーカーが残る通常のコンフリクトは、
/// ファイル全体を1本の流れ（<see cref="ConflictBlocks"/>：通常行のまとまりとコンフリクトが実際の
/// 出現順に並ぶ）として表示し、コンフリクト部分だけ Ours/Result/Theirs の3ペインにする（Rider 風）。
/// マーカーが書かれない削除/変更の衝突等はファイル全体で ours/theirs から選ばせる。
/// </summary>
public sealed partial class DiffSessionViewModel
{
    /// <summary>コンフリクト解消表示中か（true のときは <see cref="DiffRows"/>/<see cref="SideRows"/> ではなく
    /// <see cref="ConflictBlocks"/> または <see cref="IsWholeFileConflict"/> 側を表示する）。</summary>
    [ObservableProperty] private bool _isConflictMode;

    /// <summary>作業ツリーにマーカーが書かれないコンフリクト（削除/変更の衝突・リネーム等）か。
    /// true のときはリージョンではなくファイル全体の ours/theirs から選ばせる。</summary>
    [ObservableProperty] private bool _isWholeFileConflict;

    /// <summary>「解決済みにする」を押せるか（マーカー方式で、すべてのコンフリクトが解決済みのとき）。</summary>
    [ObservableProperty] private bool _canMarkResolved;

    /// <summary>マーカー方式の残りコンフリクト件数（例:「残り1/2件」）。ファイルを開いた時点の件数を分母にする。</summary>
    [ObservableProperty] private string _conflictProgressText = "";

    /// <summary>前へ/次へナビゲーションの現在位置（例:「2 / 5」）。コンフリクトが無ければ空。</summary>
    [ObservableProperty] private string _conflictPositionText = "";

    /// <summary>このファイルを開いた時点でのコンフリクト件数（進捗表示の分母）。</summary>
    private int _conflictTotalCount;

    /// <summary>前へ/次へナビゲーションの現在位置（<see cref="ConflictBlocks"/> 中の <see cref="ConflictRegionVm"/> の添字）。</summary>
    private int _conflictCursor = -1;

    /// <summary>ファイル全体を実際の出現順に並べた表示ブロック（<see cref="ConflictOrdinaryBlockVm"/> と
    /// <see cref="ConflictRegionVm"/> が混在。ItemsControl は型ごとの暗黙 DataTemplate で振り分ける）。</summary>
    public ObservableCollection<object> ConflictBlocks { get; } = new();

    /// <summary>Ours/Theirs 列見出し（1ファイル内の全コンフリクトで共通なのでファイル単位に1回だけ出す）。</summary>
    [ObservableProperty] private string _conflictOursHeader = "";
    [ObservableProperty] private string _conflictTheirsHeader = "";

    /// <summary>ファイル全体コンフリクト時の ours 側内容（無ければ null＝相手が削除等）。</summary>
    [ObservableProperty] private string? _wholeFileOurs;
    /// <summary>ファイル全体コンフリクト時の theirs 側内容（無ければ null＝自分が削除等）。</summary>
    [ObservableProperty] private string? _wholeFileTheirs;

    /// <summary>マーカー方式のときの解析結果（コンフリクト解決のたびに読み直す）。</summary>
    private ParsedConflictFile? _conflictParsed;

    /// <summary>次のコンフリクトへ（無ければ先頭へ循環）。View 側が対応する枠までスクロールする。</summary>
    [RelayCommand]
    private void NextConflict() => MoveConflictCursor(+1);

    /// <summary>前のコンフリクトへ（無ければ末尾へ循環）。</summary>
    [RelayCommand]
    private void PreviousConflict() => MoveConflictCursor(-1);

    /// <summary>View 側へ「このコンフリクトまでスクロールしてほしい」を通知する（<see cref="ConflictRegionVm.Index"/>）。</summary>
    public event Action<int>? ScrollToConflictRequested;

    private void MoveConflictCursor(int delta)
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        if (regions.Count == 0) return;
        _conflictCursor = ((_conflictCursor + delta) % regions.Count + regions.Count) % regions.Count;
        SetCurrentConflict(regions, _conflictCursor);
    }

    /// <summary>ファイルを開いた直後、先頭のコンフリクトへ自動的に注目する（通常差分の自動ジャンプと同じ考え方）。</summary>
    private void FocusFirstConflict()
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        if (regions.Count == 0) { ConflictPositionText = ""; return; }
        _conflictCursor = 0;
        SetCurrentConflict(regions, 0);
    }

    /// <summary>1件解決した直後、その次にあったコンフリクトへ注目する（無ければ末尾）。</summary>
    private void FocusConflictNear(int resolvedRegionIndex)
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        if (regions.Count == 0)
        {
            _conflictCursor = -1;
            ConflictPositionText = "";
            return;
        }
        var next = regions.FindIndex(r => r.Index > resolvedRegionIndex);
        _conflictCursor = next >= 0 ? next : regions.Count - 1;
        SetCurrentConflict(regions, _conflictCursor);
    }

    private void SetCurrentConflict(IReadOnlyList<ConflictRegionVm> regions, int cursor)
    {
        var current = regions[cursor];
        foreach (var r in regions) r.IsCurrent = ReferenceEquals(r, current);
        ConflictPositionText = $"{cursor + 1} / {regions.Count}";
        ScrollToConflictRequested?.Invoke(current.Index);
    }

    /// <summary>View 側から：Result 欄をクリック/フォーカスしたコンフリクトを「現在地」にする
    /// （前へ/次へで移動したときと同じ状態にし、ツールバーの一括操作の対象を合わせる）。</summary>
    public void FocusConflictRegion(ConflictRegionVm region)
    {
        var regions = ConflictBlocks.OfType<ConflictRegionVm>().ToList();
        var index = regions.IndexOf(region);
        if (index < 0) return;
        _conflictCursor = index;
        SetCurrentConflict(regions, index);
    }

    private ConflictRegionVm? CurrentConflictRegion() =>
        ConflictBlocks.OfType<ConflictRegionVm>().FirstOrDefault(r => r.IsCurrent);

    /// <summary>ツールバーの「現在地のコンフリクトを解決」操作：前へ/次へ、または Result 欄フォーカスで
    /// 選ばれているコンフリクトに対して Ours/Both/Theirs を採用する。コンフリクト行の中にはボタンを置かない。</summary>
    [RelayCommand]
    private Task AcceptCurrentOursAsync() => AcceptRegionAsync(CurrentConflictRegion(), ConflictResolution.Ours);

    [RelayCommand]
    private Task AcceptCurrentTheirsAsync() => AcceptRegionAsync(CurrentConflictRegion(), ConflictResolution.Theirs);

    [RelayCommand]
    private Task AcceptCurrentBothAsync() => AcceptRegionAsync(CurrentConflictRegion(), ConflictResolution.Both);

    [RelayCommand]
    private Task ApplyCurrentResultAsync() => ApplyResultTextAsync(CurrentConflictRegion());

    /// <summary>選択ファイルの表示内容を、Blame／コンフリクトかどうかで振り分けて読み込む。</summary>
    private async Task LoadSelectedContentAsync(DiffFileItem? item)
    {
        if (item?.BlamePath is not null)
        {
            IsConflictMode = false;
            ResetConflictState();
            await LoadBlameAsync(item);
            return;
        }
        IsBlameMode = false;

        if (item?.Entry?.IsConflicted == true)
        {
            IsConflictMode = true;
            await LoadConflictAsync(item);
        }
        else
        {
            IsConflictMode = false;
            ResetConflictState();
            await LoadDiffAsync(item);
        }
    }

    /// <summary>コンフリクト解消表示の状態を非コンフリクト向けに初期化する（Blame・通常差分の両方で使う）。</summary>
    private void ResetConflictState()
    {
        IsWholeFileConflict = false;
        CanMarkResolved = false;
        ConflictProgressText = "";
        ConflictPositionText = "";
        _conflictCursor = -1;
        ConflictOursHeader = "";
        ConflictTheirsHeader = "";
        _conflictTotalCount = 0;
        _conflictParsed = null;
        ConflictBlocks.Clear();
    }

    partial void OnIsConflictModeChanged(bool value) => OnPropertyChanged(nameof(ShowDiffBody));

    private async Task LoadConflictAsync(DiffFileItem item)
    {
        Hunks.Clear();
        OnPropertyChanged(nameof(CanStageHunks));
        DiffRows.Clear();
        SideRows.Clear();

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(item.FullPath);
        }
        catch (Exception ex)
        {
            IsWholeFileConflict = false;
            _conflictParsed = null;
            ConflictBlocks.Clear();
            SetStatus($"ファイルを読み込めませんでした: {ex.Message}", isError: true);
            return;
        }

        var parsed = ConflictMarkerParser.Parse(raw);
        if (parsed.HasConflicts)
        {
            IsWholeFileConflict = false;
            _conflictParsed = parsed;
            _conflictTotalCount = parsed.Regions.Count(r => r.Kind == ConflictRegionKind.Conflict);
            RebuildConflictDisplay(parsed);
            FocusFirstConflict();
            return;
        }

        // 作業ツリーにマーカーが無い＝削除/変更の衝突・リネーム等。ステージの各段から解決させる。
        _conflictParsed = null;
        ConflictBlocks.Clear();
        IsWholeFileConflict = true;
        CanMarkResolved = false;
        var (_, ours, theirs) = await _git.GetConflictSidesAsync(item.Entry!.Path);
        WholeFileOurs = ours;
        WholeFileTheirs = theirs;
    }

    /// <summary>
    /// ファイル全体を通常行のまとまりとコンフリクトが実際に現れる順のまま並べ直す。
    /// 通常行は両者で共通の内容そのもの（クリック不可）、コンフリクトは Ours/Result/Theirs の3ペインになる。
    /// </summary>
    private void RebuildConflictDisplay(ParsedConflictFile parsed)
    {
        ConflictBlocks.Clear();
        for (var i = 0; i < parsed.Regions.Count; i++)
        {
            var region = parsed.Regions[i];
            if (region.Kind == ConflictRegionKind.Ordinary)
            {
                if (region.Lines.Count == 0) continue;
                var lines = new List<ConflictOrdinaryLineVm>(region.Lines.Count);
                for (var k = 0; k < region.Lines.Count; k++)
                    lines.Add(new ConflictOrdinaryLineVm(region.StartLine + k, region.Lines[k]));
                ConflictBlocks.Add(new ConflictOrdinaryBlockVm(lines));
            }
            else
            {
                ConflictBlocks.Add(new ConflictRegionVm(
                    i, region.OursLabel ?? "Ours", region.TheirsLabel ?? "Theirs",
                    region.OursLines, region.TheirsLines));
            }
        }

        // 見出しは1ファイル内の全コンフリクトで共通（同じマージ操作の ours/theirs）なので、最初の1件から決める。
        var firstConflict = parsed.Regions.FirstOrDefault(r => r.Kind == ConflictRegionKind.Conflict);
        ConflictOursHeader = firstConflict is null ? "" : $"Ours（{firstConflict.OursLabel ?? "HEAD"}）";
        ConflictTheirsHeader = firstConflict is null ? "" : $"Theirs（{firstConflict.TheirsLabel ?? "theirs"}）";

        var remaining = parsed.Regions.Count(r => r.Kind == ConflictRegionKind.Conflict);
        CanMarkResolved = remaining == 0;
        ConflictProgressText = _conflictTotalCount == 0 ? "" : $"残り {remaining}/{_conflictTotalCount} 件";
    }

    [RelayCommand]
    private Task AcceptOursAsync(ConflictRegionVm? region) => AcceptRegionAsync(region, ConflictResolution.Ours);

    [RelayCommand]
    private Task AcceptTheirsAsync(ConflictRegionVm? region) => AcceptRegionAsync(region, ConflictResolution.Theirs);

    [RelayCommand]
    private Task AcceptBothAsync(ConflictRegionVm? region) => AcceptRegionAsync(region, ConflictResolution.Both);

    /// <summary>Result 欄に書いた（または «/» で取り込んだ）内容でこのコンフリクトを解決する。</summary>
    [RelayCommand]
    private async Task ApplyResultTextAsync(ConflictRegionVm? region)
    {
        if (region is null || _conflictParsed is null) return;
        if (SelectedFile is not { Entry: not null } item) return;

        var lines = region.ResultText.Length == 0
            ? Array.Empty<string>()
            : region.ResultText.Replace("\r\n", "\n").Split('\n');

        string resolved;
        try
        {
            resolved = ConflictMarkerParser.ResolveRegionWithLines(_conflictParsed, region.Index, lines);
        }
        catch (ArgumentException)
        {
            return; // リージョン構成が既に変わっている（多重クリック等）
        }

        await WriteResolvedAsync(item, resolved, region.Index);
    }

    /// <summary>指定コンフリクトだけ選んだ側で解決し、ディスクへ書き戻して再解析する。他のコンフリクトは元のまま残る。</summary>
    private async Task AcceptRegionAsync(ConflictRegionVm? region, ConflictResolution resolution)
    {
        if (region is null || _conflictParsed is null) return;
        if (SelectedFile is not { Entry: not null } item) return;

        string resolved;
        try
        {
            resolved = ConflictMarkerParser.ResolveRegion(_conflictParsed, region.Index, resolution);
        }
        catch (ArgumentException)
        {
            return; // リージョン構成が既に変わっている（多重クリック等）
        }

        await WriteResolvedAsync(item, resolved, region.Index);
    }

    private async Task WriteResolvedAsync(DiffFileItem item, string resolved, int resolvedRegionIndex)
    {
        try
        {
            await File.WriteAllTextAsync(item.FullPath, resolved);
        }
        catch (Exception ex)
        {
            SetStatus($"書き込みに失敗しました: {ex.Message}", isError: true);
            return;
        }

        var reparsed = ConflictMarkerParser.Parse(resolved);
        _conflictParsed = reparsed;
        RebuildConflictDisplay(reparsed);
        FocusConflictNear(resolvedRegionIndex);
    }

    /// <summary>マーカー方式：すべてのコンフリクトが解決済みのときだけ、その内容を git add してコンフリクトを終える。</summary>
    [RelayCommand]
    private async Task MarkResolvedAsync()
    {
        if (!CanMarkResolved || SelectedFile is not { Entry: not null } item) return;
        var result = await _git.StageAsync(item.Entry.Path);
        SetStatus(result.Success
            ? $"{item.DisplayPath} を解決済みにしました。"
            : $"解決済みにできませんでした: {Truncate(result.Message)}", isError: !result.Success);
        // 成功時は RepositoryChanged 経由で一覧・選択ファイルが読み直され、通常の差分表示へ戻る。
    }

    [RelayCommand]
    private Task AcceptWholeFileOursAsync() => AcceptWholeFileAsync(WholeFileOurs);

    [RelayCommand]
    private Task AcceptWholeFileTheirsAsync() => AcceptWholeFileAsync(WholeFileTheirs);

    /// <summary>ファイル全体コンフリクト：選んだ側の内容をそのまま書き込んで解決済みにする。</summary>
    private async Task AcceptWholeFileAsync(string? content)
    {
        if (content is null || SelectedFile is not { Entry: not null } item) return;
        try
        {
            await File.WriteAllTextAsync(item.FullPath, content);
        }
        catch (Exception ex)
        {
            SetStatus($"書き込みに失敗しました: {ex.Message}", isError: true);
            return;
        }
        var result = await _git.StageAsync(item.Entry.Path);
        SetStatus(result.Success
            ? $"{item.DisplayPath} を解決済みにしました。"
            : $"解決済みにできませんでした: {Truncate(result.Message)}", isError: !result.Success);
    }

    /// <summary>ファイル全体コンフリクト：削除して解決済みにする（相手の削除を受け入れる等）。</summary>
    [RelayCommand]
    private async Task DeleteFileConflictAsync()
    {
        if (SelectedFile is not { Entry: not null } item) return;
        try
        {
            if (File.Exists(item.FullPath))
                File.Delete(item.FullPath);
        }
        catch (Exception ex)
        {
            SetStatus($"削除に失敗しました: {ex.Message}", isError: true);
            return;
        }
        var result = await _git.StageAsync(item.Entry.Path);
        SetStatus(result.Success
            ? $"{item.DisplayPath} を削除して解決済みにしました。"
            : $"解決済みにできませんでした: {Truncate(result.Message)}", isError: !result.Success);
    }
}
