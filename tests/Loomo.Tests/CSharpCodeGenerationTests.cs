using System.Linq;
using System.Collections.Generic;
using System.IO;
using Editor.Core.Lsp;
using Microsoft.CodeAnalysis;
using sk0ya.Loomo.CSharp.Configuration;
using sk0ya.Loomo.CSharp.Projects;
using sk0ya.Loomo.CSharp.Refactoring;
using Xunit;

namespace sk0ya.Loomo.Tests;

public sealed class CSharpCodeGenerationTests
{
    [Fact]
    public void Generation_options_factory_uses_the_selected_project_context_and_editorconfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "LoomoGenerationOptions_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "Sample.cs");
            var projectPath = Path.Combine(root, "Sample.csproj");
            File.WriteAllText(Path.Combine(root, ".editorconfig"),
                "root = true\n[*.cs]\ndotnet_naming_rule.private_fields.symbols = field\n" +
                "dotnet_naming_rule.private_fields.style = field_style\n" +
                "dotnet_naming_rule.private_fields.severity = suggestion\n" +
                "dotnet_naming_symbols.field.applicable_kinds = field\n" +
                "dotnet_naming_symbols.field.applicable_accessibilities = private\n" +
                "dotnet_naming_style.field_style.capitalization = camel_case\n" +
                "dotnet_naming_style.field_style.required_prefix = _\n");
            File.WriteAllText(sourcePath, "class Sample {}\n");
            var target = new TargetFrameworkModel("net10.0", [], "latest",
                [new ProjectItem("Sample.cs", sourcePath)], [], [], [])
            {
                Nullable = "disable",
            };
            var solution = new SolutionModel(null, "Sample", root,
                [new ProjectModel("Sample", projectPath, root, [], [target], "net10.0", false,
                    ProjectLoadState.Ready)], ProjectLoadState.Ready);

            var options = CSharpGenerationOptionsFactory.Create(solution, sourcePath);

            Assert.False(options.NullableEnabled);
            var fieldNaming = Assert.IsType<CSharpNamingStyle>(options.FieldNaming);
            Assert.Equal("_", fieldNaming.RequiredPrefix);
            Assert.Equal("camel_case", fieldNaming.Capitalization);
            Assert.Equal("CSharp13", options.ParseOptions!.LanguageVersion.ToString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private const string Source = """
        namespace Sample;

        public class Person
        {
            private readonly string _name;
            private int _age;
        }
        """;

    [Fact]
    public void Generates_constructor_from_instance_fields_at_type_end()
    {
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", Source, line: 5, character: 10,
            CSharpCodeGenerationKind.Constructor);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values.Single());
        var updated = Apply(Source, edit);

