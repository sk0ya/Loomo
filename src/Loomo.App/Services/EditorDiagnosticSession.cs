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

/// <summary>
/// エディターバッファごとの診断正本。解析元ごとの結果を本文版に結び付け、古い解析結果を拒否する。
/// 同じ本文を再解析する間は表示を保ち、本文が変わったときは旧範囲を消す。Quick Fixには現行版の確定スナップショットだけを渡す。
/// </summary>
internal sealed class EditorDiagnosticSession
{
    private readonly Dictionary<EditorDiagnosticOrigin, IReadOnlyList<LspDiagnostic>> _byOrigin = [];
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
        var previousPresentation = _presentation;
        var canKeepPresentation = previousPresentation is not null &&
            string.Equals(previousPresentation.FilePath, fullPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(previousPresentation.Text, nextText, StringComparison.Ordinal);
        _filePath = fullPath;
        _text = nextText;
        _version++;
        _languageServerVersion = null;
        _hasCurrentResult = false;
        _byOrigin.Clear();
        _expectedOrigins.Clear();
        if (expectedOrigins is not null)
            _expectedOrigins.UnionWith(expectedOrigins);
        if (_expectedOrigins.Count == 0)
            CommitCurrent();
        else
        {
            // 同じ本文の再解析なら範囲が有効な表示だけを維持する。本文が変わった場合は古い範囲を
            // 新しい本文へ描画しないよう空の更新中スナップショットへ切り替える。
            _snapshotId++;
            _presentation = canKeepPresentation
                ? previousPresentation! with
                {
                    Version = _version,
                    SnapshotId = _snapshotId,
                    HasCurrentResult = false,
                }
                : CreateSnapshot();
        }
        return _version;
    }

    public int Version => _version;
    public long SnapshotId => _snapshotId;
    public string FilePath => _filePath;

    public int Ensure(string filePath, string text)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (_version == 0 || !string.Equals(_filePath, fullPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_text, text, StringComparison.Ordinal))
            return Begin(fullPath, text);
        return _version;
    }

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
        _expectedOrigins.Clear();
        _presentation = null;
    }

    private void CommitIfReady()
    {
        if (_expectedOrigins.All(_byOrigin.ContainsKey))
            CommitCurrent();
    }

    private void CommitCurrent()
    {
        _hasCurrentResult = true;
        _snapshotId++;
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
            if (!_byOrigin.TryGetValue(origin, out var diagnostics)) continue;
            foreach (var diagnostic in diagnostics)
            {
                if (entries.Any(existing => IsSame(existing.Diagnostic, diagnostic))) continue;
                entries.Add(new(origin, diagnostic));
            }
        }

        return new(_filePath, _version, _snapshotId, _text, Array.AsReadOnly(entries.ToArray()),
            _hasCurrentResult, _languageServerVersion);
    }

    private static bool IsSame(LspDiagnostic left, LspDiagnostic right)
        => !string.IsNullOrWhiteSpace(left.Code) &&
           string.Equals(left.Code, right.Code, StringComparison.OrdinalIgnoreCase) &&
           left.Range.Start == right.Range.Start && left.Range.End == right.Range.End;
}
