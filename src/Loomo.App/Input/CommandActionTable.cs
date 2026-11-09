using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace sk0ya.Loomo.App.Input;

/// <summary>コマンド Id → 実体の表。組み込みの表に無い Id は <c>resolve</c> に訊く——設定から足したコマンド
/// （ブラウザのスクリプト、§24.106）は起動後に増えたり消えたりするので、固定の表には載せられない。</summary>
public sealed class CommandActionTable : IReadOnlyDictionary<string, Action>
{
    private readonly IReadOnlyDictionary<string, Action> _builtIn;
    private readonly Func<string, Action?> _resolve;
    private readonly Func<IEnumerable<string>> _dynamicIds;

    public CommandActionTable(IReadOnlyDictionary<string, Action> builtIn, Func<string, Action?> resolve, Func<IEnumerable<string>> dynamicIds)
    {
        _builtIn = builtIn;
        _resolve = resolve;
        _dynamicIds = dynamicIds;
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out Action value)
    {
        if (_builtIn.TryGetValue(key, out value))
            return true;
        value = _resolve(key);
        return value is not null;
    }

    public Action this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
    public bool ContainsKey(string key) => TryGetValue(key, out _);
    public IEnumerable<string> Keys => _builtIn.Keys.Concat(_dynamicIds().Where(id => !_builtIn.ContainsKey(id)));
    public IEnumerable<Action> Values => Keys.Select(k => this[k]);
    public int Count => Keys.Count();

    public IEnumerator<KeyValuePair<string, Action>> GetEnumerator()
        => Keys.Select(k => new KeyValuePair<string, Action>(k, this[k])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
