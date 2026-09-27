using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ClrDebug;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

/// <summary>DAP の stackFrames 1 つ分。</summary>
internal sealed class StackFrameItem
{
    public StackFrameItem(int id, string name, string? path, int line, int column)
    {
        Id = id;
        Name = name;
        Path = path;
        Line = line;
        Column = column;
    }

    public int Id { get; }
    public string Name { get; }
    public string? Path { get; }
    public int Line { get; }
    public int Column { get; }
}

internal sealed partial class Engine
{
    private readonly Dictionary<int, IVariableContainer> _containers = new();
    private readonly Dictionary<int, (int ThreadId, int FrameIndex)> _frameIds = new();
    private int _nextReference;

    private void ClearReferences()
    {
        _containers.Clear();
        _frameIds.Clear();
    }

    public int AddContainer(IVariableContainer container)
    {
        var id = ++_nextReference;
        _containers[id] = container;
        return id;
    }

    public (int ThreadId, int FrameIndex)? FrameRef(int frameId)
        => _frameIds.TryGetValue(frameId, out var r) ? r : null;

    // ---------------------------------------------------------------- スタック

    public List<StackFrameItem> StackTrace(int threadId, int startFrame, int levels)
    {
        lock (Sync)
        {
            WaitIdle();
            var result = new List<StackFrameItem>();
            if (!_stopped || _process is null) return result;
            CorDebugThread thread;
            try { thread = _process.GetThread(threadId); }
            catch (Exception ex) when (ex is COMException or DebugException) { return result; }

            var frames = WalkFrames(thread);
            var externalRun = false;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var location = frame.Location;
                if (_justMyCode && (frame.Module?.Symbols is null || location is null))
                {
                    // マイコードのみ：シンボルの無い連続したフレームは 1 行にまとめる（VS の [外部コード]）。
                    if (externalRun) continue;
                    externalRun = true;
                    result.Add(new StackFrameItem(RegisterFrame(threadId, i), "[外部コード]", null, 0, 0));
                    continue;
                }
                externalRun = false;
                result.Add(new StackFrameItem(RegisterFrame(threadId, i), FrameName(frame),
                    location?.Document, location?.StartLine ?? 0, location?.StartColumn ?? 0));
            }
            return result.Skip(Math.Max(0, startFrame)).Take(levels > 0 ? levels : int.MaxValue).ToList();
        }
    }

    private int RegisterFrame(int threadId, int index)
    {
        var id = ++_nextReference;
        _frameIds[id] = (threadId, index);
        return id;
    }

    private string FrameName(FrameInfo frame)
    {
        var metadata = frame.Module?.Metadata;
        var method = metadata?.Method(frame.MethodToken);
        if (method is null) return $"[{frame.Module?.Name ?? "?"}] 0x{frame.MethodToken:X8}";
        var type = metadata!.Type(method.DeclaringTypeToken);
        return FriendlyMethodName(type?.FullName ?? "", method.Name, method.ParameterNames);
    }

    /// <summary>コンパイラが作った名前を、書いたコードの名前へ寄せる（VS に近い表記）。
    /// <c>Program.&lt;Main&gt;d__0.MoveNext()</c>（async/iterator の本体）→ <c>Program.Main()</c>、
    /// <c>Program.&lt;&gt;c.&lt;Main&gt;b__0_1(n)</c>（ラムダ）→ <c>Program.Main.AnonymousMethod__0_1(n)</c>。</summary>
    internal static string FriendlyMethodName(string typeName, string methodName, IReadOnlyList<string> parameters)
    {
        var parts = typeName.Split('.').ToList();
        // 状態機械（<Name>d__N）の MoveNext は元のメソッド名で見せる。
        if (methodName == "MoveNext" && parts.Count > 0 && CompilerName(parts[parts.Count - 1], "d__") is { } kickoff)
        {
            parts.RemoveAt(parts.Count - 1);
            return string.Join(".", parts.Append(kickoff)) + "()";
        }
        // ラムダを入れる型（<>c、<>c__DisplayClass0_0）は名前から外す。
        while (parts.Count > 0 && parts[parts.Count - 1].StartsWith("<>c", StringComparison.Ordinal)) parts.RemoveAt(parts.Count - 1);
        var name = methodName;
        var close = methodName.IndexOf('>');
        if (methodName.StartsWith("<", StringComparison.Ordinal) && close > 1 &&
            methodName.Substring(close + 1).StartsWith("b__", StringComparison.Ordinal))
            name = methodName.Substring(1, close - 1) + ".AnonymousMethod__" + methodName.Substring(close + 4);
        return string.Join(".", parts.Append(name)) + "(" + string.Join(", ", parameters) + ")";
    }

    private static string? CompilerName(string name, string marker)
    {
        var close = name.IndexOf('>');
        return name.StartsWith("<", StringComparison.Ordinal) && close > 1 &&
               name.Substring(close + 1).StartsWith(marker, StringComparison.Ordinal)
            ? name.Substring(1, close - 1)
            : null;
    }

    public string? ThreadName(CorDebugThread thread)
    {
        var obj = thread.Object;
        if (obj is null || Inspector.IsNullReference(obj)) return null;
        if (Inspector.Dereference(obj) is not CorDebugObjectValue value || Inspector.ExactType(value) is not { } type) return null;
        var name = Inspector.FieldValue(this, value, type, "m_Name");
        if (name is null || Inspector.Dereference(name) is not CorDebugStringValue text) return null;
        return Inspector.ReadString(text);
    }

    // ---------------------------------------------------------------- スコープと変数

    public int ScopeReference(int frameId)
    {
        lock (Sync)
        {
            if (FrameRef(frameId) is not { } frame) return 0;
            return AddContainer(new FrameLocals(frame.ThreadId, frame.FrameIndex));
        }
    }

    public List<VariableItem> Variables(int reference)
    {
        lock (Sync)
        {
            WaitIdle();
            if (!_stopped || !_containers.TryGetValue(reference, out var container)) return new List<VariableItem>();
            return container.Children(this);
        }
    }

    /// <summary>1 つの値を variables の 1 行にする。展開できる値には子の参照を付ける（ヒープの値は強いハンドルで押さえる）。</summary>
    public VariableItem MakeItem(string name, ValueSource source, Evaluator? evaluator, string? evaluateName = null)
    {
        var value = source.Get(this);
        if (value is null) return new VariableItem(name, "<値を読めません>", null, 0, evaluateName);
        string typeName;
        var reference = 0;
        try
        {
            var inner = Inspector.Dereference(value);
            typeName = Inspector.TypeName(this, Inspector.ExactType(inner ?? value));
            if (inner is not null && IsExpandable(inner))
            {
                var stable = KeepAlive(inner) is { } handle
                    ? new HandleSource(handle) { EvaluateName = evaluateName }
                    : source;
                reference = AddContainer(new ValueChildren(stable, evaluateName));
            }
        }
        catch (Exception ex) when (ex is COMException or DebugException or InvalidCastException)
        {
            typeName = "?";
        }
        // 表示（ToString などの評価）は最後に。評価で値が無効になっても、型と参照はもう取ってある。
        var display = Inspector.Format(this, source.Get(this) ?? value, evaluator);
        return new VariableItem(name, display, typeName, reference, evaluateName);
    }

    private bool IsExpandable(CorDebugValue inner)
    {
        switch (inner)
        {
            case CorDebugStringValue:
            case CorDebugGenericValue:
                return false;
            case CorDebugArrayValue array:
                return array.Count > 0;
            case CorDebugObjectValue obj:
                var type = Inspector.ExactType(obj);
                var meta = type is null ? null : Inspector.TypeMeta(this, type);
                return meta is not null && !meta.IsEnum;
            default:
                return false;
        }
    }

    /// <summary>set_variable：一覧に出した名前の値を書き換える。</summary>
    public string? SetVariable(int reference, string name, string text, out string? error)
    {
        lock (Sync)
        {
            WaitIdle();
            error = null;
            if (!_stopped || !_containers.TryGetValue(reference, out var container) || container is not INamedSources named ||
                named.Find(name) is not { } source)
            {
                error = "この変数は書き換えられません。";
                return null;
            }
            var value = source.Get(this);
            if (value is null) { error = "値を読めません。"; return null; }
            var thread = Thread(_stoppedThreadId);
            try
            {
                var trimmed = text.Trim();
                if (value is CorDebugReferenceValue referenceValue)
                {
                    if (trimmed == "null")
                    {
                        referenceValue.Value = 0;
                    }
                    else if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"' && thread is not null)
                    {
                        var created = NewString(thread, Evaluator.Unescape(trimmed.Substring(1, trimmed.Length - 2)));
                        if (created.Value is not CorDebugReferenceValue newString) { error = created.Error ?? "文字列を作れませんでした。"; return null; }
                        var target = source.Get(this) as CorDebugReferenceValue;
                        if (target is null) { error = "値を読めません。"; return null; }
                        target.Value = newString.Value;
                    }
                    else
                    {
                        error = "参照型には null か文字列リテラルだけ代入できます。";
                        return null;
                    }
                }
                else if (Inspector.Dereference(value) is CorDebugGenericValue generic)
                {
                    if (!Inspector.WritePrimitive(generic, generic.Type, trimmed, out error)) return null;
                }
                else
                {
                    error = "この型の値は書き換えられません。";
                    return null;
                }
            }
            catch (Exception ex) when (ex is COMException or DebugException)
            {
                error = "書き換えに失敗しました: " + Inspector.ShortError(ex);
                return null;
            }
            var updated = source.Get(this);
            return updated is null ? text : Inspector.Format(this, updated, new Evaluator(this, _stoppedThreadId, 0));
        }
    }

    // ---------------------------------------------------------------- フレームの変数の集め方

    /// <summary>フレームで見える名前と値（this・引数・ローカル）。クロージャのキャプチャ（表示クラス）や
    /// async/iterator の状態機械に持ち上げられた変数は、元の名前で平らに並べる（VS と同じ見え方）。</summary>
    public List<(string Name, ValueSource Source)> FrameVariables(int threadId, int frameIndex)
    {
        var result = new List<(string, ValueSource)>();
        var frame = FrameAt(threadId, frameIndex);
        if (frame?.Module is not { } module) return result;
        var method = module.Metadata?.Method(frame.MethodToken);
        var declaring = method is null ? null : module.Metadata!.Type(method.DeclaringTypeToken);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, ValueSource source)
        {
            if (seen.Add(name)) result.Add((name, source));
        }

        if (method is { IsStatic: false })
        {
            var thisSource = new ArgumentSource(threadId, frameIndex, 0) { EvaluateName = "this" };
            if (declaring is not null && declaring.Name.StartsWith("<", StringComparison.Ordinal))
                foreach (var (name, source) in HoistedFields(thisSource)) Add(name, source);
            else
                Add("this", thisSource);
        }

        if (method is not null)
        {
            for (var i = 0; i < method.ParameterNames.Length; i++)
            {
                var name = method.ParameterNames[i];
                Add(name, new ArgumentSource(threadId, frameIndex, i + (method.IsStatic ? 0 : 1)) { EvaluateName = name });
            }
        }

        var symbols = module.Symbols?.Method(frame.MethodToken);
        if (symbols is not null)
        {
            var offset = frame.IsLeaf ? frame.ILOffset : Math.Max(0, frame.ILOffset - 1);
            // 内側のスコープを優先（同名の変数は後に宣言されたスコープの方が内側）。
            foreach (var local in symbols.Locals
                         .Where(l => l.StartOffset <= offset && offset < l.EndOffset)
                         .OrderByDescending(l => l.StartOffset))
            {
                var source = new LocalSource(threadId, frameIndex, local.Slot) { EvaluateName = local.Name };
                if (local.Name.StartsWith("CS$<>8__locals", StringComparison.Ordinal))
                {
                    foreach (var (name, hoisted) in HoistedFields(source)) Add(name, hoisted);
                    continue;
                }
                Add(local.Name, source);
            }
        }
        return result;
    }

    /// <summary>コンパイラが作った型（表示クラス・状態機械）のフィールドを、元の変数名で返す。</summary>
    private IEnumerable<(string Name, ValueSource Source)> HoistedFields(ValueSource owner)
    {
        var value = owner.Get(this);
        if (value is null || Inspector.Dereference(value) is not CorDebugObjectValue obj || Inspector.ExactType(obj) is not { } type)
            yield break;
        foreach (var (declaring, module, meta) in Inspector.Chain(this, type))
        {
            foreach (var field in meta.Fields.Where(f => !f.IsStatic))
            {
                // ラムダに捕捉された変数は表示クラスのインスタンス（<>8__1 や CS$<>8__locals0）の中にある。
                if (field.Name.StartsWith("CS$<>8__locals", StringComparison.Ordinal) ||
                    field.Name.StartsWith("<>8__", StringComparison.Ordinal))
                {
                    var display = new FieldSource(owner, module.BaseAddress, declaring.Class.Token, field.Token);
                    foreach (var nested in HoistedFields(display)) yield return nested;
                    continue;
                }
                if (Inspector.DisplayFieldName(field.Name, out _) is not { } name) continue;
                yield return (name, new FieldSource(owner, module.BaseAddress, declaring.Class.Token, field.Token) { EvaluateName = name });
            }
            break; // 基底（object）までは辿らない
        }
    }
}

