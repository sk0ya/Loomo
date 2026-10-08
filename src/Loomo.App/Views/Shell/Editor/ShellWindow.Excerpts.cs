using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Services;

namespace sk0ya.Loomo.App.Views;

/// <summary>
/// 抜粋タブ（設計書 §35.3）の入口。参照・grep・診断などの一覧から、該当箇所の前後を1枚のタブに並べて開き、
/// その場で編集できるようにする。編集は元ファイルのタブへ即座に入る（ファイルの持ち主はタブのまま）。
/// 抜粋タブそのものは仮想文書なので、保存は「結び付いた元ファイルを保存」、タブの復元は未対応。
/// </summary>
public partial class ShellWindow
{
    /// <summary>抜粋タブ1枚と、それが属するワークスペースのタブ集合。ワークスペースごとにタブ集合
    /// （<see cref="_editorTabs"/> の中身）が入れ替わるので、別のワークスペースで開いた同名ファイルを
    /// この抜粋に結び付けないよう、いまのタブ集合に属するものだけを動かす。</summary>
    private sealed record ExcerptEntry(ExcerptTabController Controller, List<EditorTab> Owner);

    private readonly Dictionary<Guid, ExcerptEntry> _excerptTabs = new();
    private bool _excerptDocumentEventsHooked;

    /// <summary>いまのワークスペースの抜粋タブ。タブが無くなったもの（ワークスペースごと消えた等で
    /// <see cref="CloseEditorTab"/> を通らなかったもの）はここで片付ける。</summary>
    private List<(Guid Id, ExcerptTabController Controller)> LiveExcerptTabs()
    {
        foreach (var (id, entry) in _excerptTabs.ToList())
            if (!entry.Owner.Any(t => t.Id == id))
                DisposeExcerptTab(id);
        return _excerptTabs
            .Where(pair => ReferenceEquals(pair.Value.Owner, _editorTabs))
            .Select(pair => (pair.Key, pair.Value.Controller))
            .ToList();
    }

    /// <summary>一覧の各行（ファイル＋行）を前後2行ずつの抜粋にまとめ、抜粋タブで開く。</summary>
    private void OpenExcerptTab(string title, IReadOnlyList<ExcerptRequest> requests)
    {
        HookExcerptDocumentEvents();

        // 本文は、開いているタブがあればその本文（未保存の変更も含む）、無ければディスクから読む。
        // 開いているタブの本文から作るのは、すぐに結び付けて差分を運ぶため——ディスクから作ると
        // 未保存の変更の分だけ行がずれた抜粋に編集を入れてしまう。読めないファイルは抜粋から外す。
        var cache = new Dictionary<string, IReadOnlyList<string>?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? TryReadLines(string path)
        {
            if (cache.TryGetValue(path, out var lines)) return lines;
            return cache[path] = DiffWorkingDocuments.Find(path)?.Text.Split('\n') ?? ReadExcerptSourceFromDisk(path);
        }
        var existing = requests
            .Where(r => !string.IsNullOrEmpty(r.Path) && TryReadLines(r.Path) is not null)
            .ToList();
        if (existing.Count == 0)
        {
            ShowRefactorStatus("抜粋にできる箇所がありません（ファイルとして開けるものがありません）。");
            return;
        }
        IReadOnlyList<string> ReadLines(string path) => TryReadLines(path) ?? [""];

        var spans = ExcerptPlanner.Plan(existing, path => ReadLines(path).Count);
        var syntax = ExcerptSyntax(spans.Select(s => s.Path));
        var prefix = ExcerptCommentPrefix(spans.Select(s => s.Path));
        var document = new ExcerptDocument(spans, ReadLines,
            path => $"{prefix}── {_workspace.ToDisplayPath(path)}");

        var tab = CreateEditorTab();
        tab.VirtualTitle = title;
        _editorTabs.Add(tab);
        _vm.Tabs.AddEditorTab(tab.Id, title, false, false);
        ActivateEditorTab(tab.Id);
        tab.Control.OpenVirtualDocument(title, document.Text, syntax);
        // 元ファイルを探す・裏で開くのは Diff の右側と同じ入口（DiffWorkingDocuments）に任せる：
        // ワークスペース切替の途中は開かない・パスの正規化・タブが1つも無いときの選択までそちらが持っている。
        _excerptTabs[tab.Id] = new ExcerptEntry(new ExcerptTabController(
            document, tab.Control, DiffWorkingDocuments.Find, DiffWorkingDocuments.Open,
            ReadExcerptSourceFromDisk, ShowRefactorStatus), _editorTabs);
        UpdateEditorTab(tab);
        ShowRefactorStatus(
            $"抜粋 {spans.Count} か所（{document.Paths.Count()} ファイル）を開きました。見出し以外の行は、そのまま元のファイルへ入ります。");
    }

