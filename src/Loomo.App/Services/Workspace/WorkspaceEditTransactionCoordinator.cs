using System.Text;
using Editor.Controls;
using Editor.Core.Lsp;
using sk0ya.Loomo.App.Views;
using sk0ya.Loomo.CSharp.Refactoring;

namespace sk0ya.Loomo.App.Services;

/// <summary>WorkspaceEdit の成功・失敗・利用者キャンセルを区別する。</summary>
internal readonly record struct WorkspaceEditOutcome(string? Error, bool Cancelled)
{
    internal static WorkspaceEditOutcome Ok() => new(null, false);
    internal static WorkspaceEditOutcome Fail(string error) => new(error, false);
    internal static WorkspaceEditOutcome Cancel() => new(null, true);

    internal string? Describe(string what) =>
        Cancelled ? $"「{what}」は取り消しました。"
        : Error is { } error ? $"「{what}」を適用できませんでした: {error}"
        : null;
}

/// <summary>
/// 複数文書とファイル操作を一つのトランザクションとして適用する。プレビュー、エディタタブ検索、
/// ステータス表示だけを呼び出し側へ委ね、検証・snapshot・適用・rollback・undo/redoをまとめて扱う。
/// </summary>
internal sealed class WorkspaceEditTransactionCoordinator
{
    private const int MaxHistory = 50;
    private readonly List<WorkspaceEditHistoryEntry> _undo = [];
    private readonly List<WorkspaceEditHistoryEntry> _redo = [];

    public bool IsRestoring { get; private set; }

    public void OnEditorBufferChanged()
    {
        if (!IsRestoring)
            _redo.Clear();
    }