/// <summary>名前から子の辿り方を引ける入れ物（setVariable 用）。</summary>
internal interface INamedSources
{
    ValueSource? Find(string name);
}

/// <summary>フレームのローカル（this・引数・ローカル）。</summary>
internal sealed class FrameLocals : IVariableContainer, INamedSources
{
    private readonly int _threadId, _frameIndex;
    private Dictionary<string, ValueSource> _sources = new();

    public FrameLocals(int threadId, int frameIndex)
    {
        _threadId = threadId;
        _frameIndex = frameIndex;
    }

    public List<VariableItem> Children(Engine engine)
    {
        var evaluator = new Evaluator(engine, _threadId, _frameIndex);
        var variables = engine.FrameVariables(_threadId, _frameIndex);
        _sources = variables.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Source);
        return variables.Select(v => engine.MakeItem(v.Name, v.Source, evaluator, v.Source.EvaluateName ?? v.Name)).ToList();
    }

    public ValueSource? Find(string name) => _sources.TryGetValue(name, out var s) ? s : null;
}

/// <summary>値の子（フィールド・プロパティ・配列の要素・コレクションの中身）。</summary>
internal sealed class ValueChildren : IVariableContainer, INamedSources
{
    private const int MaxElements = 1000;
    private const int MaxPropertyEvaluations = 30;

