namespace sk0ya.Loomo.App.Services;

/// <summary>
/// 1つの表示面（ペイン本体・切り離しウィンドウ）が使う <see cref="IEditorSupportVisual"/> の所有者。
/// 提供者ごとに実体を1つ作って使い回し、まとめて破棄する。
///
/// <para>
/// 表示面ごとに1つ持つのが要点で、これにより同じ提供者（CSV グリッド等）をペインと切り離し
/// ウィンドウが<b>同時に</b>表示できる（WPF の単一親制約に触れない）。
/// </para>
/// <para>
/// 検索ハイライトの条件もここが保持する。以前は「表示中でない提供者にも配るので条件は自分で
/// 保持すること」と各提供者へ要求していたが、実体が遅延生成になった以上それでは足りない
/// （まだ作られていない実体には配れない）。ここが最後の条件を覚えておき、
/// <see cref="GetOrCreate"/> で作った直後にも適用する。
/// </para>
/// </summary>
public sealed class EditorSupportVisualHost : IDisposable
{
    private readonly Dictionary<IEditorSupportVisualProvider, IEditorSupportVisual> _visuals = new();
    private readonly EventHandler<EditorSupportContentEdited>? _contentEdited;
    private string _searchTerm = "";
    private bool _searchCaseSensitive;
    private bool _searchUseRegex;

    /// <param name="contentEdited">ビュー内編集の書き戻し通知の購読先（不要なら null）。</param>
    public EditorSupportVisualHost(EventHandler<EditorSupportContentEdited>? contentEdited = null)
        => _contentEdited = contentEdited;

    /// <summary>この表示面が現在持っている実体（検索ハイライトの配布などに使う）。</summary>
    public IEnumerable<IEditorSupportVisual> Visuals => _visuals.Values;

    /// <summary>提供者の表示インスタンスを取り出す（無ければ作る）。UI スレッドで呼ぶこと。</summary>
    public IEditorSupportVisual GetOrCreate(IEditorSupportVisualProvider provider)
    {
        if (_visuals.TryGetValue(provider, out var existing))
            return existing;

        var visual = provider.CreateVisual();
        if (_contentEdited is not null)
            visual.ContentEdited += _contentEdited;
        _visuals[provider] = visual;
        // 作りたてにも今の検索条件を渡す（後から作られた実体だけ塗られない、を防ぐ）。
        if (visual is IEditorSupportSearchHighlightTarget target)
            target.ApplySearchHighlight(_searchTerm, _searchCaseSensitive, _searchUseRegex);
        return visual;
    }

    /// <summary>検索ハイライトの条件を覚え、いま持っている実体すべてへ配る。</summary>
    public void SetSearchHighlight(string? term, bool caseSensitive, bool useRegex)
    {
        _searchTerm = term ?? "";
        _searchCaseSensitive = caseSensitive;
        _searchUseRegex = useRegex;
        foreach (var target in _visuals.Values.OfType<IEditorSupportSearchHighlightTarget>())
            target.ApplySearchHighlight(_searchTerm, _searchCaseSensitive, _searchUseRegex);
    }

    public void Dispose()
    {
        foreach (var visual in _visuals.Values)
        {
            if (_contentEdited is not null)
                visual.ContentEdited -= _contentEdited;
            try { visual.Dispose(); }
            catch { /* 破棄の失敗で他の実体の破棄を止めない */ }
        }
        _visuals.Clear();
    }
}
