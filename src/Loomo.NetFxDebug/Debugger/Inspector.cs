using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ClrDebug;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

/// <summary>
/// 値の「辿り方」。対象を走らせる（関数評価を含む）と値のオブジェクトは無効になるので、値は辿り方で持ち、
/// 世代（<see cref="Engine.Generation"/>）が変わったら辿り直す。
/// </summary>
internal abstract class ValueSource
{
    private CorDebugValue? _cached;
    private int _generation = -1;

    /// <summary>ウォッチ・イミディエイトへ貼るための式（分かるときだけ）。</summary>
    public string? EvaluateName { get; init; }

    public CorDebugValue? Get(Engine engine)
    {
        if (_generation == engine.Generation && _cached is not null) return _cached;
        try { _cached = Resolve(engine); }
        catch (Exception ex) when (ex is COMException or DebugException or InvalidCastException) { _cached = null; }
        _generation = engine.Generation;
        return _cached;
    }

    protected abstract CorDebugValue? Resolve(Engine engine);
}

/// <summary>強いハンドルで押さえたヒープのオブジェクト（評価で走らせても無効にならない）。</summary>
internal sealed class HandleSource : ValueSource
{
    private readonly CorDebugHandleValue _handle;
    public HandleSource(CorDebugHandleValue handle) => _handle = handle;
    protected override CorDebugValue? Resolve(Engine engine) => _handle;
}

/// <summary>フレームのローカル変数（スロット）。</summary>
internal sealed class LocalSource : ValueSource
{
    private readonly int _threadId, _frameIndex, _slot;
    public LocalSource(int threadId, int frameIndex, int slot) { _threadId = threadId; _frameIndex = frameIndex; _slot = slot; }
    protected override CorDebugValue? Resolve(Engine engine)
        => engine.FrameAt(_threadId, _frameIndex)?.Frame.GetLocalVariable(_slot);
}

/// <summary>フレームの引数（インスタンスメソッドは 0 が this）。</summary>
internal sealed class ArgumentSource : ValueSource
{
    private readonly int _threadId, _frameIndex, _index;
    public ArgumentSource(int threadId, int frameIndex, int index) { _threadId = threadId; _frameIndex = frameIndex; _index = index; }
    protected override CorDebugValue? Resolve(Engine engine)
        => engine.FrameAt(_threadId, _frameIndex)?.Frame.GetArgument(_index);
}

/// <summary>親の値のインスタンスフィールド。</summary>
internal sealed class FieldSource : ValueSource
{
    private readonly ValueSource _parent;
    private readonly ulong _moduleBase;
    private readonly int _classToken, _fieldToken;

    public FieldSource(ValueSource parent, ulong moduleBase, int classToken, int fieldToken)
    {
        _parent = parent;
        _moduleBase = moduleBase;
        _classToken = classToken;
        _fieldToken = fieldToken;
    }

    protected override CorDebugValue? Resolve(Engine engine)
    {
        var parent = _parent.Get(engine);
        if (parent is null || Inspector.Dereference(parent) is not CorDebugObjectValue obj) return null;
        var module = engine.Modules.FirstOrDefault(m => m.BaseAddress == _moduleBase);
        if (module is null) return null;
        var cls = module.Module.GetClassFromToken(_classToken);
        return obj.GetFieldValue(cls.Raw, _fieldToken);
    }
}

/// <summary>配列の要素（平らな位置）。</summary>
internal sealed class ElementSource : ValueSource
{
    private readonly ValueSource _parent;
    private readonly int _position;
    public ElementSource(ValueSource parent, int position) { _parent = parent; _position = position; }
    protected override CorDebugValue? Resolve(Engine engine)
    {
        var parent = _parent.Get(engine);
        return parent is not null && Inspector.Dereference(parent) is CorDebugArrayValue array
            ? array.GetElementAtPosition(_position)
            : null;
    }
}

/// <summary>静的フィールド。</summary>
internal sealed class StaticFieldSource : ValueSource
{
    private readonly CorDebugType _type;
    private readonly int _fieldToken, _threadId, _frameIndex;
    public StaticFieldSource(CorDebugType type, int fieldToken, int threadId, int frameIndex)
    {
        _type = type;
        _fieldToken = fieldToken;
        _threadId = threadId;
        _frameIndex = frameIndex;
    }
    protected override CorDebugValue? Resolve(Engine engine)
        => _type.GetStaticFieldValue(_fieldToken, engine.FrameAt(_threadId, _frameIndex)?.Frame.Raw);
}

