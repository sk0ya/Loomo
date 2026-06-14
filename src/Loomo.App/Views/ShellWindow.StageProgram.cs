using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using sk0ya.Loomo.App.Layout;
using sk0ya.Loomo.App.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// ShellWindow: 配役（舞台の Main＋Sub 配置）の保存・呼び出し。ワークスペース毎に保持し、
/// タイトルバーの「🎬 配役」ドロップダウンから切り替える。状態遷移そのものは
/// <see cref="StageProgramLogic"/>（純ロジック）と <c>ShellWindow.Stage.cs</c> 側が持つ。
/// </summary>
public partial class ShellWindow
{
    /// <summary>このワークスペースに保存した配役（タイトルバーのドロップダウンに並ぶ）。</summary>
    private readonly List<StageProgram> _programs = new();
    /// <summary>現在読み込み中の配役名（即席配置なら null）。</summary>
    private string? _activeProgramName;

    /// <summary>ワークスペース復元時に保存配役を読み込む。</summary>
    private void LoadPrograms(IEnumerable<StageProgram> programs)
    {
        _programs.Clear();
        _programs.AddRange(programs);
        UpdateProgramButton();
    }

    /// <summary>タイトルバーのステージトグルと配役ボタンの表示／ラベルを現状へ同期する。</summary>
    private void UpdateProgramButton()
    {
        if (StageModeToggle is not null)
            StageModeToggle.IsChecked = _stageActive;

        if (ProgramButton is null)   // InitializeComponent 前のガード
            return;
        ProgramButton.Visibility = _stageActive ? Visibility.Visible : Visibility.Collapsed;
        ProgramButtonLabel.Text = _activeProgramName is { Length: > 0 } name
            ? name
            : ProgramActive ? "（未保存）" : "なし";
    }

    private void OnProgramMenuClick(object sender, RoutedEventArgs e)
    {
        if (!_stageActive)
            return;
        BuildProgramPopup();
        ProgramPopup.IsOpen = true;
    }

    /// <summary>ポップアップの中身（保存配役の一覧）を組み直す。</summary>
    private void BuildProgramPopup()
    {
        ProgramPopupList.Children.Clear();

        // 「なし」＝配役を解除してステージ（舞台1枚）へ戻す。常に先頭に置く。
        var none = new Button
        {
            Style = (Style)FindResource("BranchMenuItem"),
            FontSize = 12,
            Content = new TextBlock
            {
                Text = "なし（ステージ／舞台1枚）",
                Foreground = ProgramActive ? (Brush)FindResource("Fg") : (Brush)FindResource("Accent"),
            },
            ToolTip = "配役を解除して舞台1枚のステージへ戻す",
        };
        none.Click += (_, _) =>
        {
            ProgramPopup.IsOpen = false;
            StopProgram();
        };
        ProgramPopupList.Children.Add(none);
        ProgramPopupList.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Border"),
            Margin = new Thickness(2, 4, 2, 4),
        });

        if (_programs.Count == 0)
        {
            ProgramPopupList.Children.Add(new TextBlock
            {
                Text = "保存した配役はまだありません",
                FontSize = 12,
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = (Brush)FindResource("FgDim"),
            });
            return;
        }

        foreach (var prog in _programs)
        {
            var row = new DockPanel { LastChildFill = true };

            var del = new Button
            {
                Content = "✕",
                FontSize = 11,
                ToolTip = "この配役を削除",
                Width = 28,
                Style = (Style)FindResource("BranchMenuItem"),
            };
            del.Click += (_, _) =>
            {
                _programs.RemoveAll(p => p.Name == prog.Name);
                if (_activeProgramName == prog.Name)
                    _activeProgramName = null;
                UpdateProgramButton();
                SaveActiveWorkspaceSnapshot();
                BuildProgramPopup();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            var load = new Button
            {
                Style = (Style)FindResource("BranchMenuItem"),
                FontSize = 12,
                Content = new TextBlock
                {
                    Text = ProgramSummary(prog),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = prog.Name == _activeProgramName
                        ? (Brush)FindResource("Accent")
                        : (Brush)FindResource("Fg"),
                },
            };
            load.Click += (_, _) =>
            {
                ProgramPopup.IsOpen = false;
                LoadProgram(prog.Name);
            };
            row.Children.Add(load);

            ProgramPopupList.Children.Add(row);
        }
    }

    private static string ProgramSummary(StageProgram prog)
    {
        var parts = new List<string> { PaneLabel(prog.Main) };
        parts.AddRange(prog.Subs.Select(s => PaneLabel(s.Kind)));
        return $"{prog.Name}  ({string.Join(" · ", parts)})";
    }

    /// <summary>保存配役を舞台へ立てる（主役＋サブを設定して組み直す）。</summary>
    private void LoadProgram(string name)
    {
        var prog = _programs.FirstOrDefault(p => p.Name == name);
        if (prog is null || !_stageActive || !_paneElements.ContainsKey(prog.Main))
            return;

        _stagePane = prog.Main;
        _stageSubs.Clear();
        foreach (var sub in prog.Subs)
            if (sub.Kind != prog.Main && _paneElements.ContainsKey(sub.Kind)
                && _stageSubs.All(s => s.Kind != sub.Kind) && _stageSubs.Count < StageProgramLogic.MaxSubs)
                _stageSubs.Add(new StageSub(sub.Kind, sub.Dock, sub.Weight));
        _stageRightFraction = prog.RightFraction;
        _stageBottomFraction = prog.BottomFraction;
        _activeProgramName = prog.Name;
        _overviewActive = false;

        UpdateProgramButton();
        RebuildStage();
        FocusPane(_stagePane);
        SaveActiveWorkspaceSnapshot();
    }

    /// <summary>現在の舞台配置（主役＋サブ）を配役として保存（同名は上書き）。</summary>
    private void SaveCurrentAsProgram(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || !_stageActive)
            return;

        var prog = new StageProgram
        {
            Name = name,
            Main = _stagePane,
            Subs = _stageSubs.Select(s => new StageSubSnapshot { Kind = s.Kind, Dock = s.Dock, Weight = s.Weight }).ToList(),
            RightFraction = _stageRightFraction,
            BottomFraction = _stageBottomFraction,
        };
        var existing = _programs.FindIndex(p => p.Name == name);
        if (existing >= 0)
            _programs[existing] = prog;
        else
            _programs.Add(prog);

        _activeProgramName = name;
        UpdateProgramButton();
        SaveActiveWorkspaceSnapshot();
    }

    private void OnProgramSaveClick(object sender, RoutedEventArgs e)
    {
        var name = ProgramNameInput.Text;
        if (string.IsNullOrWhiteSpace(name))
            name = _activeProgramName ?? $"配役 {_programs.Count + 1}";
        SaveCurrentAsProgram(name);
        ProgramNameInput.Clear();
        BuildProgramPopup();
    }
}
