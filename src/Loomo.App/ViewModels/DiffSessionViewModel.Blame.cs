using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>Blame 表示の1行（ガター相当の注釈＋本文）。</summary>
public sealed record GitBlameLineVm(string Annotation, int LineNumber, string Text);

/// <summary>
/// DiffSessionViewModel の Git Blame（行単位の変更履歴）パート。FolderTree の「Git Blame」から
/// 呼ばれ、Unified/SideBySide/Conflict と並ぶ「Blame 表示モード」として、対象ファイルの全行に
/// 短縮ハッシュ・著者・日付の注釈を付けて表示する。作業ツリーの未コミット変更行は git 標準どおり
/// 全ゼロのハッシュ（「まだコミットされていません」相当）で表示される。
/// </summary>
public sealed partial class DiffSessionViewModel
{
    /// <summary>Blame 表示中か（true のときは <see cref="BlameLines"/> を表示する）。</summary>
    [ObservableProperty] private bool _isBlameMode;

    public ObservableCollection<GitBlameLineVm> BlameLines { get; } = new();

    /// <summary>Blame 表示対象（リポジトリルートからの相対パス）。null は Blame 表示なし（作業ツリー等）。</summary>
    private string? _blameTarget;

    partial void OnIsBlameModeChanged(bool value) => OnPropertyChanged(nameof(ShowDiffBody));

    /// <summary>通常の差分本体（統合／左右）を表示するか。コンフリクト解消・Blame のどちらでもないとき。</summary>
    public bool ShowDiffBody => !IsConflictMode && !IsBlameMode;

    /// <summary>
    /// FolderTree のコンテキストメニュー「Git Blame」から：指定ファイル（リポジトリルートからの相対パス）の
    /// 行単位の変更履歴を表示する。コミット範囲は解除して作業ツリー（Git）モードへ切り替える。
    /// ペインの表示は呼び出し側（ShellWindow）が行う。
    /// </summary>
    public async Task ShowBlameAsync(string relativePath)
    {
        _loaded = true;
        _commitRange = null;
        _blameTarget = relativePath;
        GitTargetLabel = $"Blame: {relativePath}";
        IsGitMode = true;
        await RefreshAsync();
        SelectedFile = Files.FirstOrDefault(f => f.BlamePath is not null) ?? SelectedFile;
    }

    /// <summary>Blame 対象1件だけの一覧を組み立てる（実際の取得は選択時の <see cref="LoadBlameAsync"/>）。</summary>
    private (List<DiffFileItem> Items, string EmptyMessage) BuildBlameItems(string relativePath)
    {
        var root = _workspace.RootPath ?? "";
        var items = new List<DiffFileItem>
        {
            new()
            {
                FullPath = Path.Combine(root, relativePath),
                DisplayPath = relativePath,
                Badge = "Blame",
                BlamePath = relativePath,
            },
        };
        return (items, "");
    }

    /// <summary>選択された Blame 対象ファイルの行単位の変更履歴を取得して表示する。</summary>
    private async Task LoadBlameAsync(DiffFileItem item)
    {
        IsBlameMode = true;
        BlameLines.Clear();
        Hunks.Clear();
        OnPropertyChanged(nameof(CanStageHunks));
        DiffRows.Clear();
        SideRows.Clear();

        var (lines, error) = await _git.GetBlameAsync(item.BlamePath!);
        if (error is not null)
        {
            SetStatus($"Blame を取得できませんでした: {Truncate(error)}", isError: true);
            return;
        }

        foreach (var line in lines)
            BlameLines.Add(new GitBlameLineVm(
                $"{line.ShortHash}  {line.Author,-14}  {line.AuthorDate}", line.FinalLineNumber, line.Content));
    }
}
