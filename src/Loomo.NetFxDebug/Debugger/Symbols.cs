using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ClrDebug;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

/// <summary>ソース上の 1 区間と、それが始まる IL オフセット。</summary>
internal sealed class SequencePointInfo
{
    /// <summary>コンパイラが「行に対応しない」区間に付ける印（0xFEEFEE）。</summary>
    public const int HiddenLine = 0xFEEFEE;

    public SequencePointInfo(int offset, int startLine, int startColumn, int endLine, int endColumn, string document)
    {
        Offset = offset;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
        Document = document;
        DocumentKey = ModuleSymbols.NormalizePath(document);
    }

    public int Offset { get; }
    public int StartLine { get; }
    public int StartColumn { get; }
    public int EndLine { get; }
    public int EndColumn { get; }
    public string Document { get; }
    /// <summary>比較用に正規化したパス（大文字小文字は比較側で無視する）。</summary>
    public string DocumentKey { get; }
    public bool IsHidden => StartLine == HiddenLine || StartLine == 0;
}

/// <summary>ローカル変数 1 つ（スロット番号と、有効な IL 区間）。</summary>
internal sealed class LocalInfo
{
    public LocalInfo(string name, int slot, int startOffset, int endOffset)
    {
        Name = name;
        Slot = slot;
        StartOffset = startOffset;
        EndOffset = endOffset;
    }

    public string Name { get; }
    public int Slot { get; }
    public int StartOffset { get; }
    public int EndOffset { get; }
}

/// <summary>1 メソッド分のシンボル。<see cref="Points"/> は IL オフセット順。</summary>
internal sealed class MethodSymbols
{
    public MethodSymbols(int token, SequencePointInfo[] points, LocalInfo[] locals)
    {
        Token = token;
        Points = points;
        Locals = locals;
        var visible = points.Where(p => !p.IsHidden).ToArray();
        MinLine = visible.Length == 0 ? 0 : visible.Min(p => p.StartLine);
        MaxLine = visible.Length == 0 ? 0 : visible.Max(p => p.EndLine);
    }

    public int Token { get; }
    public SequencePointInfo[] Points { get; }
    public LocalInfo[] Locals { get; }
    public int MinLine { get; }
    public int MaxLine { get; }

    /// <summary>IL オフセットを含むシーケンスポイント（隠しを含む）。先頭より前なら null。</summary>
    public SequencePointInfo? At(int ilOffset)
    {
        SequencePointInfo? found = null;
        foreach (var point in Points)
        {
            if (point.Offset > ilOffset) break;
            found = point;
        }
        return found;
    }

    /// <summary>IL オフセットに対応する「見える」行（隠し区間なら手前の見える区間まで遡る）。</summary>
    public SequencePointInfo? VisibleAt(int ilOffset)
    {
        SequencePointInfo? found = null;
        foreach (var point in Points)
        {
            if (point.Offset > ilOffset) break;
            if (!point.IsHidden) found = point;
        }
        return found;
    }

    /// <summary>ステップの範囲：今いる区間の開始から次の見える区間の手前まで（途中の隠し区間も含める——
    /// 含めないと、行の途中の隠し区間でステップが止まり、同じ行でもう一度止まったように見える）。</summary>
    public (int Start, int End)? StepRange(int ilOffset)
    {
        var index = -1;
        for (var i = 0; i < Points.Length; i++)
        {
            if (Points[i].Offset > ilOffset) break;
            index = i;
        }
        if (index < 0) return null;
        var start = Points[index].Offset;
        var end = int.MaxValue;
        for (var i = index + 1; i < Points.Length; i++)
        {
            if (Points[i].IsHidden) continue;
            end = Points[i].Offset;
            break;
        }
        return (start, end);
    }
}

/// <summary>1 モジュール分のシンボル（ポータブル PDB／埋め込み PDB／Windows PDB のどれから読んでも同じ形）。</summary>
internal sealed class ModuleSymbols
{
    private readonly Dictionary<int, MethodSymbols> _methods;
    private readonly Dictionary<string, List<MethodSymbols>> _byDocument = new(StringComparer.OrdinalIgnoreCase);

