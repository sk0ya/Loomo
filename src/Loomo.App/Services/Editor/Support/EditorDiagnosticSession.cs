using Editor.Core.Lsp;

namespace sk0ya.Loomo.App.Services;

internal enum EditorDiagnosticOrigin
{
    LanguageServer,
    Compiler,
    StyleCop,
}

internal sealed record EditorDiagnosticEntry(
    EditorDiagnosticOrigin Origin,
    LspDiagnostic Diagnostic);

internal sealed record EditorDiagnosticSnapshot(
    string FilePath,
    int Version,
    long SnapshotId,
    string Text,
    IReadOnlyList<EditorDiagnosticEntry> Entries,
    bool HasCurrentResult,
    int? LanguageServerVersion)
{
    public IReadOnlyList<LspDiagnostic> Diagnostics => Entries.Select(entry => entry.Diagnostic).ToArray();
}

internal sealed record EditorDiagnosticSessionRelease(
    string FilePath,
    IReadOnlyList<LspDiagnostic> RetainedLanguageServerDiagnostics);

/// <summary>
/// エディターバッファごとの診断正本。解析元ごとの結果を本文版に結び付け、古い解析結果を拒否する。
/// 同じ本文を再解析する間は表示を保ち、本文が変わったときは旧範囲を消す。Quick Fixには現行版の確定スナップショットだけを渡す。
///
/// <para><b>表示は待たせない。</b>解析元は速さが桁で違う（LSPのpushは数百ms、StyleCopは
/// Roslyn Compilationで秒単位）。全員が揃うまで表示を止めると<b>一番遅い解析元が一番速い解析元を
/// 人質に取る</b>——開いた直後にLSPがエラーを返していても、StyleCopが終わるまで波線もProblemsも
/// 空、という壊れ方をする。そこで <see cref="Presentation"/> は届いた分だけ進め、揃ったかどうかは
/// <see cref="EditorDiagnosticSnapshot.HasCurrentResult"/> が言う。<b>行動</b>（Quick Fix）だけが
/// <see cref="TryGetCurrent"/>＝全員分が揃った現行版を要求する。</para>
/// </summary>
internal sealed class EditorDiagnosticSession
{
    /// <summary>スナップショット識別子はプロセス全体で単調増加させる。版番号（<see cref="Version"/>）は
    /// バッファごとに進むので、<b>同じファイルを分割・切り離しで2枚開くと比較できない</b>——Problems は
    /// ファイルパスで束ねているため、版番号で新旧を決めると片方の更新がもう片方に永久に弾かれる。</summary>
    private static long _snapshotSequence;

    private readonly Dictionary<EditorDiagnosticOrigin, IReadOnlyList<LspDiagnostic>> _byOrigin = [];

    /// <summary>同じ本文を再解析している間だけ使う、前回の解析元ごとの結果。まだ届いていない解析元の
    /// 表示をここから埋めるので、再解析のたびに波線が一度消えてから戻る、というちらつきが起きない。</summary>
    private readonly Dictionary<EditorDiagnosticOrigin, IReadOnlyList<LspDiagnostic>> _carriedOver = [];

    private readonly HashSet<EditorDiagnosticOrigin> _expectedOrigins = [];
    private string _filePath = "";
    private string _text = "";
    private int _version;
    private long _snapshotId;
    private int? _languageServerVersion;
    private bool _hasCurrentResult;
    private EditorDiagnosticSnapshot? _presentation;

    public int Begin(
        string filePath,
        string text,
        IEnumerable<EditorDiagnosticOrigin>? expectedOrigins = null)
    {
        var fullPath = Path.GetFullPath(filePath);
        var nextText = text ?? string.Empty;
        // 同じ本文の再解析なら前回の結果を解析元ごとに引き継ぐ。本文が変わったなら捨てる——
        // ずれた位置の波線ほど嘘に見えるものはない。
        var sameBuffer = _version > 0 &&
            string.Equals(_filePath, fullPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_text, nextText, StringComparison.Ordinal);
        _carriedOver.Clear();
        if (sameBuffer)
            foreach (var (origin, diagnostics) in _byOrigin)
                _carriedOver[origin] = diagnostics;

        _filePath = fullPath;
        _text = nextText;
        _version++;
        _languageServerVersion = sameBuffer ? _languageServerVersion : null;
        _hasCurrentResult = false;
        _byOrigin.Clear();
        _expectedOrigins.Clear();
        if (expectedOrigins is not null)
            _expectedOrigins.UnionWith(expectedOrigins);
        if (_expectedOrigins.Count == 0)
            CommitCurrent();
        else
            UpdatePresentation();
        return _version;
    }

