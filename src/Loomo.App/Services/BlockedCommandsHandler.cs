using sk0ya.Loomo.Ai;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Models;
using sk0ya.Loomo.Core.Safety;

namespace sk0ya.Loomo.App.Services;

public sealed record SettingsCommandResult(bool Success, string Message);

/// <summary>危険コマンド設定のリセット・編集・永続化を担当する Command Handler。</summary>
public sealed class BlockedCommandsHandler
{
    private readonly AiSettings _settings;
    private readonly AiSettingsStore _store;
    private readonly IEditorService _editor;

    public BlockedCommandsHandler(AiSettings settings, AiSettingsStore store, IEditorService editor)
    {
        _settings = settings;
        _store = store;
        _editor = editor;
    }

    public SettingsCommandResult Reset()
    {
        _settings.Safety.BlockedCommandPatterns = new List<string>(SafetySettings.DefaultBlockedPatterns);
        return Persist("危険コマンドのブロックリストを既定値に戻しました");
    }

    public async Task<SettingsCommandResult> OpenEditorAsync(Action<SettingsCommandResult> onSaved)
    {
        const string header =
            "# ブロックする危険コマンド（run_powershell の照合に使用）\n" +
            "# ・1行に1つ、正規表現で記述します（大文字小文字は無視）。\n" +
            "# ・'#' で始まる行と空行は無視されます。\n\n";
        await _editor.OpenDocumentAsync(new EditorDocument
        {
            FileName = "loomo-blocked-commands.txt",
            Content = header + string.Join("\n", _settings.Safety.BlockedCommandPatterns),
            OnSaved = text =>
            {
                _settings.Safety.BlockedCommandPatterns = ParsePatterns(text);
                onSaved(Persist("危険コマンドのブロックリストを保存しました"));
            }
        });
        return new SettingsCommandResult(true,
            "危険コマンド一覧をエディタで開きました。編集して保存（:w）すると反映されます。");
    }

    private SettingsCommandResult Persist(string message)
    {
        try
        {
            _store.Save(_settings);
            return new SettingsCommandResult(true, $"{message} — {_store.FilePath}");
        }
        catch (Exception ex)
        {
            return new SettingsCommandResult(false, $"保存に失敗しました: {ex.Message}");
        }
    }

    internal static List<string> ParsePatterns(string text) => text.Replace("\r\n", "\n").Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("#"))
        .ToList();
}