/// <summary>評価し直すしかない値（構造体を返すプロパティ・式の結果など）。</summary>
internal sealed class ComputedSource : ValueSource
{
    private readonly Func<Engine, CorDebugValue?> _compute;
    public ComputedSource(Func<Engine, CorDebugValue?> compute) => _compute = compute;
    protected override CorDebugValue? Resolve(Engine engine) => _compute(engine);
}

/// <summary>DAP の variables 1 行分。</summary>
internal sealed class VariableItem
{
    public VariableItem(string name, string value, string? type, int reference, string? evaluateName = null)
    {
        Name = name;
        Value = value;
        Type = type;
        Reference = reference;
        EvaluateName = evaluateName;
    }

    public string Name { get; }
    public string Value { get; }
    public string? Type { get; }
    public int Reference { get; }
    public string? EvaluateName { get; }
}

/// <summary>variablesReference の中身。子の一覧を作れるもの。</summary>
internal interface IVariableContainer
{
    List<VariableItem> Children(Engine engine);
}

/// <summary>値の表示と子の展開。</summary>
internal static class Inspector
{
    /// <summary>参照・ボックスを外した中身。null 参照なら null。</summary>
    public static CorDebugValue? Dereference(CorDebugValue value)
    {
        var current = value;
        for (var i = 0; i < 8; i++)
        {
            if (current is CorDebugReferenceValue reference)
            {
                if (reference.IsNull) return null;
                current = reference.Dereference();
                continue;
            }
            if (current is CorDebugBoxValue box)
            {
                current = box.Object;
                continue;
            }
            break;
        }
        return current;
    }

    public static bool IsNullReference(CorDebugValue value)
        => value is CorDebugReferenceValue reference && reference.IsNull;

    public static CorDebugType? ExactType(CorDebugValue value)
    {
        try { return value.ExactType; }
        catch (Exception ex) when (ex is COMException or DebugException or InvalidCastException) { return null; }
    }

    /// <summary>C# の書き方に寄せた型名。</summary>
    public static string TypeName(Engine engine, CorDebugType? type)
    {
        if (type is null) return "?";
        try
        {
            switch (type.Type)
            {
                case CorElementType.Boolean: return "bool";
                case CorElementType.Char: return "char";
                case CorElementType.I1: return "sbyte";
                case CorElementType.U1: return "byte";
                case CorElementType.I2: return "short";
                case CorElementType.U2: return "ushort";
                case CorElementType.I4: return "int";
                case CorElementType.U4: return "uint";
                case CorElementType.I8: return "long";
                case CorElementType.U8: return "ulong";
                case CorElementType.R4: return "float";
                case CorElementType.R8: return "double";
                case CorElementType.I: return "IntPtr";
                case CorElementType.U: return "UIntPtr";
                case CorElementType.String: return "string";
                case CorElementType.Object: return "object";
                case CorElementType.SZArray:
                    return TypeName(engine, type.FirstTypeParameter) + "[]";
                case CorElementType.Array:
                    return TypeName(engine, type.FirstTypeParameter) + "[" + new string(',', Math.Max(0, type.Rank - 1)) + "]";
                case CorElementType.Ptr:
                    return TypeName(engine, type.FirstTypeParameter) + "*";
                case CorElementType.ByRef:
                    return TypeName(engine, type.FirstTypeParameter) + "&";
                case CorElementType.Class:
                case CorElementType.ValueType:
                    var meta = TypeMeta(engine, type);
                    var name = meta?.FullName ?? "?";
                    var parameters = type.TypeParameters;
                    if (parameters.Length == 0) return name;
                    if (name == "System.Nullable" && parameters.Length == 1) return TypeName(engine, parameters[0]) + "?";
                    return name + "<" + string.Join(", ", parameters.Select(p => TypeName(engine, p))) + ">";
                default:
                    return type.Type.ToString();
            }
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return "?"; }
    }

    public static TypeMeta? TypeMeta(Engine engine, CorDebugType type)
    {
        try
        {
            if (type.Type is not (CorElementType.Class or CorElementType.ValueType)) return null;
            var cls = type.Class;
            var module = engine.ModuleFor(cls.Module);
            return module?.Metadata?.Type(cls.Token);
        }
        catch (Exception ex) when (ex is COMException or DebugException) { return null; }
    }

