using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace sk0ya.Loomo.App.Services;

/// <summary>Windows に Loomo を「既定のアプリの候補」として登録する／外す（HKCU のみ・管理者権限不要）。
///
/// <para><b>候補になるところまでしかできない。</b>Windows 8 以降、既定のアプリそのもの（UserChoice）は
/// 改ざん防止のハッシュ付きで、アプリが書き換えることはできない。ここで行うのは
/// 「プログラムから開く」の一覧・Windows の設定の既定のアプリ・既定のブラウザ候補に Loomo を載せることと、
/// フォルダーの右クリックに「Loomo で開く」を足すことまで。既定にするのは利用者が設定画面で選ぶ
/// （<see cref="OpenWindowsDefaultAppsSettings"/>）。</para>
///
/// <para>対象（<see cref="Targets"/>）は設定で個別に切り替える。<b>正本はレジストリそのもの</b>で、
/// 設定ファイルには控えない——利用者が Windows 側で消したときに表示と食い違わないように。</para>
///
/// <para>コマンドラインは <see cref="StartupArguments"/> が読める形にそろえる：ファイルは位置引数、
/// .html は <c>--browser</c>（エディタで開くファイルと区別するため）、フォルダーは <c>--workspace</c>。
/// 受け取ったプロセスは起動中の部屋へ渡して終わる（<see cref="ExternalOpenStartup"/>）。</para></summary>
[SupportedOSPlatform("windows")]
internal sealed class DefaultAppRegistration
{
    public const string AppName = "Loomo";
    public const string FileProgId = "sk0ya.Loomo.File";
    public const string HtmlProgId = "sk0ya.Loomo.Html";
    public const string UrlProgId = "sk0ya.Loomo.Url";
    private const string FolderVerb = "sk0ya.Loomo";
    private const string ClientKey = @"Clients\StartMenuInternet\" + AppName;
    private const string CapabilitiesKey = ClientKey + @"\Capabilities";

    /// <summary>設定で個別に切り替えられる対象。</summary>
    [Flags]
    public enum Targets
    {
        None = 0,
        /// <summary>テキスト・コード → エディタ。</summary>
        Text = 1,
        /// <summary>PDF・画像 → EditorSupport。</summary>
        Preview = 2,
        /// <summary>http/https と .html → ブラウザペイン（既定のブラウザ候補）。</summary>
        Web = 4,
        /// <summary>フォルダーの右クリック「Loomo で開く」→ 部屋。</summary>
        Folder = 8,
        All = Text | Preview | Web | Folder,
    }

    /// <summary>エディタ（テキスト・コード）で開くもの。</summary>
    public static IReadOnlyList<string> TextExtensions { get; } =
    [
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonc", ".ndjson", ".xml", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".env", ".editorconfig", ".gitignore", ".gitattributes", ".csv", ".tsv",
        ".cs", ".csx", ".csproj", ".sln", ".slnx", ".props", ".targets", ".vb", ".vbproj", ".fs", ".fsx",
        ".fsproj", ".xaml", ".razor", ".cshtml", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".css",
        ".scss", ".less", ".vue", ".svelte", ".py", ".rb", ".go", ".rs", ".java", ".kt", ".kts", ".c",
        ".h", ".cpp", ".hpp", ".cc", ".cxx", ".swift", ".php", ".lua", ".sh", ".bash", ".ps1", ".psm1",
        ".psd1", ".bat", ".cmd", ".sql", ".graphql", ".proto", ".mermaid", ".mmd",
    ];

    /// <summary>EditorSupport（プレビュー）で開くもの。</summary>
    public static IReadOnlyList<string> PreviewExtensions { get; } =
    [
        ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".svg",
    ];

    /// <summary>ブラウザペインで開くもの（既定のブラウザとしての関連付け）。</summary>
    public static IReadOnlyList<string> HtmlExtensions { get; } = [".html", ".htm", ".xhtml"];

    public static IReadOnlyList<string> UrlSchemes { get; } = ["http", "https"];

    private readonly RegistryKey _software;
    private readonly string _exePath;

    /// <param name="software">HKCU\Software に当たるキー。テストでは使い捨てのキーを渡す。</param>
    /// <param name="exePath">登録する実行ファイル（このプロセス自身）。</param>
    public DefaultAppRegistration(RegistryKey software, string exePath)
    {
        _software = software;
        _exePath = exePath;
    }

