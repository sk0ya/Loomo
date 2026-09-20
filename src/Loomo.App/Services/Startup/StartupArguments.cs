using System.IO;

namespace sk0ya.Loomo.App.Services;

/// <summary>起動引数からワークスペースフォルダーを取り出す。</summary>
internal static class StartupArguments
{
    private const string WorkspaceOption = "--workspace";

    /// <summary>
    /// <c>--workspace &lt;folder&gt;</c> と、互換性のための単独フォルダー引数を受け付ける。
    /// 存在しないパスやファイル引数は起動ワークスペースにしない。
    /// </summary>
    public static string? TryGetWorkspaceFolder(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? candidate = null;

            if (string.Equals(arg, WorkspaceOption, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                    candidate = args[++i];
            }
            else if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                candidate = arg;
            }

            if (candidate is null || !Directory.Exists(candidate))
                continue;

            try
            {
                return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // 次の候補があればそちらを試す。
            }
            catch (NotSupportedException)
            {
                // 次の候補があればそちらを試す。
            }
        }

        return null;
    }

    /// <summary>JumpTask の Arguments に入れるワークスペース引数。</summary>
    public static string FormatWorkspaceArgument(string folder)
        => $"{WorkspaceOption} {QuoteWindowsArgument(folder)}";

    // Windows のコマンドラインでは、引用符の直前／末尾のバックスラッシュを
    // 2 倍しないと、ルートフォルダー (C:\) の末尾の \ が引用符をエスケープする。
    private static string QuoteWindowsArgument(string value)
    {
        var trailingSlashes = 0;
        for (var i = value.Length - 1; i >= 0 && value[i] == '\\'; i--)
            trailingSlashes++;

        return $"\"{value}{new string('\\', trailingSlashes)}\"";
    }
}
