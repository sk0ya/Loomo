using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>C# ソリューションツリーの右クリックメニューを組み立てる。</summary>
internal static class CSharpSolutionExplorerContextMenuPresenter
{
    internal static ContextMenu Create(
        CSharpSolutionExplorerViewModel viewModel,
        CSharpSolutionNodeViewModel node)
    {
        var menu = new ContextMenu();
        // 動的に生成するため、UI Automationからもソリューション操作の
        // メニューであることを安定して識別できるようにする。
        AutomationProperties.SetAutomationId(menu, "CSharpSolutionActions");
        AutomationProperties.SetName(menu, "C#ソリューション操作");

        if (node.Kind is CSharpSolutionNodeKind.Solution or CSharpSolutionNodeKind.Project)
            AddSolutionActions(menu, viewModel, node);
        else
            AddItemActions(menu, viewModel, node);

        return menu;
    }

    private static void AddSolutionActions(
        ContextMenu menu, CSharpSolutionExplorerViewModel vm, CSharpSolutionNodeViewModel node)
    {
        foreach (var group in CSharpSolutionExplorerPolicy.SolutionActionGroups(node.Kind, node.CanRunTests))
        {
            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());
            foreach (var action in group)
                AddAction(menu, vm, node, action.Action, action.Header);
        }
        menu.Items.Add(new Separator());
        // フォルダーだけの C# ワークスペースには .sln の実体が無い（FullPath が null）。
        // 押しても何も起きない項目を出さないよう、下の AddPathCommands と同じ条件で守る。
        if (!string.IsNullOrWhiteSpace(node.FullPath))
            AddCommand(menu, "OpenProjectFile",
                node.Kind == CSharpSolutionNodeKind.Solution ? "ソリューションファイルを開く" : "プロジェクトファイルを開く",
                () => vm.OpenPath(node.FullPath));
        AddPathCommands(menu, node.FullPath);
    }

    /// <summary>ファイル・フォルダー・参照などの行。開く／パス／所属プロジェクトのビルド。</summary>
    private static void AddItemActions(
        ContextMenu menu, CSharpSolutionExplorerViewModel vm, CSharpSolutionNodeViewModel node)
    {
        if (CSharpSolutionExplorerViewModel.CanOpen(node))
            AddCommand(menu, "Open", "開く", () => vm.Open(node));
        AddPathCommands(menu, node.FullPath);

        // 所属プロジェクトを遡って提示する。ファイルを選んだままビルドしたい、が普通の流れ。
        var owner = CSharpSolutionExplorerPolicy.FindOwningProject(node);
        if (owner is null) return;
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        AddAction(menu, vm, owner, CSharpSolutionAction.Build, $"{owner.Name} をビルド");
        if (owner.CanRunTests)
            AddAction(menu, vm, owner, CSharpSolutionAction.Test, $"{owner.Name} をテスト");
    }

    private static void AddPathCommands(ContextMenu menu, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        AddCommand(menu, "CopyPath", "パスをコピー", () =>
        {
            try { Clipboard.SetText(fullPath); } catch { /* クリップボード占有中は無視 */ }
        });
        AddCommand(menu, "RevealInExplorer", "エクスプローラーで表示", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch { /* 失敗しても左列の操作は続行できる */ }
        });
    }

    private static void AddAction(
        ContextMenu menu,
        CSharpSolutionExplorerViewModel vm,
        CSharpSolutionNodeViewModel node,
        CSharpSolutionAction action,
        string header)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetAutomationId(item, $"CSharpSolutionAction.{action}");
        AutomationProperties.SetName(item, header);
        item.Click += (_, _) => vm.RequestAction(node, action);
        menu.Items.Add(item);
    }

    private static void AddCommand(ContextMenu menu, string id, string header, Action execute)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetAutomationId(item, $"CSharpSolutionCommand.{id}");
        AutomationProperties.SetName(item, header);
        item.Click += (_, _) => execute();
        menu.Items.Add(item);
    }
}
