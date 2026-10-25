namespace sk0ya.Loomo.App.Views;

/// <summary>ShellWindow: 検索パネルのターミナル横断検索のうち、まだ表示していないタブの一致へ戻る部分（§24.67）。</summary>
public partial class ShellWindow {
    /// <summary>まだ表示していなかったタブの一致へ。ホストの画面での行番号は端末とは揃わないので、表示して繋ぎ直した画面（再生）が
    /// 届くのを最大 3 秒待ち、行の頭の文字で探し直して一番下の一致を選ぶ。</summary>
    private async Task RevealUnseenTerminalHitAsync(TerminalTab tab, TerminalSearchHit hit) {
        var anchor = TerminalSearchHits.Anchor(hit.LineText);
        for (var attempt = 0; attempt < 15 && anchor is not null; attempt++) {
            await Task.Delay(200);
            var found = tab.View.FindMatches(anchor, caseSensitive: true);
            if (found.Count > 0) {
                tab.View.SelectMatch(found[^1]);
                break;
            }
        }
        tab.View.FocusTerminal();
    }
}