    public int Version => _version;
    public long SnapshotId => _snapshotId;
    public string FilePath => _filePath;

    public bool Publish(
        int version,
        EditorDiagnosticOrigin origin,
        IReadOnlyList<LspDiagnostic> diagnostics,
        int? languageServerVersion = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (version != _version) return false;

        _byOrigin[origin] = Array.AsReadOnly(diagnostics.ToArray());
        if (origin == EditorDiagnosticOrigin.LanguageServer)
            _languageServerVersion = languageServerVersion;
        CommitIfReady();
        return true;
    }

    public bool Skip(int version, EditorDiagnosticOrigin origin)
    {
        if (version != _version) return false;
        _expectedOrigins.Remove(origin);
        _byOrigin.Remove(origin);
        _carriedOver.Remove(origin);
        CommitIfReady();
        return true;
    }

    public EditorDiagnosticSnapshot? Presentation => _presentation;

    public bool TryGetCurrent(string filePath, string text, out EditorDiagnosticSnapshot snapshot)
    {
        if (_hasCurrentResult && string.Equals(_filePath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_text, text, StringComparison.Ordinal))
        {
            snapshot = CreateSnapshot();
            return true;
        }

        snapshot = null!;
        return false;
    }

    public void Clear()
    {
        _filePath = "";
        _text = "";
        _version = 0;
        _snapshotId = 0;
        _languageServerVersion = null;
        _hasCurrentResult = false;
        _byOrigin.Clear();
        _carriedOver.Clear();
        _expectedOrigins.Clear();
        _presentation = null;
    }

    /// <summary>エディタタブを閉じるときに、別タブへ戻すLSP診断だけを退避して状態を解放する。</summary>
    public EditorDiagnosticSessionRelease Release()
    {
        var retained = _presentation?.Entries
            .Where(entry => entry.Origin == EditorDiagnosticOrigin.LanguageServer)
            .Select(entry => entry.Diagnostic)
            .ToArray() ?? [];
        var release = new EditorDiagnosticSessionRelease(_filePath, retained);
        Clear();
        return release;
    }

    private void CommitIfReady()
    {
        if (_expectedOrigins.All(_byOrigin.ContainsKey))
            CommitCurrent();
        else
            UpdatePresentation();
    }

    private void CommitCurrent()
    {
        _hasCurrentResult = true;
        UpdatePresentation();
    }

    private void UpdatePresentation()
    {
        _snapshotId = Interlocked.Increment(ref _snapshotSequence);
        _presentation = CreateSnapshot();
    }

    private EditorDiagnosticSnapshot CreateSnapshot()
    {
        var entries = new List<EditorDiagnosticEntry>();
        foreach (var origin in new[]
                 {
                     EditorDiagnosticOrigin.LanguageServer,
                     EditorDiagnosticOrigin.Compiler,
                     EditorDiagnosticOrigin.StyleCop,
                 })
        {
            if (!TryGetDiagnostics(origin, out var diagnostics)) continue;
            foreach (var diagnostic in diagnostics)
            {
                if (entries.Any(existing => IsSame(existing.Diagnostic, diagnostic))) continue;
                entries.Add(new(origin, diagnostic));
            }
        }

        return new(_filePath, _version, _snapshotId, _text, Array.AsReadOnly(entries.ToArray()),
            _hasCurrentResult, _languageServerVersion);
    }

    /// <summary>この版の結果。まだ届いていない解析元は、同じ本文の前回結果で埋める（届いた時点で
    /// 置き換わる）。<see cref="Skip"/> で降りた解析元は復活させない。</summary>
    private bool TryGetDiagnostics(
        EditorDiagnosticOrigin origin, out IReadOnlyList<LspDiagnostic> diagnostics)
    {
        if (_byOrigin.TryGetValue(origin, out var published))
        {
            diagnostics = published;
            return true;
        }

        if (_expectedOrigins.Contains(origin) && _carriedOver.TryGetValue(origin, out var carried))
        {
            diagnostics = carried;
            return true;
        }

        diagnostics = [];
        return false;
    }

    private static bool IsSame(LspDiagnostic left, LspDiagnostic right)
        => !string.IsNullOrWhiteSpace(left.Code) &&
           string.Equals(left.Code, right.Code, StringComparison.OrdinalIgnoreCase) &&
           left.Range.Start == right.Range.Start && left.Range.End == right.Range.End;
}
