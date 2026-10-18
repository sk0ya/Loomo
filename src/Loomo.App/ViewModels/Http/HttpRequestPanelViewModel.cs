using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sk0ya.Loomo.App.Services;
using sk0ya.Loomo.Core.Http;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>.http パネルのリクエスト1行（§24.30）。</summary>
public sealed partial class HttpRequestRowViewModel : ObservableObject
{
    public HttpRequestRowViewModel(HttpRequestBlock block, string key)
    {
        _block = block;
        Key = key;
    }

    public string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Method), nameof(Url), nameof(Label))]
    private HttpRequestBlock _block;

    public string Method => Block.Method;

    /// <summary>書かれたままの URL（<c>{{host}}</c> を残す——ファイルと見比べるための表示）。</summary>
    public string Url => Block.Url;

    /// <summary>名前か見出し（無ければ空）。</summary>
    public string Label => Block.Name is { } name ? "@" + name : Block.Title ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBadge), nameof(IsFailure), nameof(HasResult))]
    private HttpExchangeResult? _result;

    [ObservableProperty] private bool _isRunning;

    public bool HasResult => Result is not null;
    public bool IsFailure => Result is { } r && !r.IsSuccess;

    /// <summary>行の右端に出す最後の結果（「200」「×」）。</summary>
    public string StatusBadge => Result switch
    {
        null => "",
        { StatusCode: { } code } => code.ToString(),
        _ => "×",
    };
}

/// <summary>
/// .http パネル（§24.30）。リクエストの一覧と、選んだリクエストの最後の応答。送受信と応答の保持は
/// <see cref="HttpEditorSupport"/>（工場）が持ち、ここはそれを1表示面ぶんに映すだけ。
/// </summary>
public sealed partial class HttpRequestPanelViewModel : ObservableObject
{
    private readonly HttpEditorSupport _owner;
    private HttpRequestDocument _document = new(new Dictionary<string, string>(), []);

    public HttpRequestPanelViewModel(HttpEditorSupport owner) => _owner = owner;

    public ObservableCollection<HttpRequestRowViewModel> Requests { get; } = new();

