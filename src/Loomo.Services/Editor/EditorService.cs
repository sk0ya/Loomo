using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using sk0ya.Loomo.Core.Abstractions;
using Editor.Controls;

namespace sk0ya.Loomo.Services;

/// <summary>
/// sk0ya の <see cref="VimEditorControl"/> をラップする IEditorService 実装。
/// 通常ファイルは LoadFile / Save、設定の長文項目などは仮想ドキュメント
/// （<see cref="VimEditorControl.OpenVirtualDocument"/>）で扱う。
/// 保存（:w）はエディタが <see cref="VimEditorControl.SaveRequested"/> を発火し、ホスト側で確定する契約。
/// 本実装がその保存ハンドラを担う（通常ファイルは <see cref="VimEditorControl.Save"/>、
/// 仮想ドキュメントは登録済みコールバックへ内容を渡して <see cref="VimEditorControl.MarkSaved"/>）。
/// </summary>
public sealed class EditorService : IEditorService
{
    private readonly IWorkspaceService _workspace;
    private VimEditorControl? _ctrl;

    public EditorService(IWorkspaceService workspace)
    {
        _workspace = workspace;
    }

    /// <summary>仮想ドキュメントの DocumentId → 保存コールバックの対応表。</summary>
    private readonly Dictionary<string, Action<string>> _docCallbacks =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 仮想ドキュメント（設定の長文項目など）を編集用に開く直前、ホストへ「専用のエディタタブを
    /// 用意せよ」と要求するイベント。引数は表示名。ホスト（ShellWindow）は同名タブがあれば再利用し、
    /// 無ければ新規タブを作成・アクティブ化して、その control を <see cref="Attach"/> する。
    /// これにより仮想ドキュメントが現在開いているファイルを上書きせず、独立したタブで開く。
    /// </summary>
    public event Action<string>? NewVirtualDocumentTabRequested;

    /// <summary>
    /// ファイルを「開く」要求。引数は対象のフルパス。ホスト（ShellWindow）が受けて専用のエディタタブを
    /// 作成・アクティブ化し、そこへファイルを読み込む（既に開いていればそのタブを再利用）。
    /// かつては <see cref="OpenFileAsync"/> が現在アタッチ中の control へ直接 LoadFile していたため、
    /// 呼び出し側の意図（=新しいタブで開く）に反して現在のタブの中身を上書きしていた。タブ生成は
    /// タブ管理を持つホストにしか正しく行えないので、ここでは要求だけを上げてホストへ委ねる。
    /// </summary>
    public event Action<string>? FileOpenRequested;

    /// <summary>通常ファイルの保存直前にApp層が編集を整えるためのフック。仮想文書では呼ばない。</summary>
    public Func<VimEditorControl, string?, Task>? BeforeSaveAsync { get; set; }

    public void Attach(VimEditorControl ctrl)
    {
        if (_ctrl is not null)
            _ctrl.SaveRequested -= OnSaveRequested;
        _ctrl = ctrl;
        ctrl.SaveRequested += OnSaveRequested;
    }

    public string? ActiveFilePath => Dispatch(() => _ctrl?.FilePath);