    /// <summary>ディスク上のファイルの全行（改行は "\n" に揃える）。読めなければ null（ロック中・消えた等）。</summary>
    private static IReadOnlyList<string>? ReadExcerptSourceFromDisk(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>元ファイルのタブが開かれた・閉じられたことを抜粋タブへ伝える。開いたときの写し直しは
    /// 後で行う——抜粋タブへの書き込みから裏のタブを開いた場合、まだ抜粋タブの差分通知の最中にいるため。</summary>
    private void HookExcerptDocumentEvents()
    {
        if (_excerptDocumentEventsHooked) return;
        _excerptDocumentEventsHooked = true;
        _editorDocumentEvents.Loaded += control => Dispatcher.BeginInvoke(() =>
        {
            foreach (var (_, excerpt) in LiveExcerptTabs())
                excerpt.OnSourceLoaded(control);
        });
        // 閉じたタブの持ち主はワークスペースを問わない（結び付きを外すだけなので全部へ伝える）。
        _editorDocumentEvents.Closed += control =>
        {
            foreach (var entry in _excerptTabs.Values.ToList())
                entry.Controller.OnSourceClosed(control);
        };
        // 元ファイルのタブ側で保存した：結び付いた元ファイルが全て保存済みになった抜粋タブは保存済みに揃える。
        _editorDocumentEvents.Saved += control =>
        {
            foreach (var (id, excerpt) in LiveExcerptTabs())
                if (excerpt.Sources.Contains(control))
                    SyncExcerptSavedState(id, excerpt);
        };
    }

    /// <summary>抜粋タブの未保存の印を、結び付いた元ファイルの状態に揃える（抜粋タブ自身は保存するものを持たない）。</summary>
    private void SyncExcerptSavedState(Guid id, ExcerptTabController excerpt)
    {
        if (excerpt.Sources.Any(c => c.IsModified)) return;
        excerpt.View.MarkSaved();
        if (_editorTabs.FirstOrDefault(t => t.Id == id) is { } tab)
            UpdateEditorTab(tab);
    }

    /// <summary>そのエディタを持つタブの表示（未保存の印）を更新する。裏のタブは保存しても印が残るため。</summary>
    private void UpdateEditorTabFor(VimEditorControl control)
    {
        if (_editorTabs.FirstOrDefault(t => t.IsRealized && ReferenceEquals(t.Control, control)) is { } tab)
            UpdateEditorTab(tab);
    }

    /// <summary>抜粋タブを閉じる前の後始末（<see cref="CloseEditorTab"/> から）。</summary>
    private void DisposeExcerptTab(Guid id)
    {
        if (_excerptTabs.Remove(id, out var entry))
            entry.Controller.Dispose();
    }

    /// <summary>抜粋タブでの Ctrl+S：結び付いた元ファイルのうち、未保存のものを保存する。</summary>
    private async Task<bool> TrySaveExcerptTabAsync(EditorTab tab)
    {
        if (!_excerptTabs.TryGetValue(tab.Id, out var entry)) return false;
        var excerpt = entry.Controller;
        var modified = excerpt.Sources.Where(c => c.IsModified && c.FilePath is { Length: > 0 }).ToList();
        foreach (var control in modified)
        {
            await _editor.SaveFileAsync(control);
            UpdateEditorTabFor(control);
        }
        SyncExcerptSavedState(tab.Id, excerpt);
        ShowRefactorStatus(modified.Count == 0
            ? "抜粋の元のファイルに未保存の変更はありません。"
            : $"抜粋の元のファイル {modified.Count} 件を保存しました。");
        return true;
    }

    /// <summary>右クリック：抜粋タブなら「元のファイルのこの行を開く」。</summary>
    private void AddExcerptMenuItems(ContextMenu menu, VimEditorControl? control)
    {
        if (control is null) return;
        var excerpt = _excerptTabs.Values.Select(e => e.Controller).FirstOrDefault(e => ReferenceEquals(e.View, control));
        if (excerpt is null) return;
        var info = excerpt.Document.Describe(control.Caret.Line);
        if (info.Excerpt >= excerpt.Document.Excerpts.Count) return;
        var target = excerpt.Document.Excerpts[info.Excerpt];
        var line = info.Kind == ExcerptLineKind.Content ? info.SourceLine : target.Source.Start.Line;
        var column = info.Kind == ExcerptLineKind.Content ? control.Caret.Column : 0;
        var item = new MenuItem { Header = "元のファイルのこの行を開く" };
        item.Click += (_, _) => _ = OpenPathInEditorAsync(target.Path, line + 1, column + 1);
        menu.Items.Add(item);
    }

    /// <summary>全部が同じ言語ならその色付けを使う。混ざっていれば色付けしない（見出しが別言語の構文で崩れるより良い）。</summary>
    private static string? ExcerptSyntax(IEnumerable<string> paths)
    {
        var languages = paths.Select(DiffEditorLanguage.For).Distinct().ToList();
        return languages.Count == 1 ? languages[0] : null;
    }

    /// <summary>見出し行をその言語のコメントに見せる接頭辞（色付けでコメントとして沈むように）。</summary>
    private static string ExcerptCommentPrefix(IEnumerable<string> paths)
    {
        var extensions = paths.Select(p => Path.GetExtension(p).ToLowerInvariant()).Distinct().ToList();
        if (extensions.Count != 1) return "";
        return extensions[0] switch
        {
            ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".java" or ".go" or ".rs" or
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".kt" or ".swift" or ".dart" or ".scala" or ".fs" => "// ",
            ".py" or ".ps1" or ".psm1" or ".sh" or ".bash" or ".rb" or ".yml" or ".yaml" or ".toml" or ".r" => "# ",
            ".sql" or ".lua" => "-- ",
            _ => "",
        };
    }
}
