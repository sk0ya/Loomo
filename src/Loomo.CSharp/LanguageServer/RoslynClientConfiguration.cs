namespace sk0ya.Loomo.CSharp.LanguageServer;

/// <summary>
/// Roslyn Language Server の <c>workspace/configuration</c> に Loomo が答える値（設計書 §36）。
///
/// <para>以前は全項目 null（＝サーバー既定）を返していた。Roslyn は初期化直後に 158 項目を問い合わせてくる
/// （2026-10-11 実測）。section は <c>csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters</c> や
/// <c>navigation.dotnet_navigate_to_decompiled_sources</c> の形で、言語の接頭辞（<c>csharp|</c>／
/// <c>visual_basic|</c>）とグループ（<c>inlay_hints.</c>）の後ろの<b>オプション名</b>で決める。</para>
///
/// <para>値は文字列で返す——Roslyn は文字列を各オプションの型へ解析する（bool なら <c>"true"</c>）。
/// 列挙型のオプションへ <c>"true"</c> を返すと「解析できない」警告になる（実測）ので、知らない名前は null。</para>
///
/// <para>インレイヒントはここでは常に有効にしておく。表示するかどうかは Editor 側の <c>inlayhints</c>
/// （設定の「インレイヒント」）が決め、OFF なら要求そのものが飛ばない。</para>
/// </summary>
public static class RoslynClientConfiguration
{
    private static readonly IReadOnlyDictionary<string, string> Values =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 引数名のヒント（Rider の Parameter Name Hints）。名前で意図が読める引数には出さない。
            ["dotnet_enable_inlay_hints_for_parameters"] = "true",
            ["dotnet_enable_inlay_hints_for_literal_parameters"] = "true",
            ["dotnet_enable_inlay_hints_for_indexer_parameters"] = "true",
            ["dotnet_enable_inlay_hints_for_object_creation_parameters"] = "true",
            ["dotnet_enable_inlay_hints_for_other_parameters"] = "false",
            ["dotnet_suppress_inlay_hints_for_parameters_that_differ_only_by_suffix"] = "true",
            ["dotnet_suppress_inlay_hints_for_parameters_that_match_method_intent"] = "true",
            ["dotnet_suppress_inlay_hints_for_parameters_that_match_argument_name"] = "true",
            // 型のヒント（var・ラムダ引数）。new() の型や collection expression は煩いので出さない。
            ["csharp_enable_inlay_hints_for_types"] = "true",
            ["csharp_enable_inlay_hints_for_implicit_variable_types"] = "true",
            ["csharp_enable_inlay_hints_for_lambda_parameter_types"] = "true",
            ["csharp_enable_inlay_hints_for_implicit_object_creation"] = "false",
            ["csharp_enable_inlay_hints_for_collection_expressions"] = "false",
            // 未 using の名前空間の型も補完に出し、確定時に using を足す（Rider の Import Completion）。
            ["dotnet_show_completion_items_from_unimported_namespaces"] = "true",
            // ライブラリの定義へ飛んだら逆コンパイル結果（SourceLink／埋め込みソースがあればそちら）を開く。
            ["dotnet_navigate_to_decompiled_sources"] = "true",
            ["dotnet_navigate_to_source_link_and_embedded_sources"] = "true",
        };

    /// <summary>section に対する値。Loomo が決めない項目は null（サーバー既定）。</summary>
    public static object? Resolve(string? section)
    {
        if (string.IsNullOrEmpty(section))
            return null;
        var bar = section.IndexOf('|');
        var path = bar >= 0 ? section[(bar + 1)..] : section;
        var dot = path.LastIndexOf('.');
        var option = dot >= 0 ? path[(dot + 1)..] : path;
        return Values.TryGetValue(option, out var value) ? value : null;
    }
}
