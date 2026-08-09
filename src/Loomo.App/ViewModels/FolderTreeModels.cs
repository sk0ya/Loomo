using CommunityToolkit.Mvvm.ComponentModel;
using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.App.ViewModels;

// 「ターミナルへ送る」要求の対象。フォルダなら cd、ファイルならパスをプロンプトへ入力する。
public readonly record struct TerminalSetRequest(string FullPath, bool IsDirectory);

// 「Diff へ送る」要求の対象。RightPath が null なら「クリップボードと比較」（右はクリップボード）、
// 2つ指定なら「選んだ2ファイルの比較」。左＝旧・右＝新として Diff ペインに並ぶ。
public readonly record struct FileCompareRequest(string LeftPath, string? RightPath);

/// <summary>「AIワークフロー」コンテキストメニューからの実行要求。<see cref="Input"/> は構造化された実行入力。</summary>
public readonly record struct WorkflowRunRequest(string WorkflowId, WorkflowRunInput Input);

// FolderTree でのリネーム通知。OldPath/NewPath は正規化済みフルパス。IsDirectory ならフォルダの
// リネーム（配下のファイルパスも OldPath → NewPath で付け替わる）。
public readonly record struct EntryRenamedEventArgs(string OldPath, string NewPath, bool IsDirectory);

/// <summary>ルート切替 ComboBox の 1 候補。先頭はワークスペースルート（IsPinned=false）、
/// 以降はピン留めフォルダ。Label はルートからの相対パスで同名フォルダを区別する。</summary>
public sealed partial class FolderRootOption : ObservableObject
{
    public FolderRootOption(string fullPath, string label, bool isPinned)
    {
        FullPath = fullPath;
        Label = label;
        IsPinned = isPinned;
        IsMissing = !Directory.Exists(fullPath);
    }

    public string FullPath { get; }
    public string Label { get; }
    public bool IsPinned { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private bool _isMissing;

    /// <summary>フォルダーが一時的に存在しないピンも候補に残し、状態を明示する表示名。</summary>
    public string DisplayLabel => IsMissing ? $"{Label} (フォルダーなし)" : Label;
}