    public WorkspaceEditOutcome Apply(
        IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> changes,
        IReadOnlyDictionary<string, int?>? documentVersions,
        IReadOnlyList<LspFileOperation>? fileOperations,
        WorkspaceEditPreviewFile? currentPreview,
        IReadOnlyDictionary<string, string>? expectedTexts,
        IReadOnlyList<string> folders,
        IReadOnlyList<EditorTab> editorTabs,
        Func<VimEditorControl, string?, bool> editorPathMatches,
        Func<IReadOnlyList<WorkspaceEditPreviewFile>, IReadOnlyList<WorkspaceEditPreviewOperation>, bool> showPreview,
        bool requirePreview = true)
    {
        if (folders.Count == 0)
            return WorkspaceEditOutcome.Fail("ワークスペースが開かれていません。");

        Dictionary<string, LspFileSnapshot>? fileSnapshots = null;
        Dictionary<VimEditorControl, string>? editorSnapshots = null;
        var mutationStarted = false;
        try
        {
            var operations = fileOperations ?? [];
            VerifyExpectedTexts(expectedTexts, folders, editorTabs, editorPathMatches);
            ValidateFileOperations(operations, folders);
            var plans = new List<EditPlan>();
            foreach (var (uri, edits) in changes)
            {
                var path = LspWorkspaceEditPaths.ResolveInWorkspace(uri, folders);
                int? expectedVersion = null;
                documentVersions?.TryGetValue(uri, out expectedVersion);
                var open = editorTabs
                    .Where(tab => tab.IsRealized && editorPathMatches(tab.Control, path))
                    .Select(tab => tab.Control)
                    .ToList();
                if (open.Count > 0)
                {
                    var original = open[0].Text;
                    var updated = original;
                    foreach (var editor in open)
                    {
                        if (expectedVersion is not null && editor.LspDocument?.Version is { } actual
                            && actual != expectedVersion)
                            throw new InvalidOperationException(
                                $"{path}: 文書版が一致しません（要求 {expectedVersion} / 現在 {actual}）。");
                        var candidate = VimEditorControl.ApplyTextEdits(editor.Text, edits);
                        if (ReferenceEquals(editor, open[0]))
                            updated = candidate;
                    }
                    plans.Add(new(path, edits, expectedVersion, open, original, updated, null, null));
                    continue;
                }

                if (expectedVersion is not null)
                    throw new InvalidOperationException(
                        $"{path}: 文書版 {expectedVersion} を検証できません。ファイルを開いて再度実行してください。");
                var (originalText, encoding) = ReadEditSource(path, operations);
                var updatedText = VimEditorControl.ApplyTextEdits(originalText, edits);
                plans.Add(new(path, edits, null, [], originalText, updatedText, updatedText, encoding));
            }

            var previewFiles = plans
                .Where(plan => !string.Equals(plan.OriginalText, plan.UpdatedText, StringComparison.Ordinal))
                .Select(plan => new WorkspaceEditPreviewFile(plan.Path, plan.OriginalText, plan.UpdatedText))
                .ToList();
            if (currentPreview is not null &&
                !string.Equals(currentPreview.OriginalText, currentPreview.UpdatedText, StringComparison.Ordinal))
                previewFiles.Insert(0, currentPreview);
            var previewOperations = operations.Select(ToPreviewOperation).ToList();
            fileSnapshots = CaptureFileSnapshots(operations, plans, currentPreview);
            editorSnapshots = CaptureEditorSnapshots(plans, currentPreview, editorTabs, editorPathMatches);
            if (requirePreview && (previewFiles.Count > 0 || previewOperations.Count > 0) &&
                !showPreview(previewFiles, previewOperations))
                return WorkspaceEditOutcome.Cancel();

            // プレビュー中にユーザーや別プロセスが触った場合は、確認済みの差分を上書きしない。
            VerifyTransactionSnapshots(fileSnapshots, editorSnapshots);
            if (operations.Count > 0)
            {
                mutationStarted = true;
                ApplyFileOperations(operations, folders);
            }
            foreach (var plan in plans)
            {
                // 読み取り側を先に同期し、LSPへdidChangeを送るwriterは最後に一度だけ適用する。
                foreach (var editor in plan.Open.OrderBy(editor => editor.LspDocument?.IsWriter == true))
                {
                    mutationStarted = true;
                    if (!editor.TryApplyLspTextEdits(plan.Edits, expectedVersion: null, out var error))
                        throw new InvalidOperationException($"{plan.Path}: {error}");
                }
                if (plan.DiskText is not null)
                {
                    mutationStarted = true;
                    File.WriteAllText(plan.Path, plan.DiskText, plan.Encoding!);
                }
            }

            RecordHistory("LSP／Roslyn WorkspaceEdit", fileSnapshots,
                CaptureFileSnapshots(fileSnapshots.Keys),
                CaptureEditorTextSnapshots(editorSnapshots!),
                CaptureEditorTextSnapshots(editorSnapshots!, currentPreview, useCurrentText: true));
            return WorkspaceEditOutcome.Ok();
        }
        catch (Exception ex)
        {
            // 適用後のI/O失敗でも、既に動かしたEditor／ファイルを確認済みの状態へ戻す。
            try
            {
                if (mutationStarted && fileSnapshots is not null && editorSnapshots is not null)
                    RestoreTransactionSnapshots(fileSnapshots, editorSnapshots);
            }
            catch (Exception rollback)
            {
                return WorkspaceEditOutcome.Fail($"{ex.Message} 復元にも失敗しました: {rollback.Message}");
            }
            return WorkspaceEditOutcome.Fail(ex.Message);
        }
    }

