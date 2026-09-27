using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace sk0ya.Loomo.NetFxDebug.Debugger;

internal sealed class FieldMeta
{
    public FieldMeta(int token, string name, bool isStatic, bool isLiteral)
    {
        Token = token;
        Name = name;
        IsStatic = isStatic;
        IsLiteral = isLiteral;
    }

    public int Token { get; }
    public string Name { get; }
    public bool IsStatic { get; }
    public bool IsLiteral { get; }
}

internal sealed class PropertyMeta
{
    public PropertyMeta(string name, int getterToken, bool isStatic, int parameterCount)
    {
        Name = name;
        GetterToken = getterToken;
        IsStatic = isStatic;
        ParameterCount = parameterCount;
    }

    public string Name { get; }
    public int GetterToken { get; }
    public bool IsStatic { get; }
    public int ParameterCount { get; }
}

internal sealed class MethodMeta
{
    public MethodMeta(int token, string name, bool isStatic, string[] parameterNames, int declaringTypeToken, int genericArity)
    {
        Token = token;
        Name = name;
        IsStatic = isStatic;
        ParameterNames = parameterNames;
        DeclaringTypeToken = declaringTypeToken;
        GenericArity = genericArity;
    }

    public int Token { get; }
    public string Name { get; }
    public bool IsStatic { get; }
    public string[] ParameterNames { get; }
    public int DeclaringTypeToken { get; }
    public int GenericArity { get; }
}

internal sealed class TypeMeta
{
    public int Token { get; set; }
    /// <summary>名前空間付きの名前（入れ子は <c>Outer.Inner</c>、ジェネリックの <c>`1</c> は外す）。</summary>
    public string FullName { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsEnum { get; set; }
    public bool IsFlags { get; set; }
    public bool IsCompilerGenerated { get; set; }
    public string? DebuggerDisplay { get; set; }
    public IReadOnlyList<FieldMeta> Fields { get; set; } = Array.Empty<FieldMeta>();
    public IReadOnlyList<PropertyMeta> Properties { get; set; } = Array.Empty<PropertyMeta>();
    public IReadOnlyList<MethodMeta> Methods { get; set; } = Array.Empty<MethodMeta>();
    /// <summary>列挙型の名前と値（値は ulong へ正規化）。</summary>
    public IReadOnlyList<(string Name, ulong Value)> EnumValues { get; set; } = Array.Empty<(string, ulong)>();
}

/// <summary>
/// モジュール（アセンブリ）1 つのメタデータを System.Reflection.Metadata で読む。型名・フィールド・プロパティ・
/// メソッド名・引数名・DebuggerDisplay・列挙値など、表示と式評価に要る分だけを持つ。
/// ファイルはメモリへ読み込んでから開く（デバッグ中に対象のファイルを握らない）。
/// </summary>
internal sealed class ModuleMetadata
{
    private static readonly ConcurrentDictionary<string, ModuleMetadata?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly MetadataReader _reader;
    private readonly Dictionary<int, TypeMeta> _types = new();
    private readonly Dictionary<int, MethodMeta> _methods = new();
    private Dictionary<string, int>? _typesByName;

    private ModuleMetadata(PEReader pe)
    {
        Pe = pe;
        _reader = pe.GetMetadataReader();
    }

    private PEReader Pe { get; }

    public MetadataReader Reader => _reader;

    /// <summary>エントリポイントのメソッドトークン（無ければ 0）。</summary>
    public int EntryPointToken => Pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;

