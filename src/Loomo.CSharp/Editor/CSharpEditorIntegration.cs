using Editor.Core.Engine;
using Editor.Core.Extensibility;
using Editor.Core.Syntax;

namespace sk0ya.Loomo.CSharp.Editor;

/// <summary>Loomo AppへC#固有のsyntax／edit assistを登録する唯一の入口。</summary>
public static class CSharpEditorIntegration
{
    public static void Configure(VimEngineServices services)
    {
        services.SyntaxLanguages.Register(new SyntaxLanguageDescriptor("C#", [".cs"]),
            static () => new CSharpSyntaxLanguage(), RegistrationPolicy.Replace);
        services.EditAssists.Register(new CSharpEditAssist());
    }
}
