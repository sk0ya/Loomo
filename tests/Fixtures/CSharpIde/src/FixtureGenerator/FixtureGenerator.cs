using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Loomo.CSharpFixture.Generator;

[Generator]
public sealed class FixtureGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var inputs = context.AdditionalTextsProvider.Collect()
            .Combine(context.AnalyzerConfigOptionsProvider);
        context.RegisterSourceOutput(inputs, static (output, input) =>
        {
            var file = input.Left.FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file.Path), "GeneratorInput.txt", StringComparison.OrdinalIgnoreCase));
            var value = file?.GetText()?.ToString().Trim() ?? "generated";
            if (file is not null && input.Right.GetOptions(file)
                    .TryGetValue("fixture_generator_value", out var configured) && configured.Length > 0)
                value = configured;
            var escaped = value.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "")
                .Replace("\n", "");
            output.AddSource(
                "FixtureGenerated.g.cs",
                $"namespace Loomo.CSharpFixture.Feature;\n\ninternal static class FixtureGenerated\n{{\n    public const string Value = \"{escaped}\";\n}}\n");
        });
    }
}
