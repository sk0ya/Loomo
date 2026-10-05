namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナルの出力を「コマンド1回分」の素材として扱う（§24.21）。
/// OSC 133 の C〜D で切り出された出力をコマンドごとに前回・今回の2回分だけメモリに持ち、
/// パレットから<b>前回の出力と比較する</b>（Diff のアドホック比較へ送る）／<b>エディタで開く</b>。
/// 永続化はしない（<see cref="TerminalCommandOutputs"/> の注記）。</summary>
public partial class ShellWindow {
    /// <summary>ワークスペースごとの置き場。裏のワークスペースのタブで終わった実行も、その持ち主へ積む。</summary>
    private readonly Dictionary<Guid, TerminalCommandOutputs> _terminalOutputs = new();

    /// <summary>出力は D の少し後に届く（Terminal 1.0.38〜 <c>CommandOutputCaptured</c>）。ConPTY が OSC 133 の
    /// 印を本文より先に流すので、ライブラリは描画が落ち着いてから切り出す——D と同じ瞬間には揃わない。</summary>
    private void HookTerminalCommandOutputs(TerminalTab tab)
        => tab.View.CommandOutputCaptured += (_, e) => {
            if (RecentTerminalCommands.Normalize(e.CommandLine) is { } command)
                RecordTerminalCommandOutput(tab.Id,
                    new TerminalCommandOutput(command, e.Output, e.ExitCode, DateTime.UtcNow, e.HeadLost));
        };

    private void RecordTerminalCommandOutput(Guid tabId, TerminalCommandOutput output) {
        var owner = _terminalWorkspaces.FirstOrDefault(pair => pair.Value.Tabs.Any(t => t.Id == tabId)).Key;
        if (owner == Guid.Empty)
            return;
        if (!_terminalOutputs.TryGetValue(owner, out var store))
            _terminalOutputs[owner] = store = new TerminalCommandOutputs();
        store.Record(output);
    }

    private TerminalCommandOutputs? ActiveTerminalOutputs
        => _activeWorkspace is { } active && _terminalOutputs.TryGetValue(active.Id, out var store) ? store : null;

    private const string TerminalOutputCategory = "コマンドの出力";

    /// <summary>「コマンドの出力を前回と比較」。同じコマンドを2回以上実行したものだけを新しい順に並べる。</summary>
    private void OpenTerminalOutputComparisons() {
        var pairs = ActiveTerminalOutputs?.Pairs ?? [];
        var items = pairs.Select(pair => {
            var (previous, latest) = pair;
            return new PaletteCommand(TerminalOutputCategory, RecentTerminalCommands.Title(latest.Command),
                () => ShowDiff(new DiffOpenTarget.Comparison(TerminalCommandOutputs.Compare(previous, latest)))) {
                Badge = PairBadge(previous, latest),
                Detail = TerminalCommandOutputs.PairDetail(previous, latest),
            };
        }).ToList();
        if (items.Count == 0)
            items.Add(EmptyTerminalOutputItem(
                "比べられる出力はまだありません",
                "同じコマンドを可視ターミナルで2回実行すると、前回と今回の出力をここから比べられます。"
                + Environment.NewLine + "出力は再起動で消えます（保存しない）。"));
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>「コマンドの出力をエディタで開く」。覚えている各コマンドの今回の出力を新しい順に並べる。</summary>
    private void OpenTerminalOutputs() {
        var outputs = ActiveTerminalOutputs?.Latest ?? [];
        var items = outputs.Select(output => new PaletteCommand(TerminalOutputCategory,
            RecentTerminalCommands.Title(output.Command), () => _ = OpenTerminalOutputInEditorAsync(output)) {
            Badge = RecentTerminalCommands.Badge(output.ExitCode) is { Length: > 0 } badge ? badge : null,
            Detail = TerminalCommandOutputs.OutputDetail(output),
        }).ToList();
        if (items.Count == 0)
            items.Add(EmptyTerminalOutputItem(
                "開ける出力はまだありません",
                "可視ターミナルでコマンドを実行し終えると、その出力をここからエディタで開けます。"
                + Environment.NewLine + "出力は再起動で消えます（保存しない）。"));
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>行の右端に成否の移り変わり（「✗ 1 → ✓」）。どちらも終了コード不明なら出さない。</summary>
    private static string? PairBadge(TerminalCommandOutput previous, TerminalCommandOutput latest) {
        var before = RecentTerminalCommands.Badge(previous.ExitCode);
        var after = RecentTerminalCommands.Badge(latest.ExitCode);
        return before.Length == 0 && after.Length == 0 ? null : $"{(before.Length == 0 ? "?" : before)} → {(after.Length == 0 ? "?" : after)}";
    }

    private static PaletteCommand EmptyTerminalOutputItem(string title, string detail)
        => new(TerminalOutputCategory, title, () => { }) { Detail = detail };

    private async Task OpenTerminalOutputInEditorAsync(TerminalCommandOutput output) {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Editor);
        await _editor.OpenDocumentAsync(new EditorDocument {
            FileName = TerminalCommandOutputs.DocumentName(output),
            Content = output.Text,
            OnSaved = _ => { },   // 読むための写し：保存しても残さない（ペグボードの text と同じ）
        });
    }
}
