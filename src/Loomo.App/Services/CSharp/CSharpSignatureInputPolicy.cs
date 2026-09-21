using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>
/// Change Signature ダイアログで入力そのものの整合性を検証する。
/// 呼び出し元の書き換え可否はリファクタリング計画の作成時に別途検証する。
/// </summary>
internal static class CSharpSignatureInputPolicy
{
    public static SignatureChange BuildChange(
        bool isConstructor,
        string returnType,
        IEnumerable<SignatureParameterChange> parameters)
        => new(isConstructor ? string.Empty : DialogTextPolicy.Trim(returnType), parameters.ToArray());

    public static SignatureParameterChange ToChange(
        int originalIndex,
        string modifiers,
        string type,
        string name,
        string defaultValue,
        string callSiteArgument)
        => new(
            originalIndex,
            new SignatureParameter(
                DialogTextPolicy.Trim(name), DialogTextPolicy.Trim(type), DialogTextPolicy.Trim(modifiers),
                DialogTextPolicy.TrimOrNull(defaultValue)),
            DialogTextPolicy.TrimOrNull(callSiteArgument));

    public static string? Validate(SignatureChange change)
    {
        foreach (var parameter in change.Parameters)
        {
            if (parameter.Parameter.Name.Length == 0)
                return "名前が空のパラメーターがあります。";
            if (parameter.Parameter.Type.Length == 0)
                return "型が空のパラメーターがあります。";
            if (parameter.IsNew && parameter.CallSiteArgument is null &&
                parameter.Parameter.DefaultValue is null)
                return $"追加したパラメーター '{parameter.Parameter.Name}' には、既定値か呼び出し側の値のどちらかが必要です。";
        }

        var names = change.Parameters.Select(parameter => parameter.Parameter.Name).ToList();
        return names.Distinct(StringComparer.Ordinal).Count() != names.Count
            ? "パラメーター名が重複しています。"
            : null;
    }
}
