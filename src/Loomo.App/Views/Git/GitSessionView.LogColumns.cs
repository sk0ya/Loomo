using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// コミット一覧の列の並びと表示／非表示。
///
/// <para>入口は2つ。<b>見出しの右クリック</b>（各列の表示チェック・左右へ移動・既定に戻す）と、
/// <b>見出しのドラッグ</b>（GridView 標準の並べ替え）。どちらも VM の
/// <see cref="GitSessionViewModel.SetLogColumns"/> へ書き、そこで設定へ残る。</para>
///
/// <para>隠すのは <c>Width=0</c> ではなく<b>列ごと GridView から外す</b>。幅0でも列間の隙間と境界の
/// つまみは残り、「見えない列を掴む」ことになる。外した列のインスタンスはここで持ち続け、戻すときに差し戻す。</para>
/// </summary>
public partial class GitSessionView
{
    private readonly Dictionary<string, GridViewColumn> _logColumnsById = new();
    private GitSessionViewModel? _logColumnsVm;
    private bool _applyingLogColumns;

    private void SetupLogColumns()
    {
        foreach (var column in LogGridView.Columns)
            foreach (var id in GitLogColumnLayout.DefaultOrder)
                if (Equals(column.Header, GitLogColumnLayout.Title(id)))
                    _logColumnsById[id] = column;

        // 既定の MenuItem スタイルはチェック印を描かないので、チェック表示を持つ共通メニューの見た目を使う。
        var menu = new ContextMenu { Style = (Style)FindResource("FileContextMenu") };
        menu.Opened += OnLogColumnHeaderMenuOpened;
        LogGridView.ColumnHeaderContextMenu = menu;
        LogGridView.Columns.CollectionChanged += OnLogColumnsCollectionChanged;
    }

    private void AttachLogColumns(GitSessionViewModel? vm)
    {
        if (_logColumnsVm is not null) _logColumnsVm.LogColumnsChanged -= OnVmLogColumnsChanged;
        _logColumnsVm = vm;
        if (vm is not null) vm.LogColumnsChanged += OnVmLogColumnsChanged;
        ApplyLogColumns();
    }

    private void OnVmLogColumnsChanged(object? sender, EventArgs e) => ApplyLogColumns();

    /// <summary>VM の並び・表示を GridView の列へ反映する。並びが既に一致していれば触らない
    /// （列を出し入れすると見出しと全行のセルが作り直され、幅の計測やつまみの組み直しが走る）。
    ///
    /// <para><b>Move／途中への Insert ではなく、全部外して並べ直す。</b>GridViewRowPresenter は行のセルを
    /// <b>列が足された順</b>のまま持ち、表示位置とは別の番号で引く。並べ替えても子の順は変わらないので、
    /// 「最後の子＝最後の列」と見て隙間を外す <see cref="ColumnGapGridViewRowPresenter"/> が別の列の
    /// 隙間を外してしまい、最後に来た列の中身が隙間ぶん欠ける（日時を末尾へ回すと末尾が「…」になった）。
    /// 足し直せば子の順が表示順に揃う。</para></summary>
    private void ApplyLogColumns(bool force = false)
    {
        var order = _logColumnsVm?.LogColumnOrder ?? GitLogColumnLayout.DefaultOrder;
        var hidden = _logColumnsVm?.LogHiddenColumns ?? [];
        var wanted = GitLogColumnLayout.Visible(order, hidden)
            .Where(_logColumnsById.ContainsKey)
            .Select(id => _logColumnsById[id])
            .ToList();
        var columns = LogGridView.Columns;
        if (!force && columns.SequenceEqual(wanted)) return;

        _applyingLogColumns = true;
        try
        {
            // Clear（＝Reset 通知）は使わない。見出し行が Reset 後に見出しドラッグでの並べ替えを受け付けなくなる。
            for (var i = columns.Count - 1; i >= 0; i--) columns.RemoveAt(i);
            foreach (var column in wanted) columns.Add(column);
        }
        finally { _applyingLogColumns = false; }
    }

    /// <summary>見出しのドラッグで並びが変わったら VM（＝設定）へ書き戻し、セルの順を揃えるために
    /// 列を並べ直す（理由は <see cref="ApplyLogColumns"/>。変更通知の中では列を触れないので後回しにする）。</summary>
    private void OnLogColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_applyingLogColumns || e.Action != NotifyCollectionChangedAction.Move) return;
        if (_logColumnsVm is { } vm)
        {
            var visible = LogGridView.Columns.Select(LogColumnId).OfType<string>().ToList();
            vm.SetLogColumns(
                GitLogColumnLayout.MergeVisibleOrder(vm.LogColumnOrder, vm.LogHiddenColumns, visible),
                vm.LogHiddenColumns);
        }
        Dispatcher.BeginInvoke(new Action(() => ApplyLogColumns(force: true)), DispatcherPriority.Background);
    }

    private string? LogColumnId(GridViewColumn? column)
    {
        if (column is null) return null;
        foreach (var (id, candidate) in _logColumnsById)
            if (ReferenceEquals(candidate, column)) return id;
        return null;
    }

    /// <summary>見出しの右クリック。押した列（末尾の埋め草なら無し）に効く移動と、全列の表示チェックを並べる。</summary>
    private void OnLogColumnHeaderMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if (_logColumnsVm is not { } vm) return;
        var order = vm.LogColumnOrder;
        var hidden = vm.LogHiddenColumns;
        var target = LogColumnId((menu.PlacementTarget as GridViewColumnHeader)?.Column);

        if (target is not null)
        {
            var visible = GitLogColumnLayout.Visible(order, hidden);
            var index = visible.ToList().IndexOf(target);
            var title = GitLogColumnLayout.Title(target);
            menu.Items.Add(MenuItem($"「{title}」を左へ移動", index > 0,
                () => vm.SetLogColumns(GitLogColumnLayout.MoveVisible(order, hidden, target, -1), hidden)));
            menu.Items.Add(MenuItem($"「{title}」を右へ移動", index >= 0 && index < visible.Count - 1,
                () => vm.SetLogColumns(GitLogColumnLayout.MoveVisible(order, hidden, target, +1), hidden)));
            menu.Items.Add(new Separator());
        }

        // 表示チェックは並び順で出す＝メニューを見れば今の左右の並びも分かる。
        foreach (var id in order)
        {
            var shown = !hidden.Contains(id);
            var item = MenuItem(GitLogColumnLayout.Title(id), GitLogColumnLayout.CanHide(id), () =>
            {
                var next = shown ? hidden.Append(id).ToList() : hidden.Where(h => h != id).ToList();
                vm.SetLogColumns(order, next);
            });
            item.IsCheckable = true;
            item.IsChecked = shown;
            if (!GitLogColumnLayout.CanHide(id))
            {
                item.ToolTip = "一覧の本体なので隠せません（並べ替えはできます）";
                ToolTipService.SetShowOnDisabled(item, true);
            }
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var isDefault = order.SequenceEqual(GitLogColumnLayout.DefaultOrder) && hidden.Count == 0;
        menu.Items.Add(MenuItem("既定の列に戻す", !isDefault,
            () => vm.SetLogColumns(GitLogColumnLayout.DefaultOrder, [])));

        static MenuItem MenuItem(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
    }
}