    private readonly ValueSource _source;
    private readonly string? _evaluateName;
    private readonly Dictionary<string, ValueSource> _sources = new();

    public ValueChildren(ValueSource source, string? evaluateName)
    {
        _source = source;
        _evaluateName = evaluateName;
    }

    public ValueSource? Find(string name) => _sources.TryGetValue(name, out var s) ? s : null;

    public List<VariableItem> Children(Engine engine)
    {
        var items = new List<VariableItem>();
        var value = _source.Get(engine);
        if (value is null) return items;
        var inner = Inspector.Dereference(value);
        var evaluator = new Evaluator(engine, engine.StoppedThreadId, 0);

        if (inner is CorDebugArrayValue array)
        {
            var dims = array.GetDimensions(array.Rank);
            var count = Math.Min(array.Count, MaxElements);
            for (var i = 0; i < count; i++)
            {
                var name = "[" + IndexText(dims, i) + "]";
                var source = new ElementSource(_source, i);
                _sources[name] = source;
                items.Add(engine.MakeItem(name, source, evaluator, _evaluateName is null ? null : _evaluateName + name));
            }
            if (array.Count > MaxElements)
                items.Add(new VariableItem("…", $"（先頭 {MaxElements} 件だけ表示しています。全 {array.Count} 件）", null, 0));
            return items;
        }

        if (inner is not CorDebugObjectValue obj || Inspector.ExactType(obj) is not { } type) return items;
        var meta = Inspector.TypeMeta(engine, type);

        if (meta?.FullName == "System.Nullable")
        {
            var nested = new ValueChildren(FieldOf(engine, type, "value") ?? _source, _evaluateName);
            return nested.Children(engine);
        }
        if (meta?.FullName == "System.Collections.Generic.List" && ListItems(engine, obj, type, evaluator) is { } listItems)
            return listItems;
        if (meta?.FullName == "System.Collections.Generic.Dictionary" && DictionaryItems(engine, obj, type, evaluator) is { } dictionaryItems)
            return dictionaryItems;

        return MemberItems(engine, type, evaluator);
    }

