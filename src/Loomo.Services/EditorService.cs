using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
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
    private VimEditorControl? _ctrl;

    /// <summary>仮想ドキュメントの DocumentId → 保存コールバックの対応表。</summary>
    private readonly Dictionary<string, Action<string>> _docCallbacks =
        new(StringComparer.Ordinal);

    public void Attach(VimEditorControl ctrl)
    {
        if (_ctrl is not null)
            _ctrl.SaveRequested -= OnSaveRequested;
        _ctrl = ctrl;
        ctrl.SaveRequested += OnSaveRequested;
    }

    public string? ActiveFilePath => Dispatch(() => _ctrl?.FilePath);

    public Task OpenFileAsync(string path)
    {
        DispatchVoid(() => _ctrl?.LoadFile(path));
        return Task.CompletedTask;
    }

    public Task<string> GetActiveContentAsync()
        => Task.FromResult(Dispatch(() => _ctrl?.Text) ?? string.Empty);

    public Task<string> GetSelectedTextAsync()
        => Task.FromResult(Dispatch(() => _ctrl?.SelectedText) ?? string.Empty);

    public Task<string> ShowDiffAsync(string path, string proposedContent)
    {
        var current = File.Exists(path) ? File.ReadAllText(path) : "";
        var oldLines = current.Length == 0 ? 0 : current.Split('\n').Length;
        var newLines = proposedContent.Split('\n').Length;
        // v1: エディタに新内容をプレビュー表示（適用は ApplyEdit で確定）
        DispatchVoid(() => _ctrl?.SetText(proposedContent));
        return Task.FromResult($"差分プレビュー: {path}（{oldLines} → {newLines} 行）");
    }

    public async Task<bool> ApplyEditAsync(string path, string newContent)
    {
        try
        {
            await File.WriteAllTextAsync(path, newContent);
            DispatchVoid(() => _ctrl?.LoadFile(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task OpenDocumentAsync(EditorDocument document)
    {
        // ファイルを介さない仮想ドキュメントとして開く（ディスクには一切書かない）。
        // 保存（:w）は SaveRequested(IsVirtual=true) で通知され、永続化は OnSaved コールバックが担う。
        var syntax = SyntaxFromName(document.FileName);
        DispatchVoid(() =>
        {
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
        var ctrl = _ctrl;
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

        // 通常ファイル: エディタにディスク保存を委譲（modified 解除・ウォッチャ抑制も内部で処理）。
        var path = e.FilePath ?? ctrl.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        ctrl.Save(path);
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
