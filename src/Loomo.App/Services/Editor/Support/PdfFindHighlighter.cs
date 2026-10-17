namespace sk0ya.Loomo.App.Services;

/// <summary>
/// PDF の中の検索語を塗る。PDF は Chromium 内蔵ビューアの中身でページスクリプトが届かないので、
/// WebView2 の Find API（＝Ctrl+F と同じページ内検索）を既定 UI 非表示で使う。表示面（本体ペイン・切り離した複製）
/// ごとに1つ持つ。
/// <para>
/// <see cref="CoreWebView2Find.StartAsync"/> は非同期なので、打鍵ごとの呼び出しが追い越し合うと「消したはずの語が
/// 塗られたまま」になる——ゲートで直列化し、適用済みの条件と同じ要求は捨てて、最後の要求が必ず勝つようにする。
/// ページを読み直すと Find のセッションも消えるので、そのときは <see cref="Invalidate"/> で適用済みを忘れること。
/// </para>
/// </summary>
internal sealed class PdfFindHighlighter : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _appliedTerm;          // 今の文書へ実際に適用済みの条件（null＝不明・未適用）
    private bool _appliedCaseSensitive;

    /// <summary>適用済みの条件を忘れる（ページを読み直した後。次の <see cref="ApplyAsync"/> は必ず適用し直す）。</summary>
    public void Invalidate() => _appliedTerm = null;

    /// <summary>塗る語を合わせる（空なら消す）。Find API はリテラル検索なので、正規表現の語は呼び元が空にして渡すこと。</summary>
    public async Task ApplyAsync(CoreWebView2 core, string term, bool caseSensitive)
    {
        await _gate.WaitAsync();
        try
        {
            if (_appliedTerm == term && (term.Length == 0 || _appliedCaseSensitive == caseSensitive))
                return;
            if (term.Length == 0)
            {
                core.Find.Stop();   // セッションが無ければ何もしない（API 仕様）
                _appliedTerm = "";
                return;
            }
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = caseSensitive;
            options.ShouldHighlightAllMatches = true;
            options.SuppressDefaultFindDialog = true;   // 自前の検索パネルがあるので既定の検索バーは出さない
            await core.Find.StartAsync(options);
            _appliedTerm = term;
            _appliedCaseSensitive = caseSensitive;
        }
        catch
        {
            // Find API 非対応のランタイム等：塗られないだけ。適用済みは不明になるので次回やり直す。
            _appliedTerm = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
