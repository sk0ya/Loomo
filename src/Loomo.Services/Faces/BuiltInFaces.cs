using System;
using System.IO;
using System.Text.Json;

namespace sk0ya.Loomo.Services.Faces;

/// <summary>
/// Loomo が自分で置く面（§44.3・§44.4）。<b>本体の機能も、生やせるものは面として生やす</b>——部屋の口（<c>window.loomo</c>）で
/// 足りるかの試金石で、足りなければ口のほうを直す。置くのは起動時、無いとき・版が古いときだけ（人間や Claude が書き直した
/// 面を黙って戻さない：<c>face.json</c> の <c>builtIn</c> が同じか新しければ触らない）。許しはほかの面と同じく 1 回聞く。
/// </summary>
public static class BuiltInFaces
{
    public static void Install(FaceStore store)
    {
        Place(store, "motion", MotionVersion, MotionManifest, MotionPage);
        Place(store, "orders", OrdersVersion, OrdersManifest, OrdersPage);
    }

    private static void Place(FaceStore store, string id, int version, string manifest, string page)
    {
        try
        {
            var folder = store.FolderOf(id);
            var manifestPath = Path.Combine(folder, FaceStore.ManifestFileName);
            if (File.Exists(manifestPath) && InstalledVersion(manifestPath) >= version)
                return;
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.html"), page);
            File.WriteAllText(manifestPath, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 置けなくても部屋は動く */ }
    }

    private static int InstalledVersion(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            // builtIn が無い＝人間や Claude が同じ名前で作った面。上書きしない。
            return document.RootElement.TryGetProperty("builtIn", out var value) && value.TryGetInt32(out var n) ? n : int.MaxValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return int.MaxValue;
        }
    }

    private const string Style = """
        <style>
          :root { color-scheme: dark light; }
          body { margin: 0; background: var(--loomo-panel); color: var(--loomo-fg); font: var(--loomo-font-size) system-ui, sans-serif; }
          .bar { display: flex; gap: 6px; align-items: center; padding: 6px 8px; border-bottom: 1px solid var(--loomo-border); position: sticky; top: 0; background: var(--loomo-panel); z-index: 2; }
          button { background: transparent; color: var(--loomo-fg); border: 1px solid var(--loomo-border); border-radius: 4px; padding: 2px 9px; font: inherit; cursor: pointer; }
          button:hover { border-color: var(--loomo-accent); }
          button.primary { background: var(--loomo-accent); border-color: var(--loomo-accent); color: var(--loomo-bg); }
          button:disabled { opacity: .5; cursor: default; }
          select, input, textarea { background: var(--loomo-bg); color: var(--loomo-fg); border: 1px solid var(--loomo-border); border-radius: 4px; font: inherit; padding: 2px 4px; }
          .dim { color: var(--loomo-fg-dim); }
          .small { font-size: 11px; }
          .mono { font-family: Cascadia Mono, Consolas, monospace; font-size: 12px; }
          .row { padding: 5px 8px; border-bottom: 1px solid var(--loomo-border); }
          .row.click { cursor: pointer; }
          .row.click:hover { background: var(--loomo-bg); }
          .row.sel { box-shadow: inset 2px 0 0 var(--loomo-accent); background: var(--loomo-bg); }
          .mark { display: inline-block; width: 1.2em; }
          .changed { color: var(--loomo-accent); }
          .bad { color: #e06c75; } .good { color: #98c379; }
          pre { margin: 4px 0; white-space: pre-wrap; word-break: break-all; }
          .add { background: rgba(152,195,121,.16); } .del { background: rgba(224,108,117,.16); }
          .empty { padding: 14px 10px; }
          img, canvas { max-width: 100%; border: 1px solid var(--loomo-border); }
        </style>
        """;

    // ── 動き（§44.3） ───────────────────────────────────────

    private const int MotionVersion = 1;

    private const string MotionManifest = """
        {
          "builtIn": 1,
          "title": "動き",
          "description": "変更を、行ではなく前と後の動き（出力・テストの成否・絵）で読む",
          "api": ["motion"]
        }
        """;

