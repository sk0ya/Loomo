using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 軌跡バー右端の通知欄（NotificationCenterViewModel、§21.5c）の検証。
/// 新しい順・未読件数・開いたら既読・上限・操作の実行。静的イベントは並列テストで混ざるので、
/// 購読しない形で組み立てて <see cref="NotificationCenterViewModel.Receive"/> を直接呼ぶ。
/// </summary>
public class NotificationCenterViewModelTests
{
    private static Notification Make(string message, NotificationKind kind = NotificationKind.Info, NotificationAction? action = null)
        => new(Guid.NewGuid(), message, kind, new DateTime(2026, 10, 17, 9, 5, 0), Action: action);

    [Fact]
    public void 空なら既定の1行を出し未読は無い()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);

        Assert.False(vm.HasItems);
        Assert.False(vm.HasUnread);
        Assert.Equal("通知はありません", vm.Summary);
    }

    [Fact]
    public void 新しい順に積み最新を1行に出す()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);

        vm.Receive(Make("一つ目"));
        vm.Receive(Make("二つ目", NotificationKind.Error));

        Assert.Equal(["二つ目", "一つ目"], vm.Items.Select(i => i.Message));
        Assert.Equal("二つ目", vm.Summary);
        Assert.Equal(NotificationKind.Error, vm.Latest?.Kind);
        Assert.Equal(2, vm.UnreadCount);
        Assert.Equal("09:05", vm.Items[0].TimeLabel);
    }

    [Fact]
    public void 開いたら全件既読になり開いている間に届いたものも既読()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);
        vm.Receive(Make("a"));
        vm.Receive(Make("b"));

        vm.IsOpen = true;
        Assert.Equal(0, vm.UnreadCount);

        vm.Receive(Make("c"));
        Assert.False(vm.HasUnread);

        vm.IsOpen = false;
        vm.Receive(Make("d"));
        Assert.Equal(1, vm.UnreadCount);
        Assert.Equal("d", vm.Summary);
    }

    [Fact]
    public void 上限を超えたら古いものから捨てる()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);

        for (var i = 0; i < NotificationCenterViewModel.Capacity + 5; i++)
            vm.Receive(Make($"n{i}"));

        Assert.Equal(NotificationCenterViewModel.Capacity, vm.Items.Count);
        Assert.Equal($"n{NotificationCenterViewModel.Capacity + 4}", vm.Items[0].Message);
        Assert.Equal("n5", vm.Items[^1].Message);
        Assert.Equal("99+", vm.UnreadBadge);
    }

    [Fact]
    public void 消去と1件削除で未読件数と最新が追随する()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);
        vm.Receive(Make("古い"));
        vm.Receive(Make("新しい"));

        vm.RemoveCommand.Execute(vm.Items[0]);
        Assert.Equal("古い", vm.Summary);
        Assert.Equal(1, vm.UnreadCount);

        vm.ClearCommand.Execute(null);
        Assert.False(vm.HasItems);
        Assert.Equal("通知はありません", vm.Summary);
    }

    [Fact]
    public void 最後の1件を消すとポップアップも閉じる()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);
        vm.Receive(Make("唯一"));
        vm.IsOpen = true;

        vm.RemoveCommand.Execute(vm.Items[0]);

        Assert.False(vm.HasItems);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void 操作を実行すると履歴から外れポップアップが閉じる()
    {
        var vm = new NotificationCenterViewModel(subscribe: false);
        var ran = 0;
        vm.Receive(Make("失敗しました", NotificationKind.Error, new NotificationAction("再試行", () => ran++)));
        vm.IsOpen = true;

        var entry = vm.Items[0];
        Assert.True(entry.HasAction);
        Assert.Equal("再試行", entry.ActionLabel);
        vm.RunActionCommand.Execute(entry);

        Assert.Equal(1, ran);
        Assert.False(vm.IsOpen);
        Assert.False(vm.HasItems);
    }
}
