namespace sk0ya.Loomo.App.Services;

/// <summary>エクスプローラー（フォルダーツリー／ファイル一覧ペイン）で行われたファイル操作の種類。</summary>
public enum FileOperationKind
{
    /// <summary>新規ファイル／フォルダーの作成。</summary>
    Create,
    /// <summary>名前の変更（同じ親フォルダー内）。</summary>
    Rename,
    /// <summary>移動（切り取り＋貼り付け・ドラッグ＆ドロップ）。</summary>
    Move,
    /// <summary>コピー（コピー＋貼り付け・複製・外部からのドロップ）。</summary>
    Copy,
    /// <summary>削除（ゴミ箱へ移動）。</summary>
    Delete,
    /// <summary>選択項目を ZIP アーカイブとして作成。</summary>
    Zip,
}

/// <summary>記録された 1 項目ぶんの操作。<see cref="Source"/>／<see cref="Target"/> の意味は種類ごと：
/// 作成・削除は片側だけ（作成＝Target に作った／削除＝Source を捨てた）、名前の変更・移動・コピーは
/// Source から Target へ。パスはすべて確定後のフルパス（一意化「 - コピー」適用後）。</summary>
public sealed record FileOperation(FileOperationKind Kind, string Source, string Target, bool IsDirectory, string? ReplacedPath = null,
    IReadOnlyList<string>? Sources = null)
{
    public static FileOperation Created(string path, bool isDirectory) => new(FileOperationKind.Create, "", path, isDirectory);
    public static FileOperation Renamed(string oldPath, string newPath, bool isDirectory) => new(FileOperationKind.Rename, oldPath, newPath, isDirectory);
    public static FileOperation Moved(string source, string destination, bool isDirectory, string? replacedPath = null) => new(FileOperationKind.Move, source, destination, isDirectory, replacedPath);
    public static FileOperation Copied(string source, string destination, bool isDirectory, string? replacedPath = null) => new(FileOperationKind.Copy, source, destination, isDirectory, replacedPath);
    public static FileOperation Deleted(string path, bool isDirectory) => new(FileOperationKind.Delete, path, "", isDirectory);
    public static FileOperation Compressed(IReadOnlyList<string> sources, string destination)
        => new(FileOperationKind.Zip, sources.Count > 0 ? sources[0] : "", destination, false, Sources: sources);

    /// <summary>ツリー・タブが追随する対象パス（表示名の元にもする）。</summary>
    internal string PrimaryPath => Kind is FileOperationKind.Delete ? Source : Target;
}

/// <summary>Undo／Redo の結果、UI が追随すべき変化を 1 項目ぶん表したもの。
/// 移動（<see cref="MovedFrom"/>→<see cref="MovedTo"/>）は開いているタブのパス追従、
/// <see cref="Removed"/> はタブを閉じる、<see cref="Revealed"/> はツリーで選択して見せる。</summary>
public sealed record FileOperationEffect(string? MovedFrom, string? MovedTo, string? Removed, string? Revealed, bool IsDirectory);

/// <summary>Undo／Redo 1 回ぶんの結果。<see cref="Description"/> はトースト・メニューに出す説明。</summary>
public sealed record FileOperationResult(string Description, IReadOnlyList<FileOperationEffect> Effects)
{
    /// <summary>操作後にツリーで見せるパス（無ければ null）。</summary>
    public string? RevealPath => Effects.LastOrDefault(e => e.Revealed is not null)?.Revealed
        ?? Effects.LastOrDefault(e => e.MovedTo is not null)?.MovedTo;
}

/// <summary>エクスプローラーのファイル操作（作成・名前の変更・移動・コピー・削除）の Undo／Redo 履歴。
///
/// <para><b>記録は <see cref="FolderTreeCommandHandler"/> が、逆操作はここが行う。</b>
/// 前向きの操作には検証（名前の妥当性・ワークスペース配下への限定・同名衝突時の「 - コピー」一意化）が
/// 要るが、逆操作は「さっきまであった状態へ戻す」だけなので、パスをそのまま復元する。とくに
/// ワークスペース配下への限定を逆操作へ持ち込んではいけない——外から持ち込んだファイルを移動で
/// 受け入れた直後の Undo（＝ワークスペース外の元の場所へ戻す）が拒否されてしまう。</para>
///
/// <para>ツリーとファイル一覧ペインは同じ履歴（DI シングルトン）を共有する。部屋の中のファイル操作は
/// どのペインから行っても 1 本の履歴、という意味づけ。永続化はしない（アプリを閉じたら消える）。</para></summary>
public sealed class FileOperationHistory
{
    /// <summary>保持する手数の上限。古いものから捨てる。</summary>
    private const int MaxDepth = 50;