    public static DefaultAppRegistration ForCurrentUser()
        => new(Registry.CurrentUser.CreateSubKey(@"Software"),
               Environment.ProcessPath ?? throw new InvalidOperationException("Loomo の実行ファイルが特定できません。"));

    /// <summary>いま登録されている対象と、登録先の実行ファイル。</summary>
    public (Targets Targets, string? RegisteredPath) Inspect()
    {
        var targets = Targets.None;
        if (ReadString(CapabilitiesKey + @"\FileAssociations", TextExtensions[0]) == FileProgId) targets |= Targets.Text;
        if (ReadString(CapabilitiesKey + @"\FileAssociations", PreviewExtensions[0]) == FileProgId) targets |= Targets.Preview;
        if (ReadString(CapabilitiesKey + @"\URLAssociations", UrlSchemes[0]) == UrlProgId) targets |= Targets.Web;
        var folderCommand = ReadString($@"Classes\Directory\shell\{FolderVerb}\command", null);
        if (folderCommand is not null) targets |= Targets.Folder;

        var command = ReadString($@"Classes\{FileProgId}\shell\open\command", null)
            ?? ReadString($@"Classes\{UrlProgId}\shell\open\command", null)
            ?? folderCommand;
        return (targets, command is null ? null : ExtractExecutable(command));
    }

    /// <summary>登録先が今の Loomo か（場所が変わっていれば、関連付けは古い exe を起こす）。</summary>
    public bool IsCurrentExecutable(string? registeredPath)
        => registeredPath is null || string.Equals(registeredPath, _exePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>指定した対象だけが登録された状態にする（外したものは消す）。<see cref="Targets.None"/> は全解除。</summary>
    public void Apply(Targets targets)
    {
        RemoveAll();
        if (targets != Targets.None)
            Write(targets);
        NotifyAssociationsChanged();
    }

    public void Register() => Apply(Targets.All);

    public void Unregister() => Apply(Targets.None);

    /// <summary>Windows の設定の「既定のアプリ」を Loomo のページで開く。</summary>
    public static void OpenWindowsDefaultAppsSettings()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}") { UseShellExecute = true });

    private void Write(Targets targets)
    {
        var icon = $"\"{_exePath}\",0";
        var openFile = $"\"{_exePath}\" \"%1\"";
        var fileExtensions = new List<string>();
        if (targets.HasFlag(Targets.Text)) fileExtensions.AddRange(TextExtensions);
        if (targets.HasFlag(Targets.Preview)) fileExtensions.AddRange(PreviewExtensions);
        var web = targets.HasFlag(Targets.Web);
        IReadOnlyList<string> htmlExtensions = web ? HtmlExtensions : [];

        if (fileExtensions.Count > 0)
            WriteProgId(FileProgId, "Loomo で開くファイル", icon, openFile).Dispose();
        if (web)
        {
            WriteProgId(HtmlProgId, "Loomo HTML ドキュメント", icon,
                $"\"{_exePath}\" {StartupArguments.BrowserOption} \"%1\"").Dispose();
            using var url = WriteProgId(UrlProgId, "Loomo URL", icon, openFile);
            url.SetValue("URL Protocol", "");
        }
        foreach (var ext in fileExtensions)
            AddOpenWithProgId(ext, FileProgId);
        foreach (var ext in htmlExtensions)
            AddOpenWithProgId(ext, HtmlProgId);

        if (fileExtensions.Count > 0 || web)
            WriteCapabilities(icon, openFile, fileExtensions, htmlExtensions, web);

        if (targets.HasFlag(Targets.Folder))
        {
            // フォルダーの右クリック（フォルダーの上・フォルダーの中の余白）に「Loomo で開く」。
            WriteFolderVerb($@"Classes\Directory\shell\{FolderVerb}", icon, $"\"{_exePath}\" --workspace \"%1\"");
            WriteFolderVerb($@"Classes\Directory\Background\shell\{FolderVerb}", icon, $"\"{_exePath}\" --workspace \"%V\"");
        }
    }

