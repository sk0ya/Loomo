using System.Collections.Generic;
using System.IO;
using sk0ya.Loomo.CSharp.Debug;

namespace sk0ya.Loomo.Tests;

public sealed class IisExpressLaunchCommandTests
{
    [Fact]
    public void Builds_a_quoted_command_with_environment_and_http_port()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisCommand-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App.csproj");
        Directory.CreateDirectory(root);
        File.WriteAllText(project, "<Project />");
        try
        {
            var profile = new LaunchSettingsProfile(
                "IIS Express", "IISExpress", null, null, null,
                new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Dev's" },
                ApplicationUrl: "http://localhost:53123");

            var command = IisExpressLaunchCommand.Build(
                project, profile, out var error,
                executablePath: @"C:\Program Files\IIS Express\iisexpress.exe");

            Assert.Null(error);
            Assert.NotNull(command);
            Assert.Contains("$env:ASPNETCORE_ENVIRONMENT = 'Dev''s';", command);
            Assert.Contains("& 'C:\\Program Files\\IIS Express\\iisexpress.exe'", command);
            Assert.Contains("/path:'" + root.Replace("'", "''") + "'", command);
            Assert.Contains("/port:53123", command);
            Assert.Contains("/systray:false", command);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Creates_shell_independent_process_arguments_for_dap_attach()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisSpec-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App.csproj");
        Directory.CreateDirectory(root);
        File.WriteAllText(project, "<Project />");
        try
        {
            var profile = new LaunchSettingsProfile(
                "IIS Express", "IISExpress", null, null, null,
                new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
                ApplicationUrl: "http://localhost:53123");

            var spec = IisExpressLaunchCommand.CreateSpec(
                project, profile, out var error, @"C:\Program Files\IIS Express\iisexpress.exe");

            Assert.Null(error);
            Assert.NotNull(spec);
            Assert.Equal(@"C:\Program Files\IIS Express\iisexpress.exe", spec!.ExecutablePath);
            Assert.Equal(["/path:" + root, "/port:53123", "/systray:false"], spec.ProcessArguments);
            Assert.Equal("Development", spec.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Uses_ssl_port_when_only_iis_ssl_port_is_available()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisSsl-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App.csproj");
        Directory.CreateDirectory(root);
        try
        {
            var profile = new LaunchSettingsProfile(
                "IIS Express", "IISExpress", null, null, null,
                new Dictionary<string, string>(), IisExpressSslPort: 44321);
            var command = IisExpressLaunchCommand.Build(
                project, profile, out var error, @"C:\iisexpress.exe");

            Assert.Null(error);
            Assert.Contains("/sslport:44321", command);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Uses_matching_visual_studio_applicationhost_site_for_complex_hosts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisHost-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App", "App.csproj");
        var config = Path.Combine(root, ".vs", "config", "applicationhost.config");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(config, $"""
            <configuration>
              <system.applicationHost>
                <sites>
                  <site name="Fixture Site" id="7">
                    <application path="/">
                      <virtualDirectory path="/" physicalPath="{Path.GetDirectoryName(project)!.Replace("\\", "/", StringComparison.Ordinal)}" />
                    </application>
                    <bindings>
                      <binding protocol="http" bindingInformation="*:53123:localhost" />
                    </bindings>
                  </site>
                </sites>
              </system.applicationHost>
            </configuration>
            """);
        try
        {
            var profile = new LaunchSettingsProfile(
                "IIS Express", "IISExpress", null, null, null,
                new Dictionary<string, string>(), ApplicationUrl: "http://localhost:53123");

            var spec = IisExpressLaunchCommand.CreateSpec(
                project, profile, out var error, @"C:\iisexpress.exe");
            var command = IisExpressLaunchCommand.Build(
                project, profile, out var commandError, @"C:\iisexpress.exe");

            Assert.Null(error);
            Assert.Null(commandError);
            Assert.NotNull(spec);
            Assert.True(spec!.UsesApplicationHostConfiguration);
            Assert.Equal(["/config:" + config, "/site:Fixture Site", "/systray:false"], spec.ProcessArguments);
            Assert.Contains("/config:'" + config.Replace("'", "''", StringComparison.Ordinal) + "'", command);
            Assert.Contains("/site:'Fixture Site'", command);
            Assert.DoesNotContain("/path:", command);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Uses_https_binding_from_applicationhost_for_ssl_profiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisHttpsHost-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App", "App.csproj");
        var config = Path.Combine(root, ".vs", "config", "applicationhost.config");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(project, "<Project />");
        File.WriteAllText(config, $"""
            <configuration>
              <system.applicationHost>
                <sites>
                  <site name="HTTPS Fixture" id="8">
                    <application path="/">
                      <virtualDirectory path="/" physicalPath="{Path.GetDirectoryName(project)!.Replace("\\", "/", StringComparison.Ordinal)}" />
                    </application>
                    <bindings>
                      <binding protocol="https" bindingInformation="*:44321:localhost" />
                    </bindings>
                  </site>
                </sites>
              </system.applicationHost>
            </configuration>
            """);
        try
        {
            var profile = new LaunchSettingsProfile(
                "IIS Express", "IISExpress", null, null, null,
                new Dictionary<string, string>(), ApplicationUrl: "https://localhost:44321");

            var spec = IisExpressLaunchCommand.CreateSpec(
                project, profile, out var error, @"C:\iisexpress.exe");

            Assert.Null(error);
            Assert.NotNull(spec);
            Assert.Equal("sslport", spec!.PortSwitch);
            Assert.Equal(44321, spec.Port);
            Assert.Equal(["/config:" + config, "/site:HTTPS Fixture", "/systray:false"], spec.ProcessArguments);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Refuses_command_line_args_that_iis_express_cannot_forward()
    {
        var root = Path.Combine(Path.GetTempPath(), "Loomo-iisArgs-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "App.csproj");
        Directory.CreateDirectory(root);
        try
        {
            var profile = new LaunchSettingsProfile(
                "IIS Express", "IISExpress", null, "--dev", null,
                new Dictionary<string, string>(), ApplicationUrl: "http://localhost:53123");
            var command = IisExpressLaunchCommand.Build(
                project, profile, out var error, @"C:\iisexpress.exe");

            Assert.Null(command);
            Assert.Contains("commandLineArgs", error);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
