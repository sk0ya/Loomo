using System.Text;
using System.Text.Json;

namespace sk0ya.Loomo.App.Services;

/// <summary>ブラウザペインのマウスジェスチャー（§24.105）。右ボタンを押したまま描いた向きの並びを、
/// コマンドカタログの Id へ結んで実行する（Vivaldi 流）。
///
/// <para><b>ページの中で取る</b>：WebView2 は別の HWND（合成版でも入力は Chromium が先に食う）なので、
/// WPF のマウスイベントは届かない。描く向きはドキュメント生成時に流し込む <see cref="Script"/> が数え、
/// 右ボタンを離したときに 1 回だけホストへ送る。ジェスチャーを描いたときだけ、その直後の
/// <c>contextmenu</c> を止める（描かずに右クリックすれば、いつものメニューが出る）。</para>
///
/// <para><b>合図の偽造</b>：<c>chrome.webview.postMessage</c> はページ自身のスクリプトからも呼べる。
/// 仕込みは起動ごとの合言葉を<b>クロージャの中だけ</b>に持ち、合言葉の無い合図は捨てる——ページの
/// スクリプトは仕込みより後に走るので、合言葉も、仕込みが先に掴んだ <c>postMessage</c> も見えない。
/// 呼べるのはどのみちリンクのコピーや戻る・進むなので、これで十分。</para></summary>
internal static class BrowserMouseGesture
{
    public const string MessageType = "loomo.gesture";

    /// <summary>向きの並びの上限。これより長いのは描き損じ。</summary>
    public const int MaxStrokes = 8;

    /// <summary>この起動の合言葉。</summary>
    public static string Token { get; } = Guid.NewGuid().ToString("N");

    /// <summary>ドキュメント生成時に流し込むページ側スクリプト。</summary>
    public static string Script => ScriptTemplate.Replace("__LOOMO_GESTURE_TOKEN__", Token, StringComparison.Ordinal);

    /// <summary>STEP＝1 画（向き 1 つ）と数える最小の移動量（CSS px）。描いている間は軌跡と矢印を重ねて見せる
    /// （<c>pointer-events:none</c>・離せば消す）。</summary>
    private const string ScriptTemplate = """
        (() => {
          if (window.top !== window) return;
          const webview = window.chrome && window.chrome.webview;
          if (!webview) return;
          const post = webview.postMessage.bind(webview);
          const token = '__LOOMO_GESTURE_TOKEN__';
          const STEP = 24, MAX = 8;
          const arrows = { U: '↑', D: '↓', L: '←', R: '→' };
          let tracking = false, dirs = '', lastX = 0, lastY = 0, prevX = 0, prevY = 0;
          let suppressMenu = false, canvas = null, ctx = null, label = null;

          const clearTrail = () => {
            if (canvas) { canvas.remove(); canvas = null; ctx = null; }
            if (label) { label.remove(); label = null; }
          };
          const ensureTrail = () => {
            if (canvas) return;
            const root = document.documentElement;
            if (!root) return;
            canvas = document.createElement('canvas');
            const dpr = window.devicePixelRatio || 1;
            canvas.width = Math.round(innerWidth * dpr);
            canvas.height = Math.round(innerHeight * dpr);
            canvas.style.cssText = 'position:fixed;left:0;top:0;width:100vw;height:100vh;'
              + 'pointer-events:none;z-index:2147483647;margin:0;padding:0;border:0;background:transparent';
            ctx = canvas.getContext('2d');
            ctx.scale(dpr, dpr);
            ctx.strokeStyle = 'rgba(80,160,255,0.85)';
            ctx.lineWidth = 3;
            ctx.lineCap = 'round';
            ctx.lineJoin = 'round';
            label = document.createElement('div');
            label.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483647;'
              + 'padding:2px 8px;border-radius:4px;background:rgba(30,30,30,0.85);color:#fff;'
              + 'font:14px/1.4 system-ui,sans-serif;margin:0;border:0';
            root.appendChild(canvas);
            root.appendChild(label);
          };
          const cancel = () => { tracking = false; dirs = ''; clearTrail(); };

          window.addEventListener('mousedown', e => {
            if (e.button !== 2) { if (tracking) cancel(); return; }
            tracking = true; dirs = ''; suppressMenu = false;
            lastX = prevX = e.clientX; lastY = prevY = e.clientY;
          }, true);

          window.addEventListener('mousemove', e => {
            if (!tracking) return;
            if ((e.buttons & 2) === 0) { cancel(); return; }
            if (ctx) {
              ctx.beginPath(); ctx.moveTo(prevX, prevY); ctx.lineTo(e.clientX, e.clientY); ctx.stroke();
            }
            prevX = e.clientX; prevY = e.clientY;
            const dx = e.clientX - lastX, dy = e.clientY - lastY;
            if (Math.abs(dx) < STEP && Math.abs(dy) < STEP) return;
            const d = Math.abs(dx) > Math.abs(dy) ? (dx > 0 ? 'R' : 'L') : (dy > 0 ? 'D' : 'U');
            if (dirs[dirs.length - 1] !== d) {
              if (dirs.length === 0) {
                ensureTrail();
                if (ctx) { ctx.beginPath(); ctx.moveTo(lastX, lastY); ctx.lineTo(e.clientX, e.clientY); ctx.stroke(); }
              }
              dirs += d;
            }
            lastX = e.clientX; lastY = e.clientY;
            if (label) {
              label.textContent = dirs.split('').map(c => arrows[c]).join(' ');
              label.style.left = (e.clientX + 16) + 'px';
              label.style.top = (e.clientY + 16) + 'px';
            }
          }, true);

          window.addEventListener('mouseup', e => {
            if (e.button !== 2 || !tracking) return;
            const gesture = dirs;
            cancel();
            if (!gesture) return;
            suppressMenu = true;
            e.preventDefault();
            e.stopImmediatePropagation();
            if (gesture.length <= MAX) post({ type: 'loomo.gesture', token, gesture });
          }, true);

          window.addEventListener('contextmenu', e => {
            if (!suppressMenu) return;
            suppressMenu = false;
            e.preventDefault();
            e.stopImmediatePropagation();
          }, true);

          window.addEventListener('blur', cancel, true);
        })();
        """;

