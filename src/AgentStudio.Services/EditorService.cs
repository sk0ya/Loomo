using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using AgentStudio.Core.Abstractions;
using Editor.Controls;

namespace AgentStudio.Services;

/// <summary>
/// sk0ya の <see cref="VimEditorControl"/> をラップする IEditorService 実装。
/// LoadFile / SetText / Text / FilePath を利用する。
/// （選択テキスト取得は現状の公開APIに無いため将来対応：Engine 経由 or API追加）
/// </summary>
public sealed class EditorService : IEditorService
{
    private VimEditorControl? _ctrl;

    public void Attach(VimEditorControl ctrl) => _ctrl = ctrl;

    public string? ActiveFilePath => Dispatch(() => _ctrl?.FilePath);

    public Task OpenFileAsync(string path)
    {
        DispatchVoid(() => _ctrl?.LoadFile(path));
        return Task.CompletedTask;
    }

    public Task<string> GetActiveContentAsync()
        => Task.FromResult(Dispatch(() => _ctrl?.Text) ?? string.Empty);

    public Task<string> GetSelectedTextAsync()
        => Task.FromResult(string.Empty); // TODO: VimEngine 経由で選択範囲を取得

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