    [ObservableProperty] private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(SendSelectedCommand))]
    private HttpRequestRowViewModel? _selected;

    partial void OnSelectedChanged(HttpRequestRowViewModel? value) => RaiseResultChanged();

    /// <summary>応答の欄でヘッダーを見せるか（既定は本文）。</summary>
    [ObservableProperty] private bool _showHeaders;

    partial void OnShowHeadersChanged(bool value) => OnPropertyChanged(nameof(ResultText));

    public bool HasResult => Result is not null;
    public bool IsResultFailure => Result is { } r && !r.IsSuccess;

    /// <summary>送れなかった（応答が無い）。欄には理由の文が出る。</summary>
    public bool IsResultError => Result?.Error is not null;

    /// <summary>応答の1行目（「200 OK」）。</summary>
    public string ResultStatus => Result?.StatusLine ?? "";

    /// <summary>応答の添え書き（「123 ms · 4.2 KB · HTTP/1.1 · 14:03:21」）。</summary>
    public string ResultMeta => Result is not { } r
        ? ""
        : string.Join(" · ", new[]
        {
            $"{r.Elapsed.TotalMilliseconds:0} ms",
            r.StatusCode is null ? null : HttpRequestRunner.FormatSize(r.BodyBytes),
            r.HttpVersion,
            r.StartedAt.ToString("HH:mm:ss"),
        }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>欄の中身（本文かヘッダー。送れなかったときはその理由）。</summary>
    public string ResultText => Result switch
    {
        null => "",
        { Error: { } error } => error,
        { } r when ShowHeaders => HeadersText(r),
        { IsTruncated: true } r => r.DisplayBody + "\n\n…（大きいので途中までです。全文は「エディタで開く」）",
        { } r => r.DisplayBody,
    };

    /// <summary>送る前に解決できなかった変数（送った URL・ヘッダーに <c>{{…}}</c> が残っている）。</summary>
    public string? ResultWarning => Result?.Request.Unresolved is { Count: > 0 } names
        ? $"未定義の変数: {string.Join(", ", names)}"
        : null;

    /// <summary>送った先（変数を解決した URL）。</summary>
    public string ResultRequestLine => Result is { } r ? $"{r.Request.Method} {r.Request.Url}" : "";

    private void RaiseResultChanged()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(IsResultFailure));
        OnPropertyChanged(nameof(IsResultError));
        OnPropertyChanged(nameof(ResultStatus));
        OnPropertyChanged(nameof(ResultMeta));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(ResultWarning));
        OnPropertyChanged(nameof(ResultRequestLine));
    }

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Requests.Count == 0;

    /// <summary>選んだリクエストの最後の応答。</summary>
    public HttpExchangeResult? Result => Selected?.Result;

    /// <summary>エディタの本文が変わった（PrepareAsync の反映）。行は鍵で突き合わせて使い回し、選択と応答を保つ。</summary>
    public void Update(string filePath, HttpRequestDocument document)
    {
        var fileChanged = !string.Equals(FilePath, filePath, StringComparison.OrdinalIgnoreCase);
        _document = document;
        FilePath = filePath;
        var selectedKey = fileChanged ? null : Selected?.Key;
        var existing = fileChanged
            ? new Dictionary<string, HttpRequestRowViewModel>()
            : Requests.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());

        var rows = new List<HttpRequestRowViewModel>(document.Requests.Count);
        foreach (var block in document.Requests)
        {
            var key = HttpEditorSupport.KeyOf(document, block);
            if (existing.Remove(key, out var row))
                row.Block = block;
            else
                row = new HttpRequestRowViewModel(block, key);
            row.Result = _owner.LastResult(filePath, key);
            row.IsRunning = _owner.IsRunning(filePath, key);
            rows.Add(row);
        }
        // 同じ並びなら Clear しない（ListBox の選択とスクロールを揺らさない）
        if (!rows.SequenceEqual(Requests))
        {
            Requests.Clear();
            foreach (var row in rows)
                Requests.Add(row);
        }
        Selected = rows.FirstOrDefault(r => r.Key == selectedKey) ?? (Selected is { } s && rows.Contains(s) ? s : rows.FirstOrDefault());
        OnPropertyChanged(nameof(IsEmpty));
        RaiseResultChanged();
    }

    /// <summary>工場から「状態が変わった」。自分のファイルの行だけ引き直す。</summary>
    public void Refresh(HttpRequestStateChanged change)
    {
        if (FilePath is null || HttpEditorSupport.Normalize(FilePath) != change.File)
            return;
        foreach (var row in Requests.Where(r => r.Key == change.Key))
        {
            row.Result = _owner.LastResult(FilePath, row.Key);
            row.IsRunning = _owner.IsRunning(FilePath, row.Key);
        }
        if (Selected?.Key == change.Key)
            RaiseResultChanged();
    }

    /// <summary>キャレットから送った行を選んで見せる。</summary>
    public void Select(HttpRequestStateChanged change)
    {
        if (FilePath is null || HttpEditorSupport.Normalize(FilePath) != change.File)
            return;
        if (Requests.FirstOrDefault(r => r.Key == change.Key) is { } row)
            Selected = row;
    }

    [RelayCommand]
    private async Task Send(HttpRequestRowViewModel? row)
    {
        if (row is null || FilePath is null)
            return;
        Selected = row;
        if (_owner.IsRunning(FilePath, row.Key))
        {
            _owner.Cancel(FilePath, row.Key);
            row.IsRunning = false;
            return;
        }
        await _owner.SendAsync(FilePath, _document, row.Block);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task SendSelected() => Send(Selected);

    [RelayCommand]
    private void PinToPegboard()
    {
        if (Result is { } result)
            _owner.RequestPegboard(new HttpResponseMaterial(MaterialTitle(result), MaterialText(result), result));
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (Result is { } result)
            _owner.RequestOpenInEditor(new HttpResponseMaterial(MaterialTitle(result), result.IsBinary ? result.DisplayBody : Formatted(result), result));
    }

    [RelayCommand]
    private void CopyBody()
    {
        if (Result is not { } result)
            return;
        try { Clipboard.SetText(ShowHeaders ? HeadersText(result) : Formatted(result)); }
        catch { /* クリップボード占有中は無視 */ }
    }

    /// <summary>出典の1行（「GET https://api/x → 200 OK · 10/18 14:03」）。</summary>
    public static string MaterialTitle(HttpExchangeResult result)
        => $"{result.Request.Method} {result.Request.Url} → {result.StatusLine} · {result.StartedAt:M/d HH:mm}";

    /// <summary>ペグボードへ残す中身。応答だけでは「何を投げた答えか」が残らないので、リクエスト行を頭に付ける。</summary>
    public static string MaterialText(HttpExchangeResult result)
    {
        var sb = new StringBuilder();
        sb.Append(result.Request.Method).Append(' ').AppendLine(result.Request.Url);
        sb.Append(result.HttpVersion ?? "").Append(' ').AppendLine(result.StatusLine);
        sb.AppendLine();
        sb.Append(result.Error ?? Formatted(result));
        return sb.ToString().TrimEnd();
    }

    /// <summary>整形した本文の全文（表示は切り詰めることがあるので、こちらは整形し直す）。</summary>
    private static string Formatted(HttpExchangeResult result)
        => result.IsBinary ? result.DisplayBody : HttpRequestRunner.Pretty(result.BodyText, result.ContentType);

    public static string HeadersText(HttpExchangeResult result)
        => string.Join("\n", result.Headers.Select(h => $"{h.Key}: {h.Value}"));
}