    public static ModuleMetadata? For(string modulePath)
        => Cache.GetOrAdd(modulePath, path =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(path)));
                return pe.HasMetadata ? new ModuleMetadata(pe) : null;
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        });

    public TypeMeta? Type(int typeDefToken)
    {
        lock (_types)
        {
            if (_types.TryGetValue(typeDefToken, out var cached)) return cached;
            TypeMeta? meta = null;
            try { meta = ReadType(typeDefToken); }
            catch (BadImageFormatException) { }
            if (meta is not null) _types[typeDefToken] = meta;
            return meta;
        }
    }

    public MethodMeta? Method(int methodDefToken)
    {
        lock (_methods)
        {
            if (_methods.TryGetValue(methodDefToken, out var cached)) return cached;
            var handle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(methodDefToken);
            if (handle.IsNil || MetadataTokens.GetRowNumber(handle) > _reader.MethodDefinitions.Count) return null;
            var method = ReadMethod(handle);
            _methods[methodDefToken] = method;
            return method;
        }
    }

    /// <summary>名前空間付きの型名から型定義のトークンを探す（入れ子は <c>Outer.Inner</c>／<c>Outer+Inner</c> どちらでも）。</summary>
    public int? FindType(string fullName)
    {
        lock (_types)
        {
            if (_typesByName is null)
            {
                _typesByName = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var handle in _reader.TypeDefinitions)
                {
                    var token = MetadataTokens.GetToken(handle);
                    var name = FullNameOf(handle);
                    if (!_typesByName.ContainsKey(name)) _typesByName[name] = token;
                }
            }
            return _typesByName.TryGetValue(fullName.Replace('+', '.'), out var found) ? found : null;
        }
    }

    /// <summary>名前空間を除いた単純名で型を探す（式の <c>Person.Created</c> のような短い書き方のため）。</summary>
    public IEnumerable<int> FindTypesBySimpleName(string simpleName)
    {
        FindType("");
        lock (_types)
        {
            return _typesByName!.Where(p => p.Key == simpleName || p.Key.EndsWith("." + simpleName, StringComparison.Ordinal))
                .Select(p => p.Value).ToList();
        }
    }

    private TypeMeta ReadType(int token)
    {
        var handle = (TypeDefinitionHandle)MetadataTokens.EntityHandle(token);
        var definition = _reader.GetTypeDefinition(handle);
        var meta = new TypeMeta
        {
            Token = token,
            FullName = FullNameOf(handle),
            Name = StripArity(_reader.GetString(definition.Name)),
        };

        var baseName = BaseTypeName(definition.BaseType);
        meta.IsEnum = baseName == "System.Enum";

        foreach (var attributeHandle in definition.GetCustomAttributes())
        {
            var attribute = _reader.GetCustomAttribute(attributeHandle);
            var name = AttributeTypeName(attribute);
            if (name == "System.Diagnostics.DebuggerDisplayAttribute")
                meta.DebuggerDisplay = FirstStringArgument(attribute);
            else if (name == "System.Runtime.CompilerServices.CompilerGeneratedAttribute")
                meta.IsCompilerGenerated = true;
            else if (name == "System.FlagsAttribute")
                meta.IsFlags = true;
        }

        var fields = new List<FieldMeta>();
        var enumValues = new List<(string, ulong)>();
        foreach (var fieldHandle in definition.GetFields())
        {
            var field = _reader.GetFieldDefinition(fieldHandle);
            var attributes = field.Attributes;
            var name = _reader.GetString(field.Name);
            var isStatic = (attributes & FieldAttributes.Static) != 0;
            var isLiteral = (attributes & FieldAttributes.Literal) != 0;
            fields.Add(new FieldMeta(MetadataTokens.GetToken(fieldHandle), name, isStatic, isLiteral));
            if (meta.IsEnum && isLiteral && !field.GetDefaultValue().IsNil)
                enumValues.Add((name, ConstantAsUInt64(_reader.GetConstant(field.GetDefaultValue()))));
        }
        meta.Fields = fields;
        meta.EnumValues = enumValues;

        var methods = definition.GetMethods().Select(ReadMethod).ToList();
        foreach (var method in methods) _methods[method.Token] = method;
        meta.Methods = methods;

        var properties = new List<PropertyMeta>();
        foreach (var propertyHandle in definition.GetProperties())
        {
            var property = _reader.GetPropertyDefinition(propertyHandle);
            var getter = property.GetAccessors().Getter;
            if (getter.IsNil) continue;
            var getterDefinition = _reader.GetMethodDefinition(getter);
            var isStatic = (getterDefinition.Attributes & MethodAttributes.Static) != 0;
            var parameterCount = property.DecodeSignature(new ParameterCountProvider(), null).ParameterTypes.Length;
            properties.Add(new PropertyMeta(_reader.GetString(property.Name), MetadataTokens.GetToken(getter),
                isStatic, parameterCount));
        }
        meta.Properties = properties;
        return meta;
    }

    private MethodMeta ReadMethod(MethodDefinitionHandle handle)
    {
        var method = _reader.GetMethodDefinition(handle);
        var parameters = new List<(int Sequence, string Name)>();
        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = _reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber > 0)
                parameters.Add((parameter.SequenceNumber, _reader.GetString(parameter.Name)));
        }
        var signature = method.DecodeSignature(new ParameterCountProvider(), null);
        var names = new string[signature.ParameterTypes.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = parameters.FirstOrDefault(p => p.Sequence == i + 1).Name ?? $"arg{i}";
        return new MethodMeta(MetadataTokens.GetToken(handle), _reader.GetString(method.Name),
            (method.Attributes & MethodAttributes.Static) != 0, names,
            MetadataTokens.GetToken(method.GetDeclaringType()), signature.GenericParameterCount);
    }

    private string FullNameOf(TypeDefinitionHandle handle)
    {
        var definition = _reader.GetTypeDefinition(handle);
        var name = StripArity(_reader.GetString(definition.Name));
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil) return FullNameOf(declaring) + "." + name;
        var ns = _reader.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private string? BaseTypeName(EntityHandle handle)
    {
        if (handle.IsNil) return null;
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var reference = _reader.GetTypeReference((TypeReferenceHandle)handle);
                var ns = _reader.GetString(reference.Namespace);
                var name = _reader.GetString(reference.Name);
                return ns.Length == 0 ? name : ns + "." + name;
            case HandleKind.TypeDefinition:
                return FullNameOf((TypeDefinitionHandle)handle);
            default:
                return null;
        }
    }

    private string? AttributeTypeName(CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var member = _reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return BaseTypeName(member.Parent);
            case HandleKind.MethodDefinition:
                var method = _reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return FullNameOf(method.GetDeclaringType());
            default:
                return null;
        }
    }

    /// <summary>属性の最初の固定引数が文字列ならそれを返す（DebuggerDisplay の書式文字列）。</summary>
    private string? FirstStringArgument(CustomAttribute attribute)
    {
        var blob = _reader.GetBlobReader(attribute.Value);
        if (blob.Length < 2 || blob.ReadUInt16() != 1) return null;
        try { return blob.ReadSerializedString(); }
        catch (BadImageFormatException) { return null; }
    }

    private ulong ConstantAsUInt64(Constant constant)
    {
        var blob = _reader.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.SByte => unchecked((ulong)blob.ReadSByte()),
            ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.Int16 => unchecked((ulong)blob.ReadInt16()),
            ConstantTypeCode.UInt16 => blob.ReadUInt16(),
            ConstantTypeCode.Int32 => unchecked((ulong)blob.ReadInt32()),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int64 => unchecked((ulong)blob.ReadInt64()),
            ConstantTypeCode.UInt64 => blob.ReadUInt64(),
            _ => 0,
        };
    }

    internal static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }

    /// <summary>シグネチャから引数の数だけ取る（型そのものは要らない）。</summary>
    private sealed class ParameterCountProvider : ISignatureTypeProvider<int, object?>
    {
        public int GetArrayType(int elementType, ArrayShape shape) => 0;
        public int GetByReferenceType(int elementType) => 0;
        public int GetFunctionPointerType(MethodSignature<int> signature) => 0;
        public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => 0;
        public int GetGenericMethodParameter(object? genericContext, int index) => 0;
        public int GetGenericTypeParameter(object? genericContext, int index) => 0;
        public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => 0;
        public int GetPinnedType(int elementType) => 0;
        public int GetPointerType(int elementType) => 0;
        public int GetPrimitiveType(PrimitiveTypeCode typeCode) => 0;
        public int GetSZArrayType(int elementType) => 0;
        public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => 0;
        public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => 0;
        public int GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => 0;
    }
}