    private List<VariableItem> MemberItems(Engine engine, CorDebugType type, Evaluator evaluator)
    {
        var items = new List<VariableItem>();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var hasStatics = false;
        var chain = Inspector.Chain(engine, type).ToList();

        foreach (var (declaring, module, meta) in chain)
        {
            foreach (var field in meta.Fields)
            {
                if (field.IsStatic) { hasStatics |= !field.IsLiteral; continue; }
                if (Inspector.DisplayFieldName(field.Name, out _) is not { } name) continue;
                if (!shown.Add(name)) name += "（" + meta.Name + "）";
                var source = new FieldSource(_source, module.BaseAddress, declaring.Class.Token, field.Token)
                    { EvaluateName = Member(name) };
                _sources[name] = source;
                items.Add(engine.MakeItem(name, source, evaluator, Member(name)));
            }
        }

        // 自動実装でないプロパティは getter を評価する（上限つき・時間切れが続いたらやめる）。
        var evaluations = 0;
        foreach (var (declaring, module, meta) in chain)
        {
            foreach (var property in meta.Properties)
            {
                // 明示的なインターフェイス実装（System.Collections.IList.IsFixedSize 等）は VS と同じく出さない。
                if (property.IsStatic || property.ParameterCount != 0 || property.Name.Contains(".") ||
                    shown.Contains(property.Name)) continue;
                shown.Add(property.Name);
                if (!engine.CanEvaluate || evaluations >= MaxPropertyEvaluations)
                {
                    items.Add(new VariableItem(property.Name, "<評価していません>", null, 0, Member(property.Name)));
                    continue;
                }
                evaluations++;
                var getter = property.GetterToken;
                var owner = _source;
                var result = evaluator.CallGetter(owner, declaring, module, getter);
                if (result.Error is { } error || result.Value is null)
                {
                    items.Add(new VariableItem(property.Name, "<" + (result.Error ?? "値がありません") + ">", null, 0, Member(property.Name)));
                    continue;
                }
                if (result.Threw)
                {
                    items.Add(new VariableItem(property.Name,
                        "<例外: " + Inspector.Format(engine, result.Value, null) + ">", null, 0, Member(property.Name)));
                    continue;
                }
                ValueSource source = engine.KeepAlive(Inspector.Dereference(result.Value) ?? result.Value) is { } handle
                    ? new HandleSource(handle)
                    : new ComputedSource(e => evaluator.CallGetter(owner, declaring, module, getter).Value);
                items.Add(engine.MakeItem(property.Name, source, evaluator, Member(property.Name)));
            }
        }

        if (hasStatics && chain.Count > 0)
        {
            var reference = engine.AddContainer(new StaticMembers(chain[0].Type, engine.StoppedThreadId));
            items.Add(new VariableItem("静的メンバー", "", null, reference));
        }
        return items;
    }

