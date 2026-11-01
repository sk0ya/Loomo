using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation.Peers;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.Core.Git;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.Tests;

/// <summary>本物の入力欄（ディスパッチャ越し）と本物の exe をつないだ往復（§24.101）。</summary>
[Collection(WpfViewTests.Name)]
public sealed class GitCredentialPrompterTests
{
    private readonly WpfViewHost _host;

    public GitCredentialPrompterTests(WpfViewHost host) => _host = host;

    [Fact]
    public async Task exeの問いが入力欄に出て_打った答えがexeから出てくる()
    {
        using var server = new GitAskPassServer(new GitCredentialPrompter(), Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName));
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, GitAskPass.ExecutableName))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("Username for 'https://host': ");
        foreach (var (key, value) in server.EnvironmentFor("git push origin main")!)
            info.Environment[key] = value;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();

        GitCredentialDialog? dialog = null;
        for (var i = 0; i < 100 && dialog is null; i++)
        {
            await Task.Delay(100);
            _host.Run(() => dialog = Application.Current.Windows.OfType<GitCredentialDialog>().FirstOrDefault(w => w.IsVisible));
        }
        Assert.NotNull(dialog);
        _host.Run(() =>
        {
            ((TextBox)dialog!.FindName("InputBox")).Text = "alice";
            var ok = (Button)dialog.FindName("OkButton");
            ok.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        });

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("alice\n", await output);
    }
}
