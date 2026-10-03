using Microsoft.Win32;
using sk0ya.Loomo.App.Services;
using Targets = sk0ya.Loomo.App.Services.DefaultAppRegistration.Targets;

namespace sk0ya.Loomo.Tests;

/// <summary>本物の HKCU\Software ではなく、使い捨てのキーを Software に見立てて書く。</summary>
public sealed class DefaultAppRegistrationTests : IDisposable
{
    private const string Exe = @"C:\Tools\Loomo\sk0ya.Loomo.App.exe";
    private const string Caps = @"Clients\StartMenuInternet\Loomo\Capabilities";
    private readonly string _rootPath = $@"Software\sk0ya.Loomo.Tests\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;

    public DefaultAppRegistrationTests() => _root = Registry.CurrentUser.CreateSubKey(_rootPath);

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
    }

    private string? Value(string key, string? name = null)
    {
        using var k = _root.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    private string[] OpenWithProgIds(string ext)
    {
        using var k = _root.OpenSubKey($@"Classes\{ext}\OpenWithProgids");
        return k?.GetValueNames() ?? [];
    }

    [Fact]
    public void Register_writes_commands_that_StartupArguments_understands()
    {
        new DefaultAppRegistration(_root, Exe).Register();

        Assert.Equal($"\"{Exe}\" \"%1\"", Value(@"Classes\sk0ya.Loomo.File\shell\open\command"));
        Assert.Equal($"\"{Exe}\" --browser \"%1\"", Value(@"Classes\sk0ya.Loomo.Html\shell\open\command"));
        Assert.Equal($"\"{Exe}\" \"%1\"", Value(@"Classes\sk0ya.Loomo.Url\shell\open\command"));
        Assert.Equal("", Value(@"Classes\sk0ya.Loomo.Url", "URL Protocol"));
        Assert.Equal($"\"{Exe}\" --workspace \"%1\"", Value(@"Classes\Directory\shell\sk0ya.Loomo\command"));
        Assert.Equal($"\"{Exe}\" --workspace \"%V\"", Value(@"Classes\Directory\Background\shell\sk0ya.Loomo\command"));
    }

    [Fact]
    public void Register_declares_capabilities_for_files_html_and_web()
    {
        new DefaultAppRegistration(_root, Exe).Register();

        Assert.Equal(@"Software\" + Caps, Value("RegisteredApplications", "Loomo"));
        Assert.Equal("sk0ya.Loomo.File", Value(Caps + @"\FileAssociations", ".cs"));
        Assert.Equal("sk0ya.Loomo.File", Value(Caps + @"\FileAssociations", ".pdf"));
        Assert.Equal("sk0ya.Loomo.Html", Value(Caps + @"\FileAssociations", ".html"));
        Assert.Equal("sk0ya.Loomo.Url", Value(Caps + @"\URLAssociations", "https"));
        Assert.Contains("sk0ya.Loomo.File", OpenWithProgIds(".md"));
    }

    [Fact]
    public void Inspect_reports_registered_targets_and_the_registered_exe()
    {
        var registration = new DefaultAppRegistration(_root, Exe);
        Assert.Equal(Targets.None, registration.Inspect().Targets);

        registration.Register();
        var (targets, path) = registration.Inspect();
        Assert.Equal(Targets.All, targets);
        Assert.True(registration.IsCurrentExecutable(path));

        var moved = new DefaultAppRegistration(_root, @"D:\Elsewhere\sk0ya.Loomo.App.exe");
        Assert.False(moved.IsCurrentExecutable(moved.Inspect().RegisteredPath));
    }

    [Fact]
    public void Apply_registers_only_the_selected_targets()
    {
        var registration = new DefaultAppRegistration(_root, Exe);

        registration.Apply(Targets.Preview | Targets.Folder);

        Assert.Equal(Targets.Preview | Targets.Folder, registration.Inspect().Targets);
        Assert.Equal("sk0ya.Loomo.File", Value(Caps + @"\FileAssociations", ".pdf"));
        Assert.Null(Value(Caps + @"\FileAssociations", ".cs"));
        Assert.Null(Value(Caps + @"\FileAssociations", ".html"));
        Assert.Null(_root.OpenSubKey(@"Classes\sk0ya.Loomo.Url"));
        Assert.DoesNotContain("sk0ya.Loomo.File", OpenWithProgIds(".md"));
    }

    [Fact]
    public void Turning_a_target_off_removes_it_and_keeps_the_rest()
    {
        var registration = new DefaultAppRegistration(_root, Exe);
        registration.Register();

        registration.Apply(Targets.All & ~Targets.Web);

        Assert.Equal(Targets.Text | Targets.Preview | Targets.Folder, registration.Inspect().Targets);
        Assert.Null(_root.OpenSubKey(@"Classes\sk0ya.Loomo.Html"));
        Assert.Null(_root.OpenSubKey(Caps + @"\URLAssociations"));
        Assert.Equal("sk0ya.Loomo.File", Value(Caps + @"\FileAssociations", ".cs"));
    }

    [Fact]
    public void Folder_only_does_not_appear_as_an_app_in_windows_settings()
    {
        new DefaultAppRegistration(_root, Exe).Apply(Targets.Folder);

        Assert.Null(Value("RegisteredApplications", "Loomo"));
        Assert.NotNull(Value(@"Classes\Directory\shell\sk0ya.Loomo\command"));
    }

    [Fact]
    public void Unregister_removes_everything_but_leaves_other_apps_alone()
    {
        using (var other = _root.CreateSubKey(@"Classes\.md\OpenWithProgids"))
            other.SetValue("Other.App", Array.Empty<byte>(), RegistryValueKind.None);
        using (var apps = _root.CreateSubKey("RegisteredApplications"))
            apps.SetValue("Other", @"Software\Other\Capabilities");
        var registration = new DefaultAppRegistration(_root, Exe);

        registration.Register();
        registration.Unregister();

        Assert.Equal(Targets.None, registration.Inspect().Targets);
        Assert.Null(_root.OpenSubKey(@"Clients\StartMenuInternet\Loomo"));
        Assert.Null(_root.OpenSubKey(@"Classes\Directory\shell\sk0ya.Loomo"));
        Assert.Null(Value("RegisteredApplications", "Loomo"));
        Assert.Equal(@"Software\Other\Capabilities", Value("RegisteredApplications", "Other"));
        Assert.Equal(["Other.App"], OpenWithProgIds(".md"));
    }

    [Theory]
    [InlineData("\"C:\\A B\\x.exe\" \"%1\"", @"C:\A B\x.exe")]
    [InlineData(@"C:\x.exe %1", @"C:\x.exe")]
    public void ExtractExecutable_reads_quoted_and_bare_commands(string line, string expected)
        => Assert.Equal(expected, DefaultAppRegistration.ExtractExecutable(line));
}
