using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Controls;
using Editor.Controls;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Core.Agent;
using Terminal.Tabs;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: ターミナル／エディタの選択テキストに対する右クリックアクション
/// （「AIに聞く」＝AIバーへ即送信、「ブラウザで調べる」＝内蔵ブラウザでBing検索）。
/// メニュー項目はライブラリ側の ContextMenuBuilding フックで各コントロールのネイティブメニュー末尾へ
/// 追加する（選択があるときだけ。スタイルはライブラリが自前のメニュー様式に合わせる）。
/// </summary>
public partial class ShellWindow
{
    // 検索クエリ／タブ名に使う最大長。長すぎる選択はここで切り詰める。
    private const int MaxSearchQueryLength = 300;

    private void OnEditorContextMenuBuilding(object? sender, EditorContextMenuBuildingEventArgs e)
        => AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection);

    private void OnTerminalContextMenuBuilding(object? sender, TerminalContextMenuBuildingEventArgs e)
        => AddSelectionMenuItems(e.Menu, e.SelectedText, e.HasSelection);

    // 選択テキストに対する「AIに聞く」「ブラウザで調べる」をメニュー末尾へ足す。選択が無ければ何もしない。
    private void AddSelectionMenuItems(ContextMenu menu, string selectedText, bool hasSelection)
    {
        if (!hasSelection || string.IsNullOrWhiteSpace(selectedText))
            return;

        menu.Items.Add(new Separator());

        var ask = new MenuItem
        {
            Header = "AIに聞く",
            // 処理中・暖機中は送信できないので無効化（押しても AskAbout 側で弾かれるが見た目も合わせる）。
            IsEnabled = !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
        };
        ask.Click += (_, _) =>
        {
            // AIペインがレイアウトに無ければ左上と入れ替えて表示してから送信する。
            EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
            _vm.AiBar.AskAbout(selectedText);
        };
        menu.Items.Add(ask);

        var search = new MenuItem { Header = "ブラウザで調べる" };
        search.Click += (_, _) => _ = SearchSelectionInBrowserAsync(selectedText);
        menu.Items.Add(search);

        AddWorkflowMenuItems(menu, selectedText);
    }

    // 選択テキストを {{input}} として実行する「AIワークフロー」サブメニューを足す。
    // 入力ありワークフローが無ければ何も足さない。処理中・暖機中は無効化する。
    private void AddWorkflowMenuItems(ContextMenu menu, string input)
    {
        var workflows = _vm.AiBar.Workflow.ListInputWorkflows();
        if (workflows.Count == 0)
            return;

        var parent = new MenuItem
        {
            Header = "AIワークフロー",
            IsEnabled = !_vm.AiBar.IsBusy && !_vm.AiBar.IsWarmingUp,
        };
        foreach (var wf in workflows)
        {
            var id = wf.Id;
            var item = new MenuItem { Header = wf.Name };
            item.Click += (_, _) => RunWorkflowWithInput(id, input);
            parent.Items.Add(item);
        }
        menu.Items.Add(parent);
    }

    // AIペインをワークフローモードで前面に出し、指定ワークフローを構造化 input で実行する。
    // FolderTree／エディタのコンテキストメニュー双方の合流点。
    private void RunWorkflowWithInput(string workflowId, string input)
        => RunWorkflowWithInput(workflowId, WorkflowRunInput.FromText(input));

    private void RunWorkflowWithInput(string workflowId, WorkflowRunInput input)
    {
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Ai);
        _vm.AiBar.Mode = AiBarMode.Workflow;
        _vm.AiBar.IsExpanded = true;
        _vm.AiBar.Workflow.RunWithInput(workflowId, input);
    }

    // 選択テキストを Bing で検索して内蔵ブラウザの新規タブで開く。
    private async Task SearchSelectionInBrowserAsync(string selectedText)
    {
        var query = BuildSearchQuery(selectedText);
        if (string.IsNullOrWhiteSpace(query))
            return;

        // ブラウザペインがレイアウトに無ければ左上と入れ替えて表示してから開く
        // （OpenUrlInBrowserAsync 側の表示処理は、ここで可視化済みなら何もしない）。
        EnsurePaneVisibleOrSwapTopLeft(PaneKind.Browser);

        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(query);
        await OpenUrlInBrowserAsync(url, $"検索: {query}");
    }

    // 改行・連続空白を 1 つの空白へまとめ、長すぎるクエリは切り詰める（URL・タブ名の暴発防止）。
    private static string BuildSearchQuery(string text)
    {
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Length > MaxSearchQueryLength
            ? collapsed[..MaxSearchQueryLength]
            : collapsed;
    }
}