    /// <summary>型の継承の鎖（自分から基底へ）。System.Object／ValueType／Enum で止める。</summary>
    public static IEnumerable<(CorDebugType Type, ModuleEntry Module, TypeMeta Meta)> Chain(Engine engine, CorDebugType? type)
    {
        // string は要素型が String で Class を持たない。メソッドを探せるよう System.String の型に置き換える。
        try
        {
            if (type?.Type == CorElementType.String) type = engine.WellKnownType("System.String");
        }
        catch (Exception ex) when (ex is COMException or DebugException) { }
        for (var current = type; current is not null;)
        {
            TypeMeta? meta = null;
            ModuleEntry? module = null;
            try
            {
                if (current.Type is CorElementType.Class or CorElementType.ValueType)
                {
                    module = engine.ModuleFor(current.Class.Module);
                    meta = module?.Metadata?.Type(current.Class.Token);
                }
            }
            catch (Exception ex) when (ex is COMException or DebugException) { }
            if (meta is null || module is null) yield break;
            if (meta.FullName is "System.Object" or "System.ValueType" or "System.Enum") yield break;
            yield return (current, module, meta);
            if (current.TryGetBase(out var next) != HRESULT.S_OK) yield break;
            current = next;
        }
    }

    // ---------------------------------------------------------------- 値の表示

    public static string Format(Engine engine, CorDebugValue value, Evaluator? evaluator, int depth = 0)
    {
        try
        {
            if (IsNullReference(value)) return "null";
            var inner = Dereference(value);
            if (inner is null) return "null";
            var type = ExactType(inner) ?? ExactType(value);

            if (inner is CorDebugStringValue text) return Quote(ReadString(text));
            if (inner is CorDebugGenericValue generic) return FormatPrimitive(ReadPrimitive(generic, inner.Type));
            if (inner is CorDebugArrayValue array)
            {
                var dims = array.GetDimensions(array.Rank);
                var element = TypeName(engine, type?.FirstTypeParameter);
                return "{" + element + "[" + string.Join(", ", dims) + "]}";
            }
            if (inner is CorDebugObjectValue obj && type is not null)
                return FormatObject(engine, obj, type, evaluator, depth);
            return "{" + TypeName(engine, type) + "}";
        }
        catch (Exception ex) when (ex is COMException or DebugException or InvalidCastException)
        {
            return "<値を読めません: " + ShortError(ex) + ">";
        }
    }

    private static string FormatObject(Engine engine, CorDebugObjectValue obj, CorDebugType type, Evaluator? evaluator, int depth)
    {
        var meta = TypeMeta(engine, type);
        var typeName = TypeName(engine, type);
        if (meta is null) return "{" + typeName + "}";

        if (meta.IsEnum) return FormatEnum(engine, obj, type, meta);

        // Nullable<T>
        if (meta.FullName == "System.Nullable")
        {
            var hasValue = ReadFieldPrimitive(engine, obj, type, "hasValue");
            if (hasValue is bool b && !b) return "null";
            var inner = FieldValue(engine, obj, type, "value");
            return inner is null ? "null" : Format(engine, inner, evaluator, depth + 1);
        }

        if (IsException(engine, type))
        {
            var message = FieldValue(engine, obj, type, "_message");
            var text = message is null || IsNullReference(message) ? "" : Format(engine, message, null, depth + 1);
            return "{" + typeName + ": " + text.Trim('"') + "}";
        }

        if (CollectionCount(engine, obj, type) is { } count) return "Count = " + count;

        if (evaluator is not null && depth < 2 && engine.CanEvaluate)
        {
            if (meta.DebuggerDisplay is { } display)
            {
                var rendered = evaluator.RenderDebuggerDisplay(display, obj, type, depth);
                if (rendered is not null) return rendered;
            }
            if (OverridesToString(engine, type))
            {
                var text = evaluator.CallToString(obj, type);
                if (text is not null) return "{" + text + "}";
            }
        }
        return "{" + typeName + "}";
    }

    private static string FormatEnum(Engine engine, CorDebugObjectValue obj, CorDebugType type, TypeMeta meta)
    {
        var raw = ReadFieldPrimitive(engine, obj, type, "value__");
        if (raw is null) return "{" + meta.FullName + "}";
        var value = ToUInt64(raw);
        var exact = meta.EnumValues.FirstOrDefault(v => v.Value == value);
        if (exact.Name is not null) return exact.Name;
        if (meta.IsFlags && value != 0)
        {
            var names = new List<string>();
            var rest = value;
            foreach (var (name, flag) in meta.EnumValues.Where(v => v.Value != 0).OrderByDescending(v => v.Value))
            {
                if ((rest & flag) == flag) { names.Add(name); rest &= ~flag; }
            }
            if (rest == 0) return string.Join(" | ", names.AsEnumerable().Reverse());
        }
        return FormatPrimitive(raw);
    }

    private static bool IsException(Engine engine, CorDebugType type)
        => AllTypes(engine, type).Any(m => m.FullName == "System.Exception");