    private List<VariableItem>? ListItems(Engine engine, CorDebugObjectValue obj, CorDebugType type, Evaluator evaluator)
    {
        var sizeValue = Inspector.ReadFieldPrimitive(engine, obj, type, "_size");
        var itemsSource = FieldOf(engine, type, "_items");
        if (sizeValue is null || itemsSource is null) return null;
        var size = Math.Min(Convert.ToInt32(sizeValue), MaxElements);
        var items = new List<VariableItem>();
        for (var i = 0; i < size; i++)
        {
            var name = "[" + i + "]";
            var source = new ElementSource(itemsSource, i);
            _sources[name] = source;
            items.Add(engine.MakeItem(name, source, evaluator, _evaluateName is null ? null : _evaluateName + name));
        }
        items.Add(RawView(engine));
        return items;
    }

    private List<VariableItem>? DictionaryItems(Engine engine, CorDebugObjectValue obj, CorDebugType type, Evaluator evaluator)
    {
        var entriesSource = FieldOf(engine, type, "entries");
        var countValue = Inspector.ReadFieldPrimitive(engine, obj, type, "count");
        if (entriesSource is null || countValue is null) return null;
        var items = new List<VariableItem>();
        var count = Math.Min(Convert.ToInt32(countValue), MaxElements);
        for (var i = 0; i < count; i++)
        {
            var entrySource = new ElementSource(entriesSource, i);
            if (entrySource.Get(engine) is not { } entry || Inspector.Dereference(entry) is not CorDebugObjectValue entryObj ||
                Inspector.ExactType(entryObj) is not { } entryType)
                continue;
            if (Inspector.ReadFieldPrimitive(engine, entryObj, entryType, "hashCode") is int hash && hash < 0) continue;
            var keySource = FieldOf(engine, entryType, "key", entrySource);
            var valueSource = FieldOf(engine, entryType, "value", entrySource);
            if (keySource?.Get(engine) is not { } key || valueSource is null) continue;
            var name = "[" + Inspector.Format(engine, key, evaluator) + "]";
            _sources[name] = valueSource;
            items.Add(engine.MakeItem(name, valueSource, evaluator));
        }
        items.Add(RawView(engine));
        return items;
    }