    private readonly List<FileOperationStep> _undo = [];
    private readonly List<FileOperationStep> _redo = [];
    private List<FileOperation>? _batch;
    private int _batchDepth;

    /// <summary>履歴の内容が変わった（記録・Undo・Redo・クリア）。メニューの出し分けに使う。</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>次に元に戻す操作の説明（例「削除 3件」「名前の変更「a.txt」」）。無ければ null。</summary>
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    /// <summary>次にやり直す操作の説明。無ければ null。</summary>
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>1 回の Undo でまとめて戻したい一連の操作（複数選択の削除・複数ファイルの貼り付け等）を
    /// くくる。<c>using</c> を抜けたところで 1 手として記録する（1 件も成功しなければ何も残さない）。</summary>
    public IDisposable BeginBatch()
    {
        if (_batchDepth++ == 0)
            _batch = [];
        return new BatchScope(this);
    }

    /// <summary>成功したファイル操作を記録する（<see cref="FolderTreeCommandHandler"/> から）。
    /// Redo 履歴は新しい操作で捨てる（分岐は持たない）。</summary>
    public void Record(FileOperation operation)
    {
        if (_batch is not null)
        {
            _batch.Add(operation);
            return;
        }
        Push(new FileOperationStep([operation]));
    }

    /// <summary>直近の操作を元に戻す。戻せないとき（対象が既に無い・同名の項目がある・ゴミ箱に無い）は
    /// <see cref="InvalidOperationException"/>。</summary>
    public FileOperationResult Undo() => Step(undo: true);

    /// <summary>元に戻した操作をやり直す。</summary>
    public FileOperationResult Redo() => Step(undo: false);

    /// <summary>ZIP の再生成を UI スレッドで同期実行しないための非同期版。</summary>
    public Task<FileOperationResult> RedoAsync(CancellationToken cancellationToken = default)
        => StepAsync(undo: false, cancellationToken);