        Assert.Contains("public Person(string name, int age)", updated);
        Assert.Contains("this._name = name;", updated);
        Assert.Contains("this._age = age;", updated);
    }

    [Fact]
    public void Generates_constructor_from_auto_properties_without_overwriting_initializers()
    {
        const string source = """
            class Person
            {
                public string Name { get; }
                public int Age { get; init; }
                public string Country { get; set; } = "JP";
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, line: 3, character: 5,
            CSharpCodeGenerationKind.Constructor);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public Person(string name, int age)", updated);
        Assert.Contains("this.Name = name;", updated);
        Assert.Contains("this.Age = age;", updated);
        Assert.DoesNotContain("Country country", updated);
        Assert.DoesNotContain("this.Country = country;", updated);
    }

    [Fact]
    public void Refuses_to_add_a_second_constructor_to_a_primary_constructor_type()
    {
        const string source = """
            class Person(string name)
            {
                private readonly int _age;
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 10,
            CSharpCodeGenerationKind.Constructor);

        Assert.Null(result.Edit);
        Assert.Contains("primary constructor", result.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_constructor_generation_calls_the_only_accessible_parameterized_base_constructor()
    {
        const string path = "C:\\work\\Derived.cs";
        const string source = """
            class Base
            {
                protected Base(string id) { }
            }

            class Derived : Base
            {
                private int value;
            }
            """;
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 7, character: 10, CSharpCodeGenerationKind.Constructor,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public Derived(string id, int value) : base(id)", updated);
        Assert.Contains("this.value = value;", updated);
    }

    [Fact]
    public void Semantic_constructor_generation_can_target_a_base_constructor_without_local_members()
    {
        const string path = "C:\\work\\Derived.cs";
        const string source = """
            class Base
            {
                protected Base(string id) { }
            }

            class Derived : Base
            {
            }
            """;
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, 5, 10, CSharpCodeGenerationKind.Constructor,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public Derived(string id) : base(id)", updated);
    }

    [Fact]
    public void Semantic_constructor_generation_includes_members_from_other_partial_declarations()
    {
        const string path = "C:\\work\\Person.cs";
        const string active = """
            namespace Sample;
            public partial class Person
            {
            }
            """;
        const string otherPart = """
            namespace Sample;
            public partial class Person
            {
                private string _name;
                public int Age { get; }
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            ["C:\\work\\Person.Part.cs"] = otherPart,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.Constructor,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values).Single();
        var updated = Apply(active, edit);
        Assert.Contains("public Person(string name, int age)", updated);
        Assert.Contains("this._name = name;", updated);
        Assert.Contains("this.Age = age;", updated);

        var updatedSources = new Dictionary<string, string>(sources) { [path] = updated };
        var errors = CSharpSemanticCompilation.Create(updatedSources)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void Constructor_generation_marks_required_members_as_satisfied()
    {
        const string source = """
            class Sample
            {
                public required string Name { get; init; }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, line: 2, character: 5,
            CSharpCodeGenerationKind.Constructor);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("SetsRequiredMembers", updated);
        Assert.Contains("this.Name = name;", updated);
    }

    [Fact]
    public void Semantic_constructor_generation_initializes_accessible_required_members_from_a_base_type()
    {
        const string path = "C:\\work\\Derived.cs";
        const string source = """
            class Base
            {
                public required string Name { get; init; }
            }

            class Derived : Base
            {
                private int Value;
            }
            """;
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, 7, 10, CSharpCodeGenerationKind.Constructor,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("SetsRequiredMembers", updated);
        Assert.Contains("public Derived(string name, int value)", updated);
        Assert.Contains("base.Name = name;", updated);

        var errors = CSharpSemanticCompilation.Create(new Dictionary<string, string> { [path] = updated })
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
    }

    [Fact]
    public void Semantic_constructor_generation_refuses_an_unassignable_required_base_member()
    {
        const string path = "C:\\work\\Derived.cs";
        const string source = """
            class Base
            {
                public required string Secret { get; private init; }
            }

            class Derived : Base
            {
                private int Value;
            }
            """;
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, 7, 10, CSharpCodeGenerationKind.Constructor,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Edit);
        Assert.Contains("required", result.Error);
        Assert.Contains("Secret", result.Error);
    }

    [Fact]
    public void Generates_a_readonly_field_and_constructor_assignment_from_a_parameter()
    {
        const string source = """
            class Sample
            {
                public Sample(string name)
                {
                    Console.WriteLine(name);
                }
            }
        """;
        var position = source.IndexOf("name", StringComparison.Ordinal);
        var caret = ToPosition(source, position);
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, caret.Line,
            caret.Character, CSharpCodeGenerationKind.FieldFromConstructorParameter);

        Assert.Null(result.Error);
        var edits = Assert.Single(result.Edit!.Changes.Values);
        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "private readonly string _name;", StringComparison.Ordinal));
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "this._name = name;", StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_field_generation_for_ref_parameters_and_single_line_bodies()
    {
        const string refSource = """
            class Sample
            {
                public Sample(ref string name)
                {
                }
            }
        """;
        var refPosition = refSource.IndexOf("name", StringComparison.Ordinal);
        var refCaret = ToPosition(refSource, refPosition);
        var refResult = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", refSource, refCaret.Line, refCaret.Character,
            CSharpCodeGenerationKind.FieldFromConstructorParameter);
        Assert.Null(refResult.Edit);
        Assert.Contains("ref", refResult.Error);

        const string oneLine = "class Sample { public Sample(string name) { } }";
        var oneLinePosition = oneLine.IndexOf("name", StringComparison.Ordinal);
        var oneLineCaret = ToPosition(oneLine, oneLinePosition);
        var oneLineResult = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", oneLine, oneLineCaret.Line, oneLineCaret.Character,
            CSharpCodeGenerationKind.FieldFromConstructorParameter);
        Assert.Null(oneLineResult.Edit);
    }

    [Fact]
    public void Escapes_keyword_constructor_parameters_in_generated_field_assignments()
    {
        const string source = """
            class Sample
            {
                public Sample(string @class)
                {
                }
            }
            """;
        var position = source.IndexOf("@class", StringComparison.Ordinal);
        var caret = ToPosition(source, position);

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, caret.Line, caret.Character,
            CSharpCodeGenerationKind.FieldFromConstructorParameter);

        Assert.Null(result.Error);
        var assignment = Assert.Single(result.Edit!.Changes.Values)
            .Single(edit => edit.NewText.Contains("this._class", StringComparison.Ordinal));
        Assert.Contains("this._class = @class;", assignment.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("this._class = class;", assignment.NewText, StringComparison.Ordinal);

        var updated = ApplyEdits(source, result.Edit.Changes.Values.SelectMany(edits => edits));
        Assert.DoesNotContain(
            CSharpSemanticCompilation.Create(new Dictionary<string, string> { ["C:\\work\\Sample.cs"] = updated })
                .GetDiagnostics(),
            diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Applies_editorconfig_naming_and_nullable_options_to_generation()
    {
        const string source = """
            class Sample
            {
                public Sample(string name)
                {
                }
            }
            """;
        var position = source.IndexOf("name", StringComparison.Ordinal);
        var caret = ToPosition(source, position);
        var field = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, caret.Line, caret.Character,
            CSharpCodeGenerationKind.FieldFromConstructorParameter, null,
            new CSharpGenerationOptions(
                NullableEnabled: false,
                FieldNaming: new CSharpNamingStyle("m_", "camel_case")));

        Assert.Null(field.Error);
        var fieldText = string.Join("\n", field.Edit!.Changes.Values.Single().Select(e => e.NewText));
        Assert.Contains("private readonly string m_name;", fieldText);
        Assert.Contains("this.m_name = name;", fieldText);

        const string usage = """
            class Sample
            {
                void Run()
                {
                    Missing(null);
                }
            }
            """;
        var usageResult = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", usage, 4, 12,
            CSharpCodeGenerationKind.MethodFromUsage, null,
            new CSharpGenerationOptions(NullableEnabled: false));

        Assert.Null(usageResult.Error);
        var updated = Apply(usage, usageResult.Edit!.Changes.Values.Single().Single());
        Assert.Contains("private void Missing(object arg1)", updated);
    }

    [Fact]
    public void Applies_property_naming_style_to_property_generation()
    {
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", Source, 5, 10,
            CSharpCodeGenerationKind.PropertiesFromFields, null,
            new CSharpGenerationOptions(
                PropertyNaming: new CSharpNamingStyle("", "camel_case")));

        Assert.Null(result.Error);
        var updated = Apply(Source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public string name { get => _name; }", updated);
        Assert.Contains("public int age { get => _age; set => _age = value; }", updated);
    }

    [Fact]
    public void Constructor_and_equality_use_property_naming_when_ignoring_backing_properties()
    {
        const string source = """
            class Person
            {
                private string _name;
                public string name { get; set; }
            }
            """;
        var options = new CSharpGenerationOptions(
            PropertyNaming: new CSharpNamingStyle("", "camel_case"));

        var constructor = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.Constructor, null, options);
        Assert.Null(constructor.Error);
        var constructorText = string.Join("\n",
            constructor.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.Contains("public Person(string name)", constructorText);
        Assert.DoesNotContain("name2", constructorText);

        var equality = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.EqualsAndGetHashCode, null, options);
        Assert.Null(equality.Error);
        var equalityText = string.Join("\n",
            equality.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.Contains("Object.Equals(_name, other._name)", equalityText);
        Assert.DoesNotContain("Object.Equals(name, other.name)", equalityText);
    }

    [Fact]
    public void Applies_parameter_naming_style_to_constructor_generation()
    {
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", Source, 5, 10,
            CSharpCodeGenerationKind.Constructor, null,
            new CSharpGenerationOptions(
                ParameterNaming: new CSharpNamingStyle("", "pascal_case")));

        Assert.Null(result.Error);
        var updated = Apply(Source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public Person(string Name, int Age)", updated);
        Assert.Contains("this._name = Name;", updated);
        Assert.Contains("this._age = Age;", updated);
    }

    [Fact]
    public void Generates_properties_and_preserves_readonly_semantics()
    {
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", Source, line: 5, character: 10,
            CSharpCodeGenerationKind.PropertiesFromFields);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values.Single());
        var updated = Apply(Source, edit);

        Assert.Contains("public string Name { get => _name; }", updated);
        Assert.Contains("public int Age { get => _age; set => _age = value; }", updated);
    }

    [Fact]
    public void Supports_record_declarations_for_property_generation_but_not_duplicate_equality()
    {
        const string source = """
            public record Person
            {
                private readonly string _name;
            }
            """;

        var properties = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.PropertiesFromFields);

        Assert.Null(properties.Error);
        var updated = Apply(source, Assert.Single(properties.Edit!.Changes.Values).Single());
        Assert.Contains("public string Name { get => _name; }", updated);

        var equality = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.EqualsAndGetHashCode);
        Assert.Null(equality.Edit);
        Assert.Contains("record", equality.Error);
    }

    [Fact]
    public void Semantic_property_generation_includes_fields_from_other_partial_declarations()
    {
        const string path = "C:\\work\\Person.cs";
        const string active = """
            namespace Sample;
            public partial class Person
            {
            }
            """;
        const string otherPart = """
            namespace Sample;
            public partial class Person
            {
                private readonly string _name;
                private int _age;
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            ["C:\\work\\Person.Part.cs"] = otherPart,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.PropertiesFromFields,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(active, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public string Name { get => _name; }", updated);
        Assert.Contains("public int Age { get => _age; set => _age = value; }", updated);
    }

    [Fact]
    public void Semantic_value_generation_includes_members_from_other_partial_declarations()
    {
        const string path = "C:\\work\\Person.cs";
        const string active = """
            namespace Sample;
            public partial class Person
            {
            }
            """;
        const string otherPart = """
            namespace Sample;
            public partial class Person
            {
                private int _id;
                public string Name { get; }
                public string Label => Name;
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            ["C:\\work\\Person.Part.cs"] = otherPart,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);
        var options = new CSharpGenerationOptions(SemanticCompilation: compilation);

        var equality = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.EqualsAndGetHashCode,
            sources, options);
        Assert.Null(equality.Error);
        var equalityText = Assert.Single(equality.Edit!.Changes.Values).Single().NewText;
        Assert.Contains("Object.Equals(_id, other._id)", equalityText);
        Assert.Contains("Object.Equals(Name, other.Name)", equalityText);
        Assert.DoesNotContain("Label", equalityText);

        var toString = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.ToString,
            sources, options);
        Assert.Null(toString.Error);
        var toStringText = Assert.Single(toString.Edit!.Changes.Values).Single().NewText;
        Assert.Contains("nameof(_id)", toStringText);
        Assert.Contains("nameof(Name)", toStringText);
        Assert.Contains("nameof(Label)", toStringText);

        var deconstruct = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.Deconstruct,
            sources, options);
        Assert.Null(deconstruct.Error);
        var deconstructText = Assert.Single(deconstruct.Edit!.Changes.Values).Single().NewText;
        Assert.Contains("out int id", deconstructText);
        Assert.Contains("out string name", deconstructText);
        Assert.Contains("out string label", deconstructText);
        Assert.Contains("id = this._id;", deconstructText);
        Assert.Contains("name = this.Name;", deconstructText);
        Assert.Contains("label = this.Label;", deconstructText);
    }

    [Fact]
    public void Refuses_a_second_constructor_or_static_only_type()
    {
        const string existing = """
            class Sample
            {
                private static int Count;
                public Sample() { }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", existing, 2, 5, CSharpCodeGenerationKind.Constructor);

        Assert.Null(result.Edit);
        Assert.Contains("インスタンスフィールド", result.Error);
    }

    [Fact]
    public void Generates_equality_and_hash_code_without_requiring_imports()
    {
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", Source, line: 5, character: 10,
            CSharpCodeGenerationKind.EqualsAndGetHashCode);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values.Single());
        var updated = Apply(Source, edit);

        Assert.Contains("obj is Person other", updated);
        Assert.Contains("global::System.Object.Equals(_name, other._name)", updated);
        Assert.Contains("global::System.HashCode.Combine(_name, _age)", updated);
    }

    [Fact]
    public void Generates_syntactically_valid_equality_methods()
    {
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", Source, line: 5, character: 10,
            CSharpCodeGenerationKind.EqualsAndGetHashCode);

        var updated = Apply(Source, Assert.Single(result.Edit!.Changes.Values).Single());
        var errors = CSharpSemanticCompilation.Create(
                new Dictionary<string, string> { ["C:\\work\\Person.cs"] = updated })
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();

        Assert.Empty(errors);
    }

    [Fact]
    public void Generates_equality_and_hash_code_from_instance_auto_properties()
    {
        const string source = """
            class Person
            {
                public string Name { get; set; }
                public int Age { get; init; }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, line: 2, character: 5,
            CSharpCodeGenerationKind.EqualsAndGetHashCode);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("global::System.Object.Equals(Name, other.Name)", updated);
        Assert.Contains("global::System.Object.Equals(Age, other.Age)", updated);
        Assert.Contains("global::System.HashCode.Combine(Name, Age)", updated);
    }

    [Fact]
    public void Equality_does_not_compare_a_backing_field_and_its_property_twice()
    {
        const string source = """
            class Person
            {
                private string _name;
                public string Name { get => _name; set => _name = value; }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.EqualsAndGetHashCode);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Equal(1, CountOccurrences(updated, "Object.Equals(_name, other._name)"));
        Assert.DoesNotContain("Object.Equals(Name, other.Name)", updated);
        Assert.Contains("HashCode.Combine(_name)", updated);
    }

    [Fact]
    public void Generates_to_string_from_fields_and_unbacked_properties()
    {
        const string source = """
            class Person
            {
                private string _name;
                public int Age { get; set; }
                public string WriteOnly { set { } }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, line: 3, character: 5,
            CSharpCodeGenerationKind.ToString);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public override string ToString()", updated);
        Assert.Contains("{nameof(_name)}={_name}", updated);
        Assert.Contains("{nameof(Age)}={Age}", updated);
        Assert.DoesNotContain("WriteOnly", updated[updated.IndexOf("ToString", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Equality_ignores_write_only_properties()
    {
        const string source = """
            class Person
            {
                public int WriteOnly { set { } }
                public int Age { get; set; }
            }
            """;
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.EqualsAndGetHashCode);

        Assert.Null(result.Error);
        var generated = string.Join("\n",
            result.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.Contains("Object.Equals(Age, other.Age)", generated);
        Assert.DoesNotContain("WriteOnly", generated);
    }

    [Fact]
    public void To_string_uses_property_naming_when_ignoring_backing_properties()
    {
        const string source = """
            class Person
            {
                private string _name;
                public string name { get; set; }
            }
            """;
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, 2, 5,
            CSharpCodeGenerationKind.ToString, null,
            new CSharpGenerationOptions(
                PropertyNaming: new CSharpNamingStyle("", "camel_case")));

        Assert.Null(result.Error);
        var generated = string.Join("\n",
            result.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.Contains("{nameof(_name)}={_name}", generated);
        Assert.DoesNotContain("{nameof(name)}={name}", generated);
    }

    [Fact]
    public void Refuses_to_string_generation_for_records_and_existing_methods()
    {
        const string record = "record Person(string Name);";
        var recordResult = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", record, 0, 5, CSharpCodeGenerationKind.ToString);
        Assert.Null(recordResult.Edit);
        Assert.Contains("record", recordResult.Error);

        const string existing = """
            class Person
            {
                private int _age;
                public override string ToString() => _age.ToString();
            }
            """;
        var existingResult = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", existing, 2, 5, CSharpCodeGenerationKind.ToString);
        Assert.Null(existingResult.Edit);
        Assert.Contains("既に", existingResult.Error);
    }

    [Fact]
    public void Generates_deconstruct_from_fields_and_readable_properties()
    {
        const string source = """
            class Person
            {
                private readonly string _name;
                public string Name => _name;
                public int Age { get; init; }
                public int Score => Age + 1;
                public string WriteOnly { set { } }
                public static string Kind { get; } = "person";
                public string this[int index] => _name;
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, line: 5, character: 5,
            CSharpCodeGenerationKind.Deconstruct);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public void Deconstruct(out string name, out int age, out int score)", updated);
        Assert.Contains("name = this._name;", updated);
        Assert.Contains("age = this.Age;", updated);
        Assert.Contains("score = this.Score;", updated);
        Assert.DoesNotContain("WriteOnly", updated[updated.IndexOf("Deconstruct", StringComparison.Ordinal)..]);
        var generated = updated[updated.IndexOf("Deconstruct", StringComparison.Ordinal)..];
        Assert.DoesNotContain("Kind", generated);
    }

    [Fact]
    public void Refuses_a_duplicate_deconstruct_arity()
    {
        const string source = """
            class Pair
            {
                private int _left;
                private int _right;
                public void Deconstruct(out int left, out int right)
                    => (left, right) = (_left, _right);
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Pair.cs", source, line: 1, character: 5,
            CSharpCodeGenerationKind.Deconstruct);

        Assert.Contains("同じ引数数のDeconstruct", result.Error);
    }

    [Fact]
    public void Deconstruct_parameter_names_follow_editorconfig_naming()
    {
        const string source = """
            class Person
            {
                private string _name;
                public int Age { get; }
            }
            """;
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Person.cs", source, line: 3, character: 5,
            CSharpCodeGenerationKind.Deconstruct, null,
            new CSharpGenerationOptions(ParameterNaming: new CSharpNamingStyle(
                "", "pascal_case")));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("out string Name, out int Age", updated);
        Assert.Contains("Name = this._name;", updated);
        Assert.Contains("Age = this.Age;", updated);
    }

    [Fact]
    public void Generates_null_guards_at_the_start_of_a_method_body()
    {
        const string source = """
            class Sample
            {
                void Run(string value, int count)
                {
                    Console.WriteLine(value);
                }
            }
            """;

        var result = CSharpCodeGenerationService.GenerateNullGuards(
            "C:\\work\\Sample.cs", source, line: 3, character: 12);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values.Single());
        var updated = Apply(source, edit);
        Assert.Contains("global::System.ArgumentNullException.ThrowIfNull(value);", updated);
        Assert.DoesNotContain("ThrowIfNull(count)", updated);
    }

    [Fact]
    public void Semantic_null_guard_generation_excludes_user_defined_value_types()
    {
        const string path = "C:\\work\\SemanticGuards.cs";
        const string source = """
            struct Value { }
            class Sample
            {
                void Run(Value value, string text)
                {
                    Console.WriteLine(text);
                }
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpCodeGenerationService.GenerateNullGuards(
            path, source, line: 5, character: 12, compilation);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("ThrowIfNull(text);", updated);
        Assert.DoesNotContain("ThrowIfNull(value)", updated);
    }

    [Fact]
    public void Generates_a_method_from_a_local_usage_and_infers_argument_types()
    {
        const string source = """
            class Sample
            {
                void Run()
                {
                    Missing(42, "text");
                }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, line: 4, character: 12,
            CSharpCodeGenerationKind.MethodFromUsage);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("private void Missing(int arg1, string arg2)", updated);
        Assert.Contains("NotImplementedException", updated);
    }

    [Fact]
    public void Semantic_method_generation_uses_the_declared_argument_type()
    {
        const string source = """
            using System;

            class Sample
            {
                void Run(DateTime value)
                {
                    Missing(value);
                }
            }
            """;
        var position = source.IndexOf("Missing", StringComparison.Ordinal);
        var caret = ToPosition(source, position);
        var path = "C:\\work\\Sample.cs";
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, caret.Line, caret.Character,
            CSharpCodeGenerationKind.MethodFromUsage, sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("Missing(", updated);
        Assert.Contains("DateTime arg1", updated);
        Assert.DoesNotContain("object arg1", updated);
    }

    [Fact]
    public void Generates_a_return_type_from_the_enclosing_method_and_rejects_external_receivers()
    {
        const string source = """
            class Sample
            {
                string Run()
                {
                    return this.CreateName();
                }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, line: 4, character: 24,
            CSharpCodeGenerationKind.MethodFromUsage);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("private string CreateName()", updated);

        var external = source.Replace("this.CreateName()", "factory.CreateName()", StringComparison.Ordinal);
        var rejected = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", external, line: 4, character: 24,
            CSharpCodeGenerationKind.MethodFromUsage);
        Assert.Null(rejected.Edit);
    }

    [Fact]
    public void Method_from_usage_preserves_explicit_generic_arity()
    {
        const string source = """
            class Sample
            {
                void Run()
                {
                    Missing<int, string>(42, "text");
                }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Sample.cs", source, line: 4, character: 12,
            CSharpCodeGenerationKind.MethodFromUsage);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("private void Missing<T1, T2>(int arg1, string arg2)", updated);
    }

    [Fact]
    public void Generates_unimplemented_interface_members_from_a_local_contract()
    {
        const string source = """
            using System;

            interface IRunner
            {
                string Name { get; }
                void Run(int value);
                event EventHandler Changed;
            }

            class Runner : IRunner
            {
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Runner.cs", source, line: 9, character: 10,
            CSharpCodeGenerationKind.ImplementInterface);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values.Single());
        var updated = Apply(source, edit);
        Assert.Contains("public string Name { get; }", updated);
        Assert.Contains("public void Run(int value)", updated);
        Assert.Contains("public event EventHandler Changed", updated);
        Assert.Contains("NotImplementedException", updated);
    }

    [Fact]
    public void Does_not_copy_default_interface_members_into_the_implementing_type()
    {
        const string source = """
            interface IService
            {
                void Run();
                void DefaultRun() { }
                int Value { get; }
                int Computed => 1;
            }

            class Service : IService
            {
            }
            """;
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Service.cs", source, 8, 5,
            CSharpCodeGenerationKind.ImplementInterface);

        Assert.Null(result.Error);
        var generated = string.Join("\n",
            result.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.Contains("void Run()", generated);
        Assert.Contains("int Value", generated);
        Assert.DoesNotContain("DefaultRun", generated);
        Assert.DoesNotContain("Computed", generated);
    }

    [Fact]
    public void Generates_override_members_from_a_local_base_class()
    {
        const string source = """
            abstract class Base
            {
                protected abstract string Name { get; }
                protected abstract int Count();
                public virtual void Run(int value) { }
                public virtual event System.Action? Changed;
            }

            class Derived : Base
            {
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Derived.cs", source, line: 7, character: 10,
            CSharpCodeGenerationKind.OverrideMembers);

        Assert.Null(result.Error);
        var edit = Assert.Single(result.Edit!.Changes.Values.Single());
        var updated = Apply(source, edit);
        Assert.Contains("protected override string Name { get; }", updated);
        Assert.Contains("protected override int Count()", updated);
        Assert.Contains("public override void Run(int value)", updated);
        Assert.Contains("base.Run(value);", updated);
        Assert.Contains("add => base.Changed += value;", updated);
        Assert.Contains("throw new global::System.NotImplementedException();", updated);
    }

    [Fact]
    public void Does_not_generate_non_virtual_base_members_as_overrides()
    {
        const string source = """
            class Base
            {
                public void Run() { }
                public virtual void Allowed() { }
            }

            class Derived : Base
            {
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Derived.cs", source, line: 7, character: 2,
            CSharpCodeGenerationKind.OverrideMembers);

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.DoesNotContain("override void Run", updated);
        Assert.Contains("override void Allowed", updated);
    }

    [Fact]
    public void Resolves_interface_members_from_another_compile_file_without_editing_it()
    {
        const string active = """
            namespace Sample;
            class Runner : IRunner
            {
            }
            """;
        const string contract = """
            namespace Sample;
            interface IRunner
            {
                string Name { get; }
                void Run(int value);
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Runner.cs", active, line: 2, character: 8,
            CSharpCodeGenerationKind.ImplementInterface,
            new Dictionary<string, string> { ["C:\\work\\IRunner.cs"] = contract });

        Assert.Null(result.Error);
        var changes = Assert.Single(result.Edit!.Changes);
        Assert.EndsWith("Runner.cs", changes.Key, System.StringComparison.OrdinalIgnoreCase);
        var updated = Apply(active, Assert.Single(changes.Value));
        Assert.Contains("public string Name { get; }", updated);
        Assert.Contains("public void Run(int value)", updated);
    }

    [Fact]
    public void Uses_semantic_identity_for_aliases_instead_of_merging_same_named_interfaces()
    {
        const string active = """
            using Contracts = One;

            class Runner : Contracts.IThing
            {
            }
            """;
        const string contracts = """
            namespace One
            {
                interface IThing
                {
                    void One();
                }
            }

            namespace Two
            {
                interface IThing
                {
                    void Two();
                }
            }
            """;
        var activePath = "C:\\work\\Runner.cs";
        var sourcePosition = active.IndexOf("class Runner", StringComparison.Ordinal);
        var caret = ToPosition(active, sourcePosition);
        var sources = new Dictionary<string, string>
        {
            [activePath] = active,
            ["C:\\work\\Contracts.cs"] = contracts,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            activePath, active, caret.Line, caret.Character,
            CSharpCodeGenerationKind.ImplementInterface, sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(active, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public void One()", updated);
        Assert.DoesNotContain("public void Two()", updated);
    }

    [Fact]
    public void Semantic_interface_generation_sees_implementations_in_other_partial_declarations()
    {
        const string path = "C:\\work\\Runner.cs";
        const string active = """
            namespace Sample;
            public partial class Runner : IRunner
            {
            }
            """;
        const string contract = """
            namespace Sample;
            public interface IRunner
            {
                void Run();
                string Name { get; }
            }
            """;
        const string otherPart = """
            namespace Sample;
            public partial class Runner
            {
                public void Run() { }
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            ["C:\\work\\IRunner.cs"] = contract,
            ["C:\\work\\Runner.Part.cs"] = otherPart,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.ImplementInterface,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var generated = string.Join("\n", result.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.DoesNotContain("void Run", generated);
        Assert.Contains("public string Name { get; }", generated);
    }

    [Fact]
    public void Semantic_override_generation_sees_overrides_in_other_partial_declarations()
    {
        const string path = "C:\\work\\Derived.cs";
        const string active = """
            namespace Sample;
            public partial class Derived : Base
            {
            }
            """;
        const string baseSource = """
            namespace Sample;
            public abstract class Base
            {
                public virtual void Run() { }
                protected abstract string Name { get; }
            }
            """;
        const string otherPart = """
            namespace Sample;
            public partial class Derived
            {
                public override void Run() { }
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            ["C:\\work\\Base.cs"] = baseSource,
            ["C:\\work\\Derived.Part.cs"] = otherPart,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.OverrideMembers,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var generated = string.Join("\n", result.Edit!.Changes.Values.Single().Select(edit => edit.NewText));
        Assert.DoesNotContain("override void Run", generated);
        Assert.Contains("protected override string Name { get; }", generated);
    }

    [Fact]
    public void Generates_members_for_a_metadata_interface_when_no_source_declaration_exists()
    {
        const string source = """
            using System;

            class Holder : IDisposable
            {
            }
            """;
        var position = source.IndexOf("class Holder", StringComparison.Ordinal);
        var caret = ToPosition(source, position);
        var path = "C:\\work\\Holder.cs";
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, caret.Line, caret.Character,
            CSharpCodeGenerationKind.ImplementInterface, sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public void Dispose()", updated);
        Assert.Contains("NotImplementedException", updated);
    }

    [Fact]
    public void Generates_overrides_for_a_metadata_base_class_when_source_is_unavailable()
    {
        const string source = """
            using System.IO;

            class Derived : Stream
            {
            }
            """;
        var position = source.IndexOf("class Derived", StringComparison.Ordinal);
        var caret = ToPosition(source, position);
        var path = "C:\\work\\Derived.cs";
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, caret.Line, caret.Character,
            CSharpCodeGenerationKind.OverrideMembers, sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public override bool CanRead", updated);
        Assert.Contains("public override int Read", updated);
        Assert.Contains("base.CopyTo(", updated);
        var errors = CSharpSemanticCompilation.Create(
                new Dictionary<string, string> { [path] = updated })
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Generates_delegating_members_for_a_metadata_interface_field()
    {
        const string source = """
            using System.Collections.Generic;

            class Holder
            {
                private readonly IList<string> _items;
            }
            """;
        var position = source.IndexOf("IList", StringComparison.Ordinal);
        var caret = ToPosition(source, position);
        var path = "C:\\work\\Holder.cs";
        var sources = new Dictionary<string, string> { [path] = source };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, source, caret.Line, caret.Character,
            CSharpCodeGenerationKind.DelegatingMembers, sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var updated = Apply(source, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public int Count", updated);
        Assert.Contains("_items.Count", updated);
        Assert.Contains("public void Add", updated);
        Assert.Contains("_items.Add", updated);
        var errors = CSharpSemanticCompilation.Create(
                new Dictionary<string, string> { [path] = updated })
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void Generates_members_from_inherited_interfaces_across_compile_files()
    {
        const string active = """
            namespace Sample;
            class Runner : IChild
            {
            }
            """;
        const string contracts = """
            namespace Sample;
            interface IParent
            {
                void Run();
            }
            interface IChild : IParent
            {
                string Name { get; }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Runner.cs", active, line: 2, character: 8,
            CSharpCodeGenerationKind.ImplementInterface,
            new Dictionary<string, string> { ["C:\\work\\Contracts.cs"] = contracts });

        Assert.Null(result.Error);
        var updated = Apply(active, Assert.Single(result.Edit!.Changes.Values).Single());
        Assert.Contains("public void Run()", updated);
        Assert.Contains("public string Name { get; }", updated);
    }

    [Fact]
    public void Resolves_a_base_class_from_another_compile_file()
    {
        const string active = """
            namespace Sample;
            class Derived : Base
            {
            }
            """;
        const string baseSource = """
            namespace Sample;
            abstract class Base
            {
                protected abstract string Name { get; }
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Derived.cs", active, line: 2, character: 8,
            CSharpCodeGenerationKind.OverrideMembers,
            new Dictionary<string, string> { ["C:\\work\\Base.cs"] = baseSource });

        Assert.Null(result.Error);
        var updated = Apply(active, Assert.Single(result.Edit!.Changes.Values.Single()));
        Assert.Contains("protected override string Name { get; }", updated);
    }

    [Fact]
    public void Generates_dispose_pattern_and_adds_the_disposable_contract()
    {
        const string source = """
            using System;
            class Holder
            {
                private IDisposable _resource;
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Holder.cs", source, line: 2, character: 8,
            CSharpCodeGenerationKind.DisposePattern);

        Assert.Null(result.Error);
        var edits = Assert.Single(result.Edit!.Changes.Values);
        Assert.Contains(edits, edit => edit.NewText.Contains(" : global::System.IDisposable", System.StringComparison.Ordinal));
        Assert.Contains(edits, edit => edit.NewText.Contains("public void Dispose()", System.StringComparison.Ordinal));
        Assert.Contains(edits, edit => edit.NewText.Contains("_resource?.Dispose();", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Recognizes_nullable_disposable_fields()
    {
        const string source = """
            using System;
            class Holder
            {
                private IDisposable? _resource;
            }
            """;

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Holder.cs", source, line: 2, character: 8,
            CSharpCodeGenerationKind.DisposePattern);

        Assert.Null(result.Error);
        Assert.Contains(result.Edit!.Changes.Values.Single(), edit =>
            edit.NewText.Contains("_resource?.Dispose();", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_dispose_generation_recognizes_framework_types_by_interface_identity()
    {
        const string path = "C:\\work\\SemanticHolder.cs";
        const string source = """
            using System.IO;
            class Holder
            {
                private MemoryStream _resource;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 2, character: 8,
            CSharpCodeGenerationKind.DisposePattern,
            workspaceTexts: null,
            generationOptions: new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        Assert.Contains(result.Edit!.Changes.Values.Single(), edit =>
            edit.NewText.Contains("_resource?.Dispose();", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_dispose_generation_includes_disposable_fields_from_other_partial_declarations()
    {
        const string path = "C:\\work\\Holder.cs";
        const string active = """
            namespace Sample;
            public partial class Holder
            {
            }
            """;
        const string otherPart = """
            using System.IO;
            namespace Sample;
            public partial class Holder
            {
                private readonly MemoryStream _resource;
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            ["C:\\work\\Holder.Part.cs"] = otherPart,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, 2, 10, CSharpCodeGenerationKind.DisposePattern,
            sources, new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        Assert.Contains(result.Edit!.Changes.Values.Single(), edit =>
            edit.NewText.Contains("_resource?.Dispose();", StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_dispose_generation_uses_direct_call_for_non_nullable_disposable_value_types()
    {
        const string path = "C:\\work\\ValueHolder.cs";
        const string source = """
            using System;
            struct ValueResource : IDisposable
            {
                public void Dispose() { }
            }
            class Holder
            {
                private ValueResource _resource;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });
        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 6, character: 16,
            CSharpCodeGenerationKind.DisposePattern, workspaceTexts: null,
            generationOptions: new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var edit = result.Edit!.Changes.Values.Single().Single(change =>
            change.NewText.Contains("Dispose(bool", StringComparison.Ordinal));
        Assert.Contains("_resource.Dispose();", edit.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain("_resource?.Dispose();", edit.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_dispose_generation_overrides_an_inherited_dispose_pattern()
    {
        const string path = "C:\\work\\DerivedHolder.cs";
        const string source = """
            using System;
            using System.IO;
            class BaseHolder : IDisposable
            {
                protected virtual void Dispose(bool disposing) { }
                public void Dispose() => Dispose(true);
            }
            class DerivedHolder : BaseHolder
            {
                private MemoryStream _resource;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 8, character: 10,
            CSharpCodeGenerationKind.DisposePattern,
            workspaceTexts: null,
            generationOptions: new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var generated = result.Edit!.Changes.Values.Single().Single(change =>
            change.NewText.Contains("Dispose(bool", StringComparison.Ordinal)).NewText;
        Assert.Contains("protected override void Dispose(bool disposing)", generated,
            StringComparison.Ordinal);
        Assert.Contains("base.Dispose(disposing);", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("public void Dispose()", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_async_dispose_generation_emits_a_compilable_null_safe_pattern()
    {
        const string path = "C:\\work\\AsyncHolder.cs";
        const string source = """
            using System;
            using System.Threading.Tasks;
            class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            class Holder
            {
                private AsyncResource? _resource;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 8, character: 10,
            CSharpCodeGenerationKind.AsyncDisposePattern,
            workspaceTexts: null,
            generationOptions: new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        Assert.Contains(edits, edit => edit.NewText.Contains(
            " : global::System.IAsyncDisposable", StringComparison.Ordinal));
        var generated = edits.Single(edit => edit.NewText.Contains(
            "DisposeAsync()", StringComparison.Ordinal)).NewText;
        Assert.Contains("public async global::System.Threading.Tasks.ValueTask DisposeAsync()",
            generated, StringComparison.Ordinal);
        Assert.Contains("if (_resource is not null)", generated, StringComparison.Ordinal);
        Assert.Contains("await _resource.DisposeAsync().ConfigureAwait(false);",
            generated, StringComparison.Ordinal);

        var updated = ApplyEdits(source, edits);
        var updatedCompilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = updated });
        Assert.DoesNotContain(updatedCompilation.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Async_dispose_generation_requires_a_semantic_model()
    {
        const string source = "class Holder { private System.IAsyncDisposable? _resource; }";

        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\AsyncHolder.cs", source, line: 0, character: 10,
            CSharpCodeGenerationKind.AsyncDisposePattern);

        Assert.Null(result.Edit);
        Assert.Contains("意味モデル", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_async_dispose_generation_without_fields_avoids_an_async_warning()
    {
        const string path = "C:\\work\\AlreadyAsyncDisposable.cs";
        const string source = """
            using System;
            using System.Threading.Tasks;
            class Holder : IAsyncDisposable
            {
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 3, character: 8,
            CSharpCodeGenerationKind.AsyncDisposePattern,
            workspaceTexts: null,
            generationOptions: new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        var generated = Assert.Single(edits, edit => edit.NewText.Contains(
            "DisposeAsync()", StringComparison.Ordinal)).NewText;
        Assert.Contains("ValueTask.CompletedTask", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("public async", generated, StringComparison.Ordinal);

        var updated = ApplyEdits(source, edits);
        var updatedCompilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = updated });
        Assert.DoesNotContain(updatedCompilation.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Semantic_async_dispose_generation_overrides_an_inherited_async_core()
    {
        const string path = "C:\\work\\DerivedAsyncHolder.cs";
        const string source = """
            using System;
            using System.Threading.Tasks;
            class BaseHolder : IAsyncDisposable
            {
                protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
                public async ValueTask DisposeAsync()
                {
                    await DisposeAsyncCore();
                    GC.SuppressFinalize(this);
                }
            }
            class DerivedHolder : BaseHolder
            {
                private AsyncResource _resource = new();
            }
            class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpCodeGenerationService.Generate(
            path, source, line: 11, character: 12,
            CSharpCodeGenerationKind.AsyncDisposePattern,
            workspaceTexts: null,
            generationOptions: new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        var generated = Assert.Single(edits, edit => edit.NewText.Contains(
            "DisposeAsyncCore()", StringComparison.Ordinal)).NewText;
        Assert.Contains("protected override async global::System.Threading.Tasks.ValueTask DisposeAsyncCore()",
            generated, StringComparison.Ordinal);
        Assert.Contains("await base.DisposeAsyncCore().ConfigureAwait(false);",
            generated, StringComparison.Ordinal);
        Assert.DoesNotContain("public async global::System.Threading.Tasks.ValueTask DisposeAsync()",
            generated, StringComparison.Ordinal);

        var updated = ApplyEdits(source, edits);
        var updatedCompilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = updated });
        Assert.DoesNotContain(updatedCompilation.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Semantic_async_dispose_generation_rejects_an_existing_method_in_another_partial_file()
    {
        const string path = "C:\\work\\PartialAsyncHolder.cs";
        const string otherPath = "C:\\work\\PartialAsyncHolder.Part.cs";
        const string active = """
            using System;
            using System.Threading.Tasks;
            public partial class Holder : IAsyncDisposable
            {
                private AsyncResource _resource = new();
            }
            """;
        const string other = """
            using System.Threading.Tasks;
            public partial class Holder
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            class AsyncResource : IAsyncDisposable
            {
                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            [otherPath] = other,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, line: 4, character: 10,
            CSharpCodeGenerationKind.AsyncDisposePattern,
            sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Edit);
        Assert.Contains("既にあります", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_dispose_generation_rejects_an_existing_method_in_another_partial_file()
    {
        const string path = "C:\\work\\PartialHolder.cs";
        const string otherPath = "C:\\work\\PartialHolder.Part.cs";
        const string active = """
            using System;
            public partial class Holder
            {
                private IDisposable _resource;
            }
            """;
        const string other = """
            using System;
            public partial class Holder
            {
                public void Dispose() { }
            }
            """;
        var sources = new Dictionary<string, string>
        {
            [path] = active,
            [otherPath] = other,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpCodeGenerationService.Generate(
            path, active, line: 3, character: 10,
            CSharpCodeGenerationKind.DisposePattern,
            sources,
            new CSharpGenerationOptions(SemanticCompilation: compilation));

        Assert.Null(result.Edit);
        Assert.Contains("既にあります", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Generates_json_objects_arrays_and_original_property_names()
    {
        var result = JsonToCSharpGenerator.Generate("""
            {
              "first-name": "Ada",
              "age": 3,
              "address": { "city": "Tokyo" },
              "tags": ["one", "two"]
            }
            """, "Person");

        Assert.Null(result.Error);
        Assert.Contains("public sealed class Person", result.Text);
        Assert.Contains("JsonPropertyName(\"first-name\")", result.Text);
        Assert.Contains("public int Age { get; set; }", result.Text);
        Assert.Contains("public Address? Address { get; set; }", result.Text);
        Assert.Contains("public global::System.Collections.Generic.List<string> Tags", result.Text);
        Assert.Contains("public string? City { get; set; }", result.Text);
    }

    [Fact]
    public void Json_type_generation_returns_recoverable_errors_for_invalid_input()
    {
        Assert.Contains("JSONを解析できません", JsonToCSharpGenerator.Generate("{").Error);
        Assert.Contains("ルートJSONはオブジェクト", JsonToCSharpGenerator.Generate("[1, 2]").Error);
    }

    [Fact]
    public void Json_type_generation_is_a_single_active_file_workspace_edit()
    {
        var result = CSharpCodeGenerationService.GenerateJsonTypes(
            "C:\\work\\Models.cs", "class Existing { }\n", 1, 0,
            """{ "value": 1 }""", "Payload");

        Assert.Null(result.Error);
        var changes = Assert.Single(result.Edit!.Changes);
        Assert.EndsWith("Models.cs", changes.Key, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public sealed class Payload", changes.Value.Single().NewText);
    }

    [Fact]
    public void Json_type_generation_omits_nullable_annotations_when_project_nullable_is_disabled()
    {
        var result = CSharpCodeGenerationService.GenerateJsonTypes(
            "C:\\work\\Models.cs", "class Existing { }\n", 1, 0,
            """{ "name": "Ada", "address": { "city": "London" } }""", "Payload",
            new CSharpGenerationOptions(NullableEnabled: false));

        Assert.Null(result.Error);
        var insertion = result.Edit!.Changes.Values.Single().Single().NewText;
        Assert.Contains("public string Name", insertion);
        Assert.Contains("public Address Address", insertion);
        Assert.DoesNotContain("string?", insertion);
        Assert.DoesNotContain("Address?", insertion);
    }

    [Fact]
    public void Extracts_state_members_to_a_new_class_and_keeps_forwarding_wrappers()
    {
        const string source = """
            namespace Sample;

            public class Order
            {
                private int _count;
                public string Name { get; set; }
                public int Increment(int amount)
                {
                    return _count + amount;
                }

                public void Other() { }
            }
            """;
        var sourcePath = Path.Combine(Path.GetTempPath(), "LoomoExtractClass_" + Guid.NewGuid().ToString("N") + ".cs");
        var destinationPath = Path.Combine(Path.GetDirectoryName(sourcePath)!,
            Path.GetFileNameWithoutExtension(sourcePath) + "State.cs");
        var start = source.IndexOf("private int _count", StringComparison.Ordinal);
        var end = source.IndexOf("public void Other", start, StringComparison.Ordinal);
        var result = CSharpExtractClassService.Extract(
            sourcePath, source, Range(source, start, end), "OrderState", destinationPath);

        Assert.Null(result.Error);
        Assert.NotNull(result.Edit);
        var sourceUri = LspUri.FromPath(sourcePath);
        var updated = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
        Assert.Contains("private readonly OrderState _orderState = new();", updated);
        Assert.Contains("private int _count { get => _orderState._count;", updated);
        Assert.Contains("return _orderState.Increment(amount);", updated);
        Assert.DoesNotContain("private int _count;", updated);

        var destinationText = result.Edit.Changes[LspUri.FromPath(destinationPath)].Single().NewText;
        Assert.Contains("internal sealed class OrderState", destinationText);
        Assert.Contains("internal int _count;", destinationText);
        Assert.Contains("internal int Increment(int amount)", destinationText);
        Assert.Contains(result.Edit.FileOperations!, operation =>
            operation.Kind == LspFileOperationKind.Create &&
            string.Equals(operation.Uri, LspUri.FromPath(destinationPath), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_class_refuses_public_fields_that_would_change_the_public_api()
    {
        const string source = "public class Order { public int Count; private int _hidden; }";
        var sourcePath = Path.Combine(Path.GetTempPath(), "LoomoExtractClassApi_" + Guid.NewGuid().ToString("N") + ".cs");
        var destinationPath = Path.Combine(Path.GetDirectoryName(sourcePath)!,
            "OrderState_" + Guid.NewGuid().ToString("N") + ".cs");
        var start = source.IndexOf("public int Count", StringComparison.Ordinal);
        var result = CSharpExtractClassService.Extract(
            sourcePath, source, Range(source, start, start + "public int Count;".Length),
            "OrderState", destinationPath);

        Assert.Null(result.Edit);
        Assert.Contains("API", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Semantic_extract_class_does_not_treat_local_shadowing_as_member_dependency()
    {
        const string path = "C:\\work\\ShadowedExtract.cs";
        const string source = """
            class Sample
            {
                private int value;

                public int Calculate()
                {
                    var value = 1;
                    return value + 1;
                }

                public int Other => value;
            }
            """;
        var start = source.IndexOf("public int Calculate", StringComparison.Ordinal);
        var end = source.IndexOf("public int Other", start, StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });
        var destination = Path.Combine(Path.GetTempPath(),
            "LoomoShadowedExtract_" + Guid.NewGuid().ToString("N") + ".cs");

        var result = CSharpExtractClassService.Extract(
            path, source, Range(source, start, end), "Calculated", destination,
            compilation);

        Assert.Null(result.Error);
        Assert.NotNull(result.Edit);
        var extracted = result.Edit!.Changes[LspUri.FromPath(destination)].Single().NewText;
        Assert.Contains("return value + 1;", extracted);
    }

    [Fact]
    public void Semantic_extract_class_preserves_generic_type_parameters_and_constraints()
    {
        const string source = """
            namespace Sample;

            public class Box<T> where T : class
            {
                private T _value;

                public T Get()
                {
                    return _value;
                }

                public void Other() { }
            }
            """;
        var root = Path.Combine(Path.GetTempPath(), "LoomoGenericExtract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Combine(root, "Box.cs");
            var destinationPath = Path.Combine(root, "BoxState.cs");
            var compilation = CSharpSemanticCompilation.Create(
                new Dictionary<string, string> { [sourcePath] = source });
            var start = source.IndexOf("private T _value", StringComparison.Ordinal);
            var end = source.IndexOf("public void Other", start, StringComparison.Ordinal);

            var result = CSharpExtractClassService.Extract(
                sourcePath, source, Range(source, start, end), "BoxState", destinationPath,
                compilation);

            Assert.Null(result.Error);
            var sourceUri = LspUri.FromPath(sourcePath);
            var destinationUri = LspUri.FromPath(destinationPath);
            var updatedSource = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
            var generated = result.Edit.Changes[destinationUri].Single().NewText;
            Assert.Contains("BoxState<T> _boxState", updatedSource, StringComparison.Ordinal);
            Assert.Contains("internal sealed class BoxState<T> where T : class", generated,
                StringComparison.Ordinal);
            Assert.Contains("internal T _value;", generated, StringComparison.Ordinal);
            Assert.Contains("internal T Get()", generated, StringComparison.Ordinal);

            var updatedCompilation = CSharpSemanticCompilation.Create(
                new Dictionary<string, string>
                {
                    [sourcePath] = updatedSource,
                    [destinationPath] = generated,
                });
            Assert.DoesNotContain(updatedCompilation.GetDiagnostics(), diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Semantic_extract_class_supports_selected_members_from_a_partial_declaration()
    {
        const string sourcePath = "C:\\work\\PartialOrder.cs";
        const string otherPath = "C:\\work\\PartialOrder.Other.cs";
        const string source = """
            namespace Sample;

            public partial class Order
            {
                private int _count;

                public int Increment(int amount)
                {
                    return _count + amount;
                }
            }
            """;
        const string other = """
            namespace Sample;

            public partial class Order
            {
                public void Other() { }
            }
            """;
        var root = Path.Combine(Path.GetTempPath(), "LoomoPartialExtract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var activePath = Path.Combine(root, Path.GetFileName(sourcePath));
            var partPath = Path.Combine(root, Path.GetFileName(otherPath));
            var destinationPath = Path.Combine(root, "OrderState.cs");
            var sources = new Dictionary<string, string>
            {
                [activePath] = source,
                [partPath] = other,
            };
            var compilation = CSharpSemanticCompilation.Create(sources);
            var start = source.IndexOf("private int _count", StringComparison.Ordinal);
            var end = source.Length;

            var result = CSharpExtractClassService.Extract(
                activePath, source, Range(source, start, end), "OrderState", destinationPath,
                compilation);

            Assert.Null(result.Error);
            var sourceUri = LspUri.FromPath(activePath);
            var destinationUri = LspUri.FromPath(destinationPath);
            var updatedSource = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
            var generated = result.Edit.Changes[destinationUri].Single().NewText;
            Assert.Contains("private readonly OrderState _orderState", updatedSource,
                StringComparison.Ordinal);
            Assert.Contains("internal int Increment(int amount)", generated,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Other", generated, StringComparison.Ordinal);

            var updatedCompilation = CSharpSemanticCompilation.Create(
                new Dictionary<string, string>
                {
                    [activePath] = updatedSource,
                    [partPath] = other,
                    [destinationPath] = generated,
                });
            Assert.DoesNotContain(updatedCompilation.GetDiagnostics(), diagnostic =>
                diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extracts_contiguous_statements_and_captures_outer_parameters()
    {
        const string source = """
            class Sample
            {
                void Run(int value)
                {
                    var doubled = value * 2;
                    Console.WriteLine(doubled);
                }
            }
        """;
        var start = source.IndexOf("var doubled", System.StringComparison.Ordinal);
        var statementEnd = source.IndexOf("Console.WriteLine(doubled);", start, System.StringComparison.Ordinal);
        var end = statementEnd + "Console.WriteLine(doubled);".Length;
        var result = CSharpExtractMethodService.Extract(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "WriteValue");

        Assert.Null(result.Error);
        var edits = Assert.Single(result.Edit!.Changes.Values);
        Assert.Equal(2, edits.Count);
        Assert.Contains(edits, edit => edit.NewText == "WriteValue(value);");
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "private void WriteValue(int value)", System.StringComparison.Ordinal));
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "var doubled = value * 2;", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_extract_infers_var_and_keeps_shadowed_local_identity()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                void Run(int value)
                {
                    {
                        var value = "text";
                        Console.WriteLine(value);
                    }
                    Console.WriteLine(value);
                }
            }
            """;
        var start = source.IndexOf("Console.WriteLine(value);", StringComparison.Ordinal);
        var end = start + "Console.WriteLine(value);".Length;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = source,
            });

        var result = CSharpExtractMethodService.Extract(
            path, source, Range(source, start, end), "Print", compilation);

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        Assert.Contains(edits, edit => edit.NewText == "Print(value);");
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "private void Print(string", StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_extract_preserves_writes_to_captured_parameters_with_ref()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                void Run(int value)
                {
                    value++;
                }
            }
            """;
        var start = source.IndexOf("value++;", StringComparison.Ordinal);
        var end = start + "value++;".Length;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = source,
            });

        var result = CSharpExtractMethodService.Extract(
            path, source, Range(source, start, end), "Increment", compilation);

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        Assert.Contains(edits, edit => edit.NewText == "Increment(ref value);");
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "private void Increment(ref int value)", StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_extract_refuses_instance_member_from_static_method()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private int value;

                static void Run()
                {
                    value++;
                }
            }
            """;
        var start = source.IndexOf("value++;", StringComparison.Ordinal);
        var end = start + "value++;".Length;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = source,
            });

        var result = CSharpExtractMethodService.Extract(
            path, source, Range(source, start, end), "Increment", compilation);

        Assert.Null(result.Edit);
        Assert.Contains("staticメソッド", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extracts_a_return_statement_with_the_enclosing_return_type()
    {
        const string source = """
            class Sample
            {
                int Run(int value)
                {
                    return value + 1;
                }
            }
            """;
        var start = source.IndexOf("return value", System.StringComparison.Ordinal);
        var end = source.IndexOf(';', start) + 1;
        var result = CSharpExtractMethodService.Extract(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "AddOne");

        Assert.Null(result.Error);
        Assert.Contains(result.Edit!.Changes.Values.Single(), edit => edit.NewText == "return AddOne(value);");
        Assert.Contains(result.Edit.Changes.Values.Single(), edit => edit.NewText.Contains(
            "private int AddOne(int value)", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_an_extraction_that_leaks_a_local_variable()
    {
        const string source = """
            class Sample
            {
                void Run()
                {
                    var value = 1;
                    Console.WriteLine(value);
                }
            }
            """;
        var start = source.IndexOf("var value", System.StringComparison.Ordinal);
        var end = source.IndexOf(';', start) + 1;
        var result = CSharpExtractMethodService.Extract(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "CreateValue");

        Assert.Null(result.Edit);
        Assert.Contains("範囲外", result.Error);
    }

    [Fact]
    public void Refuses_a_selection_that_cuts_through_a_statement()
    {
        const string source = """
            class Sample
            {
                void Run()
                {
                    Console.WriteLine(1);
                    Console.WriteLine(2);
                }
            }
            """;
        var start = source.IndexOf("WriteLine(1)", System.StringComparison.Ordinal) + 2;
        var end = source.IndexOf("WriteLine(2)", System.StringComparison.Ordinal) + "WriteLine(2)".Length;
        var result = CSharpExtractMethodService.Extract(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "Print");

        Assert.Null(result.Edit);
        Assert.Contains("連続した文", result.Error);
    }

    [Fact]
    public void Introduces_a_var_for_a_selected_expression_at_statement_start()
    {
        const string source = """
            class Sample
            {
                int Run(int value)
                {
                    return value + 1;
                }
            }
            """;
        var start = source.IndexOf("value + 1", System.StringComparison.Ordinal);
        var end = start + "value + 1".Length;
        var result = CSharpIntroduceVariableService.Introduce(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "next");

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        Assert.Contains(edits, edit => edit.NewText == "next");
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "var next = value + 1;", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_introduce_variable_refuses_a_void_expression()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                void Run()
                {
                    DoWork();
                }

                void DoWork() { }
            }
            """;
        var selected = "DoWork()";
        var start = source.IndexOf(selected, System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpIntroduceVariableService.Introduce(
            path, source, Range(source, start, start + selected.Length),
            "result", compilation);

        Assert.Null(result.Edit);
        Assert.Contains("戻り値のない", result.Error);
    }

    [Fact]
    public void Refuses_variable_introduction_when_the_name_is_already_in_scope()
    {
        const string source = """
            class Sample
            {
                int Run(int value)
                {
                    return value + 1;
                }
            }
            """;
        var start = source.IndexOf("value + 1", System.StringComparison.Ordinal);
        var end = start + "value + 1".Length;
        var result = CSharpIntroduceVariableService.Introduce(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "value");

        Assert.Null(result.Edit);
        Assert.Contains("同名", result.Error);
    }

    [Fact]
    public void Extracts_a_string_literal_as_a_typed_const_member()
    {
        const string source = """
            class Sample
            {
                string Run()
                    => "ready";
            }
            """;
        var start = source.IndexOf("\"ready\"", System.StringComparison.Ordinal);
        var end = start + "\"ready\"".Length;
        var result = CSharpExtractConstantService.Extract(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "ReadyText");

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        Assert.Contains(edits, edit => edit.NewText == "ReadyText");
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "private const string ReadyText = \"ready\";", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Semantic_extract_constant_accepts_a_compile_time_expression()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                int Run()
                {
                    return 1 + 2;
                }
            }
            """;
        var selected = "1 + 2";
        var start = source.IndexOf(selected, System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpExtractConstantService.Extract(
            path, source, Range(source, start, start + selected.Length),
            "Answer", compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.Contains("private const", updated);
        Assert.Contains("Answer = 1 + 2;", updated);
        Assert.Contains("return Answer;", updated);
    }

    [Fact]
    public void Refuses_non_constant_expression_for_constant_extraction()
    {
        const string source = """
            class Sample
            {
                string Run(string value)
                    => value;
            }
            """;
        var start = source.LastIndexOf("value", System.StringComparison.Ordinal);
        var end = start + "value".Length;
        var result = CSharpExtractConstantService.Extract(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "Value");

        Assert.Null(result.Edit);
        Assert.Contains("リテラル", result.Error);
    }

    [Fact]
    public void Refuses_variable_introduction_inside_a_condition()
    {
        const string source = """
            class Sample
            {
                bool Run(int value)
                {
                    if (value > 0) return true;
                    return false;
                }
            }
            """;
        var start = source.IndexOf("value > 0", System.StringComparison.Ordinal);
        var end = start + "value > 0".Length;
        var result = CSharpIntroduceVariableService.Introduce(
            "C:\\work\\Sample.cs", source, Range(source, start, end), "isPositive");

        Assert.Null(result.Edit);
        Assert.Contains("条件式", result.Error);
    }

    [Fact]
    public void Inlines_a_local_variable_and_removes_its_declaration()
    {
        const string source = """
            class Sample
            {
                void Run(int value)
                {
                    var doubled = value * 2;
                    Console.WriteLine(doubled);
                }
            }
            """;
        var start = source.IndexOf("doubled", System.StringComparison.Ordinal);
        var end = start + "doubled".Length;
        var result = CSharpInlineVariableService.Inline(
            "C:\\work\\Sample.cs", source, Range(source, start, end));

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.DoesNotContain("var doubled", updated);
        Assert.Contains("Console.WriteLine((value * 2));", updated);
    }

    [Fact]
    public void Refuses_to_inline_a_side_effecting_initializer_when_it_would_run_twice()
    {
        const string source = """
            class Sample
            {
                int Run()
                {
                    var value = GetValue();
                    return value + value;
                }
            }
            """;
        var start = source.IndexOf("value", System.StringComparison.Ordinal);
        var end = start + "value".Length;
        var result = CSharpInlineVariableService.Inline(
            "C:\\work\\Sample.cs", source, Range(source, start, end));

        Assert.Null(result.Edit);
        Assert.Contains("複数回評価", result.Error);
    }

    [Fact]
    public void Refuses_to_inline_a_variable_that_is_written_after_declaration()
    {
        const string source = """
            class Sample
            {
                int Run()
                {
                    var value = 1;
                    value++;
                    return value;
                }
            }
            """;
        var start = source.IndexOf("value", System.StringComparison.Ordinal);
        var end = start + "value".Length;
        var result = CSharpInlineVariableService.Inline(
            "C:\\work\\Sample.cs", source, Range(source, start, end));

        Assert.Null(result.Edit);
        Assert.Contains("書き換え", result.Error);
    }

    [Fact]
    public void Semantic_inline_replaces_a_nested_scope_reference_of_the_selected_local()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                int GetValue() => 3;

                void Run()
                {
                    var value = GetValue();
                    {
                        Console.WriteLine(value);
                    }
                }
            }
            """;
        var start = source.LastIndexOf("value", System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpInlineVariableService.Inline(
            path, source, Range(source, start, start + "value".Length), compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.DoesNotContain("var value", updated);
        Assert.Contains("Console.WriteLine((GetValue()));", updated);
    }

    [Fact]
    public void Semantic_inline_does_not_replace_a_shadowing_local()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                void Run()
                {
                    var value = 1;
                    {
                        var value = 2;
                        Console.WriteLine(value);
                    }
                    Console.WriteLine(value);
                }
            }
            """;
        var start = source.IndexOf("value", System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpInlineVariableService.Inline(
            path, source, Range(source, start, start + "value".Length), compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.DoesNotContain("var value = 1", updated);
        Assert.Contains("var value = 2", updated);
        Assert.Contains("Console.WriteLine(value);", updated);
        Assert.Contains("Console.WriteLine((1));", updated);
    }

    [Fact]
    public void Encapsulates_a_readonly_field_with_a_read_only_property()
    {
        const string source = """
            class Sample
            {
                private readonly string _name;
            }
            """;
        var start = source.IndexOf("_name", System.StringComparison.Ordinal);
        var result = CSharpEncapsulateFieldService.Encapsulate(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "_name".Length), "Name");

        Assert.Null(result.Error);
        var updated = Apply(source, result.Edit!.Changes.Values.Single().Single());
        Assert.Contains("public string Name => _name;", updated);
    }

    [Fact]
    public void Encapsulates_a_mutable_static_field_with_a_static_property()
    {
        const string source = """
            class Sample
            {
                private static int _count;
            }
            """;
        var start = source.IndexOf("_count", System.StringComparison.Ordinal);
        var result = CSharpEncapsulateFieldService.Encapsulate(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "_count".Length), "Count");

        Assert.Null(result.Error);
        var updated = Apply(source, result.Edit!.Changes.Values.Single().Single());
        Assert.Contains("public static int Count { get => _count; set => _count = value; }", updated);
    }

    [Fact]
    public void Semantic_encapsulate_field_uses_the_field_symbol_type()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            using System.Collections.Generic;

            class Sample
            {
                private List<string> _items;
            }
            """;
        var start = source.IndexOf("_items", System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpEncapsulateFieldService.Encapsulate(
            path, source, Range(source, start, start + "_items".Length),
            "Items", compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.Contains("public", updated);
        Assert.Contains("Items { get => _items; set => _items = value; }", updated);
        Assert.Contains("System.Collections.Generic.List<string>", updated);
    }

    [Fact]
    public void Encapsulate_field_does_not_treat_a_member_usage_as_a_name_collision()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private string _value;

                public string ReadValue() => _value;
            }
            """;
        var start = source.IndexOf("_value", System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpEncapsulateFieldService.Encapsulate(
            path, source, Range(source, start, start + "_value".Length),
            "Value", compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.Contains("Value { get => _value; set => _value = value; }", updated);
    }

    [Fact]
    public void Refuses_to_encapsulate_an_already_public_field_or_a_colliding_property()
    {
        const string publicField = """
            class Sample
            {
                public int _value;
            }
            """;
        var publicStart = publicField.IndexOf("_value", System.StringComparison.Ordinal);
        var publicResult = CSharpEncapsulateFieldService.Encapsulate(
            "C:\\work\\Sample.cs", publicField,
            Range(publicField, publicStart, publicStart + "_value".Length), "Value");
        Assert.Null(publicResult.Edit);
        Assert.Contains("公開されている", publicResult.Error);

        const string collision = """
            class Sample
            {
                private int _value;
                public int Value { get; }
            }
            """;
        var collisionStart = collision.IndexOf("_value", System.StringComparison.Ordinal);
        var collisionResult = CSharpEncapsulateFieldService.Encapsulate(
            "C:\\work\\Sample.cs", collision,
            Range(collision, collisionStart, collisionStart + "_value".Length), "Value");
        Assert.Null(collisionResult.Edit);
        Assert.Contains("同名", collisionResult.Error);
    }

    [Fact]
    public void Derives_property_names_from_common_private_field_conventions()
    {
        Assert.Equal("Name", CSharpEncapsulateFieldService.DefaultPropertyName("_name"));
        Assert.Equal("Name", CSharpEncapsulateFieldService.DefaultPropertyName("m_name"));
        Assert.Equal("Value", CSharpEncapsulateFieldService.DefaultPropertyName("value"));
    }

    [Fact]
    public void Extracts_a_literal_expression_to_a_readonly_field()
    {
        const string source = """
            class Sample
            {
                int Run()
                {
                    return 42;
                }
            }
            """;
        var start = source.IndexOf("42", System.StringComparison.Ordinal);
        var result = CSharpExtractFieldService.Extract(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + 2), "Answer");

        Assert.Null(result.Error);
        var edits = result.Edit!.Changes.Values.Single();
        Assert.Contains(edits, edit => edit.NewText.Contains(
            "private readonly int Answer = 42;", System.StringComparison.Ordinal));
        Assert.Contains(edits, edit => edit.NewText == "Answer");
    }

    [Fact]
    public void Refuses_to_extract_an_expression_that_captures_a_parameter()
    {
        const string source = """
            class Sample
            {
                int Run(int value)
                {
                    return (int)(value + 1);
                }
            }
            """;
        var start = source.IndexOf("(int)(value + 1)", System.StringComparison.Ordinal);
        var result = CSharpExtractFieldService.Extract(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "(int)(value + 1)".Length), "Answer");

        Assert.Null(result.Edit);
        Assert.Contains("捕捉", result.Error);
    }

    [Fact]
    public void Semantic_extract_field_infers_a_binary_expression_type()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private int count;

                int Run()
                {
                    return count + 1;
                }
            }
            """;
        var selected = "count + 1";
        var start = source.IndexOf(selected, System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpExtractFieldService.Extract(
            path, source, Range(source, start, start + selected.Length),
            "Answer", compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.Contains("private readonly int Answer = count + 1;", updated);
        Assert.Contains("return Answer;", updated);
    }

    [Fact]
    public void Semantic_extract_field_refuses_an_instance_member_from_static_method()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private int count;

                static int Run()
                {
                    return count + 1;
                }
            }
            """;
        var selected = "count + 1";
        var start = source.IndexOf(selected, System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpExtractFieldService.Extract(
            path, source, Range(source, start, start + selected.Length),
            "Answer", compilation);

        Assert.Null(result.Edit);
        Assert.Contains("staticメソッド", result.Error);
    }

    [Fact]
    public void Semantic_introduce_property_infers_instance_member_scope()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private int count;

                int Run()
                {
                    return count + 1;
                }
            }
            """;
        var selected = "count + 1";
        var start = source.IndexOf(selected, System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpIntroducePropertyService.Introduce(
            path, source, Range(source, start, start + selected.Length),
            "Answer", "int", "private", compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.Contains("private int Answer => count + 1;", updated);
        Assert.Contains("return Answer;", updated);
    }

    [Fact]
    public void Semantic_introduce_property_makes_static_expression_static()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private static int count;

                static int Run()
                {
                    return count + 1;
                }
            }
            """;
        var selected = "count + 1";
        var start = source.IndexOf(selected, System.StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });

        var result = CSharpIntroducePropertyService.Introduce(
            path, source, Range(source, start, start + selected.Length),
            "Answer", "int", "private", compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.Single());
        Assert.Contains("private static int Answer => count + 1;", updated);
        Assert.Contains("return Answer;", updated);
    }

    [Fact]
    public void Moves_a_top_level_type_to_a_new_file_with_usings_and_namespace()
    {
        const string source = """
            using System;

            namespace Sample;

            class Keep { }

            public class Moved
            {
                public DateTime Created { get; } = DateTime.UtcNow;
            }
            """;
        var start = source.IndexOf("Moved", System.StringComparison.Ordinal);
        var destination = Path.Combine(Path.GetTempPath(), "LoomoMoved_" + Guid.NewGuid().ToString("N") + ".cs");
        var result = CSharpMoveTypeToFileService.Move(
            "C:\\work\\Source.cs", source,
            Range(source, start, start + "Moved".Length), destination);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Edit!.Changes.Count);
        Assert.Contains(result.Edit.FileOperations!, operation =>
            operation.Kind == LspFileOperationKind.Create
            && operation.NewUri is null
            && operation.Uri.EndsWith(Path.GetFileName(destination), System.StringComparison.OrdinalIgnoreCase));
        var movedText = result.Edit.Changes.Values.Single(edits => edits.Any(edit =>
            edit.NewText.Contains("namespace Sample;", System.StringComparison.Ordinal))).Single().NewText;
        Assert.Contains("using System;", movedText);
        Assert.Contains("public class Moved", movedText);
        Assert.Contains(result.Edit.Changes.Values.SelectMany(edits => edits), edit => edit.NewText == "");
    }

    [Fact]
    public async Task Semantic_move_type_requires_the_declared_type_symbol()
    {
        const string path = "C:\\work\\Source.cs";
        const string source = "public class Moved { public int Value { get; } }";
        var destination = Path.Combine(Path.GetTempPath(),
            "LoomoSemanticMoved_" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            var start = source.IndexOf("Moved", System.StringComparison.Ordinal);
            var result = await CSharpSemanticOperations.MoveTypeToFileAsync(
                null, path, source, Range(source, start, start + "Moved".Length), destination);

            Assert.Null(result.Error);
            Assert.Contains(result.Edit!.Changes.Values.SelectMany(edits => edits),
                edit => edit.NewText.Contains("public class Moved", System.StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public void Refuses_to_move_nested_or_partial_types()
    {
        const string nested = """
            class Outer
            {
                class Inner { }
            }
            """;
        var nestedStart = nested.IndexOf("Inner", System.StringComparison.Ordinal);
        var nestedResult = CSharpMoveTypeToFileService.Move(
            "C:\\work\\Source.cs", nested,
            Range(nested, nestedStart, nestedStart + "Inner".Length),
            Path.Combine(Path.GetTempPath(), "LoomoNested_" + Guid.NewGuid().ToString("N") + ".cs"));
        Assert.Null(nestedResult.Edit);
        Assert.Contains("入れ子", nestedResult.Error);

        const string partial = """
            partial class Sample { }
            """;
        var partialStart = partial.IndexOf("Sample", System.StringComparison.Ordinal);
        var partialResult = CSharpMoveTypeToFileService.Move(
            "C:\\work\\Source.cs", partial,
            Range(partial, partialStart, partialStart + "Sample".Length),
            Path.Combine(Path.GetTempPath(), "LoomoPartial_" + Guid.NewGuid().ToString("N") + ".cs"));
        Assert.Null(partialResult.Edit);
        Assert.Contains("partial", partialResult.Error);
    }

    [Fact]
    public void Refuses_to_move_over_an_existing_destination_file()
    {
        const string source = "class Sample { }";
        var start = source.IndexOf("Sample", System.StringComparison.Ordinal);
        var destination = Path.Combine(Path.GetTempPath(), "LoomoExisting_" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(destination, "class Existing { }");
        try
        {
            var result = CSharpMoveTypeToFileService.Move(
                "C:\\work\\Source.cs", source,
                Range(source, start, start + "Sample".Length), destination);
            Assert.Null(result.Edit);
            Assert.Contains("既に存在", result.Error);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public void Inlines_a_single_private_expression_method_at_its_only_call()
    {
        const string source = """
            class Sample
            {
                private int Double(int value) => value * 2;

                int Run() => Double(3);
            }
            """;
        var start = source.IndexOf("Double", System.StringComparison.Ordinal);
        var result = CSharpInlineMethodService.Inline(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "Double".Length));

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Contains("int Run() => (3) * 2;", updated);
        Assert.DoesNotContain("private int Double", updated);
    }

    [Fact]
    public void Can_inline_by_selecting_the_only_call_site()
    {
        const string source = """
            class Sample
            {
                private string Prefix(string value) { return "x" + value; }

                string Run() => Prefix("a");
            }
            """;
        var start = source.LastIndexOf("Prefix", System.StringComparison.Ordinal);
        var result = CSharpInlineMethodService.Inline(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "Prefix".Length));

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Contains("string Run() => \"x\" + (\"a\");", updated);
    }

    [Fact]
    public void Semantic_inline_by_call_site_selects_only_the_matching_overload()
    {
        const string path = "C:\\work\\Sample.cs";
        const string source = """
            class Sample
            {
                private string Format(int value) => value.ToString();
                private string Format(string value) => value;

                string Run()
                {
                    var first = Format(1);
                    var second = Format("text");
                    return first + second;
                }
            }
            """;
        var start = source.IndexOf("Format(1)", StringComparison.Ordinal);
        var nameEnd = start + "Format".Length;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = source,
            });

        var result = CSharpInlineMethodService.Inline(
            path, source, Range(source, start, nameEnd), compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Contains("var first = (1).ToString();", updated, StringComparison.Ordinal);
        Assert.Contains("private string Format(string value)", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("private string Format(int value)", updated, StringComparison.Ordinal);

        var methodStart = source.IndexOf("Format(int", StringComparison.Ordinal);
        var declarationResult = CSharpInlineMethodService.Inline(
            path, source, Range(source, methodStart, methodStart + "Format".Length), compilation);
        Assert.Null(declarationResult.Error);
    }

    [Fact]
    public void Refuses_public_methods_and_repeated_side_effecting_arguments()
    {
        const string publicSource = """
            class Sample
            {
                public int Double(int value) => value * 2;
                int Run() => Double(3);
            }
            """;
        var publicStart = publicSource.IndexOf("Double", System.StringComparison.Ordinal);
        var publicResult = CSharpInlineMethodService.Inline(
            "C:\\work\\Sample.cs", publicSource,
            Range(publicSource, publicStart, publicStart + "Double".Length));
        Assert.Null(publicResult.Edit);
        Assert.Contains("private", publicResult.Error);

        const string repeatedSource = """
            class Sample
            {
                private int Add(int value) => value + value;
                int Run() => Add(GetValue());
                int GetValue() => 1;
            }
            """;
        var repeatedStart = repeatedSource.IndexOf("Add", System.StringComparison.Ordinal);
        var repeatedResult = CSharpInlineMethodService.Inline(
            "C:\\work\\Sample.cs", repeatedSource,
            Range(repeatedSource, repeatedStart, repeatedStart + "Add".Length));
        Assert.Null(repeatedResult.Edit);
        Assert.Contains("複数回評価", repeatedResult.Error);
    }

    [Fact]
    public void Generates_delegating_methods_properties_and_events_from_an_interface()
    {
        const string source = """
            using System;

            class Controller
            {
                private IService _service;
            }
            """;
        const string contract = """
            interface IService
            {
                string Name { get; }
                void Run(int value);
                event EventHandler Changed;
            }
            """;
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Controller.cs", source, line: 4, character: 25,
            CSharpCodeGenerationKind.DelegatingMembers,
            new Dictionary<string, string> { ["C:\\work\\IService.cs"] = contract });

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.Contains("public string Name { get => _service.Name; }", updated);
        Assert.Contains("public void Run(int value)", updated);
        Assert.Contains("_service.Run(value);", updated);
        Assert.Contains("public event EventHandler Changed", updated);
        Assert.Contains("_service.Changed += value;", updated);
        Assert.Contains("_service.Changed -= value;", updated);
    }

    [Fact]
    public void Does_not_duplicate_existing_delegating_members()
    {
        const string source = """
            class Controller
            {
                private IService _service;
                public string Name => _service.Name;
            }
            """;
        const string contract = """
            interface IService
            {
                string Name { get; }
                void Run();
            }
            """;
        var result = CSharpCodeGenerationService.Generate(
            "C:\\work\\Controller.cs", source, line: 2, character: 25,
            CSharpCodeGenerationKind.DelegatingMembers,
            new Dictionary<string, string> { ["C:\\work\\IService.cs"] = contract });

        Assert.Null(result.Error);
        var generated = result.Edit!.Changes.Values.SelectMany(edits => edits)
            .Single().NewText;
        Assert.DoesNotContain("public string Name", generated);
        Assert.Contains("public void Run()", generated);
    }

    [Fact]
    public void Refuses_ambiguous_or_generic_delegation_targets()
    {
        const string source = """
            class Controller
            {
                private IService _service;
            }
            """;
        var missing = CSharpCodeGenerationService.Generate(
            "C:\\work\\Controller.cs", source, 2, 25,
            CSharpCodeGenerationKind.DelegatingMembers);
        Assert.Null(missing.Edit);
        Assert.Contains("interface", missing.Error);

        const string genericSource = """
            class Controller
            {
                private IService<int> _service;
            }
            """;
        var generic = CSharpCodeGenerationService.Generate(
            "C:\\work\\Controller.cs", genericSource, 2, 30,
            CSharpCodeGenerationKind.DelegatingMembers,
            new Dictionary<string, string> { ["C:\\work\\IService.cs"] = "interface IService { void Run(); }" });
        Assert.Null(generic.Edit);
        Assert.Contains("ジェネリック", generic.Error);
    }

    [Fact]
    public void Safely_deletes_an_unreferenced_private_member()
    {
        const string source = """
            class Sample
            {
                private int Unused() => 1;
                int Run() => 2;
            }
            """;
        var start = source.IndexOf("Unused", System.StringComparison.Ordinal);
        var result = CSharpSafeDeleteService.Delete(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "Unused".Length));

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.DoesNotContain("Unused", updated);
        Assert.Contains("int Run() => 2;", updated);
    }

    [Fact]
    public void Semantic_safe_delete_ignores_an_unrelated_same_named_member()
    {
        const string path = "C:\\work\\First.cs";
        const string otherPath = "C:\\work\\Second.cs";
        const string source = """
            class First
            {
                private int Value;
            }
            """;
        const string other = """
            class Second
            {
                private int Value;
                int Read() => Value;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source, [otherPath] = other });
        var start = source.IndexOf("Value", StringComparison.Ordinal);

        var result = CSharpSafeDeleteService.Delete(
            path, source, Range(source, start, start + "Value".Length),
            new Dictionary<string, string> { [otherPath] = other },
            workspaceParseOptions: null,
            semanticCompilation: compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes.Values.SelectMany(edits => edits));
        Assert.DoesNotContain("Value", updated);
    }

    [Fact]
    public void Semantic_safe_delete_rejects_a_reference_to_the_target_symbol()
    {
        const string path = "C:\\work\\Referenced.cs";
        const string source = """
            class Sample
            {
                private int Value;
                int Read() => Value;
            }
            """;
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string> { [path] = source });
        var start = source.IndexOf("Value", StringComparison.Ordinal);

        var result = CSharpSafeDeleteService.Delete(
            path, source, Range(source, start, start + "Value".Length),
            workspaceTexts: null, workspaceParseOptions: null,
            semanticCompilation: compilation);

        Assert.Null(result.Edit);
        Assert.Contains("参照", result.Error);
    }

    [Fact]
    public void Refuses_safe_delete_when_a_workspace_reference_remains()
    {
        const string source = """
            partial class Sample
            {
                private int _value;
            }
            """;
        const string other = """
            partial class Sample
            {
                int Run() => _value;
            }
            """;
        var start = source.IndexOf("_value", System.StringComparison.Ordinal);
        var result = CSharpSafeDeleteService.Delete(
            "C:\\work\\Sample.cs", source,
            Range(source, start, start + "_value".Length),
            new Dictionary<string, string> { ["C:\\work\\Sample.Part2.cs"] = other });

        Assert.Null(result.Edit);
        Assert.Contains("参照", result.Error);
    }

    [Fact]
    public void Refuses_public_members_and_top_level_types()
    {
        const string publicSource = "public class Sample { public void Run() { } }";
        var publicStart = publicSource.IndexOf("Run", System.StringComparison.Ordinal);
        var publicResult = CSharpSafeDeleteService.Delete(
            "C:\\work\\Sample.cs", publicSource,
            Range(publicSource, publicStart, publicStart + "Run".Length));
        Assert.Null(publicResult.Edit);
        Assert.Contains("公開", publicResult.Error);

        const string typeSource = "class Sample { }";
        var typeStart = typeSource.IndexOf("Sample", System.StringComparison.Ordinal);
        var typeResult = CSharpSafeDeleteService.Delete(
            "C:\\work\\Sample.cs", typeSource,
            Range(typeSource, typeStart, typeStart + "Sample".Length));
        Assert.Null(typeResult.Edit);
        Assert.Contains("トップレベル", typeResult.Error);
    }

    [Fact]
    public void Extracts_a_public_class_contract_to_a_new_interface_file()
    {
        const string source = """
            using System;

            namespace Sample;

            public class Service
            {
                public string Name { get; private set; }
                public void Run(int value) { }
                public event EventHandler? Changed;
                private void Hide() { }
            }
            """;
        var destination = Path.Combine(Path.GetTempPath(), "LoomoExtracted_" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            var start = source.IndexOf("Service", System.StringComparison.Ordinal);
            var result = CSharpExtractInterfaceService.Extract(
                "C:\\work\\Service.cs", source,
                Range(source, start, start + "Service".Length), "IService", destination);

            Assert.Null(result.Error);
            var sourceUri = LspUri.FromPath("C:\\work\\Service.cs");
            var updated = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
            Assert.Contains("public class Service : IService", updated);

            var interfaceText = result.Edit.Changes.Values.Single(edits => edits.Any(edit =>
                edit.NewText.Contains("public interface IService", System.StringComparison.Ordinal))).Single().NewText;
            Assert.Contains("namespace Sample;", interfaceText);
            Assert.Contains("string Name { get; }", interfaceText);
            Assert.Contains("void Run(int value);", interfaceText);
            Assert.Contains("event EventHandler? Changed;", interfaceText);
            Assert.DoesNotContain("Hide", interfaceText);
            Assert.Contains(result.Edit.FileOperations!, operation =>
                operation.Kind == LspFileOperationKind.Create
                && operation.Uri.EndsWith(Path.GetFileName(destination), System.StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public void Extract_interface_respects_public_property_accessors()
    {
        const string source = """
            class Service
            {
                public int Value { get; private set; }
            }
            """;
        var destination = Path.Combine(Path.GetTempPath(), "LoomoPropertyContract_" + Guid.NewGuid().ToString("N") + ".cs");
        var start = source.IndexOf("Service", System.StringComparison.Ordinal);
        var result = CSharpExtractInterfaceService.Extract(
            "C:\\work\\Service.cs", source,
            Range(source, start, start + "Service".Length), "IService", destination);

        Assert.Null(result.Error);
        var interfaceText = result.Edit!.Changes.Values.Single(edits => edits.Any(edit =>
            edit.NewText.Contains("public interface IService", System.StringComparison.Ordinal))).Single().NewText;
        Assert.Contains("int Value { get; }", interfaceText);
        Assert.DoesNotContain("private", interfaceText);
    }

    [Fact]
    public async Task Semantic_extract_interface_uses_public_instance_member_symbols()
    {
        const string path = "C:\\work\\Service.cs";
        const string source = """
            namespace Sample;

            public class Service
            {
                public int Value => 1;
                public static void Helper() { }
                private void Hide() { }
            }
            """;
        var destination = Path.Combine(Path.GetTempPath(),
            "LoomoSemanticInterface_" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            var start = source.IndexOf("Service", System.StringComparison.Ordinal);
            var compilation = CSharpSemanticCompilation.Create(
                new Dictionary<string, string> { [path] = source });
            var semanticTree = compilation.SyntaxTrees.Single();
            var semanticModel = compilation.GetSemanticModel(semanticTree);
            var semanticTypeNode = semanticTree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().Single();
            var semanticPropertyNode = semanticTree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>().Single();
            var semanticTypeSymbol = semanticModel.GetDeclaredSymbol(semanticTypeNode) as INamedTypeSymbol;
            Assert.NotNull(semanticModel.GetDeclaredSymbol(semanticPropertyNode));
            Assert.Contains(semanticTypeSymbol!.GetMembers("Value"), symbol => symbol.Name == "Value");
            Assert.False(semanticTypeSymbol.GetMembers("Value").OfType<IPropertySymbol>().Single().IsStatic);
            Assert.Contains(semanticTypeNode.Members, member => member is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax property &&
                property.Identifier.ValueText == "Value" && property.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword)));

            var result = await CSharpSemanticOperations.ExtractInterfaceAsync(
                null,
                path,
                source,
                Range(source, start, start + "Service".Length),
                "IService",
                destination);

            Assert.Null(result.Error);
            var interfaceText = result.Edit!.Changes.Values.Single(edits => edits.Any(edit =>
                edit.NewText.Contains("public interface IService", System.StringComparison.Ordinal)))
                .Single().NewText;
            Assert.Contains("int Value { get; }", interfaceText);
            Assert.DoesNotContain("Helper", interfaceText);
            Assert.DoesNotContain("Hide", interfaceText);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public async Task Semantic_extract_interface_collects_same_file_partial_declarations()
    {
        const string path = "C:\\work\\Service.cs";
        const string source = """
            namespace Sample;

            public partial class Service
            {
                public void Run() { }
            }

            public partial class Service
            {
                public int Value => 1;
                private void Hide() { }
            }
            """;
        var destination = Path.Combine(Path.GetTempPath(),
            "LoomoSemanticPartialInterface_" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            var start = source.IndexOf("Service", System.StringComparison.Ordinal);
            var result = await CSharpSemanticOperations.ExtractInterfaceAsync(
                null,
                path,
                source,
                Range(source, start, start + "Service".Length),
                "IService",
                destination);

            Assert.Null(result.Error);
            var interfaceText = result.Edit!.Changes.Values.Single(edits => edits.Any(edit =>
                edit.NewText.Contains("public interface IService", System.StringComparison.Ordinal)))
                .Single().NewText;
            Assert.Contains("void Run()", interfaceText);
            Assert.Contains("int Value { get; }", interfaceText);
            Assert.DoesNotContain("Hide", interfaceText);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public void Semantic_extract_interface_collects_multi_file_partial_declarations_and_imports()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "LoomoMultiFilePartialInterface_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Service.cs");
            var otherPath = Path.Combine(root, "Service.Part.cs");
            var destination = Path.Combine(root, "IService.cs");
            var source = """
                namespace Sample;

                public partial class Service
                {
                    public void Run() { }
                }
                """;
            var other = """
                using Models = System.Collections.Generic;

                namespace Sample;

                public partial class Service
                {
                    public Models.List<string> Names { get; } = new();
                    private void Hide() { }
                }
                """;
            var compilation = CSharpSemanticCompilation.Create(
                new Dictionary<string, string>
                {
                    [path] = source,
                    [otherPath] = other,
                });
            var start = source.IndexOf("Service", StringComparison.Ordinal);
            var result = CSharpExtractInterfaceService.Extract(
                path, source, Range(source, start, start + "Service".Length),
                "IService", destination, compilation);

            Assert.Null(result.Error);
            var updated = ApplyEdits(source, result.Edit!.Changes[LspUri.FromPath(path)]);
            Assert.Contains("Service : IService", updated, StringComparison.Ordinal);
            var interfaceText = result.Edit.Changes.Values.Single(edits => edits.Any(edit =>
                edit.NewText.Contains("public interface IService", StringComparison.Ordinal))).Single().NewText;
            Assert.Contains("using Models = System.Collections.Generic;", interfaceText,
                StringComparison.Ordinal);
            Assert.Contains("void Run();", interfaceText, StringComparison.Ordinal);
            Assert.Contains("Models.List<string> Names", interfaceText, StringComparison.Ordinal);
            Assert.DoesNotContain("Hide", interfaceText, StringComparison.Ordinal);

            var errors = CSharpSemanticCompilation.Create(
                    new Dictionary<string, string>
                    {
                        [path] = updated,
                        [otherPath] = other,
                        [destination] = interfaceText,
                    })
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToArray();
            Assert.Empty(errors);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Semantic_extract_interface_rejects_conflicting_aliases_across_partial_files()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "LoomoConflictingPartialInterface_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Service.cs");
            var otherPath = Path.Combine(root, "Service.Part.cs");
            var destination = Path.Combine(root, "IService.cs");
            var source = """
                using Model = One;
                namespace Sample;
                public partial class Service { public void Run() { } }
                """;
            var other = """
                using Model = Two;
                namespace Sample;
                public partial class Service { public void Save() { } }
                """;
            var compilation = CSharpSemanticCompilation.Create(
                new Dictionary<string, string>
                {
                    [path] = source,
                    [otherPath] = other,
                });
            var start = source.IndexOf("Service", StringComparison.Ordinal);
            var result = CSharpExtractInterfaceService.Extract(
                path, source, Range(source, start, start + "Service".Length),
                "IService", destination, compilation);

            Assert.Null(result.Edit);
            Assert.Contains("using alias", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Semantic_extract_interface_preserves_internal_type_accessibility()
    {
        const string path = "C:\\work\\InternalService.cs";
        const string source = """
            namespace Sample;

            class Service
            {
                public void Run() { }
            }
            """;
        var destination = Path.Combine(Path.GetTempPath(),
            "LoomoSemanticInternalInterface_" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            var start = source.IndexOf("Service", System.StringComparison.Ordinal);
            var result = await CSharpSemanticOperations.ExtractInterfaceAsync(
                null,
                path,
                source,
                Range(source, start, start + "Service".Length),
                "IService",
                destination);

            Assert.Null(result.Error);
            var interfaceText = result.Edit!.Changes.Values.Single(edits => edits.Any(edit =>
                edit.NewText.Contains("internal interface IService", System.StringComparison.Ordinal)))
                .Single().NewText;
            Assert.Contains("void Run()", interfaceText);
            Assert.DoesNotContain("public interface IService", interfaceText);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public async Task Semantic_extract_interface_supports_generic_class_type_parameters_and_constraints()
    {
        const string path = "C:\\work\\Repository.cs";
        const string source = """
            namespace Sample;

            public class Repository<T> where T : class, new()
            {
                public T Create() => new T();
                public void Save(T value) { }
                private void Hide() { }
            }
            """;
        var destination = Path.Combine(Path.GetTempPath(),
            "LoomoGenericInterface_" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            var start = source.IndexOf("Repository", StringComparison.Ordinal);
            var result = await CSharpSemanticOperations.ExtractInterfaceAsync(
                null, path, source,
                Range(source, start, start + "Repository".Length),
                "IRepository", destination);

            Assert.Null(result.Error);
            var sourceUri = LspUri.FromPath(path);
            var updated = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
            Assert.Contains("public class Repository<T> : IRepository<T> where T : class, new()",
                updated, StringComparison.Ordinal);

            var interfaceText = result.Edit.Changes.Values.Single(edits => edits.Any(edit =>
                edit.NewText.Contains("public interface IRepository<T> where T : class, new()",
                    StringComparison.Ordinal))).Single().NewText;
            Assert.Contains("T Create();", interfaceText, StringComparison.Ordinal);
            Assert.Contains("void Save(T value);", interfaceText, StringComparison.Ordinal);
            Assert.DoesNotContain("Hide", interfaceText, StringComparison.Ordinal);

            var errors = CSharpSemanticCompilation.Create(new Dictionary<string, string>
                {
                    [path] = updated,
                    [destination] = interfaceText,
                })
                .GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToArray();
            Assert.Empty(errors);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public void Refuses_generic_or_partial_classes_for_interface_extraction()
    {
        const string generic = "public class Service<T> { public void Run() { } }";
        var genericStart = generic.IndexOf("Service", System.StringComparison.Ordinal);
        var genericResult = CSharpExtractInterfaceService.Extract(
            "C:\\work\\Service.cs", generic,
            Range(generic, genericStart, genericStart + "Service".Length), "IService",
            Path.Combine(Path.GetTempPath(), "LoomoGeneric_" + Guid.NewGuid().ToString("N") + ".cs"));
        Assert.Null(genericResult.Edit);
        Assert.Contains("意味モデル", genericResult.Error);

        const string partial = "public partial class Service { public void Run() { } }";
        var partialStart = partial.IndexOf("Service", System.StringComparison.Ordinal);
        var partialResult = CSharpExtractInterfaceService.Extract(
            "C:\\work\\Service.cs", partial,
            Range(partial, partialStart, partialStart + "Service".Length), "IService",
            Path.Combine(Path.GetTempPath(), "LoomoPartialInterface_" + Guid.NewGuid().ToString("N") + ".cs"));
        Assert.Null(partialResult.Edit);
        Assert.Contains("partial", partialResult.Error);
    }

    [Fact]
    public void Pulls_a_protected_member_into_the_direct_base_class()
    {
        const string source = """
            namespace Sample;

            class Derived : Base
            {
                protected string Name => "derived";
            }
            """;
        const string baseSource = """
            namespace Sample;

            class Base
            {
            }
            """;
        var start = source.IndexOf("Name", System.StringComparison.Ordinal);
        var result = CSharpPullUpMemberService.PullUp(
            "C:\\work\\Derived.cs", source,
            Range(source, start, start + "Name".Length),
            new Dictionary<string, string> { ["C:\\work\\Base.cs"] = baseSource });

        Assert.Null(result.Error);
        var sourceUri = LspUri.FromPath("C:\\work\\Derived.cs");
        var baseUri = LspUri.FromPath("C:\\work\\Base.cs");
        var updatedSource = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
        var updatedBase = ApplyEdits(baseSource, result.Edit.Changes[baseUri]);
        Assert.DoesNotContain("protected string Name", updatedSource);
        Assert.Contains("protected string Name => \"derived\";", updatedBase);
    }

    [Fact]
    public void Semantic_pull_up_ignores_a_local_with_the_same_name_as_a_base_member()
    {
        const string path = "C:\\work\\Derived.cs";
        const string source = """
            namespace Sample;

            class Derived : Base
            {
                public string GetName()
                {
                    var Name = "local";
                    return Name;
                }
            }
            """;
        const string basePath = "C:\\work\\Base.cs";
        const string baseSource = """
            namespace Sample;

            class Base
            {
                protected string Name => "base";
            }
            """;
        var start = source.IndexOf("GetName", StringComparison.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [path] = source,
            [basePath] = baseSource,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpPullUpMemberService.PullUp(
            path, source, Range(source, start, start + "GetName".Length),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [basePath] = baseSource,
            }, workspaceParseOptions: null, semanticCompilation: compilation);

        Assert.Null(result.Error);
        var sourceUri = LspUri.FromPath(path);
        var baseUri = LspUri.FromPath(basePath);
        var updatedSource = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
        var updatedBase = ApplyEdits(baseSource, result.Edit.Changes[baseUri]);
        Assert.DoesNotContain("GetName", updatedSource, StringComparison.Ordinal);
        Assert.Contains("public string GetName()", updatedBase, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_up_merges_two_edits_when_base_and_derived_share_a_file()
    {
        const string source = """
            class Base
            {
            }

            class Derived : Base
            {
                public void Run() { }
            }
            """;
        var start = source.LastIndexOf("Run", System.StringComparison.Ordinal);
        var result = CSharpPullUpMemberService.PullUp(
            "C:\\work\\Both.cs", source,
            Range(source, start, start + "Run".Length));

        Assert.Null(result.Error);
        var uri = LspUri.FromPath("C:\\work\\Both.cs");
        var updated = ApplyEdits(source, result.Edit!.Changes[uri]);
        Assert.DoesNotContain("public void Run", updated[(updated.IndexOf("class Derived", System.StringComparison.Ordinal))..]);
        Assert.Contains("class Base\n{\n    public void Run()", updated.Replace("\r\n", "\n", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_pull_up_when_the_member_depends_on_a_derived_member()
    {
        const string source = """
            class Derived : Base
            {
                protected int Value => _offset;
                private int _offset;
            }
            """;
        const string baseSource = "class Base\n{\n}\n";
        var start = source.IndexOf("Value", System.StringComparison.Ordinal);
        var result = CSharpPullUpMemberService.PullUp(
            "C:\\work\\Derived.cs", source,
            Range(source, start, start + "Value".Length),
            new Dictionary<string, string> { ["C:\\work\\Base.cs"] = baseSource });

        Assert.Null(result.Edit);
        Assert.Contains("派生クラス固有", result.Error);
    }

    [Fact]
    public void Pushes_a_protected_member_into_the_direct_derived_class()
    {
        const string source = """
            namespace Sample;

            class Base
            {
                protected string Name => "base";
            }
            """;
        const string derivedSource = """
            namespace Sample;

            class Derived : Base
            {
            }
            """;
        var start = source.IndexOf("Name", System.StringComparison.Ordinal);
        var result = CSharpPushDownMemberService.PushDown(
            "C:\\work\\Base.cs", source,
            Range(source, start, start + "Name".Length),
            new Dictionary<string, string> { ["C:\\work\\Derived.cs"] = derivedSource });

        Assert.Null(result.Error);
        var sourceUri = LspUri.FromPath("C:\\work\\Base.cs");
        var derivedUri = LspUri.FromPath("C:\\work\\Derived.cs");
        var updatedSource = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
        var updatedDerived = ApplyEdits(derivedSource, result.Edit.Changes[derivedUri]);
        Assert.DoesNotContain("protected string Name", updatedSource);
        Assert.Contains("protected string Name => \"base\";", updatedDerived);
    }

    [Fact]
    public void Push_down_merges_two_edits_when_base_and_derived_share_a_file()
    {
        const string source = """
            class Base
            {
                public void Run() { }
            }

            class Derived : Base
            {
            }
            """;
        var start = source.IndexOf("Run", System.StringComparison.Ordinal);
        var result = CSharpPushDownMemberService.PushDown(
            "C:\\work\\Both.cs", source,
            Range(source, start, start + "Run".Length));

        Assert.Null(result.Error);
        var uri = LspUri.FromPath("C:\\work\\Both.cs");
        var updated = ApplyEdits(source, result.Edit!.Changes[uri]);
        Assert.DoesNotContain("public void Run", updated[..updated.IndexOf("class Derived", System.StringComparison.Ordinal)]);
        Assert.Contains("class Derived : Base\n{\n    public void Run()", updated.Replace("\r\n", "\n", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_push_down_when_multiple_direct_derived_classes_exist()
    {
        const string source = """
            class Base
            {
                public void Run() { }
            }

            class First : Base { }
            class Second : Base { }
            """;
        var start = source.IndexOf("Run", System.StringComparison.Ordinal);
        var result = CSharpPushDownMemberService.PushDown(
            "C:\\work\\Both.cs", source,
            Range(source, start, start + "Run".Length));

        Assert.Null(result.Edit);
        Assert.Contains("複数", result.Error);
    }

    [Fact]
    public void Refuses_push_down_when_the_member_depends_on_another_base_member()
    {
        const string source = """
            class Base
            {
                protected string Name => _prefix;
                private string _prefix = "base";
            }

            class Derived : Base { }
            """;
        var start = source.IndexOf("Name", System.StringComparison.Ordinal);
        var result = CSharpPushDownMemberService.PushDown(
            "C:\\work\\Both.cs", source,
            Range(source, start, start + "Name".Length));

        Assert.Null(result.Edit);
        Assert.Contains("基底クラス固有", result.Error);
    }

    [Fact]
    public void Semantic_push_down_allows_an_inherited_protected_dependency()
    {
        const string path = "C:\\work\\Base.cs";
        const string source = """
            namespace Sample;

            class Base
            {
                protected string Prefix => "base";
                public string GetName() => Prefix;
            }
            """;
        const string derivedPath = "C:\\work\\Derived.cs";
        const string derivedSource = """
            namespace Sample;

            class Derived : Base
            {
            }
            """;
        var start = source.IndexOf("GetName", StringComparison.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [path] = source,
            [derivedPath] = derivedSource,
        };
        var compilation = CSharpSemanticCompilation.Create(sources);

        var result = CSharpPushDownMemberService.PushDown(
            path, source, Range(source, start, start + "GetName".Length),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [derivedPath] = derivedSource,
            }, destinationPath: null, workspaceParseOptions: null,
            semanticCompilation: compilation);

        Assert.Null(result.Error);
        var sourceUri = LspUri.FromPath(path);
        var derivedUri = LspUri.FromPath(derivedPath);
        var updatedSource = ApplyEdits(source, result.Edit!.Changes[sourceUri]);
        var updatedDerived = ApplyEdits(derivedSource, result.Edit.Changes[derivedUri]);
        Assert.DoesNotContain("GetName", updatedSource, StringComparison.Ordinal);
        Assert.Contains("public string GetName() => Prefix;", updatedDerived,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Introduces_a_parameter_and_updates_same_type_calls()
    {
        const string source = """
            class Service
            {
                public string Format(int value) => value.ToString();

                public string Run(int value)
                {
                    return Format(value);
                }
            }
            """;
        var start = source.IndexOf("Format", System.StringComparison.Ordinal);
        var result = CSharpIntroduceParameterService.Introduce(
            "C:\\work\\Service.cs", source,
            Range(source, start, start + "Format".Length),
            "culture", "System.Globalization.CultureInfo", "CultureInfo.InvariantCulture");

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes[LspUri.FromPath("C:\\work\\Service.cs")]);
        Assert.Contains("Format(int value, System.Globalization.CultureInfo culture)", updated);
        Assert.Contains("Format(value, CultureInfo.InvariantCulture)", updated);
    }

    [Fact]
    public void Semantic_parameter_introduction_updates_only_the_selected_overload()
    {
        const string path = "C:\\work\\Service.cs";
        const string source = """
            class Service
            {
                public void Run(int value) { }
                public void Run(string value) { }

                void Invoke()
                {
                    Run("text");
                    Run(1);
                }
            }
            """;
        var targetStart = source.IndexOf("Run(int", StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = source,
            });

        var result = CSharpIntroduceParameterService.Introduce(
            path, source, Range(source, targetStart, targetStart + 3),
            "culture", "int", "42", null, null, null, compilation);

        Assert.Null(result.Error);
        var updated = ApplyEdits(source, result.Edit!.Changes[LspUri.FromPath(path)]);
        Assert.Contains("Run(int value, int culture)", updated, StringComparison.Ordinal);
        Assert.Contains("Run(\"text\")", updated, StringComparison.Ordinal);
        Assert.Contains("Run(1, 42)", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_parameter_introduction_refuses_to_break_an_interface_contract()
    {
        const string path = "C:\\work\\Service.cs";
        const string source = """
            interface IContract
            {
                void Run();
            }

            class Service : IContract
            {
                public void Run() { }
            }
            """;
        var targetStart = source.LastIndexOf("Run", StringComparison.Ordinal);
        var compilation = CSharpSemanticCompilation.Create(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = source,
            });

        var result = CSharpIntroduceParameterService.Introduce(
            path, source, Range(source, targetStart, targetStart + 3),
            "value", "int", "42", null, null, null, compilation);

        Assert.Null(result.Edit);
        Assert.Contains("interface契約", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Introduces_a_parameter_across_files_for_a_static_type_call()
    {
        const string source = """
            namespace Sample;

            public static class Service
            {
                public static void Run() { }
            }
            """;
        const string caller = """
            namespace Sample;

            class Caller
            {
                void Invoke() => Service.Run();
            }
            """;
        var start = source.IndexOf("Run", System.StringComparison.Ordinal);
        var result = CSharpIntroduceParameterService.Introduce(
            "C:\\work\\Service.cs", source,
            Range(source, start, start + "Run".Length),
            "value", "int", "42",
            new Dictionary<string, string> { ["C:\\work\\Caller.cs"] = caller });

        Assert.Null(result.Error);
        var serviceUri = LspUri.FromPath("C:\\work\\Service.cs");
        var callerUri = LspUri.FromPath("C:\\work\\Caller.cs");
        var updatedService = ApplyEdits(source, result.Edit!.Changes[serviceUri]);
        var updatedCaller = ApplyEdits(caller, result.Edit.Changes[callerUri]);
        Assert.Contains("Run(int value)", updatedService);
        Assert.Contains("Service.Run(42)", updatedCaller);
    }

    [Fact]
    public void Refuses_parameter_introduction_for_an_unresolved_method_group()
    {
        const string source = """
            class Service
            {
                public void Run() { }

                void Invoke()
                {
                    System.Action action = Run;
                }
            }
            """;
        var start = source.IndexOf("Run", System.StringComparison.Ordinal);
        var result = CSharpIntroduceParameterService.Introduce(
            "C:\\work\\Service.cs", source,
            Range(source, start, start + "Run".Length),
            "value", "int", "42");

        Assert.Null(result.Edit);
        Assert.Contains("メソッドグループ", result.Error);
    }

    [Fact]
    public void Refuses_parameter_introduction_without_calls_or_default_value()
    {
        const string source = "class Service { public void Run() { } }";
        var start = source.IndexOf("Run", System.StringComparison.Ordinal);
        var result = CSharpIntroduceParameterService.Introduce(
            "C:\\work\\Service.cs", source,
            Range(source, start, start + "Run".Length),
            "value", "int", "42");

        Assert.Null(result.Edit);
        Assert.Contains("既定値", result.Error);
    }

    private static LspRange Range(string source, int start, int end)
        => new(ToPosition(source, start), ToPosition(source, end));

    private static LspPosition ToPosition(string source, int offset)
    {
        var line = source[..offset].Count(c => c == '\n');
        var lineStart = source.LastIndexOf('\n', Math.Max(0, offset - 1));
        return new LspPosition(line, offset - (lineStart < 0 ? 0 : lineStart + 1));
    }

    private static string Apply(string source, LspTextEdit edit)
    {
        var lines = source.Replace("\r\n", "\n", System.StringComparison.Ordinal).Split('\n').ToList();
        lines.Insert(edit.Range.Start.Line, edit.NewText.TrimEnd('\r', '\n'));
        return string.Join('\n', lines);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var offset = 0; (offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0;
             offset += value.Length)
            count++;
        return count;
    }

    private static string ApplyEdits(string source, IEnumerable<LspTextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(edit => edit.Range.Start.Line)
                     .ThenByDescending(edit => edit.Range.Start.Character))
        {
            var start = Offset(source, edit.Range.Start);
            var end = Offset(source, edit.Range.End);
            source = source[..start] + edit.NewText + source[end..];
        }
        return source;
    }

    private static int Offset(string source, LspPosition position)
    {
        var lineStart = 0;
        for (var i = 0; i < position.Line; i++)
            lineStart = source.IndexOf('\n', lineStart) + 1;
        return lineStart + position.Character;
    }
}