    private void WriteCapabilities(
        string icon, string openFile, IReadOnlyList<string> fileExtensions, IReadOnlyList<string> htmlExtensions, bool web)
    {
        // 「プログラムから開く」の一覧に出る名前と、対応する種類。
        using (var app = _software.CreateSubKey($@"Classes\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var command = app.CreateSubKey(@"shell\open\command"))
                command.SetValue(null, openFile);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in fileExtensions.Concat(htmlExtensions))
                types.SetValue(ext, "");
        }

        // Windows の設定の「既定のアプリ」に出るための能力宣言。ブラウザ候補にもなるよう StartMenuInternet に置く。
        using (var client = _software.CreateSubKey(ClientKey))
        {
            client.SetValue(null, AppName);
            using (var defaultIcon = client.CreateSubKey("DefaultIcon"))
                defaultIcon.SetValue(null, icon);
            using (var command = client.CreateSubKey(@"shell\open\command"))
                command.SetValue(null, $"\"{_exePath}\"");
            using var caps = client.CreateSubKey("Capabilities");
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Loomo — 人間が作業する部屋。ファイルはエディタ、Web はブラウザペインで開きます。");
            caps.SetValue("ApplicationIcon", icon);
            using (var files = caps.CreateSubKey("FileAssociations"))
            {
                foreach (var ext in fileExtensions)
                    files.SetValue(ext, FileProgId);
                foreach (var ext in htmlExtensions)
                    files.SetValue(ext, HtmlProgId);
            }
            if (web)
            {
                using (var startMenu = caps.CreateSubKey("StartMenu"))
                    startMenu.SetValue("StartMenuInternet", AppName);
                using var urls = caps.CreateSubKey("URLAssociations");
                foreach (var scheme in UrlSchemes)
                    urls.SetValue(scheme, UrlProgId);
            }
        }
        using var registered = _software.CreateSubKey("RegisteredApplications");
        registered.SetValue(AppName, $@"Software\{CapabilitiesKey}");
    }

    private void RemoveAll()
    {
        foreach (var progId in new[] { FileProgId, HtmlProgId, UrlProgId })
            _software.DeleteSubKeyTree($@"Classes\{progId}", throwOnMissingSubKey: false);
        foreach (var ext in TextExtensions.Concat(PreviewExtensions))
            RemoveOpenWithProgId(ext, FileProgId);
        foreach (var ext in HtmlExtensions)
            RemoveOpenWithProgId(ext, HtmlProgId);
        _software.DeleteSubKeyTree($@"Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        _software.DeleteSubKeyTree($@"Classes\Directory\shell\{FolderVerb}", throwOnMissingSubKey: false);
        _software.DeleteSubKeyTree($@"Classes\Directory\Background\shell\{FolderVerb}", throwOnMissingSubKey: false);
        _software.DeleteSubKeyTree(ClientKey, throwOnMissingSubKey: false);
        using var registered = _software.OpenSubKey("RegisteredApplications", writable: true);
        registered?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private string ExeName => Path.GetFileName(_exePath);

    private string? ReadString(string key, string? name)
    {
        using var k = _software.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    private RegistryKey WriteProgId(string progId, string description, string icon, string command)
    {
        var key = _software.CreateSubKey($@"Classes\{progId}");
        key.SetValue(null, description);
        using (var defaultIcon = key.CreateSubKey("DefaultIcon"))
            defaultIcon.SetValue(null, icon);
        using (var open = key.CreateSubKey(@"shell\open\command"))
            open.SetValue(null, command);
        return key;
    }

    private void WriteFolderVerb(string path, string icon, string command)
    {
        using var verb = _software.CreateSubKey(path);
        verb.SetValue(null, "Loomo で開く");
        verb.SetValue("Icon", icon);
        using var cmd = verb.CreateSubKey("command");
        cmd.SetValue(null, command);
    }

    private void AddOpenWithProgId(string ext, string progId)
    {
        using var key = _software.CreateSubKey($@"Classes\{ext}\OpenWithProgids");
        key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private void RemoveOpenWithProgId(string ext, string progId)
    {
        using var key = _software.OpenSubKey($@"Classes\{ext}\OpenWithProgids", writable: true);
        key?.DeleteValue(progId, throwOnMissingValue: false);
    }

    /// <summary><c>"C:\path\app.exe" "%1"</c> の先頭（実行ファイル）を取り出す。</summary>
    internal static string ExtractExecutable(string commandLine)
    {
        var line = commandLine.Trim();
        if (line.StartsWith('"'))
        {
            var end = line.IndexOf('"', 1);
            return end > 0 ? line[1..end] : line.Trim('"');
        }
        var space = line.IndexOf(' ');
        return space > 0 ? line[..space] : line;
    }

    private static void NotifyAssociationsChanged()
    {
        const int SHCNE_ASSOCCHANGED = 0x08000000;
        const int SHCNF_IDLIST = 0;
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { /* 通知は最善努力。エクスプローラーは次の起動で読み直す */ }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
