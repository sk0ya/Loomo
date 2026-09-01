using System.IO;
using sk0ya.Loomo.CSharp.Configuration;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpEditorConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "loomo-editorconfig-" + Guid.NewGuid().ToString("N"));

    public CSharpEditorConfigTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Applies_ancestor_then_child_sections_and_exposes_formatting_values()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "Feature")).FullName;
        var file = Path.Combine(nested, "Widget.cs");
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.cs]
            indent_style = space
            indent_size = 4
            dotnet_analyzer_diagnostic.severity = warning
            csharp_style_var_for_built_in_types = false:suggestion
            """);
        File.WriteAllText(Path.Combine(_root, "src", ".editorconfig"), """
            [**/*.cs]
            indent_size = 2
            dotnet_diagnostic.CS0168.severity = error
            """);

        var config = new CSharpEditorConfigService().Resolve(file);

        Assert.Equal(2, config.SourceFiles.Count);
        Assert.Equal("space", config.IndentStyle);
        Assert.Equal(2, config.IndentSize);
        Assert.Equal(CSharpDiagnosticSeverity.Error, config.GetDiagnosticSeverity("CS0168"));
        Assert.Equal(CSharpDiagnosticSeverity.Warning, config.GetDiagnosticSeverity("CS0169"));
        Assert.Equal(("false", CSharpDiagnosticSeverity.Suggestion),
            config.GetStyle("csharp_style_var_for_built_in_types"));
    }

    [Fact]
    public void Root_true_in_nearer_config_stops_ancestor_configurations()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var file = Path.Combine(nested, "Widget.cs");
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true
            [*.cs]
            indent_size = 8
            """);
        File.WriteAllText(Path.Combine(nested, ".editorconfig"), """
            root = true
            [*.cs]
            indent_size = 2
            """);

        var config = new CSharpEditorConfigService().Resolve(file);

        Assert.Single(config.SourceFiles);
        Assert.Equal(2, config.IndentSize);
    }

    [Fact]
    public void Supports_filename_patterns_braces_and_category_severity_precedence()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "Feature")).FullName;
        var file = Path.Combine(nested, "Widget.cs");
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            [*.{cs,csx}]
            dotnet_analyzer_diagnostic.severity = suggestion
            dotnet_analyzer_diagnostic.category-Style.severity = warning
            dotnet_diagnostic.IDE0001.severity = error
            insert_final_newline = true
            tab_width = invalid
            """);

        var config = new CSharpEditorConfigService().Resolve(file);

        Assert.Equal(CSharpDiagnosticSeverity.Error, config.GetDiagnosticSeverity("IDE0001", "Style"));
        Assert.Equal(CSharpDiagnosticSeverity.Warning, config.GetDiagnosticSeverity("IDE0002", "Style"));
        Assert.Equal(CSharpDiagnosticSeverity.Suggestion, config.GetDiagnosticSeverity("CA1822", "Performance"));
        Assert.True(config.InsertFinalNewline);
        Assert.Null(config.TabWidth);
    }

    [Fact]
    public void Resolves_naming_rule_for_a_symbol_kind_and_accessibility()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        var file = Path.Combine(nested, "Widget.cs");
        File.WriteAllText(Path.Combine(_root, ".editorconfig"), """
            root = true

            [*.cs]
            dotnet_naming_rule.private_fields.symbols = private_fields
            dotnet_naming_rule.private_fields.style = private_field_style
            dotnet_naming_symbols.private_fields.applicable_kinds = field
            dotnet_naming_symbols.private_fields.applicable_accessibilities = private
            dotnet_naming_style.private_field_style.required_prefix = m_
            dotnet_naming_style.private_field_style.capitalization = camel_case
            """);

        var config = new CSharpEditorConfigService().Resolve(file);

        var style = config.ResolveNamingStyle("field", "private");
        Assert.NotNull(style);
        Assert.Equal("m_", style.RequiredPrefix);
        Assert.Equal("camel_case", style.Capitalization);
        Assert.Null(config.ResolveNamingStyle("property", "public"));
    }
}
