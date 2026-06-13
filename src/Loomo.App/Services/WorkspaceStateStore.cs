using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

public sealed class WorkspaceStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public WorkspaceStateStore() : this(DefaultPath()) { }

    public WorkspaceStateStore(string filePath) => _filePath = filePath;

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Loomo", "workspaces.json");

    public WorkspaceState Load()
    {
        if (!File.Exists(_filePath))
            return new WorkspaceState();

        try
        {
            return JsonSerializer.Deserialize<WorkspaceState>(
                File.ReadAllText(_filePath), JsonOptions) ?? new WorkspaceState();
        }
        catch
        {
            return new WorkspaceState();
        }
    }

    public void Save(WorkspaceState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(state, JsonOptions));
    }
}

public sealed class WorkspaceState
{
    public Guid? ActiveWorkspaceId { get; set; }
    public List<WorkspaceSnapshot> Workspaces { get; set; } = new();
}

public sealed class WorkspaceSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RootPath { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public TerminalSnapshot Terminal { get; set; } = new();
    public EditorSnapshot Editor { get; set; } = new();
    public List<TerminalTabSnapshot> TerminalTabs { get; set; } = new();
    public List<EditorTabSnapshot> EditorTabs { get; set; } = new();
    public List<BrowserTabSnapshot> BrowserTabs { get; set; } = new();

    /// <summary>FolderTree でピン留めしたフォルダ（フルパス）。ルート切替 ComboBox の候補になる。</summary>
    public List<string> PinnedFolders { get; set; } = new();

    /// <summary>FolderTree の表示中ルート。null ならワークスペースルートを表示する。</summary>
    public string? TreeRootPath { get; set; }

    /// <summary>
    /// メイン領域のレイアウトツリー（リーフ＝ペイン、スプリット＝行/列の入れ子）。
    /// null なら既定レイアウトを使う。非表示のペインもツリーに残り、リーフの Hidden で表す。
    /// </summary>
    public PaneNodeSnapshot? PaneLayout { get; set; }

    /// <summary>ステージモードの表示状態。未保存の旧ワークスペースは既定でステージ表示にする。</summary>
    public StageSnapshot? Stage { get; set; } = StageSnapshot.Default();

    /// <summary>コマンドコンポーザ（§23.2）の本文。エディタタブ同様、全文をそのまま保存する。</summary>
    public string? ComposerText { get; set; }

    /// <summary>コマンドコンポーザの表示状態（開いたまま離れたら開いたまま戻る）。</summary>
    public bool ComposerVisible { get; set; }

    /// <summary>コマンドコンポーザの高さ（px）。null なら既定値。</summary>
    public double? ComposerHeight { get; set; }

    /// <summary>ペグボード（§23.3）のアイテム。ワークスペース毎に持つ。</summary>
    public List<PegboardItemSnapshot> Pegboard { get; set; } = new();
}

/// <summary>ペグボード（§23.3）の1アイテム。</summary>
public sealed class PegboardItemSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>"text" | "url" | "file"（開くときの振る舞いを決める）。</summary>
    public string Type { get; set; } = "text";
    /// <summary>本文（text はスニペット全文、url は URL、file はフルパス）。</summary>
    public string Content { get; set; } = "";
    /// <summary>一覧の見出し。null なら Content の先頭行を表示に使う。</summary>
    public string? Title { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>上部固定。</summary>
    public bool Pinned { get; set; }
}

/// <summary>メイン領域に並ぶペインの種別。値は JSON へ数値で永続化されるため末尾追加のみ可。</summary>
public enum PaneKind
{
    Terminal,
    Editor,
    Browser,
    Ai,
    EditorSupport,
    Git,
    Diff,
    Trace
}

/// <summary>
/// レイアウトツリーの1ノード。<see cref="Kind"/> があればリーフ（ペイン）、
/// <see cref="Children"/> があればスプリット（入れ子の行/列）。
/// </summary>
public sealed class PaneNodeSnapshot
{
    /// <summary>親スプリット内での star 比率。</summary>
    public double Weight { get; set; } = 1;
    /// <summary>リーフのとき、ペイン種別。</summary>
    public PaneKind? Kind { get; set; }
    /// <summary>リーフのとき、非表示中か。位置・比率を保ったまま隠す。</summary>
    public bool Hidden { get; set; }
    /// <summary>スプリットのとき、"Rows"（上下に積む）か "Columns"（左右に並べる）。</summary>
    public string? Orientation { get; set; }
    /// <summary>スプリットの子（行なら上→下、列なら左→右の順）。</summary>
    public List<PaneNodeSnapshot> Children { get; set; } = new();
}

public sealed class StageSnapshot
{
    public static StageSnapshot Default() => new() { IsActive = true, Pane = PaneKind.Editor };

    /// <summary>ステージモード中か。</summary>
    public bool IsActive { get; set; }
    /// <summary>舞台に立っているペイン。null なら復元時に既定選択へフォールバックする。</summary>
    public PaneKind? Pane { get; set; }
    /// <summary>俯瞰（全カード一望）レイヤを開いたまま離れたか。</summary>
    public bool Overview { get; set; }
}

public sealed class TerminalSnapshot
{
    public string? WorkingDirectory { get; set; }
    public string? Title { get; set; }
}

public sealed class EditorSnapshot
{
    public string? FilePath { get; set; }
    public string? Text { get; set; }
    public bool IsModified { get; set; }
}

public sealed class TerminalTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? WorkingDirectory { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; }
}

public sealed class EditorTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? FilePath { get; set; }
    public string? Text { get; set; }
    public string? Title { get; set; }
    public bool IsModified { get; set; }
    public bool IsActive { get; set; }

    /// <summary>カーソル位置（0始まり）。復元時に <c>NavigateTo</c> で戻す。</summary>
    public int CaretLine { get; set; }
    public int CaretColumn { get; set; }

    /// <summary>縦スクロール位置（0..1）。レイアウト前は取れないため null あり・復元はベストエフォート。</summary>
    public double? ScrollRatio { get; set; }
}

public sealed class BrowserTabSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Url { get; set; }
    public string? Title { get; set; }
    public bool IsActive { get; set; }
}
