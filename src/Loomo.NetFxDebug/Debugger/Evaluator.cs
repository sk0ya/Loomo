using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ClrDebug;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

/// <summary>式の値。デバッグ対象の外で計算できる値（数値・真偽・文字・文字列・null）は CLR の値として持ち、
/// それ以外（オブジェクト・構造体・配列）は辿り方（<see cref="ValueSource"/>）で持つ。</summary>
internal abstract class EValue { }

internal sealed class ClrValue : EValue
{
    public static readonly ClrValue Null = new(null);

    /// <param name="typeName">C# での型名（int 等）。数値は計算のため long／double へ広げて持つので、表示の型は別に覚える。</param>
    public ClrValue(object? value, string? typeName = null)
    {
        Value = value;
        TypeName = typeName ?? value switch
        {
            null => null,
            bool => "bool",
            char => "char",
            long => "long",
            ulong => "ulong",
            double => "double",
            string => "string",
            _ => value.GetType().Name,
        };
    }

    public object? Value { get; }
    public string? TypeName { get; }
}

internal sealed class DebuggeeValue : EValue
{
    public DebuggeeValue(ValueSource source) => Source = source;
    public ValueSource Source { get; }
}

/// <summary>型名（静的メンバーへの入口）。</summary>
internal sealed class TypeValue : EValue
{
    public TypeValue(ModuleEntry module, TypeMeta meta, CorDebugType type)
    {
        Module = module;
        Meta = meta;
        Type = type;
    }

    public ModuleEntry Module { get; }
    public TypeMeta Meta { get; }
    public CorDebugType Type { get; }
}

internal sealed class EvaluationException : Exception
{
    public EvaluationException(string message) : base(message) { }
}

/// <summary>評価結果（表示・型・子の参照）。</summary>
internal sealed class EvaluationResult
{
    public EvaluationResult(string display, string? type, int reference)
    {
        Display = display;
        Type = type;
        Reference = reference;
    }

    public string Display { get; }
    public string? Type { get; }
    public int Reference { get; }
}

/// <summary>
/// C# のサブセットの式を評価する（ウォッチ・イミディエイト・条件付きブレークポイント・ログポイント・DebuggerDisplay）。
/// 名前はローカル → 引数 → this のメンバー → 宣言型の静的メンバー → 型名 の順に引く。プロパティとメソッド呼び出しは
/// 関数評価（対象を走らせる）になるので、評価をまたいだ値は辿り方で持ち、世代が変われば辿り直す。
/// </summary>
internal sealed class Evaluator
{
    private readonly Engine _engine;
    private readonly int _threadId;
    private readonly int _frameIndex;
    private readonly EValue? _thisOverride;
    private List<(string Name, ValueSource Source)>? _frameVariables;

    public Evaluator(Engine engine, int threadId, int frameIndex, EValue? thisOverride = null)
    {
        _engine = engine;
        _threadId = threadId;
        _frameIndex = frameIndex;
        _thisOverride = thisOverride;
    }

    private CorDebugThread Thread => _engine.Thread(_threadId) ?? throw new EvaluationException("スレッドがありません。");

    // ---------------------------------------------------------------- 入口

    public EvaluationResult Evaluate(string expression)
    {
        var value = EvaluateValue(expression);
        return Describe(value, expression);
    }

    public EValue EvaluateValue(string expression)
    {
        var parser = new Parser(expression, this);
        var value = parser.ParseExpression();
        parser.ExpectEnd();
        return value;
    }

    public EvaluationResult Describe(EValue value, string? evaluateName)
    {
        switch (value)
        {
            case ClrValue clr:
                return new EvaluationResult(Inspector.FormatPrimitive(clr.Value), clr.TypeName, 0);
            case DebuggeeValue debuggee:
                var item = _engine.MakeItem("", debuggee.Source, this, evaluateName);
                return new EvaluationResult(item.Value, item.Type, item.Reference);
            case TypeValue type:
                return new EvaluationResult(type.Meta.FullName, "型", 0);
            default:
                return new EvaluationResult("", null, 0);
        }
    }

    /// <summary>条件式を評価する。bool にならなければ null。</summary>
    public bool? EvaluateBoolean(string expression)
    {
        try
        {
            var value = Normalize(EvaluateValue(expression));
            return value is ClrValue { Value: bool b } ? b : null;
        }
        catch (EvaluationException) { return null; }
    }

