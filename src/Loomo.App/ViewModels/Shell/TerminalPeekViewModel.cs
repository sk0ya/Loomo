using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace sk0ya.Loomo.App.ViewModels;

/// <summary>
/// ターミナルのタブを切り替えずに覗く窓（§24.108・zellij 0.46.0 の Session Previews）。そのタブの画面の末尾を出し、
/// 1 行の入力欄から文字列を送る。いま前に出ているペインも、そのタブの並びも動かさない——承認待ちの Claude Code に答える、
/// 裏で流しているビルドの最後を確かめる、をペインを行き来せずに済ませる。
/// <para>画面はホストの画面モデルから取る（<see cref="_fetch"/>）。開いている間は少しずつ取り直し、送った直後は早めに取り直す。</para>
/// <para>送るのは入力欄で確定した文字列と Enter だけ。Ctrl+C などの制御文字は送らない（押し間違いで頼みごとを捨てない・§24.54 と同じ）。</para>
/// </summary>
public sealed partial class TerminalPeekViewModel : ObservableObject, IDisposable
{
    /// <summary>覗く行数。小窓で読み切れる分だけ。</summary>
    public const int Lines = 18;

    /// <summary>開いている間に取り直す間隔。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(900);

    private readonly Func<Task<string?>> _fetch;
    private readonly Func<string, bool> _send;
    private readonly DispatcherTimer _timer;
    private bool _fetching;
    private bool _disposed;

    /// <param name="fetch">画面の末尾を取る。取れなければ null。</param>
    /// <param name="send">そのタブへ文字列を送る（送れたら true）。</param>
    public TerminalPeekViewModel(Guid tabId, string title, Func<Task<string?>> fetch, Func<string, bool> send)
    {
        TabId = tabId;
        Title = title;
        _fetch = fetch;
        _send = send;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
    }

    public Guid TabId { get; }
    public string Title { get; }

    [ObservableProperty] private string _screenText = "読み込み中…";
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isUnavailable;

    /// <summary>送ったあと、画面に出るまでの間の短い知らせ。</summary>
    [ObservableProperty] private string _status = "";

    /// <summary>画面がまだ取れていない・取れなかったときの説明。</summary>
    public const string UnavailableMessage =
        "このタブの画面を取れませんでした。常駐ホストが古い（この機能より前に起動した）か、動いていません。"
        + "Loomo をすべて閉じて開き直すと、新しいホストで覗けるようになります。";

    /// <summary>開いた：すぐ 1 度取り、以後は間隔をあけて取り直す。</summary>
    public async Task StartAsync()
    {
        await RefreshAsync();
        if (!_disposed)
            _timer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_fetching || _disposed) return;
        _fetching = true;
        try
        {
            var text = await _fetch();
            if (_disposed) return;
            IsUnavailable = text is null;
            ScreenText = text switch
            {
                null => UnavailableMessage,
                { Length: 0 } => "（画面は空です）",
                _ => text,
            };
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>入力欄の文字列を送って Enter する（空なら Enter だけ）。</summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input;
        // 文字列と Enter を分けて送る。続けて一度に送ると、貼り付けとみなして Enter を改行として受け取るプログラム（Claude Code 等）がある。
        if (text.Length > 0 && !_send(text))
        {
            Status = "送れませんでした（タブが閉じられたかもしれません）。";
            return;
        }
        if (text.Length > 0)
            await Task.Delay(60);
        if (!_send("\r"))
        {
            Status = "送れませんでした（タブが閉じられたかもしれません）。";
            return;
        }
        Input = "";
        Status = text.Length > 0 ? $"送りました：{text}" : "Enter を送りました";
        await Task.Delay(250);
        await RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
