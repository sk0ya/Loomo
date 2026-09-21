using sk0ya.Loomo.App.ViewModels;

namespace sk0ya.Loomo.App.Services;

/// <summary>アウトラインクリックから作られるジャンプ先（1 始まり行＋0 始まり列）。</summary>
public sealed class SourceLocationActivatedEventArgs : EventArgs
{
    public SourceLocationActivatedEventArgs(int line1, int column0)
    {
        Line1 = line1;
        Column0 = column0;
    }

    public SourceLocationActivatedEventArgs(CodeOutlineItem item)
        : this(item.JumpLine1, item.JumpColumn0)
    {
    }

    public int Line1 { get; }
    public int Column0 { get; }
}

/// <summary>②パネル行クリックから作られるジャンプ先（ローカルパス＋行＋列）。</summary>
public sealed class FileLocationActivatedEventArgs : EventArgs
{
    public FileLocationActivatedEventArgs(string path, int line1, int column0 = 0)
    {
        Path = path;
        Line1 = line1;
        Column0 = column0;
    }

    public FileLocationActivatedEventArgs(CodeCallRow row)
        : this(row.Path!, row.Line1, row.Column0)
    {
    }

    public string Path { get; }
    public int Line1 { get; }
    public int Column0 { get; }
}
