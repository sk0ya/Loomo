using System.Reflection;

namespace sk0ya.Loomo.App.Services;

/// <summary>インスタンスのラッパーフィールドから、指定型のコントローラーを返す唯一のプロパティを辿る。</summary>
internal sealed class ReflectedControllerAccessor
{
    private readonly FieldInfo _wrapperField;
    private readonly PropertyInfo _controllerProperty;

    private ReflectedControllerAccessor(FieldInfo wrapperField, PropertyInfo controllerProperty)
    {
        _wrapperField = wrapperField;
        _controllerProperty = controllerProperty;
    }

    public static ReflectedControllerAccessor? Resolve(Type hostType, Type controllerType)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var candidates = hostType.GetFields(members)
            .Select(field => new
            {
                Field = field,
                Property = field.FieldType.GetProperties(members)
                    .FirstOrDefault(property => property.CanRead
                        && controllerType.IsAssignableFrom(property.PropertyType)),
            })
            .Where(candidate => candidate.Property is not null)
            .ToArray();
        return candidates.Length == 1
            ? new ReflectedControllerAccessor(candidates[0].Field, candidates[0].Property!)
            : null;
    }

    public object? Get(object host)
    {
        try
        {
            return _wrapperField.GetValue(host) is { } wrapper
                ? _controllerProperty.GetValue(wrapper)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
