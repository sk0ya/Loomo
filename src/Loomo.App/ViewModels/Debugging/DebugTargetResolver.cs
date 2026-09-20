using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sk0ya.Loomo.CSharp.Build;
using sk0ya.Loomo.Core.Abstractions;
using sk0ya.Loomo.Core.Debug;
using sk0ya.Loomo.CSharp.Debug;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>デバッグ起動・手動ビルドの対象解決（プロジェクト探索・任意ビルド・出力 dll 探索）をまとめたヘルパ。
/// メッセージ出力・状態文言は <see cref="IDebugSession"/> 経由でコンソール/ヘッダへ流す。</summary>
internal static class DebugTargetResolver
{
    /// <summary>ワークスペースが C#/.NET プロジェクト（.sln/.csproj）を含むか（いずれかのワークスペース
    /// フォルダーが対象）。IDE（ビルド・テスト・デバッグ）ペインの表示要否判定に使う。folders が空、または
    /// ビルド対象が 1 つも無ければ false。</summary>
    public static bool HasCSharpProject(IReadOnlyList<string> folders)
        => CSharpDebugTargetResolver.HasCSharpProject(folders);

    /// <summary>ビルド/テスト対象を解決する。各ワークスペースフォルダー直下の .sln を優先し
    /// （フォルダーの並び順）、無ければ最初に見つかった .csproj。</summary>
    public static string? FindBuildTarget(IWorkspaceService workspace, IDebugSession session)
    {
        var folders = workspace.Folders;
        if (folders.Count == 0)
        {
            session.Append(DebugOutputCategory.Important, "ワークスペースが開かれていません。");
            return null;
        }

        if (CSharpDebugTargetResolver.FindBuildTarget(folders) is { } target)
            return target;

        session.Append(DebugOutputCategory.Important, "ワークスペースに .sln/.csproj が見つかりません。");
        return null;
    }

    /// <summary>デバッグ対象（実行する .dll）を解決する。明示指定が無ければ <paramref name="explicitProjectPath"/>
    /// （起動プロジェクト選択コンボボックスの選択）を、それも無ければワークスペースの .csproj を 1 つ探し、
    /// 任意でビルドしてから出力 dll を見つける。解決できなければ null（理由はコンソールへ）。</summary>
    public static async Task<string?> ResolveProgramAsync(IWorkspaceService workspace, ITerminalService terminal,
        IDebugSession session, string targetProgram, bool buildFirst, string? explicitProjectPath = null,
        string configuration = "Debug", string? targetFramework = null)
    {
        var root = workspace.PrimaryFolder;

        // 明示指定があればそれを優先（相対はワークスペース基準）。
        if (!string.IsNullOrWhiteSpace(targetProgram))
        {
            var p = Path.IsPathRooted(targetProgram) || root is null
                ? targetProgram
                : Path.GetFullPath(Path.Combine(root, targetProgram));

            // 明示指定でも「ビルドしてから起動」は尊重する。以前はここで無条件に buildFirst を無視しており、
            // チェックが ON でもビルドされないまま古い実行対象が起動される／削除済みで「見つかりません」に
            // なるケースがあった。関連プロジェクトが分かる場合のみビルドする（分からなければ静かにスキップ、
            // 従来どおり存在チェックのみ）。
            if (buildFirst)
            {
                var proj = string.IsNullOrWhiteSpace(explicitProjectPath)
                    ? CSharpDebugTargetResolver.FindProjectNear(p)
                    : explicitProjectPath;
                if (proj is not null && File.Exists(proj) &&
                    !await BuildAsync(terminal, session, proj, configuration: configuration,
                        targetFramework: targetFramework))
                    return null;
            }

            if (File.Exists(p)) return p;
            session.Append(DebugOutputCategory.Important, $"指定された実行対象が見つかりません: {p}");
            return null;
        }

        if (root is null)
        {
            session.Append(DebugOutputCategory.Important, "ワークスペースが開かれていません。デバッグ対象を指定してください。");
            return null;
        }

        var csproj = string.IsNullOrWhiteSpace(explicitProjectPath)
            ? workspace.Folders.Select(CSharpDebugTargetResolver.FindProject).FirstOrDefault(p => p is not null)
            : explicitProjectPath;
        if (csproj is null || !File.Exists(csproj))
        {
            session.Append(DebugOutputCategory.Important,
                "ワークスペースに .csproj が見つかりません。デバッグ対象（.dll/.exe）を直接指定してください。");
            return null;
        }

        if (buildFirst && !await BuildAsync(terminal, session, csproj, configuration: configuration,
                targetFramework: targetFramework))
            return null;

        var dll = CSharpDebugTargetResolver.FindOutputDll(csproj, configuration, targetFramework);
        if (dll is null)
        {
            session.Append(DebugOutputCategory.Important,
                "ビルド出力 (.dll) が見つかりません。先にビルドするか、対象を直接指定してください。");
            return null;
        }
        return dll;
    }

    /// <summary><c>dotnet build</c> を実行し、出力をコンソールへ。成功（exit 0）なら true。</summary>
    public static async Task<bool> BuildAsync(ITerminalService terminal, IDebugSession session, string projectOrSln,
        string label = "ビルド", string configuration = "Debug", string? targetFramework = null)
    {
        session.StatusMessage = "ビルド中…";
        session.Append(DebugOutputCategory.Important, $"{label}: {Path.GetFileName(projectOrSln)}");
        var result = await CSharpBuildService.RunAsync(
            terminal, projectOrSln, configuration, CancellationToken.None, targetFramework);
        session.WriteConsole(result.Output);
        session.ReportBuildOutput(result.Output);
        if (!result.Success)
        {
            session.StatusMessage = $"ビルド失敗（{result.ExitCode}）";
            session.Append(DebugOutputCategory.Important, $"ビルドに失敗しました（終了コード {result.ExitCode}）。");
            return false;
        }
        return true;
    }

    /// <summary>ワークスペース直下、無ければ浅い再帰で最初の .csproj を探す。</summary>
    public static string? FindProject(string root)
        => CSharpDebugTargetResolver.FindProject(root);

    /// <summary>プロジェクトの <c>bin/&lt;configuration&gt;</c> 配下から <c>&lt;projName&gt;.dll</c> を新しい順に探す。</summary>
    public static string? FindOutputDll(
        string csproj, string configuration = "Debug", string? targetFramework = null)
        => CSharpDebugTargetResolver.FindOutputDll(csproj, configuration, targetFramework);
}
