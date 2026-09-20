namespace sk0ya.Loomo.App.Services;

/// <summary>表示中の Web ページを Markdown にしてエディタへ渡す（「ページをエディタへ送る」）ための変換。
///
/// <para>変換はページの中（JavaScript）で行う。<see cref="HtmlToMarkdownConverter"/> は Mammoth が出す
/// 整形済み XHTML 専用（<c>XElement.Parse</c> でパースする）で、実世界の HTML には通らないため使えない。
/// DOM を歩けるのはブラウザ自身なので、そこで見出し・箇条書き・表・リンク・コードだけを拾う。</para>
/// </summary>
public static class BrowserPageMarkdown
{
    /// <summary>本文を Markdown 文字列（JSON 文字列）で返すスクリプト。
    /// 記事本体（article / main）があればそこを、無ければ body を対象にし、
    /// ナビゲーション・広告・スクリプト等の非本文は落とす。</summary>
    public const string ExtractScript = """
(function(){
  var SKIP = {SCRIPT:1,STYLE:1,NOSCRIPT:1,NAV:1,HEADER:1,FOOTER:1,ASIDE:1,FORM:1,SVG:1,BUTTON:1,IFRAME:1,TEMPLATE:1};
  function visible(el){
    if(!el || el.nodeType!==1) return false;
    if(SKIP[el.tagName]) return false;
    var st = window.getComputedStyle(el);
    return !(st && (st.display==='none' || st.visibility==='hidden'));
  }
  function esc(text){ return text.replace(/([\\`*_\[\]])/g,'\\$1'); }
  function inline(node){
    var out='';
    node.childNodes.forEach(function(child){
      if(child.nodeType===3){ out += child.nodeValue.replace(/\s+/g,' '); return; }
      if(!visible(child)) return;
      var tag = child.tagName;
      if(tag==='BR'){ out += '\n'; return; }
      var inner = inline(child);
      if(tag==='A' && child.getAttribute('href')){
        var href = child.href || child.getAttribute('href');
        out += inner.trim() ? '['+esc(inner.trim())+']('+href+')' : '';
      } else if(tag==='IMG'){
        out += '!['+esc(child.getAttribute('alt')||'')+']('+(child.src||'')+')';
      } else if(tag==='CODE' || tag==='KBD' || tag==='SAMP'){
        out += inner.trim() ? '`'+inner.trim()+'`' : '';
      } else if(tag==='STRONG' || tag==='B'){
        out += inner.trim() ? '**'+inner.trim()+'**' : '';
      } else if(tag==='EM' || tag==='I'){
        out += inner.trim() ? '*'+inner.trim()+'*' : '';
      } else {
        out += inner;
      }
    });
    return out;
  }
  function cell(el){ return inline(el).replace(/\n/g,' ').replace(/\|/g,'\\|').trim(); }
  function table(el){
    var rows = Array.prototype.slice.call(el.querySelectorAll('tr'));
    if(!rows.length) return '';
    var lines = [];
    rows.forEach(function(row, index){
      var cells = Array.prototype.slice.call(row.children).map(cell);
      if(!cells.length) return;
      lines.push('| '+cells.join(' | ')+' |');
      if(index===0) lines.push('|'+cells.map(function(){return ' --- ';}).join('|')+'|');
    });
    return lines.join('\n');
  }
  var out=[];
  function walk(el, depth){
    if(depth>24) return;
    Array.prototype.forEach.call(el.children, function(child){
      if(!visible(child)) return;
      var tag = child.tagName;
      if(/^H[1-6]$/.test(tag)){
        var text = inline(child).trim();
        if(text) out.push(new Array(+tag[1]+1).join('#')+' '+text);
        return;
      }
      if(tag==='P' || tag==='BLOCKQUOTE'){
        var body = inline(child).trim();
        if(body) out.push(tag==='BLOCKQUOTE' ? body.split('\n').map(function(l){return '> '+l;}).join('\n') : body);
        return;
      }
      if(tag==='PRE'){
        var code = child.innerText.replace(/\s+$/,'');
        if(code) out.push('```\n'+code+'\n```');
        return;
      }
      if(tag==='UL' || tag==='OL'){
        var ordered = tag==='OL';
        Array.prototype.forEach.call(child.children, function(li, index){
          if(li.tagName!=='LI') return;
          var text = inline(li).trim();
          if(text) out.push((ordered ? (index+1)+'. ' : '- ')+text.split('\n').join(' '));
        });
        return;
      }
      if(tag==='TABLE'){
        var rendered = table(child);
        if(rendered) out.push(rendered);
        return;
      }
      if(tag==='HR'){ out.push('---'); return; }
      walk(child, depth+1);
    });
  }
  var root = document.querySelector('article') || document.querySelector('main') || document.body;
  if(root) walk(root, 0);
  return out.join('\n\n');
})()
""";

    /// <summary>抽出結果に出所（タイトルと URL）を付けて 1 つの Markdown 文書にする。
    /// 空行の詰めもここで行う（ページ側は素直に段落を並べるだけにして、整形はこちらの責務）。</summary>
    public static string BuildDocument(string? title, string? url, string? body)
    {
        var heading = string.IsNullOrWhiteSpace(title) ? "(無題のページ)" : title!.Trim();
        var lines = new List<string> { $"# {heading}", "" };
        if (!string.IsNullOrWhiteSpace(url))
            lines.Add($"<{url!.Trim()}>");
        lines.Add("");
        lines.Add(Collapse(body ?? ""));
        return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
    }

    /// <summary>3行以上の空行を2行に詰め、行末の空白を落とす。</summary>
    public static string Collapse(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.Trim();
    }

    /// <summary>エディタタブの名前（<c>ホスト名.md</c>）。Markdown なのでプレビューもそのまま効く。</summary>
    public static string FileNameFor(string? url)
    {
        var host = Uri.TryCreate(url ?? "", UriKind.Absolute, out var uri) && uri.Host is { Length: > 0 } h
            ? h
            : "page";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            host = host.Replace(invalid, '-');
        return host + ".md";
    }
}