    /// <summary>コレクションの整形表示とは別に、生のフィールドを見るための子。</summary>
    private VariableItem RawView(Engine engine)
        => new("生データ", "", null, engine.AddContainer(new RawMembers(_source, _evaluateName)));

    private ValueSource? FieldOf(Engine engine, CorDebugType type, string fieldName, ValueSource? owner = null)
    {
        foreach (var (declaring, module, meta) in Inspector.Chain(engine, type))
        {
            var field = meta.Fields.FirstOrDefault(f => !f.IsStatic && f.Name == fieldName);
            if (field is not null)
                return new FieldSource(owner ?? _source, module.BaseAddress, declaring.Class.Token, field.Token);
        }
        return null;
    }

    private string? Member(string name) => _evaluateName is null ? null : _evaluateName + "." + name;

    private static string IndexText(int[] dims, int position)
    {
        if (dims.Length <= 1) return position.ToString();
        var indices = new int[dims.Length];
        for (var d = dims.Length - 1; d >= 0; d--)
        {
            indices[d] = position % dims[d];
            position /= dims[d];
        }
        return string.Join(", ", indices);
    }

    /// <summary>コレクションの「生データ」：整形しないフィールド一覧。</summary>
    private sealed class RawMembers : IVariableContainer
    {
        private readonly ValueSource _source;
        private readonly string? _evaluateName;
        public RawMembers(ValueSource source, string? evaluateName) { _source = source; _evaluateName = evaluateName; }

        public List<VariableItem> Children(Engine engine)
        {
            var value = _source.Get(engine);
            if (value is null || Inspector.Dereference(value) is not CorDebugObjectValue obj || Inspector.ExactType(obj) is not { } type)
                return new List<VariableItem>();
            return new ValueChildren(_source, _evaluateName).MemberItems(engine, type, new Evaluator(engine, engine.StoppedThreadId, 0));
        }
    }
}

/// <summary>型の静的フィールド。</summary>
internal sealed class StaticMembers : IVariableContainer
{
    private readonly CorDebugType _type;
    private readonly int _threadId;

    public StaticMembers(CorDebugType type, int threadId)
    {
        _type = type;
        _threadId = threadId;
    }

    public List<VariableItem> Children(Engine engine)
    {
        var items = new List<VariableItem>();
        var evaluator = new Evaluator(engine, _threadId, 0);
        foreach (var (declaring, _, meta) in Inspector.Chain(engine, _type))
        {
            foreach (var field in meta.Fields.Where(f => f.IsStatic && !f.IsLiteral))
            {
                if (Inspector.DisplayFieldName(field.Name, out _) is not { } name) continue;
                var source = new StaticFieldSource(declaring, field.Token, _threadId, 0);
                items.Add(engine.MakeItem(name, source, evaluator, meta.FullName + "." + name));
            }
        }
        return items;
    }
}
