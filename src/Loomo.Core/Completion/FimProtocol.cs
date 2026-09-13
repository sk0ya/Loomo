using System.Text.Json;
using System.Text.Json.Serialization;

namespace sk0ya.Loomo.Core.Completion;

/// <summary>
/// 本体とワーカーのあいだの約束。<b>1 行 1 メッセージの JSON</b>——長さ prefix も枠組みも要らず、
/// 詰まったら行が届かないだけで、どちらの側も相手を待たない。
///
/// <para>やり取りするのは組み立て済みのプロンプトと生の出力だけ。プロンプトの組み立ても
/// 候補の選別も本体側に残してある（文脈を知っているのは本体で、ワーカーはモデルを回すだけ）。</para>
/// </summary>
public static class FimProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>生成の依頼。<paramref name="Id"/> は本体が振る通し番号で、応答の照合に使う。</summary>
    /// <param name="Id">通し番号。</param>
    /// <param name="Prompt">組み立て済みの FIM プロンプト。</param>
    /// <param name="MaxTokens">生成の上限トークン数。</param>
    public readonly record struct Request(long Id, string Prompt, int MaxTokens);

    /// <summary>待機中または実行中の依頼を取り消す通知。</summary>
    public readonly record struct Cancellation(long Id);

    /// <summary>生成の結果。<paramref name="Text"/> が null なら「出せなかった」。</summary>
    /// <param name="Id">対応する依頼の通し番号。</param>
    /// <param name="Text">モデルの生の出力（選別前）。</param>
    /// <param name="ReusedTokens">KV から再利用できたトークン数。</param>
    /// <param name="PrefillTokens">送り直したトークン数。</param>
    /// <param name="PrefillMs">送り直しにかかった時間。</param>
    /// <param name="GeneratedTokens">生成したトークン数。</param>
    /// <param name="GenerateMs">生成にかかった時間。</param>
    /// <param name="Error">組み立てや読み込みに失敗したときの理由（診断用）。</param>
    public readonly record struct Response(
        long Id,
        string? Text = null,
        int ReusedTokens = 0,
        int PrefillTokens = 0,
        long PrefillMs = 0,
        int GeneratedTokens = 0,
        long GenerateMs = 0,
        string? Error = null);

    public static string Serialize(in Request request) => JsonSerializer.Serialize(request, Json);

    public static string Serialize(in Cancellation cancellation)
        => JsonSerializer.Serialize(new CancellationEnvelope(true, cancellation.Id), Json);

    public static string Serialize(in Response response) => JsonSerializer.Serialize(response, Json);

    /// <summary>1 行を依頼として読む。壊れた行は null（相手の不調でこちらが落ちないため）。</summary>
    public static Request? ReadRequest(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var request = JsonSerializer.Deserialize<Request>(line, Json);
            return request.Id <= 0 || string.IsNullOrEmpty(request.Prompt) ? null : request;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>1 行を応答として読む。壊れた行は null。</summary>
    public static Response? ReadResponse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var response = JsonSerializer.Deserialize<Response>(line, Json);
            return response.Id <= 0 ? null : response;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>1 行を取り消し通知として読む。依頼・応答の行は null。</summary>
    public static Cancellation? ReadCancellation(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var message = JsonSerializer.Deserialize<CancellationEnvelope>(line, Json);
            return message.Cancel && message.Id > 0 ? new Cancellation(message.Id) : null;
        }
        catch (JsonException) { return null; }
    }

    private readonly record struct CancellationEnvelope(bool Cancel, long Id);
}
