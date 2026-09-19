using System.Diagnostics;
using System.Text.Json;
using sk0ya.Loomo.Core.Abstractions;

namespace sk0ya.Loomo.CSharp.Projects;

/// <summary>dotnet msbuild の実評価結果を JSON で取り込む。MSBuildWorkspaceを機能ごとに乱立させない。</summary>
public sealed class MsBuildProjectEvaluator : IProjectEvaluator
{
    private static readonly string[] Properties = ["TargetFramework", "TargetFrameworks", "DefineConstants", "LangVersion",
        "Nullable", "ProjectAssetsFile", "AssemblyName", "IntermediateOutputPath"];
    private static readonly string[] Items = ["Compile", "ProjectReference", "Analyzer", "AdditionalFiles", "None", "PackageReference", "ReferencePath"];

    public async Task<ProjectEvaluation> EvaluateAsync(string projectPath, string? targetFramework,
        CancellationToken cancellationToken = default)
        => await EvaluateAsync(projectPath, targetFramework, null, cancellationToken);

    public async Task<ProjectEvaluation> EvaluateAsync(string projectPath, string? targetFramework,
        string? configuration, CancellationToken cancellationToken = default)
    {
        // まずdesign-time buildまで走らせる。失敗したら評価だけに落として、少なくとも作成済みの
        // Compile 項目は返す（生成ソースと参照は欠ける）。
        var (exitCode, stdout, stderr) = await RunAsync(
            projectPath, targetFramework, configuration, designTimeBuild: true, cancellationToken);
        if (exitCode != 0)
            (exitCode, stdout, stderr) = await RunAsync(
                projectPath, targetFramework, configuration, designTimeBuild: false, cancellationToken);
        if (exitCode != 0)
            throw new InvalidOperationException($"MSBuild評価に失敗しました ({exitCode}): {stderr.Trim()}");

        try
        {
            var evaluation = Parse(stdout, projectPath);
            evaluation = AddPackageAnalyzers(evaluation, projectPath, targetFramework);
            return await AddProjectReferenceAnalyzersAsync(
                evaluation, projectPath, targetFramework, configuration, cancellationToken);
        }
        catch (JsonException ex) { throw new InvalidOperationException("MSBuild評価結果をJSONとして読めません。", ex); }
    }

    /// <summary>
    /// <c>dotnet msbuild</c> を1回走らせる。
    ///
    /// <para><paramref name="designTimeBuild"/> が要るのは、<c>-getProperty</c>／<c>-getItem</c> だけを渡すと
    /// MSBuild が<b>評価しかせずターゲットを実行しない</b>ためで、そこで欠けるものが2つある。
    /// ひとつは <c>@(ReferencePath)</c>——<c>ResolveAssemblyReferences</c> が作るので必ず空になり、
    /// 意味解析（診断・リファクタリング）が「このプロジェクトの参照」を知らないまま走る。
    /// もうひとつは<b>生成ソース</b>——XAMLの <c>*.g.cs</c>、<c>AssemblyInfo.cs</c>、global usings は
    /// プロジェクトのファイル一覧ではなく、<c>Compile</c> までのターゲットが <c>@(Compile)</c> へ<b>足す</b>。
    /// これが欠けると <c>x:Name</c> のフィールドと <c>InitializeComponent</c> を宣言する partial half が
    /// Compilation に入らず、コードビハインド全体がCS0103（名前が存在しません）の誤検出になる。</para>
    ///
    /// <para>走らせるターゲットが <c>ResolveReferences</c> ではなく <c>Compile</c> なのはそのため。
    /// <c>MarkupCompilePass1</c> のような生成ターゲットを名指しすると、Loomo が「どの生成ターゲットが要るか」を
    /// プロジェクト種別ごとに数え上げることになり、しかもWPF以外には存在しないターゲット名なので
    /// 非WPFプロジェクトが軒並み MSB4057 で失敗する。<c>Compile</c> は全プロジェクトに在り、
    /// 各SDKが自分の生成ターゲットをその前段へ繋いでいる。
    /// コンパイル自体は要らないので <c>SkipCompilerExecution</c>／<c>BuildProjectReferences=false</c> を付け、
    /// 参照プロジェクトはビルドせず TargetPath だけ解決させる（IDE の design-time build と同じ形）。</para>
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string projectPath, string? targetFramework, string? configuration,
        bool designTimeBuild, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(projectPath);
        AddSingleProcessSwitches(process.StartInfo);
        if (designTimeBuild)
        {
            process.StartInfo.ArgumentList.Add("/t:Compile");
            process.StartInfo.ArgumentList.Add("/p:BuildProjectReferences=false");
            process.StartInfo.ArgumentList.Add("/p:SkipCompilerExecution=true");
        }
        process.StartInfo.ArgumentList.Add("/getProperty:" + string.Join(',', Properties));
        process.StartInfo.ArgumentList.Add("/getItem:" + string.Join(',', Items));
        process.StartInfo.ArgumentList.Add("/p:Configuration=" +
            (string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration));
        process.StartInfo.ArgumentList.Add("/p:DesignTimeBuild=true");
        process.StartInfo.ArgumentList.Add("/nologo");
        if (!string.IsNullOrWhiteSpace(targetFramework))
            process.StartInfo.ArgumentList.Add("/p:TargetFramework=" + targetFramework);

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;
        try
        {
            process.Start();
            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }

            if (stdoutTask is not null)
            {
                try { await stdoutTask; } catch (Exception) { }
            }
            if (stderrTask is not null)
            {
                try { await stderrTask; } catch (Exception) { }
            }
            try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>MSBuildを<b>このプロセスだけ</b>で走らせる。
    ///
    /// <para><c>dotnet msbuild</c> は既定で <c>-maxcpucount</c>＋node reuse なので、ワーカーノードを
    /// 別プロセスとして起こし、それをビルド後も15分間常駐させる。そのノードは親から
    /// <b>リダイレクトした標準出力のハンドルを継承する</b>ので、msbuild本体が終了しても
    /// <c>ReadToEndAsync</c> が返らない——評価1回がノードの寿命ぶん止まる（design-time buildへ
    /// 広げた時点で実際に13分ハングした）。プロジェクト1つの評価に並列ノードは要らないので、
    /// 単一プロセスで走らせて常駐ノードを作らせない。並列化はプロジェクト単位で
    /// <see cref="SolutionModelService"/> が既に持っている。</para></summary>
    private static void AddSingleProcessSwitches(ProcessStartInfo startInfo)
    {
        startInfo.ArgumentList.Add("/m:1");
        startInfo.ArgumentList.Add("/nodeReuse:false");
    }

    private static ProjectEvaluation Parse(string json, string projectPath)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var properties = root.TryGetProperty("Properties", out var p) ? p : default;
        string? Property(string name) => properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var v)
            ? v.GetString() : null;
        var items = root.TryGetProperty("Items", out var i) ? i : default;
        IReadOnlyList<ProjectItemEvaluation> ReadItems(string name)
        {
            if (items.ValueKind != JsonValueKind.Object || !items.TryGetProperty(name, out var values)
                || values.ValueKind != JsonValueKind.Array) return Array.Empty<ProjectItemEvaluation>();
            return values.EnumerateArray().Select(item =>
            {
                if (item.ValueKind == JsonValueKind.String) return new ProjectItemEvaluation(item.GetString() ?? "");
                var include = item.TryGetProperty("Identity", out var id) ? id.GetString() ?? "" : "";
                var fullPath = item.TryGetProperty("FullPath", out var full) ? full.GetString() : null;
                var link = item.TryGetProperty("Link", out var l) ? l.GetString() : null;
                var outputItemType = item.TryGetProperty("OutputItemType", out var output)
                    ? output.GetString() : null;
                bool? referenceOutputAssembly = item.TryGetProperty("ReferenceOutputAssembly", out var reference)
                    && bool.TryParse(reference.GetString(), out var parsedReference)
                    ? parsedReference : null;
                return new ProjectItemEvaluation(include, fullPath, link,
                    outputItemType, referenceOutputAssembly);
            }).Where(x => x.Include.Length > 0).ToList();
        }
        return new ProjectEvaluation(Property("TargetFramework"), Property("TargetFrameworks"),
            Property("DefineConstants"), Property("LangVersion"),
            MarkGenerated(ReadItems("Compile"), projectPath, Property("IntermediateOutputPath")),
            ReadItems("ProjectReference"), ReadItems("Analyzer"), ReadItems("AdditionalFiles"),
            ReadItems("None"), ReadTestProject(), ReadItems("PackageReference"), ReadItems("ReferencePath"),
            Property("ProjectAssetsFile"), Property("Nullable"), Property("AssemblyName"));

        bool ReadTestProject()
        {
            var marker = Property("IsTestProject");
            return string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase)
                || ReadItems("PackageReference").Any(i => i.Include.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>design-time buildが<c>@(Compile)</c>へ足した<b>生成ソース</b>に印を付ける。
    /// 判定は「プロジェクトの中間出力（<c>$(IntermediateOutputPath)</c>）の下にあるか」——
    /// <c>*.g.cs</c>のような名前や<c>obj</c>という綴りを当てにしない。SDKごとに生成ターゲットも
    /// 出力先の綴りも違い、中間出力はMSBuild自身が知っている唯一の正本だから。
    ///
    /// <para>印が要るのは、これらが<b>コンパイラには必要でユーザーには見せない</b>ファイルだから。
    /// 意味解析（<see cref="CSharpWorkspaceSourceLoader"/>）は全部を読み、Solution Explorerの一覧・
    /// テスト探索・Fix Allの書き換え対象は
    /// <see cref="TargetFrameworkModel.AuthoredCompileFiles"/> だけを見る。</para></summary>
    internal static IReadOnlyList<ProjectItemEvaluation> MarkGenerated(
        IReadOnlyList<ProjectItemEvaluation> items, string projectPath, string? intermediateOutputPath)
    {
        if (string.IsNullOrWhiteSpace(intermediateOutputPath)) return items;
        string projectDirectory;
        string intermediateDirectory;
        try
        {
            projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            intermediateDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(projectDirectory, intermediateOutputPath)));
        }
        catch (ArgumentException) { return items; }

        return items.Select(item =>
            item.IsGenerated || !IsUnder(intermediateDirectory, projectDirectory, item)
                ? item
                : item with { IsGenerated = true }).ToArray();
    }

    /// <summary>項目が指定ディレクトリの下にあるか。相対Includeはプロジェクトの場所を基準に解決し
    /// （カレントディレクトリではない）、比較は区切り文字まで含める——前方一致だけだと
    /// <c>obj</c> の判定が <c>obj2</c> のような兄弟ディレクトリを取り込む。</summary>
    private static bool IsUnder(string directory, string projectDirectory, ProjectItemEvaluation item)
    {
        var candidate = item.FullPath ?? item.Include;
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(projectDirectory, candidate));
            return full.Length > directory.Length &&
                   full.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                   (full[directory.Length] == Path.DirectorySeparatorChar ||
                    full[directory.Length] == Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>NuGetの依存パッケージAnalyzerは通常のMSBuild評価だけでは@(Analyzer)に現れない。
    /// project.assets.jsonの実体を同じ評価結果へ追加し、IDE診断とBuildのAnalyzer集合を揃える。</summary>
    private static ProjectEvaluation AddPackageAnalyzers(
        ProjectEvaluation evaluation, string projectPath, string? requestedTargetFramework)
    {
        var assetsPath = evaluation.ProjectAssetsFile;
        if (string.IsNullOrWhiteSpace(assetsPath))
            assetsPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "obj", "project.assets.json");
        if (!File.Exists(assetsPath)) return evaluation;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            if (!document.RootElement.TryGetProperty("targets", out var targets) ||
                !document.RootElement.TryGetProperty("libraries", out var libraries)) return evaluation;
            var targetName = requestedTargetFramework ?? evaluation.TargetFramework;
            if (string.IsNullOrWhiteSpace(targetName) || !targets.TryGetProperty(targetName, out var target)) return evaluation;

            var packageFolders = GetPackageFolders(evaluation, document);
            var analyzers = evaluation.Analyzers.ToList();
            foreach (var dependency in target.EnumerateObject())
            {
                if (!libraries.TryGetProperty(dependency.Name, out var library) ||
                    !library.TryGetProperty("path", out var packagePathElement)) continue;
                var packagePath = packagePathElement.GetString();
                if (string.IsNullOrWhiteSpace(packagePath) || !library.TryGetProperty("files", out var files) ||
                    files.ValueKind != JsonValueKind.Array) continue;
                foreach (var file in files.EnumerateArray())
                {
                    var relative = file.GetString();
                    if (relative is null || !relative.StartsWith("analyzers/dotnet/cs/", StringComparison.OrdinalIgnoreCase) ||
                        !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        relative.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) ||
                        relative.Contains("CodeFixes", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var folder in packageFolders)
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(folder, packagePath, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (File.Exists(fullPath)) analyzers.Add(new ProjectItemEvaluation(fullPath, fullPath));
                    }
                }
            }
            return evaluation with { Analyzers = analyzers
                .GroupBy(item => item.FullPath ?? item.Include, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray() };
        }
        catch (IOException) { return evaluation; }
        catch (JsonException) { return evaluation; }
    }

    private static IReadOnlyList<string> GetPackageFolders(ProjectEvaluation evaluation, JsonDocument document)
    {
        var folders = new List<string>();
        if (document.RootElement.TryGetProperty("packageFolders", out var assetFolders) &&
            assetFolders.ValueKind == JsonValueKind.Object)
            folders.AddRange(assetFolders.EnumerateObject().Select(property => property.Name));
        if (folders.Count == 0)
        {
            var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            folders.Add(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
                : configured);
        }
        return folders.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>ProjectReferenceのOutputItemType=Analyzerを、実際の参照先TargetPathへ解決する。
    /// DesignTimeBuildの@(Analyzer)にはプロジェクト生成Source Generatorが現れないため、参照先を
    /// 軽量なgetPropertyで評価する。解決できない／未ビルドのDLLは通常Analyzerと同様に読み飛ばす。</summary>
    private static async Task<ProjectEvaluation> AddProjectReferenceAnalyzersAsync(
        ProjectEvaluation evaluation,
        string projectPath,
        string? targetFramework,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var analyzerReferences = evaluation.ProjectReferences
            .Where(reference => string.Equals(reference.OutputItemType, "Analyzer",
                StringComparison.OrdinalIgnoreCase))
            .Select(reference => ResolveItemPath(projectPath, reference))
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (analyzerReferences.Length == 0) return evaluation;

        var analyzers = evaluation.Analyzers.ToList();
        foreach (var analyzerProject in analyzerReferences)
        {
            var targetPath = await ResolveTargetPathAsync(
                analyzerProject, targetFramework: null, configuration, cancellationToken);
            if (targetPath is not null && File.Exists(targetPath))
                analyzers.Add(new ProjectItemEvaluation(targetPath, targetPath));
        }

        return evaluation with
        {
            Analyzers = analyzers
                .GroupBy(item => item.FullPath ?? item.Include, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray(),
        };
    }

    private static async Task<string?> ResolveTargetPathAsync(
        string projectPath,
        string? targetFramework,
        string? configuration,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("msbuild");
        process.StartInfo.ArgumentList.Add(projectPath);
        AddSingleProcessSwitches(process.StartInfo);
        // MSBuild emits a bare scalar for a single requested property. Request a harmless
        // second property so the result is always the JSON envelope parsed below.
        process.StartInfo.ArgumentList.Add("/getProperty:TargetPath,AssemblyName");
        process.StartInfo.ArgumentList.Add("/p:Configuration=" +
            (string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration));
        process.StartInfo.ArgumentList.Add("/p:DesignTimeBuild=true");
        process.StartInfo.ArgumentList.Add("/nologo");
        if (!string.IsNullOrWhiteSpace(targetFramework))
            process.StartInfo.ArgumentList.Add("/p:TargetFramework=" + targetFramework);

        Task<string>? stdoutTask = null;
        try
        {
            process.Start();
            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) return null;
            using var document = JsonDocument.Parse(await stdoutTask);
            if (!document.RootElement.TryGetProperty("Properties", out var properties) ||
                !properties.TryGetProperty("TargetPath", out var target) ||
                target.ValueKind != JsonValueKind.String) return null;
            var path = target.GetString();
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (stdoutTask is not null)
            {
                try { await stdoutTask; } catch (Exception) { }
            }
            try { await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            throw;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? ResolveItemPath(string projectPath, ProjectItemEvaluation item)
    {
        var candidate = item.FullPath ?? item.Include;
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (Path.IsPathRooted(candidate)) return Path.GetFullPath(candidate);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, candidate));
    }
}