    public Task OpenFileAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // タブの作成・再利用・アクティブ化はホスト側でしか正しく行えないので委ねる。
        // 購読者（ShellWindow）が居なければ何もしない（best-effort）。
        DispatchVoid(() => FileOpenRequested?.Invoke(path));
        return Task.CompletedTask;
    }

    public Task<string> GetActiveContentAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Dispatch(() => _ctrl?.Text) ?? string.Empty);
    }

    public Task<string> GetSelectedTextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Dispatch(() => _ctrl?.SelectedText) ?? string.Empty);
    }

    public Task OpenDocumentAsync(EditorDocument document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // ファイルを介さない仮想ドキュメントとして開く（ディスクには一切書かない）。
        // 保存（:w）は SaveRequested(IsVirtual=true) で通知され、永続化は OnSaved コールバックが担う。
        var syntax = SyntaxFromName(document.FileName);
        DispatchVoid(() =>
        {
            // 仮想ドキュメント専用のタブを用意してもらう（同名は再利用）。これにより現在開いている
            // ファイルを上書きせず、Attach 済みの control が当該タブのものへ差し替わる。
            NewVirtualDocumentTabRequested?.Invoke(document.FileName);
            var id = _ctrl?.OpenVirtualDocument(document.FileName, document.Content, syntax);
            if (id is not null)
                lock (_docCallbacks)
                    _docCallbacks[id] = document.OnSaved;
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// ユーザーが :w で保存したときの処理。エディタはイベントを上げるだけなのでホストが確定する。
    /// 仮想ドキュメントはディスクに書かず、内容をコールバックへ渡して modified フラグを解除する。
    /// 通常ファイルは <see cref="VimEditorControl.Save"/> でディスクへ保存する。
    /// </summary>
    private void OnSaveRequested(object? sender, SaveRequestedEventArgs e)
    {
        var ctrl = sender as VimEditorControl ?? _ctrl;
        if (ctrl is null) return;

        if (e.IsVirtual)
        {
            var content = ctrl.Text ?? string.Empty;
            Action<string>? callback = null;
            if (e.DocumentId is not null)
                lock (_docCallbacks)
                    _docCallbacks.TryGetValue(e.DocumentId, out callback);

            if (callback is not null)
            {
                try { callback(content); }
                catch { /* 保存コールバック側の失敗で :w 自体を妨げない */ }
            }
            ctrl.MarkSaved(e.DocumentId);   // 永続化済みとして modified フラグを解除
            return;
        }

        _ = SaveRequestedFileAsync(ctrl, e.FilePath);
    }

    /// <summary>
    /// 走っている保存（control と保存先が同じもの）。2度目の :w／Ctrl+S はここへ相乗りさせる。
    ///
    /// <para>保存前フック（<see cref="BeforeSaveAsync"/>＝C# の保存時クリーンアップ）は大規模な
    /// ソリューションで<b>数秒</b>かかる。その間の2打鍵目を2本目の保存として走らせると、
    /// 2本が同じバッファへ同時に手を入れ、後から入った方の WorkspaceEdit が
    /// 「プレビュー中に本文が変わった」と見て中止する——本文は rollback で守られるが、
    /// 人には<b>ただ Ctrl+S を2回押しただけ</b>で保存が失敗したようにしか見えない。</para>
    /// </summary>
    private readonly Dictionary<VimEditorControl, (string Path, Task<bool> Save)> _savesInFlight = new();

    /// <summary>Ctrl+SとVimの:wが共有する通常ファイル保存経路。</summary>
    public Task<bool> SaveFileAsync(VimEditorControl control, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control.IsVirtualDocument) return Task.FromResult(false);

        var targetPath = path ?? control.FilePath;
        if (string.IsNullOrWhiteSpace(targetPath))
            targetPath = PromptSaveAsPath();
        if (string.IsNullOrWhiteSpace(targetPath)) return Task.FromResult(false);

        TaskCompletionSource<bool> completion;
        lock (_savesInFlight)
        {
            // 保存先まで見るのは、走っているのが「名前を付けて保存」のときに別の宛先の保存を
            // 巻き込まないため。同じ宛先なら、走っている保存が今の本文を書く＝やることは同じ。
            if (_savesInFlight.TryGetValue(control, out var running) &&
                string.Equals(running.Path, targetPath, StringComparison.OrdinalIgnoreCase))
                return running.Save;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _savesInFlight[control] = (targetPath!, completion.Task);
        }
        return TrackSaveAsync(control, targetPath!, completion);
    }

    /// <summary>実際の保存を走らせ、相乗りしている呼び出しへ同じ結果を配って記録を落とす。</summary>
    private async Task<bool> TrackSaveAsync(
        VimEditorControl control, string targetPath, TaskCompletionSource<bool> completion)
    {
        try
        {
            var saved = await SaveFileCoreAsync(control, targetPath);
            completion.TrySetResult(saved);
            return saved;
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            throw;
        }
        finally
        {
            lock (_savesInFlight)
                if (_savesInFlight.TryGetValue(control, out var entry) &&
                    ReferenceEquals(entry.Save, completion.Task))
                    _savesInFlight.Remove(control);
            // 相乗りが居なければ誰も await しないので、例外を観測済みにしておく
            // （投げた先はこのメソッドの呼び出し元が受け取る）。
            _ = completion.Task.Exception;
        }
    }

    private async Task<bool> SaveFileCoreAsync(VimEditorControl control, string targetPath)
    {
        if (BeforeSaveAsync is { } prepare)
        {
            try
            {
                await prepare(control, targetPath);
            }
            catch (Exception ex)
            {
                // 保存前の任意処理で利用者の保存を失敗させない。処理側のWorkspaceEditは
                // 失敗時にrollbackされるため、ここでは現在の本文をそのまま保存する。
                control.ShowStatusMessage($"保存前処理に失敗しました。本文をそのまま保存します: {ex.Message}");
            }
        }

        control.Save(targetPath);
        return true;
    }

    private async Task SaveRequestedFileAsync(VimEditorControl control, string? path)
    {
        try
        {
            await SaveFileAsync(control, path);
        }
        catch (Exception ex)
        {
            control.ShowStatusMessage($"保存に失敗しました: {ex.Message}");
        }
    }

    /// <summary>Untitled タブの保存時に表示するファイル保存ダイアログ。キャンセルなら null。</summary>
    private string? PromptSaveAsPath()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "すべてのファイル (*.*)|*.*",
            InitialDirectory = _workspace.PrimaryFolder,
            FileName = "Untitled.txt",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? SyntaxFromName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".md" => "markdown",
            ".json" => "json",
            ".txt" => null,
            _ => null,
        };

    private static void DispatchVoid(Action action)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }

    private static T? Dispatch<T>(Func<T?> func)
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) return func();
        return app.Dispatcher.Invoke(func);
    }
}
