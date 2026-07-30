using System;
using System.IO;
using sk0ya.Loomo.Ai;
using sk0ya.Loomo.App.ViewModels;
using sk0ya.Loomo.Services.Lsp;
using Xunit;

namespace sk0ya.Loomo.Tests;

/// <summary>
/// 促しバー（「言語サーバーが設定されていません／見つかりません」）の抑止まわり。
/// 「今後表示しない」はバーだけでなく EditorSupport の案内にも効く必要があるので、
/// 共有フィルタ <see cref="LspPromptViewModel.Filter"/> の挙動を軸に検証する。
/// </summary>
public class LspPromptViewModelTests
{
    private static (LspPromptViewModel Vm, AiSettings Settings, string SettingsPath) CreateSut()
    {
        var settings = new AiSettings();
        var path = Path.Combine(Path.GetTempPath(), $"loomo-lspprompt-{Guid.NewGuid():N}.json");
        var service = new LspManagementService(new FakeTerminalService(), new LspServerTable(null));
        return (new LspPromptViewModel(service, settings, new AiSettingsStore(path)), settings, path);
    }

    private static LspPromptInfo Info(string ext) =>
        new(ext, LspPromptKind.NotConfigured,
            $"「{ext}」に対応する言語サーバーが設定されていません。", null, null, null);

    [Fact]
    public void 今後表示しない_同じ拡張子はフィルタで落ちる()
    {
        var (vm, settings, path) = CreateSut();
        try
        {
            vm.Show(Info(".java"));
            Assert.True(vm.IsVisible);

            vm.DismissForeverCommand.Execute(null);

            Assert.False(vm.IsVisible);
            Assert.Null(vm.Filter(Info(".java")));          // アウトラインの案内もこれを通す
            Assert.NotNull(vm.Filter(Info(".kt")));         // 他の拡張子までは抑止しない
            Assert.Contains(".java", settings.Lsp.DismissedPromptExtensions);
            Assert.True(File.Exists(path));                 // settings.json へ永続化されている
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void 今後表示しない_大文字の拡張子でも小文字で保存して抑止する()
    {
        var (vm, settings, path) = CreateSut();
        try
        {
            vm.Show(Info(".JAVA"));
            vm.DismissForeverCommand.Execute(null);

            Assert.Equal([".java"], settings.Lsp.DismissedPromptExtensions);
            Assert.Null(vm.Filter(Info(".java")));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void 閉じるはセッション中だけ抑止し永続化しない()
    {
        var (vm, settings, path) = CreateSut();
        try
        {
            vm.Show(Info(".java"));
            vm.CloseCommand.Execute(null);

            Assert.False(vm.IsVisible);
            Assert.Null(vm.Filter(Info(".java")));
            Assert.Empty(settings.Lsp.DismissedPromptExtensions);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
