using sk0ya.Loomo.Core.Harness;
using sk0ya.Loomo.Services.ClaudeCode;

namespace sk0ya.Loomo.App.Views;
/// <summary>
/// ShellWindow: 出自（§43）。コミットしていない行の blame（行末の注記・<c>:Gblame</c> の欄）に、その行を書いた
/// Claude Code の頼みごとを出す。行から頼みごとへ（全文・会話の再開）、頼みごとから「それで変わった場所」へ。
/// 索引（<see cref="ClaudeProvenanceIndex"/>）は記録を裏で読む——ここ（UI スレッド）では、すでに読んだものを引くだけ。
/// </summary>
public partial class ShellWindow {
    private ClaudeProvenanceIndex? _claudeProvenance;
    private ClaudeProvenanceIndex ClaudeProvenance => _claudeProvenance ??= new ClaudeProvenanceIndex(
        _workspace, store: new ProvenanceStore(ProvenanceStore.DefaultPath()));

    /// <summary>blame の欄で Claude の行を押した：頼みごとを通知欄に出す（コミットの行なら差分が開く場所）。</summary>
    private void ShowClaudeBlameRequest(Editor.Controls.Git.EditorBlameLine blame) {
        if (ClaudeProvenance.FromHandle(blame.OriginalLine) is { } write)
            ShowClaudeRequest(write);
    }

    /// <summary>出自の項目を押した回数（§43.6 の測り直し）。使われていなければ外す、の物差し。</summary>
    private ProvenanceUsageLog? _provenanceUsage;
    private ProvenanceUsageLog ProvenanceUsage
        => _provenanceUsage ??= new ProvenanceUsageLog(ProvenanceUsageLog.DefaultPath());

    private void ShowClaudeRequest(ClaudeWrite write) {
        ProvenanceUsage.Record("request.show");
        var at = write.At?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
        ToastService.Info($"この行を書いた頼みごと（{at}）\n{ClaudeTranscriptParser.Shorten(write.Prompt, 400)}");
    }

    /// <summary>頼みごとへの項目（変わった場所・全文のコピー・会話の再開）。コミットしていない行でも、最近コミットした
    /// 行（§43.6 A2a）でも同じ並び。<paramref name="withShow"/>：欄を押すと差分が開く行（コミット済み）には
    /// 「この行を書いた頼みごと」も足す。</summary>
    private void AddClaudeRequestMenuItems(ContextMenu menu, ClaudeWrite write, bool withShow) {
        if (withShow) {
            var show = new MenuItem { Header = $"この行を書いた頼みごと：{ClaudeTranscriptParser.Shorten(write.PromptHead, 40)}" };
            show.Click += (_, _) => ShowClaudeRequest(write);
            menu.Items.Add(show);
        }
        // 置き場から読み戻した頼みごと（§43.6 A2b）で、元の記録がもう無いもの：出せるのは本文だけ。
        // 差分・変わった場所・会話・再開は記録が要るので並べない（押しても何も出ない項目を置かない）。
        if (write.Stored && !File.Exists(write.TranscriptPath)) {
            var gone = new MenuItem { Header = "（元の記録は消えています。残っているのは頼みごとの本文だけ）", IsEnabled = false };
            menu.Items.Add(gone);
            AddClaudeCopyRequestItem(menu, write);
            return;
        }
        var diff = new MenuItem { Header = "この頼みごとの差分を開く" };
        diff.Click += (_, _) => {
            ProvenanceUsage.Record("request.diff");
            OpenClaudeRequestDiff(write);
        };
        menu.Items.Add(diff);
        var read = new MenuItem { Header = "この頼みごとの会話を読む（ターミナルサポート）" };
        read.Click += (_, _) => {
            ProvenanceUsage.Record("request.read");
            OpenClaudeTurnRecord(write);
        };
        menu.Items.Add(read);
        var places = new MenuItem { Header = "この頼みごとで変わった場所…" };
        places.Click += (_, _) => {
            ProvenanceUsage.Record("request.places");
            OpenClaudeRequestPlaces(write);
        };
        menu.Items.Add(places);
        AddClaudeCopyRequestItem(menu, write);
        // セッション ID は記録のファイル名から来る文字列なので、シェルに打つ前に形を確かめる（UUID の文字だけ）。
        if (write.SessionId.Length is > 0 and <= 64 && write.SessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) {
            var resume = new MenuItem { Header = "この会話を再開（新しいタブで claude --resume）" };
            resume.Click += (_, _) => {
                ProvenanceUsage.Record("request.resume");
                ResumeClaudeSession(write.SessionId);
            };
            menu.Items.Add(resume);
        }
    }

