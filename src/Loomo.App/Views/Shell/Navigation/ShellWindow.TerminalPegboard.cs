namespace sk0ya.Loomo.App.Views;
/// <summary>ShellWindow: ターミナルの出力・選択を<b>出典付き</b>でペグボードへ残し、カードからそのタブの該当箇所へ戻る（§24.26）。
/// §24.24 のブラウザの抜き書きと同じ形——残すのは本文と出典（タブ・コマンド・場所・時刻・終了コード）、戻るのは
/// カードの「出典を開く」。コマンドの境界は §24.21 の切り出し（<see cref="TerminalCommandOutputs"/>）をそのまま使う。
/// 右クリックで走るのは選択1つ分の照合だけで、キー入力ごとの仕事は増やさない（§31.15）。</summary>
public partial class ShellWindow {
    /// <summary>ターミナルの右クリック（選択があるときだけ開く）に足す「素材」の束。</summary>
    private void AddTerminalMaterialMenuItems(ContextMenu menu, TerminalTabView? view, string selectedText, bool hasSelection) {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText))
            return;
        var tab = view is null ? null : FindTerminalTab(view);
        // 選択がどのコマンドの出力の中にあるか。分かれば出典にコマンドが付き、出力全体も残せる。
        var containing = tab is null ? null : FindTerminalOutputContaining(tab.Id, selectedText);

        var pin = new MenuItem {
            Header = "選択をペグボードへ残す",
            ToolTip = containing is null
                ? "このタブと場所・時刻を出典として残す（カードの「出典を開く」でここへ戻る）"
                : $"「{RecentTerminalCommands.Title(containing.Command)}」の出力として出典付きで残す（カードの「出典を開く」でここへ戻る）",
        };
        pin.Click += (_, _) => PinTerminalSelection(tab, selectedText, containing);
        menu.Items.Add(pin);
        if (containing is not null) {
            var whole = new MenuItem {
                Header = $"コマンドの出力をペグボードへ残す（{ShortCommand(containing.Command)}）",
                ToolTip = "選択を含むコマンド1回分の出力を、コマンド・場所・終了コード付きで残す",
            };
            whole.Click += (_, _) => PinTerminalOutput(containing);
            menu.Items.Add(whole);
        }
        // コンポーザはコマンドを組む作業台なので出典は付けない（§24.24 と同じ判断：出典の行が混ざると実行されてしまう）。
        var composer = new MenuItem { Header = "選択をコンポーザへ送る", ToolTip = "コンポーザの本文末尾へ追記する" };
        composer.Click += (_, _) => InsertIntoComposer(selectedText);
        menu.Items.Add(composer);
    }

    /// <summary>メニューの見出しに入れるコマンド（長いものは頭だけ）。</summary>
    private static string ShortCommand(string command) {
        var title = RecentTerminalCommands.Title(command);
        return title.Length <= 30 ? title : title[..29] + "…";
    }

    /// <summary>可視ターミナルのタブを、どのワークスペースのものかを問わず探す（裏の部屋・ワークスペース前の仮の集合も）。</summary>
    private TerminalTab? FindTerminalTab(TerminalTabView view)
        => _terminalWorkspaces.Values.SelectMany(w => w.Tabs)
            .Concat(_scratchTerminalWorkspace.Tabs)
            .FirstOrDefault(t => ReferenceEquals(t.View, view));

    private TerminalCommandOutput? FindTerminalOutputContaining(Guid tabId, string selection) {
        var owner = _terminalWorkspaces.FirstOrDefault(pair => pair.Value.Tabs.Any(t => t.Id == tabId)).Key;
        return owner != Guid.Empty && _terminalOutputs.TryGetValue(owner, out var store)
            ? TerminalPegboardMaterial.FindContaining(store.All, tabId, selection)
            : null;
    }

    /// <summary>タブの見出し（TABS に出ている名前。人が付けた名前があればそれを含む）。閉じたタブは null。</summary>
    private string? TerminalTabTitle(Guid tabId)
        => _vm.Tabs.TerminalTabs.FirstOrDefault(t => t.Id == tabId)?.Title
           ?? _terminalTabs.FirstOrDefault(t => t.Id == tabId)?.View.HeaderTitle;

    private void PinTerminalSelection(TerminalTab? tab, string selection, TerminalCommandOutput? containing) {
        var item = TerminalPegboardMaterial.FromSelection(
            selection, tab?.Id ?? Guid.Empty, tab is null ? null : TerminalTabTitle(tab.Id),
            tab?.View.WorkingDirectory, containing, DateTime.UtcNow);
        if (item is not null)
            AddTerminalMaterialToPegboard(item, "選択");
    }

    private void PinTerminalOutput(TerminalCommandOutput output) {
        if (TerminalPegboardMaterial.FromOutput(output, TerminalTabTitle(output.TabId)) is { } item)
            AddTerminalMaterialToPegboard(item, "コマンドの出力");
        else
            ToastService.Info("このコマンドの出力は空です。");
    }

    private void AddTerminalMaterialToPegboard(TerminalPegboardItem item, string what) {
        _vm.Pegboard.AddContent(item.Content, type: "text", title: item.Title, terminalSource: item.Source);
        ToastService.Success(item.Source.HeadOmitted
            ? $"{what}を出典付きでペグボードへ残しました（長いので先頭を省きました）。"
            : $"{what}を出典付きでペグボードへ残しました。");
    }

    /// <summary>パレット「コマンドの出力をペグボードへ残す」。覚えている各コマンドの今回の出力を新しい順に並べる。</summary>
    private void OpenTerminalOutputsToPin() {
        var outputs = ActiveTerminalOutputs?.Latest ?? [];
        var items = outputs.Select(output => new PaletteCommand(TerminalOutputCategory,
            RecentTerminalCommands.Title(output.Command), () => PinTerminalOutput(output)) {
            Badge = RecentTerminalCommands.Badge(output.ExitCode) is { Length: > 0 } badge ? badge : null,
            Detail = $"{RecentTerminalCommands.Title(output.Command)}{Environment.NewLine}{Environment.NewLine}"
                     + $"{TerminalCommandOutputs.SideTitle("実行", output)}（{TerminalCommandOutputs.LineCount(output.Text)} 行）"
                     + $"{Environment.NewLine}{Environment.NewLine}"
                     + "Enter でペグボードへ残す（コマンド・場所・終了コードを出典として付ける。"
                     + $"{TerminalPegboardMaterial.MaxChars:N0} 字を超える分は先頭を省く）",
        }).ToList();
        if (items.Count == 0)
            items.Add(EmptyTerminalOutputItem(
                "残せる出力はまだありません",
                "可視ターミナルでコマンドを実行し終えると、その出力を出典付きでペグボードへ残せます。"
                + Environment.NewLine + "出力は再起動で消えます（残したカードは消えません）。"));
        OpenCommandPalette(PaletteMode.Command, items);
    }

    /// <summary>パレット「ターミナルの選択をペグボードへ残す」。右クリックと同じ照合で出典を決める。</summary>
    private void PinActiveTerminalSelection() {
        if (_activeTerminalTab is not { } tab || !tab.View.HasSelection
            || tab.View.SelectedText is not { } selection || string.IsNullOrWhiteSpace(selection)) {
            ToastService.Info("ターミナルで残したい範囲を選択してから実行してください。");
            return;
        }
        PinTerminalSelection(tab, selection, FindTerminalOutputContaining(tab.Id, selection));
    }

    /// <summary>カードの「出典を開く」（ターミナルの出典）。タブが生きていればそこへ戻って残した箇所を選択し、
    /// 閉じていれば残した本文をエディタで開く——戻り先が無くても素材そのものは手元にある。</summary>
    private async Task ReturnToTerminalSourceAsync(PegboardItemVm item) {
        var source = item.Snapshot.TerminalSource!;
        var tab = _terminalTabs.FirstOrDefault(t => t.Id == source.TabId);
        if (tab is null) {
            await OpenPegboardTextInEditorAsync(item);
            ToastService.Info("出典のターミナルタブはもう閉じています。残した本文をエディタで開きました。");
            return;
        }
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Terminal);
        ActivateTerminalTab(tab.Id);
        // ペインを出した直後はまだ並べ終わっていないので、スクロールはレイアウトの後に。
        await Dispatcher.InvokeAsync(() => {
            if (!RevealTerminalPassage(tab.View, item.Content))
                ToastService.Info("出典のタブへ戻りました。残した箇所はスクロールバックにもう見当たりません。");
            tab.View.FocusTerminal();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>本文の先頭行の頭を目印にスクロールバックを探し、<b>一番新しい（下にある）一致</b>を選択して見せる。
    /// 同じ出力を何度も流していれば最新の回に当たる——当たらなくても戻ったタブはそのまま（§24.24 と同じく、
    /// 照合の厳密さより「戻れること」を取る）。</summary>
    private static bool RevealTerminalPassage(TerminalTabView view, string content) {
        if (TerminalPegboardMaterial.Anchor(content) is not { } anchor)
            return false;
        var matches = view.FindMatches(anchor, caseSensitive: true);
        return matches.Count > 0 && view.SelectMatch(matches[^1]);
    }
}