    /// <summary>ToString() の結果（上書きしている型だけ呼ぶこと）。</summary>
    public string? CallToString(CorDebugObjectValue obj, CorDebugType type)
    {
        foreach (var (declaring, module, meta) in Inspector.Chain(_engine, type))
        {
            var method = meta.Methods.FirstOrDefault(m => m.Name == "ToString" && !m.IsStatic && m.ParameterNames.Length == 0);
            if (method is null) continue;
            var outcome = _engine.Call(Thread, module, method.Token, declaring.TypeParameters, new CorDebugValue[] { obj }, 1000);
            if (outcome.Threw || outcome.Value is null) return null;
            return Inspector.Dereference(outcome.Value) is CorDebugStringValue text ? Inspector.ReadString(text) : null;
        }
        return null;
    }

    /// <summary>[DebuggerDisplay("…{式}…")] を展開する。式は対象オブジェクトを this として評価する。</summary>
    public string? RenderDebuggerDisplay(string template, CorDebugObjectValue obj, CorDebugType type, int depth)
    {
        ValueSource owner = _engine.KeepAlive(obj) is { } handle ? new HandleSource(handle) : new ComputedSource(_ => obj);
        var inner = new Evaluator(_engine, _threadId, _frameIndex, new DebuggeeValue(owner));
        var builder = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '{') { builder.Append(c); continue; }
            var close = template.IndexOf('}', i + 1);
            if (close < 0) return null;
            var expression = template.Substring(i + 1, close - i - 1);
            var noQuotes = false;
            var comma = expression.LastIndexOf(',');
            if (comma > 0 && expression.Substring(comma + 1).Trim() == "nq")
            {
                noQuotes = true;
                expression = expression.Substring(0, comma);
            }
            try
            {
                var value = inner.EvaluateValue(expression);
                var text = value is DebuggeeValue d && d.Source.Get(_engine) is { } v
                    ? Inspector.Format(_engine, v, depth < 1 ? inner : null, depth + 1)
                    : inner.Describe(value, null).Display;
                if (noQuotes && text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                    text = text.Substring(1, text.Length - 2);
                builder.Append(text);
            }
            catch (EvaluationException) { return null; }
            i = close;
        }
        return builder.ToString();
    }

    /// <summary>プロパティの getter を呼ぶ（<paramref name="owner"/> が this）。</summary>
    public EvalOutcome CallGetter(ValueSource owner, CorDebugType declaring, ModuleEntry module, int getterToken)
    {
        var target = owner.Get(_engine);
        if (target is null) return new EvalOutcome(null, false, "値を読めません。");
        return _engine.Call(Thread, module, getterToken, declaring.TypeParameters, new[] { target }, 1500);
    }

    // ---------------------------------------------------------------- 名前の解決

    public EValue ResolveIdentifier(string name)
    {
        if (name == "this")
            return _thisOverride ?? Lookup("this") ?? throw new EvaluationException("this はありません（静的メソッド）。");

        if (_thisOverride is null && Lookup(name) is { } local) return local;

        var self = _thisOverride ?? Lookup("this");
        if (self is not null && TryMember(self, name, out var member)) return member;

        if (_thisOverride is null && DeclaringType() is { } declaring && TryMember(declaring, name, out var staticMember))
            return staticMember;

        if (FindType(name) is { } type) return type;
        throw new EvaluationException($"名前 '{name}' は現在のコンテキストにありません。");
    }

    private EValue? Lookup(string name)
    {
        _frameVariables ??= _engine.FrameVariables(_threadId, _frameIndex);
        foreach (var (variable, source) in _frameVariables)
            if (variable == name) return new DebuggeeValue(source);
        return null;
    }

    private TypeValue? DeclaringType()
    {
        var frame = _engine.FrameAt(_threadId, _frameIndex);
        if (frame?.Module?.Metadata is not { } metadata || metadata.Method(frame.MethodToken) is not { } method) return null;
        return TypeFor(frame.Module, method.DeclaringTypeToken);
    }

    private TypeValue? TypeFor(ModuleEntry module, int typeToken)
    {
        var meta = module.Metadata?.Type(typeToken);
        if (meta is null) return null;
        try
        {
            var cls = module.Module.GetClassFromToken(typeToken);
            var type = cls.GetParameterizedType(CorElementType.Class, 0, null);
            return new TypeValue(module, meta, type);
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
    }

    /// <summary>型名を探す。ユーザーのモジュール（シンボルあり）を先に、単純名でも名前空間付きでも。</summary>
    public TypeValue? FindType(string name)
    {
        foreach (var module in _engine.Modules.OrderByDescending(m => m.Symbols is not null))
        {
            var metadata = module.Metadata;
            if (metadata is null) continue;
            var token = metadata.FindType(name);
            if (token is null && !name.Contains("."))
            {
                var matches = metadata.FindTypesBySimpleName(name).ToList();
                if (matches.Count == 1) token = matches[0];
            }
            if (token is { } t && TypeFor(module, t) is { } type) return type;
        }
        return null;
    }

    // ---------------------------------------------------------------- メンバー・添字・呼び出し

    public EValue Member(EValue target, string name)
    {
        if (TryMember(target, name, out var result)) return result;
        throw new EvaluationException($"'{name}' というメンバーはありません。");
    }

    private bool TryMember(EValue target, string name, out EValue result)
    {
        result = ClrValue.Null;
        switch (target)
        {
            case ClrValue { Value: string s } when name == "Length":
                result = new ClrValue((long)s.Length, "int");
                return true;
            case ClrValue { Value: null }:
                throw new EvaluationException("null 参照のメンバーは読めません。");
            case ClrValue:
                return false;
            case TypeValue type:
                return TryStaticMember(type, name, out result);
            case DebuggeeValue debuggee:
                return TryInstanceMember(debuggee, name, out result);
            default:
                return false;
        }
    }

    private bool TryInstanceMember(DebuggeeValue target, string name, out EValue result)
    {
        result = ClrValue.Null;
        var value = target.Source.Get(_engine) ?? throw new EvaluationException("値を読めません。");
        if (Inspector.IsNullReference(value)) throw new EvaluationException("null 参照のメンバーは読めません。");
        var inner = Inspector.Dereference(value);
        if (inner is CorDebugArrayValue array)
        {
            if (name is "Length" or "LongLength") { result = new ClrValue((long)array.Count, name == "Length" ? "int" : "long"); return true; }
            if (name == "Rank") { result = new ClrValue((long)array.Rank, "int"); return true; }
            return false;
        }
        if (inner is CorDebugStringValue str)
        {
            if (name == "Length") { result = new ClrValue((long)str.Length, "int"); return true; }
            return false;
        }
        if (inner is not CorDebugObjectValue obj || Inspector.ExactType(obj) is not { } type) return false;

        if (name == "Count" && Inspector.CollectionCount(_engine, obj, type) is { } count)
        {
            result = new ClrValue((long)count, "int");
            return true;
        }

        var chain = Inspector.Chain(_engine, type).ToList();
        foreach (var (declaring, module, meta) in chain)
        {
            var field = meta.Fields.FirstOrDefault(f => !f.IsStatic && (f.Name == name || f.Name == "<" + name + ">k__BackingField"));
            if (field is null) continue;
            result = new DebuggeeValue(new FieldSource(target.Source, module.BaseAddress, declaring.Class.Token, field.Token));
            return true;
        }
        foreach (var (declaring, module, meta) in chain)
        {
            var property = meta.Properties.FirstOrDefault(p => !p.IsStatic && p.ParameterCount == 0 && p.Name == name);
            if (property is null) continue;
            result = FromOutcome(CallGetter(target.Source, declaring, module, property.GetterToken));
            return true;
        }
        // 静的メンバーをインスタンス越しに書いたとき（C# では書けないが、ウォッチでは便利）。
        if (chain.Count > 0 && TypeFor(chain[0].Module, chain[0].Type.Class.Token) is { } typeValue)
            return TryStaticMember(typeValue, name, out result);
        return false;
    }

    private bool TryStaticMember(TypeValue type, string name, out EValue result)
    {
        result = ClrValue.Null;
        foreach (var (declaring, module, meta) in Inspector.Chain(_engine, type.Type))
        {
            var field = meta.Fields.FirstOrDefault(f => f.IsStatic && (f.Name == name || f.Name == "<" + name + ">k__BackingField"));
            if (field is not null)
            {
                if (field.IsLiteral && meta.IsEnum)
                {
                    var value = meta.EnumValues.FirstOrDefault(v => v.Name == name);
                    result = new ClrValue((long)value.Value);
                    return true;
                }
                if (field.IsLiteral) return false;
                result = new DebuggeeValue(new StaticFieldSource(declaring, field.Token, _threadId, _frameIndex));
                return true;
            }
            var property = meta.Properties.FirstOrDefault(p => p.IsStatic && p.ParameterCount == 0 && p.Name == name);
            if (property is not null)
            {
                result = FromOutcome(_engine.Call(Thread, module, property.GetterToken, declaring.TypeParameters,
                    Array.Empty<CorDebugValue>()));
                return true;
            }
        }
        // 入れ子の型（Outer.Inner）
        if (FindType(type.Meta.FullName + "." + name) is { } nested) { result = nested; return true; }
        return false;
    }

    public EValue Index(EValue target, EValue index)
    {
        var indexValue = Normalize(index);
        if (target is ClrValue { Value: string s })
        {
            var i = (int)ToLong(indexValue);
            if (i < 0 || i >= s.Length) throw new EvaluationException("インデックスが範囲外です。");
            return new ClrValue(s[i]);
        }
        if (target is not DebuggeeValue debuggee) throw new EvaluationException("添字を付けられない値です。");
        var value = debuggee.Source.Get(_engine) ?? throw new EvaluationException("値を読めません。");
        var inner = Inspector.Dereference(value) ?? throw new EvaluationException("null 参照に添字は付けられません。");
        if (inner is CorDebugArrayValue array)
        {
            var i = (int)ToLong(indexValue);
            if (i < 0 || i >= array.Count) throw new EvaluationException("インデックスが範囲外です。");
            return new DebuggeeValue(new ElementSource(debuggee.Source, i));
        }
        if (inner is CorDebugStringValue text)
        {
            var i = (int)ToLong(indexValue);
            var s2 = Inspector.ReadString(text);
            if (i < 0 || i >= s2.Length) throw new EvaluationException("インデックスが範囲外です。");
            return new ClrValue(s2[i]);
        }
        if (inner is CorDebugObjectValue obj && Inspector.ExactType(obj) is { } type)
        {
            if (Inspector.TypeMeta(_engine, type)?.FullName == "System.Collections.Generic.List")
            {
                var size = Convert.ToInt32(Inspector.ReadFieldPrimitive(_engine, obj, type, "_size") ?? 0);
                var i = (int)ToLong(indexValue);
                if (i < 0 || i >= size) throw new EvaluationException("インデックスが範囲外です。");
                var items = Member(debuggee, "_items");
                return Index(items, new ClrValue((long)i));
            }
            return Call(debuggee, "get_Item", new[] { index });
        }
        throw new EvaluationException("添字を付けられない値です。");
    }

    public EValue Call(EValue target, string name, IReadOnlyList<EValue> args)
    {
        if (target is ClrValue { Value: var clr } && name == "ToString" && args.Count == 0)
            return new ClrValue(clr is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : clr?.ToString() ?? "");
        // 読み出し済みの文字列（例: person.Name）のメソッドは、対象の中に文字列を作り直して呼ぶ。
        if (target is ClrValue { Value: string text })
        {
            var created = _engine.NewString(Thread, text);
            if (created.Value is null) throw new EvaluationException(created.Error ?? "文字列を作れませんでした。");
            var inner = Inspector.Dereference(created.Value) ?? created.Value;
            target = _engine.KeepAlive(inner) is { } kept
                ? new DebuggeeValue(new HandleSource(kept))
                : new DebuggeeValue(new ComputedSource(_ => created.Value));
        }

        CorDebugType? type;
        CorDebugValue? self = null;
        bool isStatic;
        switch (target)
        {
            case TypeValue typeValue:
                type = typeValue.Type;
                isStatic = true;
                break;
            case DebuggeeValue debuggee:
                self = debuggee.Source.Get(_engine) ?? throw new EvaluationException("値を読めません。");
                if (Inspector.IsNullReference(self)) throw new EvaluationException("null 参照のメソッドは呼べません。");
                type = Inspector.ExactType(Inspector.Dereference(self) ?? self);
                isStatic = false;
                break;
            default:
                throw new EvaluationException($"'{name}' を呼べる値ではありません。");
        }
        if (type is null) throw new EvaluationException("型が分かりません。");

        foreach (var (declaring, module, meta) in Inspector.Chain(_engine, type))
        {
            var method = meta.Methods.FirstOrDefault(m => m.Name == name && m.IsStatic == isStatic &&
                                                          m.ParameterNames.Length == args.Count && m.GenericArity == 0);
            if (method is null) continue;
            var arguments = new List<CorDebugValue>();
            // 引数を作る評価（文字列の生成）で this が無効になるので、引数を先に作ってから this を取り直す。
            foreach (var arg in args) arguments.Add(ToDebuggee(arg));
            if (!isStatic)
            {
                var current = ((DebuggeeValue)target).Source.Get(_engine) ?? throw new EvaluationException("値を読めません。");
                arguments.Insert(0, current);
            }
            return FromOutcome(_engine.Call(Thread, module, method.Token, declaring.TypeParameters, arguments.ToArray()));
        }
        throw new EvaluationException($"'{name}'（引数 {args.Count} 個）というメソッドはありません。");
    }

    private CorDebugValue ToDebuggee(EValue value)
    {
        switch (Normalize(value))
        {
            case DebuggeeValue d:
                return d.Source.Get(_engine) ?? throw new EvaluationException("値を読めません。");
            case ClrValue { Value: string s }:
                var created = _engine.NewString(Thread, s);
                if (created.Value is null) throw new EvaluationException(created.Error ?? "文字列を作れませんでした。");
                return created.Value;
            case ClrValue { Value: var v } when v is not null:
                var (type, boxed) = v switch
                {
                    bool b => (CorElementType.Boolean, (object)b),
                    char c => (CorElementType.Char, c),
                    double d => (CorElementType.R8, d),
                    ulong u => (CorElementType.U8, u),
                    long l when l >= int.MinValue && l <= int.MaxValue => (CorElementType.I4, (int)l),
                    long l => (CorElementType.I8, l),
                    _ => throw new EvaluationException("この値は引数に渡せません。"),
                };
                return _engine.NewPrimitive(Thread, type, boxed) ?? throw new EvaluationException("引数を作れませんでした。");
            default:
                throw new EvaluationException("null は引数に渡せません。");
        }
    }

    private EValue FromOutcome(EvalOutcome outcome)
    {
        if (outcome.Error is { } error) throw new EvaluationException(error);
        if (outcome.Value is null) return ClrValue.Null;
        if (outcome.Threw)
            throw new EvaluationException("例外がスローされました: " + Inspector.Format(_engine, outcome.Value, null));
        var inner = Inspector.Dereference(outcome.Value);
        if (inner is null) return ClrValue.Null;
        if (_engine.KeepAlive(inner) is { } handle) return Normalize(new DebuggeeValue(new HandleSource(handle)));
        var captured = outcome.Value;
        return Normalize(new DebuggeeValue(new ComputedSource(_ => captured)));
    }

    // ---------------------------------------------------------------- 値の正規化と演算

    /// <summary>プリミティブと文字列は CLR の値へ読み出す（以後の評価で無効にならない）。</summary>
    public EValue Normalize(EValue value)
    {
        if (value is not DebuggeeValue debuggee) return value;
        var raw = debuggee.Source.Get(_engine);
        if (raw is null) throw new EvaluationException("値を読めません。");
        if (Inspector.IsNullReference(raw)) return ClrValue.Null;
        var inner = Inspector.Dereference(raw);
        switch (inner)
        {
            case CorDebugStringValue text:
                return new ClrValue(Inspector.ReadString(text));
            case CorDebugGenericValue generic:
                return new ClrValue(Widen(Inspector.ReadPrimitive(generic, inner.Type)),
                    Inspector.TypeName(_engine, Inspector.ExactType(inner)));
            case CorDebugObjectValue obj when Inspector.ExactType(obj) is { } type &&
                                               Inspector.TypeMeta(_engine, type) is { IsEnum: true }:
                // 列挙値は演算では数値として扱う（表示は元の値のまま＝呼び出し側が DebuggeeValue を持っている）。
                return value;
            default:
                return value;
        }
    }

    private static object? Widen(object? value) => value switch
    {
        sbyte v => (long)v,
        short v => (long)v,
        int v => (long)v,
        long v => v,
        byte v => (long)v,
        ushort v => (long)v,
        uint v => (long)v,
        ulong v => v,
        float v => (double)v,
        _ => value,
    };

    /// <summary>演算用に数値化する（列挙値は基底の値）。</summary>
    private object? Operand(EValue value)
    {
        var normalized = Normalize(value);
        if (normalized is ClrValue clr) return clr.Value;
        if (normalized is DebuggeeValue d && d.Source.Get(_engine) is { } raw &&
            Inspector.Dereference(raw) is CorDebugObjectValue obj && Inspector.ExactType(obj) is { } type &&
            Inspector.TypeMeta(_engine, type) is { IsEnum: true })
            return Widen(Inspector.ReadFieldPrimitive(_engine, obj, type, "value__"));
        return normalized;
    }

    public EValue Binary(string op, EValue left, EValue right)
    {
        if (op == "==" || op == "!=")
        {
            var equal = AreEqual(left, right);
            return new ClrValue(op == "==" ? equal : !equal);
        }
        var l = Operand(left);
        var r = Operand(right);
        if (op == "+" && (l is string || r is string))
            return new ClrValue(Text(left, l) + Text(right, r));
        if (op is "<" or ">" or "<=" or ">=")
        {
            var c = Compare(l, r);
            return new ClrValue(op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 });
        }
        var resultType = PromotedType(TypeOf(left), TypeOf(right));
        if (IsFloating(l) || IsFloating(r))
        {
            var a = ToDouble(l);
            var b = ToDouble(r);
            return new ClrValue(op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => a / b,
                "%" => a % b,
                _ => throw new EvaluationException($"演算子 '{op}' は使えません。"),
            }, "double");
        }
        var x = ToLong(l);
        var y = ToLong(r);
        if (op is "/" or "%" && y == 0) throw new EvaluationException("0 で除算しました。");
        return new ClrValue(op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => x / y,
            "%" => x % y,
            "&" => x & y,
            "|" => x | y,
            "^" => x ^ y,
            _ => throw new EvaluationException($"演算子 '{op}' は使えません。"),
        }, resultType);
    }

    /// <summary>式の値の C# の型名（列挙値は基底の int として扱う）。</summary>
    private string? TypeOf(EValue value) => Normalize(value) switch
    {
        ClrValue clr => clr.TypeName,
        _ => "int",
    };

    /// <summary>C# の二項数値昇格（int より狭い型は int、どちらかが long／ulong ならそれ）。</summary>
    private static string PromotedType(string? left, string? right)
    {
        if (left is "double" or "float" || right is "double" or "float") return "double";
        if (left == "ulong" || right == "ulong") return "ulong";
        if (left is "long" or "uint" || right is "long" or "uint") return "long";
        return "int";
    }

    private string Text(EValue original, object? operand)
        => operand switch
        {
            string s => s,
            null => "",
            bool b => b ? "True" : "False",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => original is DebuggeeValue d && d.Source.Get(_engine) is { } v
                ? Inspector.Format(_engine, v, this)
                : operand.ToString() ?? "",
        };

    private bool AreEqual(EValue left, EValue right)
    {
        var l = Operand(left);
        var r = Operand(right);
        if (l is DebuggeeValue || r is DebuggeeValue)
        {
            // オブジェクト同士は参照の等しさ。null との比較は null 参照かどうか。
            var la = Address(left);
            var ra = Address(right);
            return la == ra;
        }
        if (l is null || r is null) return l is null && r is null;
        if (l is string || r is string) return string.Equals(l as string, r as string, StringComparison.Ordinal);
        if (l is bool lb && r is bool rb) return lb == rb;
        if (IsFloating(l) || IsFloating(r)) return ToDouble(l) == ToDouble(r);
        return ToLong(l) == ToLong(r);
    }

    private ulong? Address(EValue value)
    {
        if (value is ClrValue { Value: null }) return 0;
        if (value is DebuggeeValue d && d.Source.Get(_engine) is { } raw)
        {
            if (raw is CorDebugReferenceValue reference) return reference.IsNull ? 0 : reference.Value.Value;
            return raw.Address.Value;
        }
        return null;
    }

    private static int Compare(object? l, object? r)
    {
        if (l is string ls && r is string rs) return string.CompareOrdinal(ls, rs);
        if (IsFloating(l) || IsFloating(r)) return ToDouble(l).CompareTo(ToDouble(r));
        return ToLong(l).CompareTo(ToLong(r));
    }

    private static bool IsFloating(object? value) => value is double or float or decimal;

    public static long ToLong(object? value) => value switch
    {
        ClrValue c => ToLong(c.Value),
        long v => v,
        ulong v => unchecked((long)v),
        int v => v,
        char v => v,
        double v => (long)v,
        bool _ => throw new EvaluationException("真偽値は数値として使えません。"),
        null => throw new EvaluationException("null は数値として使えません。"),
        _ => throw new EvaluationException("数値ではありません。"),
    };

    private static double ToDouble(object? value) => value switch
    {
        double v => v,
        float v => v,
        long v => v,
        ulong v => v,
        char v => v,
        _ => throw new EvaluationException("数値ではありません。"),
    };

    public bool Truthy(EValue value)
        => Operand(value) is bool b ? b : throw new EvaluationException("真偽値ではありません。");

    public EValue Negate(EValue value)
    {
        var operand = Operand(value);
        return operand is double d ? new ClrValue(-d) : new ClrValue(-ToLong(operand), PromotedType(TypeOf(value), null));
    }

    public static string Unescape(string text)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length) { builder.Append(c); continue; }
            var next = text[++i];
            builder.Append(next switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                _ => next,
            });
        }
        return builder.ToString();
    }

    // ---------------------------------------------------------------- 構文解析（再帰下降）

    private sealed class Parser
    {
        private readonly string _text;
        private readonly Evaluator _evaluator;
        private int _position;

        public Parser(string text, Evaluator evaluator)
        {
            _text = text;
            _evaluator = evaluator;
        }

        public void ExpectEnd()
        {
            SkipSpace();
            if (_position < _text.Length) throw new EvaluationException($"式を解釈できません（位置 {_position + 1} 付近）。");
        }

        public EValue ParseExpression() => ParseConditional();

        private EValue ParseConditional()
        {
            var condition = ParseBinary(0);
            if (!Accept("?")) return condition;
            var whenTrue = ParseExpression();
            Expect(":");
            var whenFalse = ParseExpression();
            return _evaluator.Truthy(condition) ? whenTrue : whenFalse;
        }

        private static readonly string[][] Levels =
        {
            new[] { "||" },
            new[] { "&&" },
            new[] { "|" },
            new[] { "^" },
            new[] { "&" },
            new[] { "==", "!=" },
            new[] { "<=", ">=", "<", ">" },
            new[] { "+", "-" },
            new[] { "*", "/", "%" },
        };

        private EValue ParseBinary(int level)
        {
            if (level >= Levels.Length) return ParseUnary();
            var left = ParseBinary(level + 1);
            while (true)
            {
                SkipSpace();
                var op = Levels[level].FirstOrDefault(o => Peek(o) && !(o is "|" or "&" && Peek(o + o)));
                if (op is null) return left;
                _position += op.Length;
                if (op == "&&")
                {
                    if (!_evaluator.Truthy(left)) { SkipOperand(level + 1); left = new ClrValue(false); continue; }
                    left = new ClrValue(_evaluator.Truthy(ParseBinary(level + 1)));
                    continue;
                }
                if (op == "||")
                {
                    if (_evaluator.Truthy(left)) { SkipOperand(level + 1); left = new ClrValue(true); continue; }
                    left = new ClrValue(_evaluator.Truthy(ParseBinary(level + 1)));
                    continue;
                }
                left = _evaluator.Binary(op, left, ParseBinary(level + 1));
            }
        }

        /// <summary>短絡評価で右辺を評価せずに読み飛ばす（副作用のある呼び出しを走らせない）。</summary>
        private void SkipOperand(int level)
        {
            var depth = 0;
            SkipSpace();
            while (_position < _text.Length)
            {
                var c = _text[_position];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') { if (depth == 0) return; depth--; }
                else if (c == '"') { SkipString(); continue; }
                else if (depth == 0 && (Peek("&&") || Peek("||") || c is '?' or ':')) return;
                _position++;
            }
        }

        private void SkipString()
        {
            _position++;
            while (_position < _text.Length && _text[_position] != '"')
            {
                if (_text[_position] == '\\') _position++;
                _position++;
            }
            _position++;
        }

        private EValue ParseUnary()
        {
            SkipSpace();
            if (Accept("!")) return new ClrValue(!_evaluator.Truthy(ParseUnary()));
            if (Accept("-")) return _evaluator.Negate(ParseUnary());
            if (Accept("+")) return ParseUnary();
            return ParsePostfix(ParsePrimary());
        }

        private EValue ParsePostfix(EValue value)
        {
            while (true)
            {
                SkipSpace();
                if (Accept("."))
                {
                    var name = ParseIdentifier() ?? throw new EvaluationException("'.' の後に名前がありません。");
                    SkipSpace();
                    if (Accept("(")) value = _evaluator.Call(value, name, ParseArguments(")"));
                    else value = _evaluator.Member(value, name);
                    continue;
                }
                if (Accept("["))
                {
                    var index = ParseExpression();
                    Expect("]");
                    value = _evaluator.Index(value, index);
                    continue;
                }
                return value;
            }
        }

        private List<EValue> ParseArguments(string close)
        {
            var args = new List<EValue>();
            SkipSpace();
            if (Accept(close)) return args;
            while (true)
            {
                args.Add(ParseExpression());
                if (Accept(close)) return args;
                Expect(",");
            }
        }

        private EValue ParsePrimary()
        {
            SkipSpace();
            if (_position >= _text.Length) throw new EvaluationException("式が途中で終わっています。");
            var c = _text[_position];
            if (Accept("("))
            {
                var inner = ParseExpression();
                Expect(")");
                return inner;
            }
            if (c == '"' || c == '@' && Peek("@\"")) return new ClrValue(ParseString());
            if (c == '\'') return new ClrValue(ParseChar());
            if (char.IsDigit(c) || c == '.' && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1]))
                return ParseNumber();

            var name = ParseIdentifier() ?? throw new EvaluationException($"式を解釈できません（'{c}'）。");
            switch (name)
            {
                case "true": return new ClrValue(true);
                case "false": return new ClrValue(false);
                case "null": return ClrValue.Null;
            }
            SkipSpace();
            if (Accept("("))
            {
                // 修飾なしのメソッド呼び出し：this か宣言型のメソッド。
                var args = ParseArguments(")");
                var target = TryResolve("this") ?? _evaluator.DeclaringType() as EValue
                    ?? throw new EvaluationException($"'{name}' を呼べません。");
                return _evaluator.Call(target, name, args);
            }
            try { return _evaluator.ResolveIdentifier(name); }
            catch (EvaluationException) when (Peek("."))
            {
                // 名前空間付きの型名（System.DateTime.Now など）：型が見つかるまで名前を伸ばす。
                var full = name;
                var saved = _position;
                while (Accept("."))
                {
                    var part = ParseIdentifier();
                    if (part is null) break;
                    full += "." + part;
                    if (_evaluator.FindType(full) is { } type) return type;
                }
                _position = saved;
                throw;
            }
        }

        private EValue? TryResolve(string name)
        {
            try { return _evaluator.ResolveIdentifier(name); }
            catch (EvaluationException) { return null; }
        }

        private EValue ParseNumber()
        {
            var start = _position;
            if (Peek("0x") || Peek("0X"))
            {
                _position += 2;
                while (_position < _text.Length && Uri.IsHexDigit(_text[_position])) _position++;
                var hex = ulong.Parse(_text.Substring(start + 2, _position - start - 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                SkipSuffix();
                return new ClrValue(hex <= long.MaxValue ? (long)hex : (object)hex);
            }
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '_')) _position++;
            var floating = false;
            if (_position < _text.Length && _text[_position] == '.' && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1]))
            {
                floating = true;
                _position++;
                while (_position < _text.Length && char.IsDigit(_text[_position])) _position++;
            }
            if (_position < _text.Length && (_text[_position] is 'e' or 'E'))
            {
                floating = true;
                _position++;
                if (_position < _text.Length && _text[_position] is '+' or '-') _position++;
                while (_position < _text.Length && char.IsDigit(_text[_position])) _position++;
            }
            var literal = _text.Substring(start, _position - start).Replace("_", "");
            if (_position < _text.Length && _text[_position] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M') { floating = true; _position++; }
            else SkipSuffix();
            if (floating) return new ClrValue(double.Parse(literal, CultureInfo.InvariantCulture));
            if (long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return new ClrValue(l, l is >= int.MinValue and <= int.MaxValue ? "int" : "long");
            return new ClrValue(ulong.Parse(literal, CultureInfo.InvariantCulture));
        }

        private void SkipSuffix()
        {
            while (_position < _text.Length && (_text[_position] is 'u' or 'U' or 'l' or 'L')) _position++;
        }

        private string ParseString()
        {
            var verbatim = Accept("@");
            _position++; // "
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var c = _text[_position++];
                if (c == '"')
                {
                    if (verbatim && _position < _text.Length && _text[_position] == '"') { builder.Append('"'); _position++; continue; }
                    return verbatim ? builder.ToString() : Unescape(builder.ToString());
                }
                builder.Append(c);
                if (!verbatim && c == '\\' && _position < _text.Length) builder.Append(_text[_position++]);
            }
            throw new EvaluationException("文字列リテラルが閉じていません。");
        }

        private char ParseChar()
        {
            var end = _text.IndexOf('\'', _position + 1);
            while (end > 0 && _text[end - 1] == '\\' && end - 2 > _position) end = _text.IndexOf('\'', end + 1);
            if (end < 0) throw new EvaluationException("文字リテラルが閉じていません。");
            var body = Unescape(_text.Substring(_position + 1, end - _position - 1));
            _position = end + 1;
            if (body.Length != 1) throw new EvaluationException("文字リテラルは 1 文字です。");
            return body[0];
        }

        private string? ParseIdentifier()
        {
            SkipSpace();
            var start = _position;
            if (_position < _text.Length && _text[_position] == '@') _position++;
            if (_position >= _text.Length || !(char.IsLetter(_text[_position]) || _text[_position] == '_'))
            {
                _position = start;
                return null;
            }
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_')) _position++;
            return _text.Substring(start, _position - start).TrimStart('@');
        }

        private void SkipSpace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }

        private bool Peek(string token)
            => string.CompareOrdinal(_text, _position, token, 0, token.Length) == 0;

        private bool Accept(string token)
        {
            SkipSpace();
            if (!Peek(token)) return false;
            _position += token.Length;
            return true;
        }

        private void Expect(string token)
        {
            if (!Accept(token)) throw new EvaluationException($"'{token}' が必要です。");
        }
    }
}
