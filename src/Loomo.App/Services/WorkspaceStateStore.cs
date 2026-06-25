using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.App.Services;

/// <summary>workspaces.json（実機で 380KB 規模）の<b>読み書き</b>用 System.Text.Json ソースジェネレータ文脈。
/// 起動時、ShellViewModel 解決中（初フレーム前）に <see cref="WorkspaceStateStore.Load"/> がこの巨大JSONを
/// デシリアライズし、ワークスペース切替のたびに <see cref="WorkspaceStateStore.Save"/> が同サイズを直列化するため、
/// リフレクションのメタデータ生成コストを両経路から外す。enum は数値・camelCase・インデント付きで
/// 既存ファイルとバイト一致する（書込経路をリフレクションから移しても出力フォーマットは変わらない）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceState))]
internal partial class WorkspaceStateJsonContext : JsonSerializerContext;

public sealed class WorkspaceStateStore
{
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
            // 読み書きともソースジェネレータ経路（リフレクションのメタデータ生成コストを外す）。
            return JsonSerializer.Deserialize(
                File.ReadAllText(_filePath), WorkspaceStateJsonContext.Default.WorkspaceState) ?? new WorkspaceState();
        }
        catch
        {
            return new WorkspaceState();
        }
    }

    /// <summary>状態を同期でディスクへ書き出す。書込直後の <see cref="Load"/>（別インスタンス含む）が
    /// 確実に最新を読めるよう、あえて同期のまま（read-after-write の耐久性契約）。直列化はソース
    /// ジェネレータ経路でリフレクションコストを外す。呼び出し回数は切替経路側で間引く。</summary>
    public void Save(WorkspaceState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(
            _filePath, JsonSerializer.Serialize(state, WorkspaceStateJsonContext.Default.WorkspaceState));
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

    /// <summary>表示モード（ソロ／レイアウト）。null の旧データは復元時に <see cref="Stage"/> から移行する。</summary>
    public DisplayMode? Mode { get; set; }

    /// <summary>有効なセッション（タイトルバーの表示トグルが ON のもの）。Main（タイル／舞台）と
    /// 袖（ミニチュア）のどちらかに必ず出る。Main に出ていない有効セッションは袖に出るため、
    /// この集合がタイル配置より広いほど袖は常時にぎわう。null／空の旧データは全セッション有効として復元する。</summary>
    public List<PaneKind>? EnabledSessions { get; set; }

    /// <summary>ソロモード（単一ステージ＋袖＋俯瞰）の表示状態。未保存の旧ワークスペースは既定でソロにする。</summary>
    public StageSnapshot? Stage { get; set; } = StageSnapshot.Default();

    /// <summary>レイアウトモードに保存した名前付きレイアウト（Ctrl+T 巡回に並ぶ）。空なら既定3種を投入する。</summary>
    public List<SavedLayout> Layouts { get; set; } = new();

    /// <summary>未保存作業を退避する単一スクラッチ枠（次の未保存編集で上書きされる）。</summary>
    public PaneNodeSnapshot? ScratchLayout { get; set; }

    /// <summary>レイアウト巡回の現在位置（-1＝スクラッチ、0..n＝<see cref="Layouts"/>[i]）。</summary>
    public int ActiveLayoutIndex { get; set; } = -1;

    /// <summary>現在のタイル配置が保存レイアウトから変化しているか（巡回で現配置をスクラッチへ退避するかの判定に使う）。</summary>
    public bool LayoutDirty { get; set; }

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

/// <summary>セッションの表示モード。値は JSON へ数値で永続化されるため末尾追加のみ可。</summary>
public enum DisplayMode
{
    /// <summary>ソロ：1ペインを舞台に立て、他は袖でライブ待機（＋俯瞰）。</summary>
    Solo,
    /// <summary>レイアウト：自由タイルで組み、名前付きで保存・Ctrl+T で巡回する。</summary>
    Layout
}

/// <summary>レイアウトモードに保存した名前付きレイアウト（ワークスペース毎）。ツリーは <see cref="PaneNodeSnapshot"/>。</summary>
public sealed class SavedLayout
{
    public string Name { get; set; } = "";
    /// <summary>このレイアウトのペイン配置ツリー（リーフ＝ペイン、スプリット＝行/列の入れ子）。</summary>
    public PaneNodeSnapshot Tree { get; set; } = new();

    /// <summary>新規ワークスペース／旧データ移行時に投入する既定レイアウト3種。</summary>
    public static List<SavedLayout> Defaults() => new()
    {
        new SavedLayout { Name = "エディタ＋サポート", Tree = Columns(Leaf(PaneKind.Editor), Leaf(PaneKind.EditorSupport)) },
        new SavedLayout { Name = "Web開発", Tree = Rows(Columns(Leaf(PaneKind.Editor), Leaf(PaneKind.Browser)), Leaf(PaneKind.Terminal)) },
        new SavedLayout { Name = "差分レビュー", Tree = Rows(Leaf(PaneKind.Diff), Leaf(PaneKind.Git)) },
    };

    private static PaneNodeSnapshot Leaf(PaneKind kind) => new() { Kind = kind };
    private static PaneNodeSnapshot Columns(params PaneNodeSnapshot[] children)
        => new() { Orientation = "Columns", Children = children.ToList() };
    private static PaneNodeSnapshot Rows(params PaneNodeSnapshot[] children)
        => new() { Orientation = "Rows", Children = children.ToList() };
}

/// <summary>ソロモード（単一ステージ＋袖＋俯瞰）の表示状態。</summary>
public sealed class StageSnapshot
{
    public static StageSnapshot Default() => new() { IsActive = true, Pane = PaneKind.Editor };

    /// <summary>旧データ移行用：かつてステージモード中だったか（現在は <see cref="WorkspaceSnapshot.Mode"/> が正）。</summary>
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
