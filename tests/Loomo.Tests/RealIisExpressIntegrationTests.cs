using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using sk0ya.Loomo.CSharp.Debug;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.Services.Debug;
using Xunit.Sdk;

namespace sk0ya.Loomo.Tests;

/// <summary>launchSettings.jsonから作ったIIS Express起動仕様を実プロセスへ渡す確認。
/// 通常のテストではIIS Expressを起動せず、<c>LOOMO_RUN_REAL_IIS=1</c> のときだけ実行する。</summary>
[Collection(CSharpExternalProcessCollection.Name)]
public sealed class RealIisExpressIntegrationTests
{
    [RealIisFact]
    public async Task Launch_profile_starts_iis_express_and_cleans_up()
    {
        var project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "CSharpIde",
            "src", "Client", "Client.csproj"));
        Assert.True(File.Exists(project), $"fixture projectがありません: {project}");

        var parsed = LaunchSettingsProfileParser.ParseProject(project, out var parseError);
        Assert.Null(parseError);
        var profile = Assert.Single(parsed, candidate => candidate.IsIisExpress);
        var port = GetFreePort();
        profile = profile with { ApplicationUrl = $"http://localhost:{port}" };

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "IIS Express", "iisexpress.exe");
        Assert.True(File.Exists(executable), $"iisexpress.exeがありません: {executable}");

        var spec = IisExpressLaunchCommand.CreateSpec(project, profile, out var specError, executable);
        Assert.Null(specError);
        Assert.NotNull(spec);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec!.ExecutablePath,
                WorkingDirectory = spec.ProjectDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in spec.ProcessArguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        try
        {
            await WaitForPortAsync(port);
            var exit = process.HasExited ? process.ExitCode.ToString() : "running";
            Assert.False(process.HasExited,
                $"IIS Expressが待受前に終了しました（exit={exit}）。");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [RealIisFact]
    public async Task Netcoredbg_attaches_to_the_running_iis_express_process()
    {
        var project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "CSharpIde",
            "src", "Client", "Client.csproj"));
        var profile = Assert.Single(
            LaunchSettingsProfileParser.ParseProject(project, out var parseError),
            candidate => candidate.IsIisExpress);
        Assert.Null(parseError);

        var port = GetFreePort();
        profile = profile with { ApplicationUrl = $"http://localhost:{port}" };
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "IIS Express", "iisexpress.exe");
        var spec = IisExpressLaunchCommand.CreateSpec(project, profile, out var specError, executable);
        Assert.Null(specError);
        Assert.NotNull(spec);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec!.ExecutablePath,
                WorkingDirectory = spec.ProjectDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in spec.ProcessArguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        var debug = new NetcoredbgDebugService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await WaitForPortAsync(port);
            await debug.AttachAsync(new DebugAttachConfig(process.Id, "iisexpress"), timeout.Token);
            Assert.True(debug.State is DebugSessionState.Running or DebugSessionState.Stopped,
                $"IIS ExpressへのDAP attach後の状態が不正です: {debug.State}");
        }
        finally
        {
            await debug.StopAsync(CancellationToken.None);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [RealIisFact]
    public async Task Applicationhost_site_configuration_starts_the_fixture()
    {
        var project = GetFixtureProject();
        var projectDirectory = Path.GetDirectoryName(project)!;
        var profile = Assert.Single(
            LaunchSettingsProfileParser.ParseProject(project, out var parseError),
            candidate => candidate.IsIisExpress);
        Assert.Null(parseError);

        var defaultConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "IISExpress", "config", "applicationhost.config");
        Assert.True(File.Exists(defaultConfig), $"既定のapplicationhost.configがありません: {defaultConfig}");

        var root = Path.Combine(Path.GetTempPath(), "Loomo-realIisHost-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "applicationhost.config");
        Directory.CreateDirectory(root);
        File.Copy(defaultConfig, config);

        var port = GetFreePort();
        const string siteName = "Loomo CSharp fixture host";
        var document = XDocument.Load(config);
        var sites = document.Descendants().Single(element => element.Name.LocalName == "sites");
        var nextId = (sites.Elements().Where(element => element.Name.LocalName == "site")
            .Select(element => (int?)element.Attribute("id"))
            .Max() ?? 0) + 1;
        sites.Add(new XElement("site",
            new XAttribute("name", siteName),
            new XAttribute("id", nextId),
            new XElement("application",
                new XAttribute("path", "/"),
                new XAttribute("applicationPool", "Clr4IntegratedAppPool"),
                new XElement("virtualDirectory",
                    new XAttribute("path", "/"),
                    new XAttribute("physicalPath", projectDirectory)),
                new XElement("virtualDirectory",
                    new XAttribute("path", "/assets"),
                    new XAttribute("physicalPath", projectDirectory))),
            new XElement("bindings",
                new XElement("binding",
                    new XAttribute("protocol", "http"),
                    new XAttribute("bindingInformation", $"*:{port}:localhost")))));
        document.Save(config);

        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "IIS Express", "iisexpress.exe");
        var spec = IisExpressLaunchCommand.CreateSpec(
            project, profile with { ApplicationUrl = $"http://localhost:{port}" },
            out var specError, executable, config);
        Assert.Null(specError);
        Assert.NotNull(spec);
        Assert.True(spec!.UsesApplicationHostConfiguration);
        Assert.Contains("/config:" + config, spec.ProcessArguments);
        Assert.Contains("/site:" + siteName, spec.ProcessArguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = spec.ExecutablePath,
                WorkingDirectory = projectDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in spec.ProcessArguments)
            process.StartInfo.ArgumentList.Add(argument);

        try
        {
            Assert.True(process.Start());
            await WaitForPortAsync(port);
            Assert.False(process.HasExited,
                $"applicationhost siteが待受前に終了しました（exit={(process.HasExited ? process.ExitCode : 0)}）。");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string GetFixtureProject()
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "CSharpIde",
            "src", "Client", "Client.csproj"));

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForPortAsync(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    private sealed class RealIisFactAttribute : FactAttribute
    {
        public RealIisFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("LOOMO_RUN_REAL_IIS"), "1",
                    StringComparison.Ordinal))
                Skip = "LOOMO_RUN_REAL_IIS=1 のときだけ実IIS Expressを起動します。";
        }
    }
}