    /// <summary>履歴を捨てる。</summary>
    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0)
            return;
        foreach (var step in _undo.Concat(_redo))
            CleanupBackups(step);
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // 1 手ぶんを適用して、成功したときだけ履歴を反対側へ移す。
    //
    // 履歴から先に降ろしてはいけない——検証で弾かれるのは日常的（戻す先に別のファイルが置かれている等）で、
    // そこで一手を失うと「片付けてからもう一度 Ctrl+Z」ができなくなるうえ、次の Ctrl+Z がひとつ前の
    // 無関係な操作に効いてしまう。だから覗くだけにして、通ってから降ろす。
    private FileOperationResult Step(bool undo)
        => StepAsync(undo, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<FileOperationResult> StepAsync(bool undo, CancellationToken cancellationToken)
    {
        var from = undo ? _undo : _redo;
        var to = undo ? _redo : _undo;
        if (from.Count == 0)
            throw new InvalidOperationException(undo ? "元に戻せる操作がありません。" : "やり直せる操作がありません。");

        var step = from[^1];
        // 戻すときは逆順（例：a→b と b→c を1手にまとめた場合、c→b、b→a の順でないと衝突する）。
        var operations = Order(step, undo);

        // 検証は全件まとめて先に。1 件でも通らなければ 1 件も動かさず、履歴もそのまま残す。
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Validate(operation, undo);
        }

        var effects = new List<FileOperationEffect>(operations.Count);
        try
        {
            foreach (var operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                effects.Add(undo
                    ? UndoOne(operation)
                    : await RedoOneAsync(operation, cancellationToken));
            }
        }
        catch
        {
            // 検証を通ったあとの I/O 失敗（ロック・権限）。ここで履歴とディスクが食い違うと、次の一手が
            // まったく別の場所を壊すので、動かせた分だけを反対側へ移し、残りは元の側に残す。
            SplitStep(from, to, operations, effects.Count, undo);
            throw;
        }

        from.RemoveAt(from.Count - 1);
        to.Add(step);
        Changed?.Invoke(this, EventArgs.Empty);
        return new FileOperationResult(step.Description, effects);
    }

    /// <summary>適用順（戻すときは記録の逆順）。</summary>
    private static List<FileOperation> Order(FileOperationStep step, bool undo)
        => undo ? step.Operations.Reverse().ToList() : step.Operations.ToList();

    // 途中で失敗した一手を「済んだ分」と「残った分」に割り、それぞれの側へ置き直す。
    // 記録の並びは常に前向き順なので、戻す向きで割ったものはここで戻して積む。
    private void SplitStep(List<FileOperationStep> from, List<FileOperationStep> to,
        List<FileOperation> operations, int appliedCount, bool undo)
    {
        if (appliedCount == 0)
            return;   // 1 件も動いていない＝履歴はそのままでよい。

        var applied = operations.Take(appliedCount).ToList();
        var remaining = operations.Skip(appliedCount).ToList();
        from.RemoveAt(from.Count - 1);
        if (remaining.Count > 0)
            from.Add(new FileOperationStep(undo ? Enumerable.Reverse(remaining).ToList() : remaining));
        to.Add(new FileOperationStep(undo ? Enumerable.Reverse(applied).ToList() : applied));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Validate(FileOperation operation, bool undo)
    {
        var name = Path.GetFileName(operation.PrimaryPath);
        switch (operation.Kind, undo)
        {
            // 作成／コピーの取り消しと、削除のやり直し＝作った（残った）ものを捨てる。
            case (FileOperationKind.Create, true):
            case (FileOperationKind.Copy, true):
                RequireExists(operation.Target, operation.IsDirectory, name);
                RequireBackupIfNeeded(operation);
                break;
            case (FileOperationKind.Delete, false):
                RequireExists(operation.Source, operation.IsDirectory, name);
                break;

            // 名前の変更・移動は、行き先が空いていて元が在ることを両方向で確かめる。
            case (FileOperationKind.Rename, true):
            case (FileOperationKind.Move, true):
                RequireExists(operation.Target, operation.IsDirectory, name);
                RequireVacant(operation.Source, operation.Target);
                RequireBackupIfNeeded(operation);
                break;
            case (FileOperationKind.Rename, false):
            case (FileOperationKind.Move, false):
                RequireExists(operation.Source, operation.IsDirectory, name);
                if (operation.ReplacedPath is null)
                    RequireVacant(operation.Target, operation.Source);
                else
                {
                    RequireEntryExists(operation.Target, name);
                    RequireVacant(operation.ReplacedPath);
                }
                break;

            case (FileOperationKind.Create, false):
                RequireVacant(operation.Target);
                break;
            case (FileOperationKind.Copy, false):
                RequireExists(operation.Source, operation.IsDirectory, Path.GetFileName(operation.Source));
                if (operation.ReplacedPath is null)
                    RequireVacant(operation.Target);
                else
                {
                    RequireEntryExists(operation.Target, name);
                    RequireVacant(operation.ReplacedPath);
                }
                // Redo of an overwrite reuses the same backup slot after the current target is moved there.
                break;

            // 削除の取り消し（ゴミ箱からの復元）は行き先が空いていることだけ。実体の有無は
            // RecycleBin.TryRestore が見る（ゴミ箱を空にされていれば、そこで理由付きで失敗する）。
            case (FileOperationKind.Delete, true):
                RequireVacant(operation.Source);
                break;

            case (FileOperationKind.Zip, true):
                RequireExists(operation.Target, false, name);
                break;
            case (FileOperationKind.Zip, false):
                if (operation.Sources is null || operation.Sources.Count == 0)
                    throw new InvalidOperationException("ZIP の元項目がありません。");
                foreach (var source in operation.Sources)
                    RequireEntryExists(source, Path.GetFileName(source));
                RequireVacant(operation.Target);
                break;
        }
    }

    private static void RequireExists(string path, bool isDirectory, string name)
    {
        if (!(isDirectory ? Directory.Exists(path) : File.Exists(path)))
            throw new InvalidOperationException($"「{name}」が見つかりません（別の場所へ移動・削除された可能性があります）。");
    }

    private static void RequireBackupIfNeeded(FileOperation operation)
    {
        if (operation.ReplacedPath is not null
            && !File.Exists(operation.ReplacedPath)
            && !Directory.Exists(operation.ReplacedPath))
            throw new InvalidOperationException("上書き前の項目が見つからないため戻せません。");
    }

    private static void RequireEntryExists(string path, string name)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new InvalidOperationException($"「{name}」が見つかりません（別の場所へ移動・削除された可能性があります）。");
    }

    /// <summary>行き先が空いていることを確かめる。<paramref name="movedFrom"/> は移動元で、大文字小文字だけを
    /// 変える名前の変更（a.txt → A.txt）では「行き先」に居るのが移動元自身なので、そのときは空きとみなす。</summary>
    private static void RequireVacant(string path, string? movedFrom = null)
    {
        if (movedFrom is not null && string.Equals(path, movedFrom, StringComparison.OrdinalIgnoreCase))
            return;
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidOperationException($"「{Path.GetFileName(path)}」と同じ名前の項目が既にあるため戻せません。");
    }

    private static FileOperationEffect UndoOne(FileOperation operation) => operation.Kind switch
    {
        // 作った／コピーしたものはゴミ箱へ（消さずに捨てる＝取り消しの取り消しがきかない状況でも救える）。
        FileOperationKind.Create => Discard(operation.Target, operation.IsDirectory),
        FileOperationKind.Copy => UndoCopy(operation),
        FileOperationKind.Rename => MoveEntry(operation.Target, operation.Source, operation.IsDirectory),
        FileOperationKind.Move => UndoMove(operation),
        FileOperationKind.Delete => Restore(operation.Source, operation.IsDirectory),
        FileOperationKind.Zip => Discard(operation.Target, false),
        _ => throw new InvalidOperationException("不明な操作です。"),
    };

    private static FileOperationEffect RedoOne(FileOperation operation) => operation.Kind switch
    {
        FileOperationKind.Create => Recreate(operation.Target, operation.IsDirectory),
        FileOperationKind.Copy => RedoCopy(operation),
        FileOperationKind.Rename => MoveEntry(operation.Source, operation.Target, operation.IsDirectory),
        FileOperationKind.Move => RedoMove(operation),
        FileOperationKind.Delete => Discard(operation.Source, operation.IsDirectory),
        FileOperationKind.Zip => ZipEntry(operation),
        _ => throw new InvalidOperationException("不明な操作です。"),
    };

    private static Task<FileOperationEffect> RedoOneAsync(
        FileOperation operation,
        CancellationToken cancellationToken)
        => operation.Kind == FileOperationKind.Zip
            ? RedoZipAsync(operation, cancellationToken)
            : Task.FromResult(RedoOne(operation));

    private static async Task<FileOperationEffect> RedoZipAsync(
        FileOperation operation,
        CancellationToken cancellationToken)
    {
        await FolderTreeCommandHandler.CreateZipFileAsync(
            operation.Sources!, operation.Target, cancellationToken);
        return new FileOperationEffect(null, null, null, operation.Target, false);
    }

    private static FileOperationEffect UndoCopy(FileOperation operation)
    {
        var effect = Discard(operation.Target, operation.IsDirectory);
        if (operation.ReplacedPath is null)
            return effect;
        FolderTreeCommandHandler.RestoreBackup(operation.ReplacedPath, operation.Target);
        // 同じパスの元ファイルを復元したので、開いているタブを閉じる通知は出さない。
        return new FileOperationEffect(null, null, null, operation.Target, operation.IsDirectory);
    }

    private static FileOperationEffect UndoMove(FileOperation operation)
    {
        var effect = MoveEntry(operation.Target, operation.Source, operation.IsDirectory);
        if (operation.ReplacedPath is not null)
            FolderTreeCommandHandler.RestoreBackup(operation.ReplacedPath, operation.Target);
        return effect with { Revealed = operation.Source };
    }

    private static FileOperationEffect RedoCopy(FileOperation operation)
    {
        PrepareRedoReplacement(operation);
        return CopyEntry(operation.Source, operation.Target, operation.IsDirectory);
    }

    private static FileOperationEffect RedoMove(FileOperation operation)
    {
        PrepareRedoReplacement(operation);
        return MoveEntry(operation.Source, operation.Target, operation.IsDirectory);
    }

    private static FileOperationEffect ZipEntry(FileOperation operation)
    {
        FolderTreeCommandHandler.CreateZipFile(operation.Sources!, operation.Target);
        return new FileOperationEffect(null, null, null, operation.Target, false);
    }

    private static void PrepareRedoReplacement(FileOperation operation)
    {
        if (operation.ReplacedPath is null || !File.Exists(operation.Target) && !Directory.Exists(operation.Target))
            return;
        try
        {
            if (Directory.Exists(operation.Target)) Directory.Move(operation.Target, operation.ReplacedPath);
            else File.Move(operation.Target, operation.ReplacedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"上書き対象を退避できませんでした: {ex.Message}", ex);
        }
    }

    private static FileOperationEffect MoveEntry(string from, string to, bool isDirectory)
    {
        try
        {
            var parent = Path.GetDirectoryName(to);
            if (parent is not null)
                Directory.CreateDirectory(parent);
            if (isDirectory) Directory.Move(from, to);
            else File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"移動に失敗しました: {ex.Message}", ex);
        }
        return new FileOperationEffect(from, to, null, to, isDirectory);
    }

    private static FileOperationEffect CopyEntry(string source, string destination, bool isDirectory)
    {
        try
        {
            if (isDirectory) FolderTreeCommandHandler.CopyDirectoryTree(source, destination);
            else File.Copy(source, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"コピーに失敗しました: {ex.Message}", ex);
        }
        return new FileOperationEffect(null, null, null, destination, isDirectory);
    }

    // 作成のやり直し。Undo で捨てたものがゴミ箱にまだ居ればそれを戻す——作ったあとに書き込んだ中身は
    // 「作成」の記録には入っていないので、素直に作り直すと Undo→Redo で中身が空になって消える。
    // ゴミ箱を空にされていれば、本来の意味どおり空で作り直す。
    private static FileOperationEffect Recreate(string path, bool isDirectory)
    {
        if (RecycleBin.TryRestore(path, out _))
            return new FileOperationEffect(null, null, null, path, isDirectory);

        try
        {
            if (isDirectory)
                Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (File.Create(path)) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"作成に失敗しました: {ex.Message}", ex);
        }
        return new FileOperationEffect(null, null, null, path, isDirectory);
    }

    private static FileOperationEffect Discard(string path, bool isDirectory)
    {
        FolderTreeCommandHandler.SendToRecycleBin(path, isDirectory);
        return new FileOperationEffect(null, null, path, null, isDirectory);
    }

    private static FileOperationEffect Restore(string path, bool isDirectory)
    {
        if (!RecycleBin.TryRestore(path, out var error))
            throw new InvalidOperationException(error ?? "ゴミ箱から戻せませんでした。");
        return new FileOperationEffect(null, null, null, path, isDirectory);
    }

    private void Push(FileOperationStep step)
    {
        _undo.Add(step);
        if (_undo.Count > MaxDepth)
        {
            CleanupBackups(_undo[0]);
            _undo.RemoveAt(0);
        }
        foreach (var oldStep in _redo)
            CleanupBackups(oldStep);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void CleanupBackups(FileOperationStep step)
    {
        foreach (var path in step.Operations.Select(o => o.ReplacedPath).Where(p => p is not null).Cast<string>())
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { /* 履歴の破棄をファイルロックで失敗させない */ }
        }
    }

    private void EndBatch()
    {
        if (--_batchDepth > 0)
            return;
        var operations = _batch;
        _batch = null;
        if (operations is { Count: > 0 })
            Push(new FileOperationStep(operations));
    }

    private sealed class BatchScope(FileOperationHistory history) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            history.EndBatch();
        }
    }

    /// <summary>Undo 1 回で戻る単位（1 件の操作、または 1 回の一括操作）。</summary>
    private sealed record FileOperationStep(IReadOnlyList<FileOperation> Operations)
    {
        /// <summary>メニュー・トーストに出す説明。種類が揃っていれば「削除 3件」、
        /// 1 件なら名前入りで「削除「a.txt」」、混在なら件数だけ。</summary>
        public string Description
        {
            get
            {
                var first = Operations[0];
                var uniform = Operations.All(o => o.Kind == first.Kind);
                var kind = uniform ? Label(first.Kind) : "ファイル操作";
                return Operations.Count == 1
                    ? $"{kind}「{Path.GetFileName(first.PrimaryPath)}」"
                    : $"{kind} {Operations.Count}件";
            }
        }

        private static string Label(FileOperationKind kind) => kind switch
        {
            FileOperationKind.Create => "作成",
            FileOperationKind.Rename => "名前の変更",
            FileOperationKind.Move => "移動",
            FileOperationKind.Copy => "コピー",
            FileOperationKind.Delete => "削除",
            FileOperationKind.Zip => "ZIPに圧縮",
            _ => "ファイル操作",
        };
    }
}