    /// <summary>WorkspaceEdit undo/redo。状態が外部変更されていたら通常のエディタUndoへ譲る。</summary>
    public bool TryRestoreHistory(bool redo, string? activePath,
        IReadOnlyList<EditorTab> editorTabs,
        Func<VimEditorControl, string?, bool> editorPathMatches,
        Action<string> reportStatus)
    {
        var history = redo ? _redo : _undo;
        if (history.Count == 0 || activePath is null)
            return false;

        var entry = history[^1];
        if (!entry.AfterEditors.ContainsKey(Path.GetFullPath(activePath)))
            return false;
        try
        {
            if (!HistoryStateMatches(
                redo ? entry.BeforeFiles : entry.AfterFiles,
                redo ? entry.BeforeEditors : entry.AfterEditors,
                editorTabs, editorPathMatches))
                return false;
        }
        catch
        {
            return false;
        }

        try
        {
            IsRestoring = true;
            RestoreHistorySnapshots(
                redo ? entry.AfterFiles : entry.BeforeFiles,
                redo ? entry.AfterEditors : entry.BeforeEditors,
                editorTabs, editorPathMatches);
            history.RemoveAt(history.Count - 1);
            (redo ? _undo : _redo).Add(entry);
            reportStatus(redo
                ? $"{entry.Description} をやり直しました。"
                : $"{entry.Description} を元に戻しました。");
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                // Undo/Redo中にディスクがロックされた場合も、先に戻せたEditorを残さない。
                RestoreHistorySnapshots(
                    redo ? entry.BeforeFiles : entry.AfterFiles,
                    redo ? entry.BeforeEditors : entry.AfterEditors,
                    editorTabs, editorPathMatches);
            }
            catch (Exception rollback)
            {
                reportStatus($"WorkspaceEditの復元に失敗しました: {ex.Message} 復元にも失敗しました: {rollback.Message}");
                return true;
            }
            reportStatus($"WorkspaceEditの復元に失敗しました: {ex.Message}");
            return true;
        }
        finally
        {
            IsRestoring = false;
        }
    }

    private static void VerifyExpectedTexts(IReadOnlyDictionary<string, string>? expectedTexts,
        IReadOnlyList<string> folders, IReadOnlyList<EditorTab> editorTabs,
        Func<VimEditorControl, string?, bool> editorPathMatches)
    {
        var error = CSharpEditSnapshotValidator.Validate(expectedTexts, folders, path =>
        {
            var editor = editorTabs
                .Where(tab => tab.IsRealized && editorPathMatches(tab.Control, path))
                .Select(tab => tab.Control)
                .FirstOrDefault();
            if (editor is not null)
                return editor.Text;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });
        if (error is not null)
            throw new InvalidOperationException(error);
    }

    private static (string Text, Encoding Encoding) ReadEditSource(
        string path, IReadOnlyList<LspFileOperation> operations)
    {
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            return (reader.ReadToEnd(), reader.CurrentEncoding);
        }
        if (IsCreatedByOperation(path, operations))
            return ("", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (FindRenameSource(path, operations) is { } source && File.Exists(source))
        {
            using var reader = new StreamReader(source, detectEncodingFromByteOrderMarks: true);
            return (reader.ReadToEnd(), reader.CurrentEncoding);
        }
        throw new InvalidOperationException($"{path}: 編集対象のファイルが見つかりません。");
    }

    private static Dictionary<string, LspFileSnapshot> CaptureFileSnapshots(
        IReadOnlyList<LspFileOperation> operations, IReadOnlyList<EditPlan> plans,
        WorkspaceEditPreviewFile? currentPreview = null)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in operations)
        {
            if (LspUri.TryToLocalPath(operation.Uri) is { } path)
                paths.Add(Path.GetFullPath(path));
            if (operation.NewUri is not null && LspUri.TryToLocalPath(operation.NewUri) is { } newPath)
                paths.Add(Path.GetFullPath(newPath));
        }
        // 開いている文書もディスク内容を記録し、preview中の外部書き込みを上書きしない。
        foreach (var plan in plans)
            paths.Add(Path.GetFullPath(plan.Path));
        if (currentPreview is { Path.Length: > 0 })
            paths.Add(Path.GetFullPath(currentPreview.Path));

        var result = new Dictionary<string, LspFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
            result[path] = File.Exists(path)
                ? new LspFileSnapshot(true, File.ReadAllBytes(path))
                : new LspFileSnapshot(false, []);
        return result;
    }

    private static Dictionary<string, LspFileSnapshot> CaptureFileSnapshots(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, LspFileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in paths)
        {
            var path = Path.GetFullPath(rawPath);
            result[path] = File.Exists(path)
                ? new LspFileSnapshot(true, File.ReadAllBytes(path))
                : new LspFileSnapshot(false, []);
        }
        return result;
    }

    private static Dictionary<VimEditorControl, string> CaptureEditorSnapshots(
        IReadOnlyList<EditPlan> plans, WorkspaceEditPreviewFile? currentPreview,
        IReadOnlyList<EditorTab> editorTabs,
        Func<VimEditorControl, string?, bool> editorPathMatches)
    {
        var editors = plans.SelectMany(plan => plan.Open).ToHashSet();
        if (currentPreview is not null)
            foreach (var tab in editorTabs.Where(tab => tab.IsRealized &&
                editorPathMatches(tab.Control, currentPreview.Path)))
                editors.Add(tab.Control);
        return editors.ToDictionary(editor => editor, editor => editor.Text);
    }

    private static Dictionary<string, string> CaptureEditorTextSnapshots(
        IReadOnlyDictionary<VimEditorControl, string> editors,
        WorkspaceEditPreviewFile? currentPreview = null, bool useCurrentText = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (editor, text) in editors)
            if (editor.FilePath is { Length: > 0 } path)
                result[Path.GetFullPath(path)] = useCurrentText ? editor.Text : text;
        if (currentPreview is { Path.Length: > 0 })
            result[Path.GetFullPath(currentPreview.Path)] = currentPreview.UpdatedText;
        return result;
    }

    private static void VerifyTransactionSnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<VimEditorControl, string> editors)
    {
        foreach (var (path, expected) in files)
        {
            var actual = CaptureFileSnapshot(path);
            if (!SameFileSnapshot(expected, actual))
                throw new InvalidOperationException($"{path}: preview後に外部変更が検出されました。再度実行してください。");
        }
        foreach (var (editor, expected) in editors)
            if (!string.Equals(editor.Text, expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"{editor.FilePath}: preview後に編集中の内容が変更されました。再度実行してください。");
    }

    private static void RestoreTransactionSnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<VimEditorControl, string> editors)
    {
        foreach (var (editor, text) in editors)
            if (!string.Equals(editor.Text, text, StringComparison.Ordinal) &&
                !editor.TryRestoreWorkspaceText(text, out var error))
                throw new InvalidOperationException($"{editor.FilePath}: {error}");
        RestoreFileSnapshots(files);
    }

    private void RecordHistory(string description,
        IReadOnlyDictionary<string, LspFileSnapshot> beforeFiles,
        IReadOnlyDictionary<string, LspFileSnapshot> afterFiles,
        IReadOnlyDictionary<string, string> beforeEditors,
        IReadOnlyDictionary<string, string> afterEditors)
    {
        if (beforeFiles.Count == 0 && beforeEditors.Count == 0)
            return;
        _undo.Add(new WorkspaceEditHistoryEntry(description,
            CopyFiles(beforeFiles), CopyFiles(afterFiles), CopyTexts(beforeEditors), CopyTexts(afterEditors)));
        if (_undo.Count > MaxHistory)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private static Dictionary<string, LspFileSnapshot> CopyFiles(
        IReadOnlyDictionary<string, LspFileSnapshot> source)
        => new(source, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> CopyTexts(IReadOnlyDictionary<string, string> source)
        => new(source, StringComparer.OrdinalIgnoreCase);

    private static bool HistoryStateMatches(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<string, string> editors,
        IReadOnlyList<EditorTab> editorTabs,
        Func<VimEditorControl, string?, bool> editorPathMatches)
    {
        foreach (var (path, expected) in files)
            if (!SameFileSnapshot(expected, CaptureFileSnapshot(path)))
                return false;
        foreach (var (path, expected) in editors)
        {
            var open = editorTabs.Where(tab => tab.IsRealized && editorPathMatches(tab.Control, path))
                .Select(tab => tab.Control).ToArray();
            if (open.Length == 0 || open.Any(editor =>
                !string.Equals(editor.Text, expected, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static void RestoreHistorySnapshots(
        IReadOnlyDictionary<string, LspFileSnapshot> files,
        IReadOnlyDictionary<string, string> editors,
        IReadOnlyList<EditorTab> editorTabs,
        Func<VimEditorControl, string?, bool> editorPathMatches)
    {
        foreach (var (path, text) in editors)
        {
            var open = editorTabs.Where(tab => tab.IsRealized && editorPathMatches(tab.Control, path))
                .Select(tab => tab.Control).ToArray();
            if (open.Length == 0)
                throw new InvalidOperationException($"{path}: 対応するエディタタブが閉じられています。");
            foreach (var editor in open)
                if (!string.Equals(editor.Text, text, StringComparison.Ordinal) &&
                    !editor.TryRestoreWorkspaceText(text, out var error))
                    throw new InvalidOperationException($"{path}: {error}");
        }
        RestoreFileSnapshots(files);
    }

    private static void RestoreFileSnapshots(IReadOnlyDictionary<string, LspFileSnapshot> files)
    {
        foreach (var (path, snapshot) in files)
        {
            if (snapshot.Exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, snapshot.Content);
            }
            else if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static LspFileSnapshot CaptureFileSnapshot(string path)
        => File.Exists(path)
            ? new LspFileSnapshot(true, File.ReadAllBytes(path))
            : new LspFileSnapshot(false, []);

    private static bool SameFileSnapshot(LspFileSnapshot left, LspFileSnapshot right)
        => left.Exists == right.Exists && left.Content.AsSpan().SequenceEqual(right.Content);

    private static void ValidateFileOperations(
        IReadOnlyList<LspFileOperation> operations, IReadOnlyList<string> folders)
    {
        foreach (var operation in operations)
        {
            var path = LspWorkspaceEditPaths.ResolveInWorkspace(operation.Uri, folders);
            switch (operation.Kind)
            {
                case LspFileOperationKind.Create:
                    if (File.Exists(path) && !operation.IgnoreIfExists && !operation.Overwrite)
                        throw new InvalidOperationException($"{path}: すでに存在します。");
                    break;
                case LspFileOperationKind.Rename:
                    if (!File.Exists(path))
                        throw new InvalidOperationException($"{path}: 名前変更元のファイルが見つかりません。");
                    var destination = LspWorkspaceEditPaths.ResolveInWorkspace(
                        operation.NewUri ?? throw new InvalidOperationException("改名先が指定されていません。"), folders);
                    if (File.Exists(destination) && !operation.IgnoreIfExists && !operation.Overwrite)
                        throw new InvalidOperationException($"{destination}: すでに存在します。");
                    break;
                case LspFileOperationKind.Delete:
                    if (!File.Exists(path) && !operation.IgnoreIfNotExists)
                        throw new InvalidOperationException($"{path}: 削除対象のファイルが見つかりません。");
                    break;
            }
        }
    }

    private static bool IsCreatedByOperation(string path, IReadOnlyList<LspFileOperation> operations)
        => operations.Any(operation => operation.Kind == LspFileOperationKind.Create &&
            string.Equals(LspUri.TryToLocalPath(operation.Uri), path, StringComparison.OrdinalIgnoreCase));

    private static string? FindRenameSource(string path, IReadOnlyList<LspFileOperation> operations)
    {
        var operation = operations.FirstOrDefault(candidate =>
            candidate.Kind == LspFileOperationKind.Rename &&
            string.Equals(LspUri.TryToLocalPath(candidate.NewUri ?? ""), path, StringComparison.OrdinalIgnoreCase));
        return operation is null ? null : LspUri.TryToLocalPath(operation.Uri);
    }

    private static WorkspaceEditPreviewOperation ToPreviewOperation(LspFileOperation operation)
        => new(operation.Kind switch
        {
            LspFileOperationKind.Create => "create",
            LspFileOperationKind.Rename => "rename",
            LspFileOperationKind.Delete => "delete",
            _ => "file operation",
        }, LspUri.TryToLocalPath(operation.Uri) ?? operation.Uri,
            operation.NewUri is null ? null : LspUri.TryToLocalPath(operation.NewUri) ?? operation.NewUri);

    private static void ApplyFileOperations(
        IReadOnlyList<LspFileOperation> operations, IReadOnlyList<string> folders)
    {
        foreach (var operation in operations)
        {
            var path = LspWorkspaceEditPaths.ResolveInWorkspace(operation.Uri, folders);
            switch (operation.Kind)
            {
                case LspFileOperationKind.Create:
                    if (File.Exists(path))
                    {
                        if (operation.IgnoreIfExists) break;
                        if (!operation.Overwrite)
                            throw new InvalidOperationException($"{path}: すでに存在します。");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "");
                    break;
                case LspFileOperationKind.Rename:
                    var destination = LspWorkspaceEditPaths.ResolveInWorkspace(
                        operation.NewUri ?? throw new InvalidOperationException("改名先が指定されていません。"), folders);
                    if (File.Exists(destination) && !operation.Overwrite)
                    {
                        if (operation.IgnoreIfExists) break;
                        throw new InvalidOperationException($"{destination}: すでに存在します。");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(path, destination, operation.Overwrite);
                    break;
                case LspFileOperationKind.Delete:
                    if (!File.Exists(path))
                    {
                        if (operation.IgnoreIfNotExists) break;
                        throw new InvalidOperationException($"{path}: 存在しません。");
                    }
                    File.Delete(path);
                    break;
            }
        }
    }

    private sealed record EditPlan(string Path, IReadOnlyList<LspTextEdit> Edits, int? Version,
        List<VimEditorControl> Open, string OriginalText, string UpdatedText,
        string? DiskText, Encoding? Encoding);

    private sealed record LspFileSnapshot(bool Exists, byte[] Content);

    private sealed record WorkspaceEditHistoryEntry(string Description,
        IReadOnlyDictionary<string, LspFileSnapshot> BeforeFiles,
        IReadOnlyDictionary<string, LspFileSnapshot> AfterFiles,
        IReadOnlyDictionary<string, string> BeforeEditors,
        IReadOnlyDictionary<string, string> AfterEditors);
}