    private void AddClaudeCopyRequestItem(ContextMenu menu, ClaudeWrite write) {
        var copy = new MenuItem { Header = "頼みごとの全文をコピー" };
        copy.Click += (_, _) => {
            ProvenanceUsage.Record("request.copy");
            try { Clipboard.SetText(write.Prompt); } catch { /* クリップボード占有中は無視 */ }
        };
        menu.Items.Add(copy);
    }

    private void AddClaudeBlameMenuItems(ContextMenu menu, VimEditorControl control, Editor.Controls.Git.EditorBlameLine blame) {
        if (ClaudeProvenance.FromHandle(blame.OriginalLine) is { } write) {
            AddClaudeRequestMenuItems(menu, write, withShow: false);
            menu.Items.Add(new Separator());
        }
        var close = new MenuItem { Header = "Blame を閉じる", InputGestureText = ":Gblame" };
        close.Click += (_, _) => control.ExecuteCommand("Gblame");
        menu.Items.Add(close);
    }

    /// <summary>頼みごと → その頼みごとの差分（§43.6 A3）。書き換えたファイルを、頼みごとの前と後で Diff ペインに並べる。
    /// 記録とバックアップを読むのは裏で。あとから変わったファイル・前後を組めず抜粋になったファイルは、一覧の見出しで言う。</summary>
    private async void OpenClaudeRequestDiff(ClaudeWrite origin) {
        try {
            if (await ShowClaudeRequestDiffAsync(origin) == 0)
                ToastService.Info("この頼みごとの書き込みは、記録から読めませんでした");
        } catch (Exception ex) {
            ToastService.Error($"頼みごとの差分を組めませんでした: {ex.Message}");
        }
    }

    /// <summary>頼みごとの差分を Diff に出す。返すのは出したファイルの数（0 なら何も出していない）。</summary>
    private async Task<int> ShowClaudeRequestDiffAsync(ClaudeWrite origin) {
        IReadOnlyList<ClaudeWrite> writes = ClaudeProvenance.WritesOfRequest(origin);
        var comparisons = await Task.Run(() => ClaudeRequestComparisons.Build(
            ClaudeRequestDiffBuilder.Build(writes), origin, _workspace.ToDisplayPath));
        if (comparisons.Count > 0)
            ShowDiff(new DiffOpenTarget.Request(comparisons, origin.PromptHead));
        return comparisons.Count;
    }

    /// <summary>行 → 頼みごと → そのときの Claude の返事（§43.6 A4）。ターミナルサポートに記録を留めて、そのターンを開く。
    /// 再開（<c>--resume</c>）と違って何も動かさない——読むだけ。</summary>
    private void OpenClaudeTurnRecord(ClaudeWrite write) => OpenClaudeRecord(write.TranscriptPath, write.PromptLine);

