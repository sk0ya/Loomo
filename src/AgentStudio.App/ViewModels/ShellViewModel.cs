using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentStudio.App.ViewModels;

/// <summary>ルートウィンドウの ViewModel。各ペインの VM を束ねる。</summary>
public sealed class ShellViewModel : ObservableObject
{
    public FolderTreeViewModel FolderTree { get; }
    public AiBarViewModel AiBar { get; }

    public ShellViewModel(FolderTreeViewModel folderTree, AiBarViewModel aiBar)
    {
        FolderTree = folderTree;
        AiBar = aiBar;
    }
}