    private const string MotionPage = "<!doctype html><meta charset=\"utf-8\">" + Style + """
        <div class="bar">
          <button id="record" class="primary" title="いまの作業ツリーで、見どころを全部回して録る">録る</button>
          <span id="status" class="dim small"></span>
        </div>
        <div id="list"></div>
        <div id="detail"></div>
        <script>
        const $ = id => document.getElementById(id);
        const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
        const time = at => new Date(at).toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });
        let selected = null;

        async function refresh() {
          const state = await loomo.motion.state();
          const list = $('list'); list.textContent = '';
          $('record').disabled = !state.approved || state.probes.length === 0;
          if (state.error) { list.appendChild(el('div', 'empty bad', '見どころの定義を読めません: ' + state.error)); return; }
          if (state.probes.length === 0) {
            const box = el('div', 'empty dim');
            box.appendChild(el('div', '', '見どころがまだありません。'));
            box.appendChild(el('div', 'small', 'ターミナルの Claude Code に「ビルドを見どころにして」のように頼むか、パレット「動き：見どころを編集」で書きます。'));
            list.appendChild(box); $('detail').textContent = ''; return;
          }
          if (!state.approved) {
            list.appendChild(el('div', 'empty changed', '見どころのコマンドがまだ許されていません。パレット「動き：見どころを許す」で中身を確かめてください。'));
          }
          $('status').textContent = state.probes.map(p => p.title + (p.everyTurn ? '' : '（手で）')).join('・');
          const snapshots = await loomo.motion.snapshots();
          if (snapshots.length === 0) list.appendChild(el('div', 'empty dim', 'まだ録った動きがありません。Claude のターンが終わると録ります。'));
          for (const s of snapshots) {
            const row = el('div', 'row click' + (s.id === selected ? ' sel' : ''));
            const head = el('div');
            head.appendChild(el('span', 'mark ' + (s.changed ? 'changed' : 'dim'), s.changed ? '●' : '·'));
            head.appendChild(el('span', 'dim small', time(s.at) + '　'));
            head.appendChild(el('span', '', s.promptHead || (s.trigger === 'manual' ? '（手で録った）' : s.trigger === 'order' ? '（言いつけ）' : '（頼みごと不明）')));
            row.appendChild(head);
            row.appendChild(el('div', 'small ' + (s.changed ? 'changed' : 'dim'), s.headline || (s.hasBefore ? '動きは同じ' : '最初の記録')));
            row.onclick = () => { selected = s.id; refresh(); show(s.id); };
            list.appendChild(row);
          }
        }

        function lineDiff(a, b) {
          const x = a.split('\n'), y = b.split('\n');
          if (x.length * y.length > 4_000_000) return [{ t: ' ', s: '（出力が大きすぎるので、後だけを出します）' }].concat(y.map(s => ({ t: ' ', s })));
          const n = x.length, m = y.length, w = m + 1;
          const d = new Uint32Array((n + 1) * w);
          for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--)
            d[i * w + j] = x[i] === y[j] ? d[(i + 1) * w + j + 1] + 1 : Math.max(d[(i + 1) * w + j], d[i * w + j + 1]);
          const out = []; let i = 0, j = 0;
          while (i < n && j < m) {
            if (x[i] === y[j]) { out.push({ t: ' ', s: x[i] }); i++; j++; }
            else if (d[(i + 1) * w + j] >= d[i * w + j + 1]) out.push({ t: '-', s: x[i++] });
            else out.push({ t: '+', s: y[j++] });
          }
          while (i < n) out.push({ t: '-', s: x[i++] });
          while (j < m) out.push({ t: '+', s: y[j++] });
          return out;
        }

        async function imageCompare(host, beforeId, afterId, file) {
          const [a, b] = await Promise.all([beforeId ? loomo.motion.image(beforeId, file) : null, loomo.motion.image(afterId, file)]);
          const bar = el('div', 'small'); host.appendChild(bar);
          const view = el('div'); host.appendChild(view);
          const load = src => new Promise(done => { if (!src) return done(null); const i = new Image(); i.onload = () => done(i); i.onerror = () => done(null); i.src = src; });
          const [ia, ib] = await Promise.all([load(a), load(b)]);
          const modes = {
            '並べる': () => { view.textContent = ''; for (const [label, img] of [['前', ia], ['後', ib]]) { view.appendChild(el('div', 'dim small', label)); if (img) view.appendChild(img.cloneNode()); else view.appendChild(el('div', 'dim', '（無い）')); } },
            '重ねる': () => {
              view.textContent = ''; if (!ia || !ib) return modes['並べる']();
              const wrap = el('div'); wrap.style.position = 'relative'; const under = ia.cloneNode(), over = ib.cloneNode();
              over.style.position = 'absolute'; over.style.left = 0; over.style.top = 0; over.style.opacity = .5;
              const slider = el('input'); slider.type = 'range'; slider.min = 0; slider.max = 100; slider.value = 50; slider.style.width = '100%';
              slider.oninput = () => over.style.opacity = slider.value / 100;
              wrap.appendChild(under); wrap.appendChild(over); view.appendChild(el('div', 'dim small', '前 ← → 後')); view.appendChild(slider); view.appendChild(wrap);
            },
            '違い': () => {
              view.textContent = ''; if (!ia || !ib) return modes['並べる']();
              const w = Math.max(ia.width, ib.width), h = Math.max(ia.height, ib.height);
              const pixels = img => { const c = document.createElement('canvas'); c.width = w; c.height = h; const g = c.getContext('2d'); g.drawImage(img, 0, 0); return g.getImageData(0, 0, w, h); };
              const pa = pixels(ia), pb = pixels(ib), out = document.createElement('canvas'); out.width = w; out.height = h;
              const g = out.getContext('2d'), o = g.createImageData(w, h); let count = 0;
              for (let k = 0; k < o.data.length; k += 4) {
                const diff = Math.abs(pa.data[k] - pb.data[k]) + Math.abs(pa.data[k + 1] - pb.data[k + 1]) + Math.abs(pa.data[k + 2] - pb.data[k + 2]) + Math.abs(pa.data[k + 3] - pb.data[k + 3]);
                if (diff > 24) { o.data[k] = 255; o.data[k + 1] = 40; o.data[k + 2] = 90; o.data[k + 3] = 255; count++; }
                else { const v = (pb.data[k] + pb.data[k + 1] + pb.data[k + 2]) / 3; o.data[k] = o.data[k + 1] = o.data[k + 2] = v; o.data[k + 3] = 70; }
              }
              g.putImageData(o, 0, 0); view.appendChild(el('div', 'dim small', '違う所：' + count.toLocaleString() + ' 画素')); view.appendChild(out);
            },
          };
          for (const name of Object.keys(modes)) { const b2 = el('button', '', name); b2.style.marginRight = '4px'; b2.onclick = modes[name]; bar.appendChild(b2); }
          modes[ia && ib ? '重ねる' : '並べる']();
        }

        async function show(id) {
          const detail = $('detail'); detail.textContent = '';
          const data = await loomo.motion.get(id);
          if (!data) return;
          const head = el('div', 'bar');
          head.appendChild(el('span', '', time(data.snapshot.at) + ' の動き'));
          if (data.snapshot.sessionId) {
            const open = el('button', '', 'この頼みごとの差分を開く'); open.onclick = () => loomo.motion.openRequest(id).catch(e => loomo.toast(e.message));
            head.appendChild(open);
          }
          detail.appendChild(head);
          for (const change of data.changes) {
            const sample = data.snapshot.samples.find(s => s.probeId === change.probeId);
            const before = data.before && data.before.samples.find(s => s.probeId === change.probeId);
            const row = el('div', 'row');
            const title = el('div');
            title.appendChild(el('span', 'mark ' + (change.changed ? 'changed' : 'dim'), change.changed ? '●' : '·'));
            title.appendChild(el('span', '', change.title + '　'));
            title.appendChild(el('span', 'small ' + (change.changed ? 'changed' : 'dim'), change.summary));
            if (sample.timedOut) title.appendChild(el('span', 'small bad', '　時間切れ'));
            row.appendChild(title);
            if (change.flips.length) {
              for (const flip of change.flips.slice(0, 200)) {
                const line = el('div', 'mono');
                line.appendChild(el('span', flip.after === 'failed' ? 'bad' : flip.after === 'passed' ? 'good' : 'dim', (flip.before || '無し') + ' → ' + (flip.after || '無し') + '　'));
                line.appendChild(el('span', '', flip.name));
                row.appendChild(line);
              }
            }
            for (const file of change.imagesChanged) {
              row.appendChild(el('div', 'dim small', file));
              const host = el('div'); row.appendChild(host);
              imageCompare(host, data.before && data.before.id, id, file);
            }
            if (sample.kind !== 2 && (change.outputChanged || !change.hasBefore) && !(sample.kind === 1 && change.flips.length)) {
              const pre = el('pre', 'mono');
              const lines = before ? lineDiff(before.output, sample.output) : sample.output.split('\n').map(s => ({ t: ' ', s }));
              const shown = before ? lines.filter((l, i) => l.t !== ' ' || lines.slice(Math.max(0, i - 2), i + 3).some(n => n.t !== ' ')) : lines;
              for (const l of shown.slice(0, 600)) pre.appendChild(el('div', l.t === '+' ? 'add' : l.t === '-' ? 'del' : '', l.t + ' ' + l.s));
              row.appendChild(pre);
            }
            detail.appendChild(row);
          }
        }

        $('record').onclick = async () => {
          $('record').disabled = true; $('status').textContent = '録っています…';
          try { const id = await loomo.motion.record(); if (id) { selected = id; await show(id); } else loomo.toast('録れませんでした（回している間に作業ツリーが変わった・見どころが無い）'); }
          catch (e) { loomo.toast(e.message); }
          await refresh();
        };
        loomo.on('motionRecorded', () => refresh());
        refresh();
        </script>
        """;