    /// <summary>System.Object で止めない版の型の鎖（例外判定用）。</summary>
    private static IEnumerable<TypeMeta> AllTypes(Engine engine, CorDebugType type)
    {
        for (CorDebugType? current = type; current is not null;)
        {
            var meta = TypeMeta(engine, current);
            if (meta is null) yield break;
            yield return meta;
            if (current.TryGetBase(out var next) != HRESULT.S_OK) yield break;
            current = next;
        }
    }

    public static bool OverridesToString(Engine engine, CorDebugType type)
        => Chain(engine, type).Any(c => c.Meta.Methods.Any(m => m.Name == "ToString" && !m.IsStatic && m.ParameterNames.Length == 0));

    /// <summary>List／Dictionary など、評価せずに件数が分かるコレクション。</summary>
    public static int? CollectionCount(Engine engine, CorDebugObjectValue obj, CorDebugType type)
    {
        var meta = TypeMeta(engine, type);
        switch (meta?.FullName)
        {
            case "System.Collections.Generic.List":
                return ReadFieldPrimitive(engine, obj, type, "_size") is { } size ? (int?)Convert.ToInt32(size) : null;
            case "System.Collections.Generic.Dictionary":
                var count = ReadFieldPrimitive(engine, obj, type, "count");
                var free = ReadFieldPrimitive(engine, obj, type, "freeCount");
                return count is null ? null : Convert.ToInt32(count) - (free is null ? 0 : Convert.ToInt32(free));
            case "System.Collections.Generic.HashSet":
                var hashCount = ReadFieldPrimitive(engine, obj, type, "m_count");
                var hashFree = ReadFieldPrimitive(engine, obj, type, "m_freeCount") ?? 0;
                return hashCount is null ? null : Convert.ToInt32(hashCount) - Convert.ToInt32(hashFree);
            default:
                return null;
        }
    }

    // ---------------------------------------------------------------- フィールドの読み取り

    public static CorDebugValue? FieldValue(Engine engine, CorDebugObjectValue obj, CorDebugType type, string fieldName)
    {
        foreach (var (declaring, module, meta) in Chain(engine, type))
        {
            var field = meta.Fields.FirstOrDefault(f => !f.IsStatic && f.Name == fieldName);
            if (field is null) continue;
            try { return obj.GetFieldValue(declaring.Class.Raw, field.Token); }
            catch (Exception ex) when (ex is COMException or DebugException) { return null; }
        }
        return null;
    }

    public static object? ReadFieldPrimitive(Engine engine, CorDebugObjectValue obj, CorDebugType type, string fieldName)
    {
        var value = FieldValue(engine, obj, type, fieldName);
        if (value is null) return null;
        var inner = Dereference(value);
        return inner is CorDebugGenericValue generic ? ReadPrimitive(generic, inner.Type) : null;
    }

    // ---------------------------------------------------------------- プリミティブ

    public static string ReadString(CorDebugStringValue value)
    {
        var length = value.Length;
        return length == 0 ? "" : value.GetString(length);
    }