    public ModuleSymbols(IEnumerable<MethodSymbols> methods, string kind, int userEntryPoint = 0,
        IReadOnlyDictionary<int, int>? kickoffToMoveNext = null)
    {
        _methods = methods.ToDictionary(m => m.Token);
        Kind = kind;
        UserEntryPoint = userEntryPoint;
        KickoffToMoveNext = kickoffToMoveNext ?? new Dictionary<int, int>();
        foreach (var method in _methods.Values)
        {
            foreach (var key in method.Points.Where(p => !p.IsHidden).Select(p => p.DocumentKey).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_byDocument.TryGetValue(key, out var list)) _byDocument[key] = list = new List<MethodSymbols>();
                list.Add(method);
            }
        }
    }

    /// <summary>どの形式から読んだか（モジュール一覧の表示用）。</summary>
    public string Kind { get; }

    public IEnumerable<string> Documents => _byDocument.Keys;

    /// <summary>PDB が記録しているユーザーのエントリポイント（async Main なら <c>&lt;Main&gt;</c> ではなく Main）。0 なら不明。</summary>
    public int UserEntryPoint { get; }

    /// <summary>async／iterator の元メソッド → 本体（状態機械の MoveNext）。</summary>
    public IReadOnlyDictionary<int, int> KickoffToMoveNext { get; }

    public MethodSymbols? Method(int token) => _methods.TryGetValue(token, out var m) ? m : null;

    /// <summary>ソースの行に置くブレークポイントの位置を決める。行にコードが無ければ同じメソッド内の次の行へずらす
    /// （VS と同じ）。1 行にラムダと外側の文が同居するときは、行の最も左で始まる文を選ぶ（外側の文が先に来る）。</summary>
    public (MethodSymbols Method, SequencePointInfo Point)? Resolve(string documentPath, int line)
    {
        var key = NormalizePath(documentPath);
        if (!_byDocument.TryGetValue(key, out var methods))
        {
            // PDB が別の場所でビルドされたとき：ファイル名が一意に一致すればそれを使う。
            var name = Path.GetFileName(documentPath);
            var matches = _byDocument.Where(p => string.Equals(Path.GetFileName(p.Key), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) return null;
            key = matches[0].Key;
            methods = matches[0].Value;
        }

        List<(MethodSymbols Method, SequencePointInfo Point)> PointsOf(IEnumerable<MethodSymbols> source)
            => source.SelectMany(m => m.Points
                    .Where(p => !p.IsHidden && p.StartLine >= line &&
                                string.Equals(p.DocumentKey, key, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (m, p)))
                .ToList();

        var containing = methods.Where(m => m.MinLine <= line && line <= m.MaxLine).ToList();
        var candidates = PointsOf(containing.Count > 0 ? containing : methods);
        if (candidates.Count == 0) return null;
        var target = candidates.Min(c => c.Point.StartLine);
        return candidates
            .Where(c => c.Point.StartLine == target)
            .OrderBy(c => c.Point.StartColumn)
            .ThenBy(c => c.Method.MaxLine - c.Method.MinLine)
            .ThenBy(c => c.Point.Offset)
            .First();
    }

    /// <summary>メソッドの中で、指定行から始まる最初の見える区間（次のステートメントの設定用）。</summary>
    public SequencePointInfo? PointOnLine(int methodToken, int line)
        => Method(methodToken)?.Points.Where(p => !p.IsHidden && p.StartLine == line).OrderBy(p => p.Offset).FirstOrDefault();

    public static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}

/// <summary>モジュールのシンボルを読む。ポータブル（隣の .pdb／埋め込み）は System.Reflection.Metadata、
/// Windows 形式（旧形式プロジェクトの既定 <c>DebugType=full</c>）は .NET Framework 同梱の diasymreader で読む。</summary>
internal static class SymbolLoader
{
    /// <summary>.NET Framework 同梱の diasymreader.dll が登録している SxS バインダー。</summary>
    private static readonly Guid CorSymBinderSxS = new("0A29FF9E-7F9C-4437-8B11-F424491E3931");

    public static ModuleSymbols? Load(string modulePath, CorDebugModule module, Action<string> log)
    {
        if (!File.Exists(modulePath)) return null;
        try
        {
            var portable = TryLoadPortable(modulePath);
            if (portable is not null) return portable;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            log($"ポータブル PDB を読めませんでした（{Path.GetFileName(modulePath)}）: {ex.Message}");
        }

        var pdb = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdb)) return null;
        try
        {
            return LoadWindowsPdb(modulePath, module);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or DebugException or ArgumentException)
        {
            log($"Windows 形式の PDB を読めませんでした（{Path.GetFileName(pdb)}）: {ex.Message}");
            return null;
        }
    }

    private static ModuleSymbols? TryLoadPortable(string modulePath)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(modulePath));
        using var pe = new PEReader(stream);
        if (!pe.TryOpenAssociatedPortablePdb(modulePath, OpenPdb, out var provider, out _) || provider is null)
            return null;
        using (provider)
        {
            var reader = provider.GetMetadataReader();
            var methods = new List<MethodSymbols>();
            foreach (var handle in reader.MethodDebugInformation)
            {
                var info = reader.GetMethodDebugInformation(handle);
                if (info.SequencePointsBlob.IsNil) continue;
                var definition = handle.ToDefinitionHandle();
                var token = MetadataTokens.GetToken(definition);
                var points = new List<SequencePointInfo>();
                foreach (var point in info.GetSequencePoints())
                {
                    var document = reader.GetString(reader.GetDocument(point.Document).Name);
                    points.Add(point.IsHidden
                        ? new SequencePointInfo(point.Offset, SequencePointInfo.HiddenLine, 0, SequencePointInfo.HiddenLine, 0, document)
                        : new SequencePointInfo(point.Offset, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn, document));
                }
                var locals = new List<LocalInfo>();
                foreach (var scopeHandle in reader.GetLocalScopes(definition))
                {
                    var scope = reader.GetLocalScope(scopeHandle);
                    foreach (var variableHandle in scope.GetLocalVariables())
                    {
                        var variable = reader.GetLocalVariable(variableHandle);
                        if ((variable.Attributes & LocalVariableAttributes.DebuggerHidden) != 0) continue;
                        locals.Add(new LocalInfo(reader.GetString(variable.Name), variable.Index,
                            scope.StartOffset, scope.EndOffset));
                    }
                }
                methods.Add(new MethodSymbols(token, points.OrderBy(p => p.Offset).ToArray(), locals.ToArray()));
            }
            var kickoff = new Dictionary<int, int>();
            // async／iterator の元メソッド（Main 等）→ 本体の MoveNext。stopAtEntry が async Main で本体に止まるため。
            foreach (var moveNext in methods.Select(m => m.Token))
            {
                var definition = (MethodDefinitionHandle)MetadataTokens.EntityHandle(moveNext);
                var debug = reader.GetMethodDebugInformation(definition);
                var kick = debug.GetStateMachineKickoffMethod();
                if (!kick.IsNil) kickoff[MetadataTokens.GetToken(kick)] = moveNext;
            }
            var entry = reader.DebugMetadataHeader?.EntryPoint is { IsNil: false } e ? MetadataTokens.GetToken(e) : 0;
            return new ModuleSymbols(methods, "ポータブル PDB", entry, kickoff);
        }

        static Stream? OpenPdb(string path)
            => File.Exists(path) ? new MemoryStream(File.ReadAllBytes(path)) : null;
    }

    private static ModuleSymbols LoadWindowsPdb(string modulePath, CorDebugModule module)
    {
        var binderType = Type.GetTypeFromCLSID(CorSymBinderSxS, throwOnError: true)!;
        var binder = new SymUnmanagedBinder((ISymUnmanagedBinder)Activator.CreateInstance(binderType)!);
        var importer = (IMetaDataImport)module.GetMetaDataInterface(typeof(IMetaDataImport).GUID);
        var reader = binder.GetReaderForFile(importer, modulePath, Path.GetDirectoryName(modulePath));

        var documentUrls = new Dictionary<IntPtr, string>();
        var methods = new List<MethodSymbols>();
        foreach (var token in MethodTokens(modulePath))
        {
            if (reader.TryGetMethod(token, out var method) != HRESULT.S_OK || method is null) continue;
            var count = method.SequencePointCount;
            if (count <= 0) continue;
            var raw = method.GetSequencePoints(count);
            var points = new List<SequencePointInfo>(count);
            for (var i = 0; i < raw.offsets.Length; i++)
            {
                var document = DocumentUrl(raw.documents[i], documentUrls);
                points.Add(new SequencePointInfo(raw.offsets[i], raw.lines[i], raw.columns[i],
                    raw.endLines[i], raw.endColumns[i], document));
            }
            var locals = new List<LocalInfo>();
            CollectLocals(method.RootScope, locals);
            methods.Add(new MethodSymbols(token, points.OrderBy(p => p.Offset).ToArray(), locals.ToArray()));
        }
        var userEntry = 0;
        try { userEntry = reader.UserEntryPoint; }
        catch (Exception ex) when (ex is COMException or DebugException) { }
        return new ModuleSymbols(methods, "Windows PDB", userEntry);
    }

    private static string DocumentUrl(ISymUnmanagedDocument document, Dictionary<IntPtr, string> cache)
    {
        var unknown = Marshal.GetIUnknownForObject(document);
        try
        {
            if (cache.TryGetValue(unknown, out var url)) return url;
            url = new SymUnmanagedDocument(document).URL;
            cache[unknown] = url;
            return url;
        }
        finally { Marshal.Release(unknown); }
    }

    private static void CollectLocals(SymUnmanagedScope? scope, List<LocalInfo> locals)
    {
        if (scope is null) return;
        foreach (var variable in scope.Locals)
        {
            var name = variable.Name;
            // コンパイラの一時変数（CS$…）は見せない。
            if (string.IsNullOrEmpty(name) || name.StartsWith("CS$", StringComparison.Ordinal)) continue;
            if (variable.AddressKind != CorSymAddrKind.ADDR_IL_OFFSET) continue;
            locals.Add(new LocalInfo(name, variable.AddressField1, scope.StartOffset, scope.EndOffset));
        }
        foreach (var child in scope.Children) CollectLocals(child, locals);
    }

    private static IEnumerable<int> MethodTokens(string modulePath)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(modulePath));
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.MethodDefinitions.Select(h => MetadataTokens.GetToken(h)).ToList();
    }
}