    // ── 言いつけ（§44.4） ───────────────────────────────────

    private const int OrdersVersion = 2;

    private const string OrdersManifest = """
        {
          "builtIn": 2,
          "title": "言いつけ",
          "description": "常設の頼みごとを裏のワークツリーで進め、仕上がりを盆から採るか捨てる",
          "api": ["orders"]
        }
        """;

    private const string OrdersPage = "<!doctype html><meta charset=\"utf-8\">" + Style + """
        <div class="bar"><span>盆</span><span id="status" class="dim small"></span></div>
        <div id="tray"></div>
        <div class="bar"><span>言いつけ</span><button id="add">足す</button></div>
        <div id="orders"></div>
        <div id="form" class="row" style="display:none">
          <div class="small dim">名前（英数字とハイフン）</div><input id="f-id" style="width:100%">
          <div class="small dim">頼みごと（裏のワークツリーで Claude Code に渡す文）</div><textarea id="f-prompt" rows="5" style="width:100%"></textarea>
          <div class="small dim">きっかけ</div>
          <select id="f-trigger"><option value="manual">手で</option><option value="daily">毎日この時刻に</option><option value="motion">動きが変わったら（テストが落ちた等）</option></select>
          <input id="f-at" type="time" value="07:00">
          <div style="margin-top:6px"><button id="f-save" class="primary">置く</button> <button id="f-cancel">やめる</button></div>
        </div>
        <script>
        const $ = id => document.getElementById(id);
        const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
        const time = at => new Date(at).toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' });
        const button = (text, f, cls) => { const b = el('button', cls || '', text); b.style.marginRight = '4px'; b.onclick = async e => { e.stopPropagation(); b.disabled = true; try { await f(); } catch (x) { loomo.toast(x.message); } await refresh(); }; return b; };

        async function refresh() {
          const state = await loomo.orders.state();
          $('status').textContent = state.running ? '「' + state.running + '」を進めています…' : state.note || '';
          const tray = $('tray'); tray.textContent = '';
          if (state.results.length === 0) tray.appendChild(el('div', 'empty dim', '仕上がりはまだありません。'));
          for (const r of state.results) {
            const row = el('div', 'row');
            const head = el('div');
            head.appendChild(el('span', 'mark ' + (r.status === 'done' ? 'changed' : r.status === 'failed' ? 'bad' : 'dim'), r.status === 'done' ? '●' : r.status === 'failed' ? '✗' : '·'));
            head.appendChild(el('span', '', r.orderId + '　'));
            head.appendChild(el('span', 'dim small', time(r.at) + '　' + (r.status === 'done' ? r.files + ' ファイル' : r.status === 'failed' ? '失敗' : r.status === 'empty' ? '変更なし' : r.status === 'taken' ? '採った（作業ツリーへ当てた）' : r.status === 'running' ? '進めています…' : r.status)));
            row.appendChild(head);
            if (r.motion) row.appendChild(el('div', 'small changed', '動き：' + r.motion));
            if (r.summary) { const pre = el('pre', 'small'); pre.textContent = r.summary.slice(0, 1500); row.appendChild(pre); }
            const acts = el('div');
            if (r.status === 'done') {
              acts.appendChild(button('差分を開く', () => loomo.orders.openDiff(r.id)));
              acts.appendChild(button('採る', () => loomo.orders.take(r.id), 'primary'));
            }
            if (r.status !== 'running') acts.appendChild(button(r.status === 'done' ? '捨てる' : '盆から外す', () => loomo.orders.discard(r.id)));
            row.appendChild(acts);
            tray.appendChild(row);
          }
          const orders = $('orders'); orders.textContent = '';
          if (state.orders.length === 0) orders.appendChild(el('div', 'empty dim', '言いつけはまだありません。「足す」で置きます。'));
          for (const o of state.orders) {
            const row = el('div', 'row');
            const head = el('div');
            head.appendChild(el('span', '', o.id + '　'));
            head.appendChild(el('span', 'dim small', o.trigger === 'daily' ? '毎日 ' + o.at : o.trigger === 'motion' ? '動きが変わったら' : '手で'));
            row.appendChild(head);
            row.appendChild(el('div', 'small dim', o.prompt.slice(0, 300)));
            const acts = el('div');
            acts.appendChild(button('いま進める', () => loomo.orders.run(o.id)));
            acts.appendChild(button('外す', () => loomo.orders.remove(o.id)));
            row.appendChild(acts);
            orders.appendChild(row);
          }
        }
        $('add').onclick = () => { $('form').style.display = ''; };
        $('f-cancel').onclick = () => { $('form').style.display = 'none'; };
        $('f-save').onclick = async () => {
          try {
            await loomo.orders.save({ id: $('f-id').value.trim(), prompt: $('f-prompt').value, trigger: $('f-trigger').value, at: $('f-at').value });
            $('form').style.display = 'none'; $('f-id').value = ''; $('f-prompt').value = '';
          } catch (e) { loomo.toast(e.message); }
          refresh();
        };
        loomo.on('ordersChanged', () => refresh());
        refresh();
        </script>
        """;
}
