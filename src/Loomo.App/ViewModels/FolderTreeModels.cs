using sk0ya.Loomo.Core.Agent;

namespace sk0ya.Loomo.App.ViewModels;

// 「ターミナルにセット」要求の対象。フォルダなら cd、ファイルならパスをプロンプトへ入力する。
public readonly record struct TerminalSetRequest(string FullPath, bool IsDirectory);

/// <summary>「AIワークフロー」コンテキストメニューからの実行要求。<see cref="Input"/> は構造化された実行入力。</summary>
public readonly record struct WorkflowRunRequest(string WorkflowId, WorkflowRunInput Input);

// FolderTree でのリネーム通知。OldPath/NewPath は正規化済みフルパス。IsDirectory ならフォルダの
// リネーム（配下のファイルパスも OldPath → NewPath で付け替わる）。
public readonly record struct EntryRenamedEventArgs(string OldPath, string NewPath, bool IsDirectory);

/// <summary>ルート切替 ComboBox の 1 候補。先頭はワークスペースルート（IsPinned=false）、
/// 以降はピン留めフォルダ。Label はルートからの相対パスで同名フォルダを区別する。</summary>
public sealed class FolderRootOption
{
    public FolderRootOption(string fullPath, string label, bool isPinned)
    {
        FullPath = fullPath;
        Label = label;
        IsPinned = isPinned;
    }

    public string FullPath { get; }
    public string Label { get; }
    public bool IsPinned { get; }
}
