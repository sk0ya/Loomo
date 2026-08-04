using System.Collections.Generic;
using sk0ya.Loomo.Core.Debug;

namespace sk0ya.Loomo.Services.Debug;

/// <summary>
/// <c>setBreakpoints</c> の <c>breakpoints</c> 配列を組み立てる（netcoredbg / js-debug 共通）。
///
/// <para><b>任意項目（condition / hitCondition / logMessage）は値が無ければキーごと省略する。</b>
/// 明示的な <c>null</c> を書くと netcoredbg は
/// <c>can't parse: [json.exception.type_error.302] type must be string, but is null</c> で
/// <b>リクエスト全体</b>を失敗させる。つまり条件なしブレークポイントが 1 件でもあると、そのソースの
/// setBreakpoints がまるごと通らず「ブレークポイントを置いても止まらない」状態になる。
/// 構成フェーズ（起動時の一括送信）で起きると失敗が画面に出にくいので、ここで確実に落とす。</para>
/// </summary>
internal static class DapBreakpointPayload
{
    /// <summary>有効な永続ブレークポイント（条件付き）＋一時行（Run to Cursor・条件なし）を 1 つの配列にする。
    /// 無効な行は除外し、永続行と重なる一時行は永続を優先して重複させない。</summary>
    public static object[] Build(IReadOnlyList<DebugBreakpoint> bps, IReadOnlyCollection<int>? tempLines)
    {
        var items = new List<Dictionary<string, object>>();
        var lines = new HashSet<int>();

        foreach (var b in bps)
        {
            if (!b.Enabled || !lines.Add(b.Line)) continue;
            var item = new Dictionary<string, object> { ["line"] = b.Line };
            AddIfPresent(item, "condition", b.Condition);
            AddIfPresent(item, "hitCondition", b.HitCondition);
            AddIfPresent(item, "logMessage", b.LogMessage);
            items.Add(item);
        }

        if (tempLines is { Count: > 0 })
            foreach (var line in tempLines)
                if (lines.Add(line))
                    items.Add(new Dictionary<string, object> { ["line"] = line });

        return items.ToArray();
    }

    private static void AddIfPresent(Dictionary<string, object> item, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) item[key] = value;
    }
}
