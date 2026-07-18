namespace sk0ya.Loomo.App.Services;

public enum EditorSupportKind
{
    Unsupported,
    Provider,
    Code
}

/// <summary>ファイルに対して選択された EditorSupport の実装方式。</summary>
public sealed record EditorSupportSelection(
    EditorSupportKind Kind,
    IEditorSupportProvider? Provider = null);

/// <summary>
/// 登録 Provider、コードアウトライン、Hex フォールバックの優先順位を一元管理する。
/// </summary>
public sealed class EditorSupportResolver
{
    private readonly EditorSupportRegistry _registry;
    private readonly CodeEditorSupport _code;
    private readonly HexEditorSupport _hex;

    public EditorSupportResolver(
        EditorSupportRegistry registry,
        CodeEditorSupport code,
        HexEditorSupport hex)
    {
        _registry = registry;
        _code = code;
        _hex = hex;
    }

    public EditorSupportSelection Resolve(string? filePath)
    {
        if (_registry.Resolve(filePath) is { } provider)
            return new EditorSupportSelection(EditorSupportKind.Provider, provider);

        if (_code.CanHandle(filePath))
            return new EditorSupportSelection(EditorSupportKind.Code);

        if (filePath is not null && BinaryFileDetector.IsBinary(filePath))
            return new EditorSupportSelection(EditorSupportKind.Provider, _hex);

        return new EditorSupportSelection(EditorSupportKind.Unsupported);
    }
}
