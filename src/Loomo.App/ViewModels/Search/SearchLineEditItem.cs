using CommunityToolkit.Mvvm.ComponentModel;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>検索結果を書き換えるモード（§24.86）の 1 行。同じ行に一致が複数あっても 1 行にまとめる。
/// <see cref="Original"/> は検索したときの行（末尾の空白を落としたもの）で、書き戻す前の照合に使う。</summary>
public sealed partial class SearchLineEditItem : ObservableObject
{
    public SearchLineEditItem(string fullPath, string relativePath, int line, string original, bool isFirstOfFile)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Line = line;
        Original = original;
        _edited = original;
        IsFirstOfFile = isFirstOfFile;
    }

    public string FullPath { get; }
    public string RelativePath { get; }
    public int Line { get; }
    public string Original { get; }

    /// <summary>ファイルの見出しを出す行か（同じファイルの最初の行だけ）。</summary>
    public bool IsFirstOfFile { get; }

    /// <summary>書き換えた行。改行は入れない（1 行は 1 行のまま戻す）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private string _edited;

    public bool IsChanged => !string.Equals(Edited, Original, StringComparison.Ordinal);

    partial void OnEditedChanged(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
            Edited = value.Replace("\r", "").Replace("\n", "");
    }
}