    /// <summary>ページからの合図がこの仕込みのジェスチャーなら、正規化した向きの並びを返す。</summary>
    public static bool TryRead(string? webMessageJson, string token, out string gesture)
    {
        gesture = "";
        if (string.IsNullOrEmpty(webMessageJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(webMessageJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != MessageType
                || !root.TryGetProperty("token", out var t) || t.GetString() != token
                || !root.TryGetProperty("gesture", out var g) || Normalize(g.GetString()) is not { } normalized)
                return false;
            gesture = normalized;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;   // 値が文字列でない
        }
    }

    /// <summary>向きの並びを正規化する（大文字に・続く同じ向きは 1 つに）。U/D/L/R 以外を含む・空・長すぎるなら null。</summary>
    public static string? Normalize(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return null;
        var sb = new StringBuilder();
        foreach (var raw in gesture.Trim())
        {
            var c = char.ToUpperInvariant(raw);
            if (c is not ('U' or 'D' or 'L' or 'R'))
                return null;
            if (sb.Length == 0 || sb[^1] != c)
                sb.Append(c);
        }
        return sb.Length is > 0 and <= MaxStrokes ? sb.ToString() : null;
    }

    /// <summary>表の中から、描いた向きに結ばれたコマンド Id。表の鍵も正規化して照らす（手で書いた <c>"d r"</c> 等は無効のまま）。</summary>
    public static string? Resolve(IReadOnlyDictionary<string, string> bindings, string gesture)
    {
        foreach (var (key, id) in bindings)
        {
            if (Normalize(key) == gesture && !string.IsNullOrWhiteSpace(id))
                return id.Trim();
        }
        return null;
    }

    /// <summary>表示用の矢印（<c>DR</c> → <c>↓→</c>）。</summary>
    public static string Arrows(string gesture)
    {
        var sb = new StringBuilder(gesture.Length);
        foreach (var c in gesture)
            sb.Append(c switch { 'U' => '↑', 'D' => '↓', 'L' => '←', 'R' => '→', _ => c });
        return sb.ToString();
    }
}
