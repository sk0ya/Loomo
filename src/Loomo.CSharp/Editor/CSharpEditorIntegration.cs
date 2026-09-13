using Editor.Core.Engine;
using Editor.Core.Extensibility;
using Editor.Core.Syntax;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>Loomo AppへC#固有のsyntax／edit assistを登録する唯一の入口。</summary>
public static class CSharpEditorIntegration
{
    public static void Configure(VimEngineServices services)
    {
        ConfigureSyntax(services.SyntaxLanguages);
        services.EditAssists.Register(new CSharpEditAssist());
    }

    /// <summary>エディタ本体以外の表示（Diff など）にも同じ字句解析器を渡す。</summary>
    public static void ConfigureSyntax(SyntaxLanguageRegistry languages)
    {
        languages.Register(new SyntaxLanguageDescriptor("C#", [".cs"]),
            static () => new CSharpSyntaxLanguage(), RegistrationPolicy.Replace);
        languages.Register(new SyntaxLanguageDescriptor("Solution", [".sln", ".slnx"]),
            static () => new SolutionSyntax(), RegistrationPolicy.Replace);
    }
}
