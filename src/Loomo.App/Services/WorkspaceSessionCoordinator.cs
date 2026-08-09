using sk0ya.Loomo.App.Views;

namespace sk0ya.Loomo.App.Services;

/// <summary>ワークスペースセッションの復元判断と表示モデル変換。</summary>
public static class WorkspaceSessionCoordinator
{
    public static bool ResolveSoloMode(WorkspaceSnapshot workspace) => workspace.Mode switch
    {
        DisplayMode.Solo => true,
        DisplayMode.Layout => false,
        _ => workspace.Stage?.IsActive == true,
    };

    public static string NormalizeBrowserAddress(string? text, string defaultUrl)
    {
        var address = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address))
            return defaultUrl;
        // スキーム付きの URL はそのまま通す。ただし「絶対 URI として解釈できるか」だけで判断しない——
        // URI のスキームはドットも数字も許すので、`localhost:5173` はスキーム "localhost" の
        // 絶対 URI として通ってしまい、そのままではどこへも遷移しない文字列が返っていた。
        if (KnownSchemes.Contains(SchemeOf(address))
            && Uri.TryCreate(address, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Scheme))
            return uri.ToString();
        // ローカルパス（`C:\…` と UNC の `\\srv\share\…`）は file: URI へ。スキーム判定を既知の名前に
        // 絞った副作用で、`C:\notes\a.html` のドライブレターが未知スキーム "C" として素通りし、
        // `https://C:\notes\a.html` という Uri に載らない文字列を作ってしまう（＝遷移で例外）。
        if (TryLocalPathUri(address) is { } fileUri)
            return fileUri;
        // 空白を含むものはホスト名になり得ないので、ローカル判定より先に検索語として抜く
        // （「localhost is down」のような文が `http://localhost is down` になって遷移で弾かれていた）。
        if (address.Contains(' '))
            return SearchUrl(address);
        var isLocal = address.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                      || address.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                      // `devbox:3000` のような社内・開発サーバー（ドット無しホスト＋ポート）も
                      // ホストとして扱う。ドット判定だけだと検索へ流れて、どこへも行けなくなる。
                      || (!address.Contains('.') && HasPortSuffix(address));
        // ホスト名に見えないものは検索語として扱う。ドットの無い一語（「loomo」等）も——
        // https://loomo へ行っても名前が引けず、ただの失敗ページになる。
        if (!isLocal && !address.Contains('.'))
            return SearchUrl(address);
        // 名前が引ける保証のない社内ホストは http：この形（ドット無し＋ポート）で待っているのは
        // ほぼ開発サーバーで、https だと証明書以前に接続できない。
        return (isLocal ? "http://" : "https://") + address;
    }

    /// <summary>アドレスを検索語として扱うときの遷移先。</summary>
    private static string SearchUrl(string address)
        => $"https://www.google.com/search?q={Uri.EscapeDataString(address)}";

    /// <summary><c>ホスト:数字</c>（パス・クエリが続いてもよい）の形か。</summary>
    private static bool HasPortSuffix(string address)
    {
        var colon = address.IndexOf(':');
        if (colon <= 0)
            return false;
        var rest = address[(colon + 1)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var port = end < 0 ? rest : rest[..end];
        return port.Length is > 0 and <= 5 && port.All(char.IsAsciiDigit);
    }

    /// <summary>アドレス欄にそのまま渡してよいスキーム（それ以外の "xxx:" はホスト名か検索語として扱う）。</summary>
    private static readonly HashSet<string> KnownSchemes = new(StringComparer.OrdinalIgnoreCase) {
        "http", "https", "file", "about", "data", "view-source", "ftp", "mailto", "edge", "chrome",
    };

    /// <summary>先頭の <c>スキーム:</c> 部分（無ければ空文字）。</summary>
    private static string SchemeOf(string address)
    {
        var colon = address.IndexOf(':');
        return colon > 0 ? address[..colon] : "";
    }

    /// <summary>ローカルパスなら <c>file:///…</c> を返す（そうでなければ null）。</summary>
    private static string? TryLocalPathUri(string address)
    {
        try
        {
            return Path.IsPathRooted(address) && !address.StartsWith('/')
                ? new Uri(address).AbsoluteUri
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException)
        {
            return null;   // パスに見えて Uri へ載らないものは、後段の検索語／ホスト名の判定に任せる
        }
    }

    internal static void RestoreEditor(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.FilePath) && File.Exists(snapshot.FilePath))
        {
            editor.LoadFile(snapshot.FilePath);
            if (!snapshot.IsModified)
            {
                RestoreEditorViewState(editor, snapshot);
                return;
            }
        }
        if (snapshot.IsModified || string.IsNullOrWhiteSpace(snapshot.FilePath))
        {
            editor.SetText(snapshot.LoadText());
            RestoreEditorViewState(editor, snapshot);
            return;
        }
        editor.SetText(string.Empty);
    }

    internal static EditorTabSnapshot CaptureEditorTab(EditorTab tab, Guid? activeTabId)
    {
        var isActive = tab.Id == activeTabId;
        if (!tab.IsRealized && tab.Pending is { } pending)
        {
            return new EditorTabSnapshot
            {
                Id = tab.Id,
                FilePath = pending.FilePath,
                Text = pending.Text,
                DeferredTextPath = pending.DeferredTextPath,
                Title = pending.Title,
                IsModified = pending.IsModified,
                IsActive = isActive,
                CaretLine = pending.CaretLine,
                CaretColumn = pending.CaretColumn,
                ScrollRatio = pending.ScrollRatio
            };
        }

        var editor = tab.Control;
        return new EditorTabSnapshot
        {
            Id = tab.Id,
            FilePath = editor.FilePath,
            Text = editor.Text,
            Title = string.IsNullOrWhiteSpace(editor.FilePath) ? "Untitled" : Path.GetFileName(editor.FilePath),
            IsModified = editor.IsModified,
            IsActive = isActive,
            CaretLine = editor.Caret.Line,
            CaretColumn = editor.Caret.Column,
            ScrollRatio = editor.VerticalScrollRatio
        };
    }

    private static void RestoreEditorViewState(VimEditorControl editor, EditorTabSnapshot snapshot)
    {
        // 0 行・0 列、先頭スクロールも明示的な保存状態。現在値がたまたま初期値と同じことへ依存しない。
        editor.NavigateTo(Math.Max(0, snapshot.CaretLine), Math.Max(0, snapshot.CaretColumn));
        if (snapshot.ScrollRatio is { } ratio && double.IsFinite(ratio))
            editor.Dispatcher.BeginInvoke(
                new Action(() => editor.ScrollToVerticalRatio(Math.Clamp(ratio, 0, 1))), DispatcherPriority.Loaded);
    }
}