    private async void OpenClaudeRecord(string transcriptPath, int promptLine) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.TerminalSupport);
        try {
            await _terminalSupport.ShowRecordAsync(transcriptPath, promptLine, _activeTerminalTab?.Id);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ToastService.Error($"記録を読めませんでした: {ex.Message}");
        }
    }

    /// <summary>ターミナルサポートのターンから（§39・§43.6 A3）。記録を読むのは裏で。ファイルを書き換えていない
    /// 頼みごと・索引の期間より古い記録は、そう言う。</summary>
    private async void OpenClaudeTurnDiff(string sessionId, int promptLine, string usageKey = "turn.diff") {
        ProvenanceUsage.Record(usageKey);
        IReadOnlyList<ClaudeWrite> writes;
        try {
            writes = await Task.Run(() => ClaudeProvenance.WritesOfRequest(sessionId, promptLine));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ToastService.Error($"記録を読めませんでした: {ex.Message}");
            return;
        }
        if (writes.Count == 0) {
            ToastService.Info("この頼みごとはファイルを書き換えていません（または記録が古くて読んでいません）");
            return;
        }
        OpenClaudeRequestDiff(writes[0]);
    }

    // ===== 軌跡の二声化（§43.6 C）：展開ビューの Claude の段 =====

    private ClaudeDayActivity? _claudeDayActivity;
    /// <summary>読み終えた日のターン。段を描くとき（UI スレッド）はここから引くだけ。</summary>
    private (DateOnly Day, IReadOnlyList<ClaudeTurnSpan> Turns, DateTime LoadedUtc)? _claudeDayTurns;
    private DateOnly? _claudeDayLoading;

    /// <summary>読み直すまでの間。展開ビューは 1 分ごと・記録のたびに組み直すが、記録フォルダーを見に行くのはこの間隔まで。</summary>
    private static readonly TimeSpan ClaudeDayTurnsMaxAge = TimeSpan.FromSeconds(20);

    private void OnTrailTimelineLoaded(object sender, RoutedEventArgs e) {
        if (TrailTimeline.ClaudeTurnsProvider is not null)
            return;
        TrailTimeline.ClaudeTurnsProvider = ClaudeTurnsOfDay;
        TrailTimeline.ClaudeTurnClicked += (_, turn) => OpenClaudeTurnFromTrail(turn);
    }

    /// <summary>その日のターン。手元にあるものを返し、古ければ裏で読み直して、変わっていたら段を描き直させる。</summary>
    private IReadOnlyList<ClaudeTurnSpan> ClaudeTurnsOfDay(DateOnly day) {
        var known = _claudeDayTurns is { } cached && cached.Day == day ? cached : ((DateOnly, IReadOnlyList<ClaudeTurnSpan>, DateTime)?)null;
        if ((known is null || DateTime.UtcNow - known.Value.Item3 > ClaudeDayTurnsMaxAge) && _claudeDayLoading != day)
            _ = LoadClaudeTurnsAsync(day);
        return known?.Item2 ?? [];
    }

    private async Task LoadClaudeTurnsAsync(DateOnly day) {
        _claudeDayLoading = day;
        var activity = _claudeDayActivity ??= new ClaudeDayActivity(_workspace);
        IReadOnlyList<ClaudeTurnSpan> turns;
        try {
            turns = await Task.Run(() => activity.Read(day));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            turns = [];
        } finally {
            if (_claudeDayLoading == day)
                _claudeDayLoading = null;
        }
        var changed = _claudeDayTurns is not { } previous || previous.Day != day || !previous.Turns.SequenceEqual(turns);
        _claudeDayTurns = (day, turns, DateTime.UtcNow);
        if (changed)
            TrailTimeline.RefreshClaude();
    }

    /// <summary>展開ビューの Claude の段を押した：書き換えたターンは頼みごとの差分、そうでなければ会話のその場所。</summary>
    private void OpenClaudeTurnFromTrail(ClaudeTurnSpan turn) {
        if (turn.FilesWritten > 0) {
            OpenClaudeTurnDiff(turn.SessionId, turn.PromptLine, "trail.diff");
            return;
        }
        ProvenanceUsage.Record("trail.read");
        OpenClaudeRecord(turn.TranscriptPath, turn.PromptLine);
    }

    /// <summary>頼みごと → それで変わった場所。ファイルごとに 1 行、選ぶとその書き込みがいまある行へ行く
    /// （行は中身で探す。もう残っていなければファイルの先頭）。</summary>
    private async void OpenClaudeRequestPlaces(ClaudeWrite origin) {
        var writes = ClaudeProvenance.WritesOfRequest(origin);
        var places = await Task.Run(() => writes
            .GroupBy(write => write.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Path: group.Key, Edits: group.Count(), Added: group.Sum(w => w.Lines.Count),
                Line: ClaudeRequestPlaces.FirstLineOf(group.Key, group)))
            .ToList());
        var items = places.Select(place => new PaletteCommand("変わった場所",
            _workspace.ToDisplayPath(place.Path),
            () => {
                ProvenanceUsage.Record("places.jump");
                _ = OpenPathInEditorAsync(place.Path, place.Line ?? 1, 1);
            }) {
            Badge = $"+{place.Added}",
            Detail = string.Join(Environment.NewLine, new[] {
                $"頼みごと: {origin.PromptHead}",
                $"書き込み {place.Edits} 回・足した行 {place.Added}",
                place.Line is { } line ? $"いまの {line} 行目" : "この頼みごとが足した行は、もうファイルに残っていません",
            }),
        }).ToList();
        if (items.Count == 0)
            items.Add(new PaletteCommand("変わった場所", "この頼みごとの書き込みは見つかりませんでした", () => { }));
        OpenCommandPalette(PaletteMode.Command, items);
    }
}

/// <summary>頼みごとの書き込みが、いまのファイルのどこにあるか（中身で探す）。</summary>
internal static class ClaudeRequestPlaces {
    /// <summary>書き込みが足した行のうち、いまのファイルで最初に見つかる行（1 始まり）。読めない・残っていなければ null。</summary>
    public static int? FirstLineOf(string path, IEnumerable<ClaudeWrite> writes) {
        string[] current;
        try { current = File.ReadAllLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return FirstLineOf(current, writes);
    }

    public static int? FirstLineOf(IReadOnlyList<string> current, IEnumerable<ClaudeWrite> writes) {
        var added = new HashSet<string>(
            writes.SelectMany(write => write.Lines).Where(line => line.Trim().Length >= 6), StringComparer.Ordinal);
        for (var i = 0; i < current.Count; i++) {
            if (added.Contains(current[i].TrimEnd()))
                return i + 1;
        }
        return null;
    }
}
