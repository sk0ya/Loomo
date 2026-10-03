using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.Core.Files;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>FolderTreeViewModel の「関連ファイルのまとめ表示」（VS Code の Explorer File Nesting 相当）パート。
/// 判定そのものは純関数 <see cref="FileNesting"/> に任せ、ここは設定（<see cref="LoomoSettings.Explorer"/>）の
/// 窓口と、変更時のツリーの組み直しだけを持つ。設定画面（外観カテゴリ）はこのプロパティへ直接結ぶ。</summary>
public sealed partial class FolderTreeViewModel
{
    private readonly LoomoSettings? _settings;
    private readonly SettingsStore? _settingsStore;

    // 設定のルールから組んだ判定器。ルール文字列が変わるまで使い回す（フォルダーを開くたびに Regex を組まない）。
    private FileNesting? _fileNesting;

    /// <summary>まとめ表示に使う判定器。設定を持たない構成（テスト等）と OFF のときは何もまとめない。</summary>
    private FileNesting CurrentFileNesting()
    {
        if (_settings is not { Explorer.FileNestingEnabled: true } settings)
            return FileNesting.Empty;
        return _fileNesting ??= FileNesting.Create(settings.Explorer.FileNestingPatterns);
    }

    /// <summary>関連ファイルを親ファイルの下へまとめて表示するか（設定画面のチェックボックス）。</summary>
    public bool FileNestingEnabled
    {
        get => _settings?.Explorer.FileNestingEnabled ?? false;
        set
        {
            if (_settings is null || _settings.Explorer.FileNestingEnabled == value)
                return;
            _settings.Explorer.FileNestingEnabled = value;
            OnPropertyChanged();
            ApplyFileNestingChange();
        }
    }

    /// <summary>まとめ方のルールを 1 行 1 ルール（<c>親 = 子1, 子2</c>）で編集するテキスト。
    /// 書き損じた行は読み捨て、読めた分だけを保存する（保存後は正規化した形で表示し直す）。</summary>
    public string FileNestingPatternsText
    {
        get => _settings is null ? "" : FileNesting.FormatText(_settings.Explorer.FileNestingPatterns);
        set
        {
            if (_settings is null)
                return;
            var parsed = FileNesting.ParseText(value);
            if (SamePatterns(parsed, _settings.Explorer.FileNestingPatterns))
            {
                OnPropertyChanged();   // 表記ゆれ（空白・空行）だけなら正規化した形へ戻す
                return;
            }
            _settings.Explorer.FileNestingPatterns.Clear();
            _settings.Explorer.FileNestingPatterns.AddRange(parsed);
            OnPropertyChanged();
            ApplyFileNestingChange();
        }
    }

    /// <summary>まとめ方のルールを既定へ戻す。</summary>
    [RelayCommand]
    private void ResetFileNestingPatterns()
        => FileNestingPatternsText = FileNesting.FormatText(FileNesting.DefaultPatterns);

    private void ApplyFileNestingChange()
    {
        _fileNesting = null;
        try { _settingsStore?.Save(_settings!); }
        catch { /* 永続化失敗でも表示の切替自体は効かせる */ }
        // 差分更新（同一インスタンス再利用）で組み直すので、展開・選択はそのまま残る。
        RefreshWorkspace();
    }

    private static bool SamePatterns(
        IReadOnlyList<KeyValuePair<string, string>> a, IReadOnlyList<KeyValuePair<string, string>> b)
        => a.Count == b.Count
           && a.Zip(b).All(p => string.Equals(p.First.Key, p.Second.Key, StringComparison.Ordinal)
                                && string.Equals(p.First.Value, p.Second.Value, StringComparison.Ordinal));
}