    public static object? ReadPrimitive(CorDebugGenericValue value, CorElementType type)
    {
        var size = value.Size;
        var buffer = Marshal.AllocHGlobal(Math.Max(size, 8));
        try
        {
            value.GetValue(buffer);
            return type switch
            {
                CorElementType.Boolean => Marshal.ReadByte(buffer) != 0,
                CorElementType.Char => (char)Marshal.ReadInt16(buffer),
                CorElementType.I1 => (sbyte)Marshal.ReadByte(buffer),
                CorElementType.U1 => Marshal.ReadByte(buffer),
                CorElementType.I2 => Marshal.ReadInt16(buffer),
                CorElementType.U2 => (ushort)Marshal.ReadInt16(buffer),
                CorElementType.I4 => Marshal.ReadInt32(buffer),
                CorElementType.U4 => (uint)Marshal.ReadInt32(buffer),
                CorElementType.I8 => Marshal.ReadInt64(buffer),
                CorElementType.U8 => (ulong)Marshal.ReadInt64(buffer),
                CorElementType.R4 => BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(buffer)), 0),
                CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)),
                CorElementType.I => size == 8 ? new IntPtr(Marshal.ReadInt64(buffer)) : new IntPtr(Marshal.ReadInt32(buffer)),
                CorElementType.U => size == 8 ? new UIntPtr((ulong)Marshal.ReadInt64(buffer)) : new UIntPtr((uint)Marshal.ReadInt32(buffer)),
                _ => null,
            };
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>プリミティブを書き込む（setVariable）。文字列から型に合わせて読み替える。</summary>
    public static bool WritePrimitive(CorDebugGenericValue value, CorElementType type, string text, out string? error)
    {
        error = null;
        text = text.Trim();
        byte[] bytes;
        try
        {
            bytes = type switch
            {
                CorElementType.Boolean => new[] { (byte)(bool.Parse(text) ? 1 : 0) },
                CorElementType.Char => BitConverter.GetBytes(ParseChar(text)),
                CorElementType.I1 => new[] { unchecked((byte)sbyte.Parse(text, CultureInfo.InvariantCulture)) },
                CorElementType.U1 => new[] { byte.Parse(text, CultureInfo.InvariantCulture) },
                CorElementType.I2 => BitConverter.GetBytes(short.Parse(text, CultureInfo.InvariantCulture)),
                CorElementType.U2 => BitConverter.GetBytes(ushort.Parse(text, CultureInfo.InvariantCulture)),
                CorElementType.I4 => BitConverter.GetBytes(int.Parse(text, CultureInfo.InvariantCulture)),
                CorElementType.U4 => BitConverter.GetBytes(uint.Parse(text.TrimEnd('u', 'U'), CultureInfo.InvariantCulture)),
                CorElementType.I8 => BitConverter.GetBytes(long.Parse(text.TrimEnd('l', 'L'), CultureInfo.InvariantCulture)),
                CorElementType.U8 => BitConverter.GetBytes(ulong.Parse(text.TrimEnd('u', 'U', 'l', 'L'), CultureInfo.InvariantCulture)),
                CorElementType.R4 => BitConverter.GetBytes(float.Parse(text.TrimEnd('f', 'F'), CultureInfo.InvariantCulture)),
                CorElementType.R8 => BitConverter.GetBytes(double.Parse(text.TrimEnd('d', 'D'), CultureInfo.InvariantCulture)),
                _ => throw new FormatException("この型の値は書き換えられません。"),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            error = ex.Message;
            return false;
        }
        var buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, value.Size));
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            value.SetValue(buffer);
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static char ParseChar(string text)
    {
        if (text.Length >= 3 && text[0] == '\'' && text[text.Length - 1] == '\'') return text[1];
        if (text.Length == 1) return text[0];
        return (char)int.Parse(text, CultureInfo.InvariantCulture);
    }

    public static string FormatPrimitive(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        char c => ((int)c).ToString(CultureInfo.InvariantCulture) + " '" + EscapeChar(c) + "'",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        string s => Quote(s),
        _ => value.ToString() ?? "",
    };

    public static ulong ToUInt64(object value) => value switch
    {
        sbyte v => unchecked((ulong)v),
        short v => unchecked((ulong)v),
        int v => unchecked((ulong)v),
        long v => unchecked((ulong)v),
        byte v => v,
        ushort v => v,
        uint v => v,
        ulong v => v,
        char v => v,
        bool v => v ? 1UL : 0UL,
        _ => 0,
    };

    public static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text.Length > 4096 ? text.Substring(0, 4096) : text) builder.Append(EscapeChar(c, inString: true));
        if (text.Length > 4096) builder.Append("…");
        return builder.Append('"').ToString();
    }

    private static string EscapeChar(char c, bool inString = false) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\0' => "\\0",
        '\\' => "\\\\",
        '"' when inString => "\\\"",
        '\'' when !inString => "\\'",
        _ => c.ToString(),
    };

    public static string ShortError(Exception ex)
        => ex is DebugException debug ? debug.HResult.ToString() : ex.Message;

    // ---------------------------------------------------------------- 名前の後始末

    /// <summary>コンパイラが付けた名前を見せる名前へ。null なら隠す。
    /// <c>&lt;Name&gt;k__BackingField</c>→Name、<c>&lt;x&gt;5__2</c>→x（async/iterator に持ち上げたローカル）、
    /// <c>&lt;&gt;4__this</c>→this。それ以外の <c>&lt;</c> で始まるもの（状態番号・builder 等）は隠す。</summary>
    public static string? DisplayFieldName(string name, out bool isBackingField)
    {
        isBackingField = false;
        if (!name.StartsWith("<", StringComparison.Ordinal)) return name;
        if (name == "<>4__this") return "this";
        var close = name.IndexOf('>');
        if (close > 1)
        {
            var inner = name.Substring(1, close - 1);
            var suffix = name.Substring(close + 1);
            if (suffix == "k__BackingField") { isBackingField = true; return inner; }
            if (suffix.StartsWith("5__", StringComparison.Ordinal)) return inner;
        }
        return null;
    }
}
