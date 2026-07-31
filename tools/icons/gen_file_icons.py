"""Catppuccin の VS Code アイコンを FolderTree 用の C# テーブルへ変換する。

使い方（リポジトリのルートで）:
    python tools/icons/gen_file_icons.py

やること:
  1. catppuccin/vscode-icons のリポジトリを tar.gz で 1 回だけ取得（tools/icons/cache/ にキャッシュ）
  2. `icons/css-variables/*.svg` から**形と色名**を読む（この版だけ色が `var(--vscode-ctp-<名前>)`
     という意味名で入っている）。実際の色は `icons/mocha` と `icons/latte` の同じ位置のレイヤーから
     引き当てて、暗色テーマ用・明色テーマ用の 16 色パレット 2 本にする。
  3. `src/defaults/fileIcons.ts` の拡張子・ファイル名の対応表をそのまま使う
  4. src/Loomo.App/ViewModels/FileIconData.cs を書き出す

アイコンは全て**線画**（fill は none、色は stroke）。viewBox はほぼ 0 0 16 16 なので、描画側は
拡大縮小なしでそのまま 16x16 に置ける。

出典: https://github.com/catppuccin/vscode-icons (MIT)
ライセンス本文は src/Loomo.App/Assets/Icons/LICENSE-catppuccin-vscode-icons.txt。
"""
import os, re, math, tarfile, urllib.request
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cache")
OUTPUT = os.path.join(HERE, "..", "..", "src", "Loomo.App", "ViewModels", "FileIconData.cs")
TARBALL = "https://codeload.github.com/catppuccin/vscode-icons/tar.gz/refs/heads/main"
NS = "{http://www.w3.org/2000/svg}"

# 暗色テーマ用 / 明色テーマ用に使うフレーバー
DARK_FLAVOR, LIGHT_FLAVOR = "mocha", "latte"

# 特別扱いのアイコン（拡張子表には出てこないが必ず要るもの）
FOLDER, FOLDER_OPEN, DEFAULT_FILE = "_folder", "_folder_open", "_file"

IDENT = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)


# ---------------------------------------------------------------- 取得

def repo_files():
    """リポジトリの tar.gz を 1 回だけ取って {リポジトリ内パス: 中身} を返す。"""
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, "catppuccin-vscode-icons.tar.gz")
    if not os.path.exists(path) or os.path.getsize(path) < 1024:
        with urllib.request.urlopen(TARBALL, timeout=180) as r:
            data = r.read()
        open(path, "wb").write(data)
    files = {}
    with tarfile.open(path, "r:gz") as tar:
        for member in tar.getmembers():
            if not member.isfile():
                continue
            # 先頭の "<リポジトリ名>-<ブランチ>/" を落とす
            rel = member.name.split("/", 1)[1] if "/" in member.name else member.name
            if (rel.startswith("icons/") and rel.endswith(".svg")) or rel.endswith("fileIcons.ts"):
                files[rel] = tar.extractfile(member).read().decode("utf-8")
    return files


# ---------------------------------------------------------------- SVG

def mat_mul(m, n):
    a1, b1, c1, d1, e1, f1 = m
    a2, b2, c2, d2, e2, f2 = n
    return (a1 * a2 + c1 * b2, b1 * a2 + d1 * b2,
            a1 * c2 + c1 * d2, b1 * c2 + d1 * d2,
            a1 * e2 + c1 * f2 + e1, b1 * e2 + d1 * f2 + f1)


def parse_transform(s):
    m = IDENT
    for fn, args in re.findall(r"(\w+)\s*\(([^)]*)\)", s):
        v = [float(x) for x in re.split(r"[ ,]+", args.strip()) if x]
        if fn == "translate":
            t = (1, 0, 0, 1, v[0], v[1] if len(v) > 1 else 0)
        elif fn == "scale":
            t = (v[0], 0, 0, v[1] if len(v) > 1 else v[0], 0, 0)
        elif fn == "matrix":
            t = tuple(v)
        elif fn == "rotate":
            r = math.radians(v[0])
            t = (math.cos(r), math.sin(r), -math.sin(r), math.cos(r), 0, 0)
            if len(v) == 3:
                t = mat_mul(mat_mul((1, 0, 0, 1, v[1], v[2]), t), (1, 0, 0, 1, -v[1], -v[2]))
        else:
            raise ValueError(f"transform {fn}")
        m = mat_mul(m, t)
    return m


def shape_to_path(tag, a):
    f = lambda k, d=0: float(a.get(k, d))
    if tag == "circle":
        cx, cy, r = f("cx"), f("cy"), f("r")
        return f"M{cx - r},{cy}a{r},{r} 0 1,0 {2 * r},0a{r},{r} 0 1,0 {-2 * r},0Z"
    if tag == "ellipse":
        cx, cy, rx, ry = f("cx"), f("cy"), f("rx"), f("ry", a.get("rx", 0))
        return f"M{cx - rx},{cy}a{rx},{ry} 0 1,0 {2 * rx},0a{rx},{ry} 0 1,0 {-2 * rx},0Z"
    if tag == "rect":
        x, y, w, h, rx = f("x"), f("y"), f("width"), f("height"), f("rx")
        if rx:
            return (f"M{x + rx},{y}H{x + w - rx}a{rx},{rx} 0 0,1 {rx},{rx}V{y + h - rx}"
                    f"a{rx},{rx} 0 0,1 {-rx},{rx}H{x + rx}a{rx},{rx} 0 0,1 {-rx},{-rx}"
                    f"V{y + rx}a{rx},{rx} 0 0,1 {rx},{-rx}Z")
        return f"M{x},{y}H{x + w}V{y + h}H{x}Z"
    if tag in ("polygon", "polyline"):
        p = [t for t in re.split(r"[ ,\s]+", a["points"].strip()) if t]
        pts = "M" + "L".join(f"{p[i]},{p[i + 1]}" for i in range(0, len(p) - 1, 2))
        return pts + "Z" if tag == "polygon" else pts
    if tag == "line":
        return f"M{f('x1')},{f('y1')}L{f('x2')},{f('y2')}"
    return None


INHERITED = ("stroke", "fill", "stroke-width", "stroke-linecap", "stroke-linejoin")


def collect(elem, inherited, out):
    for child in elem:
        tag = child.tag.replace(NS, "")
        # マスク・クリップ・グラデーション定義は線画では実質使われないので落とす
        if tag in ("defs", "title", "desc", "style", "metadata", "clipPath", "mask"):
            continue
        cur = dict(inherited)
        for k in INHERITED:
            if k in child.attrib:
                cur[k] = child.attrib[k]
        if "transform" in child.attrib:
            cur["xf"] = mat_mul(cur["xf"], parse_transform(child.attrib["transform"]))
        if tag == "g":
            collect(child, cur, out)
            continue
        d = child.get("d") if tag == "path" else shape_to_path(tag, child.attrib)
        if d is None:
            raise ValueError(f"unsupported <{tag}>")
        cur["d"] = d.strip()
        out.append(cur)


def read_layers(src):
    root = ET.fromstring(src)
    vb = root.get("viewBox")
    x, y, w, h = ([float(v) for v in re.split(r"[ ,]+", vb.strip())] if vb else [0, 0, 16, 16])
    base = {"stroke": root.get("stroke"), "fill": root.get("fill", "none"),
            "stroke-width": root.get("stroke-width", "1"),
            "stroke-linecap": root.get("stroke-linecap", "butt"),
            "stroke-linejoin": root.get("stroke-linejoin", "miter"), "xf": IDENT}
    out = []
    collect(root, base, out)
    return (x, y, w, h), out


def color_name(value):
    m = re.search(r"--vscode-ctp-([a-z0-9-]+)", value or "")
    return m.group(1) if m else None


def painted_color(layer, is_fill):
    return layer.get("fill") if is_fill else layer.get("stroke")


# ---------------------------------------------------------------- 変換

class Converter:
    def __init__(self, files):
        self.files = files
        self.palette = {}   # 色名 -> (暗色 hex, 明色 hex)

    def convert(self, name):
        src = self.files.get(f"icons/css-variables/{name}.svg")
        if src is None:
            raise FileNotFoundError("css-variables 版が無い")
        (x, y, w, h), layers = read_layers(src)
        try:
            _, dark = read_layers(self.files[f"icons/{DARK_FLAVOR}/{name}.svg"])
            _, light = read_layers(self.files[f"icons/{LIGHT_FLAVOR}/{name}.svg"])
        except KeyError as e:
            raise ValueError(f"フレーバー版が無い {e}")
        if len(dark) != len(layers) or len(light) != len(layers):
            raise ValueError("フレーバー間でレイヤー数が違う")

        out = []
        for i, layer in enumerate(layers):
            stroke, fill = layer.get("stroke"), layer.get("fill")
            is_fill = not (stroke and stroke != "none")
            painted = fill if is_fill else stroke
            if not painted or painted == "none":
                continue
            # 色は意味名（var(--vscode-ctp-*)）で持つ。解決できないもの（グラデーション等）は text 扱い。
            cname = color_name(painted) or "text"
            hd, hl = painted_color(dark[i], is_fill), painted_color(light[i], is_fill)
            if hd and hl and hd.startswith("#") and hl.startswith("#"):
                self.palette.setdefault(cname, (expand_hex(hd), expand_hex(hl)))

            out.append(dict(
                d=layer["d"], color=cname, fill=is_fill,
                width=float(re.sub(r"[a-z%]", "", layer.get("stroke-width", "1")) or 1),
                cap=layer.get("stroke-linecap", "butt"),
                join=layer.get("stroke-linejoin", "miter"),
                xf=layer["xf"],
            ))
        if not out:
            raise ValueError("塗られるレイヤーが無い")

        # viewBox を 16x16 へ合わせる行列を、レイヤー自身の transform に前から掛けて畳み込む。
        # transform を持つレイヤーと持たないレイヤーが混ざるアイコンがあるので、行列は
        # アイコン単位ではなくレイヤー単位で持つ（同じ行列は後で 1 本に束ねる）。
        s = 16.0 / max(w, h)
        norm = (s, 0, 0, s, -x * s + (16 - w * s) / 2, -y * s + (16 - h * s) / 2)
        for l in out:
            m = mat_mul(norm, l.pop("xf"))
            l["m"] = tuple(round(v, 5) for v in m)
            # 線幅も一緒に拡大縮小しないと、正規化した分だけ線が太く／細くなる。
            # 回転・平行移動は太さに効かないので、拡大率だけを見る。
            l["width"] *= math.sqrt(abs(m[0] * m[3] - m[1] * m[2])) or 1
        return dict(layers=out)


def expand_hex(h):
    h = h.lstrip("#").lower()
    if len(h) == 3:
        h = "".join(c * 2 for c in h)
    return h if len(h) == 6 else "cdd6f4"


# ---------------------------------------------------------------- 対応表

def parse_file_icons(ts):
    """src/defaults/fileIcons.ts（JS のオブジェクトリテラル）から
    アイコン名 -> (拡張子リスト, ファイル名リスト) を取り出す。"""
    body = ts.split("const fileIcons: FileIcons = {", 1)[1]
    result = {}
    for m in re.finditer(r"\n  '?([^'\s:]+)'?\s*:\s*\{(.*?)\n  \},", body, re.S):
        name, block = m.group(1), m.group(2)

        def items(key):
            k = re.search(key + r"\s*:\s*\[(.*?)\]", block, re.S)
            return re.findall(r"'([^']*)'", k.group(1)) if k else []

        exts, names = items("fileExtensions"), items("fileNames")
        if exts or names:
            result[name] = (exts, names)
    return result


# ---------------------------------------------------------------- 出力

def cs_escape(s):
    return s.replace("\\", "\\\\").replace('"', '\\"')


def fmt(v):
    r = round(v, 5)
    return "0" if r == 0 else ("%g" % r)


def main():
    files = repo_files()
    mapping = parse_file_icons(files["src/defaults/fileIcons.ts"])
    conv = Converter(files)

    order, icons, failed = [], {}, {}
    for name in [FOLDER, FOLDER_OPEN, DEFAULT_FILE] + sorted(mapping):
        if name in icons:
            continue
        try:
            icons[name] = conv.convert(name)
            order.append(name)
        except Exception as e:
            failed[name] = str(e)

    for special in (FOLDER, FOLDER_OPEN, DEFAULT_FILE):
        if special not in icons:
            raise SystemExit(f"必須アイコン {special} を変換できない: {failed.get(special)}")

    idx = {n: i for i, n in enumerate(order)}
    colors = sorted(conv.palette)
    cidx = {c: i for i, c in enumerate(colors)}

    by_ext, by_name = {}, {}
    for name, (exts, names) in mapping.items():
        if name not in idx:
            continue
        for e in exts:
            # 多段拡張子（d.ts 等）は Path.GetExtension で引けないので入れない
            if e and "." not in e:
                by_ext.setdefault("." + e.lower(), idx[name])
        for f in names:
            if "*" not in f:
                by_name.setdefault(f.lower(), idx[name])

    # レイヤーは 1 本の配列に並べ、アイコンは開始位置と本数だけ持つ（入れ子配列を作らない）
    flat, spans = [], []
    for n in order:
        spans.append((len(flat), len(icons[n]["layers"])))
        flat.extend(icons[n]["layers"])

    # 行列は重複が多い（大半が単位行列）ので、一意なものだけ別表にしてレイヤーは添字で指す。
    # 添字 0 は必ず単位行列＝変換なしにしておき、描画側がそのまま素通りできるようにする。
    matrices = [IDENT]
    for l in flat:
        if l["m"] not in matrices:
            matrices.append(l["m"])
    if len(matrices) > 255:
        raise SystemExit(f"行列が多すぎて byte に収まらない（{len(matrices)}）")
    midx = {m: i for i, m in enumerate(matrices)}

    L = []
    w = L.append
    w("// <auto-generated />")
    w("// Catppuccin VS Code Icons (https://github.com/catppuccin/vscode-icons, MIT) の SVG から")
    w("// tools/icons/gen_file_icons.py で機械変換したもの。手で編集しないこと。")
    w("// ライセンスは Assets/Icons/LICENSE-catppuccin-vscode-icons.txt。")
    w("")
    w("namespace sk0ya.Loomo.App.ViewModels;")
    w("")
    w("/// <summary>アイコンを構成する線（まれに塗り）1 本ぶん。<see cref=\"Data\"/> は WPF のパス")
    w("/// ミニ言語、<see cref=\"Color\"/> は <see cref=\"FileIconData.DarkPalette\"/> ／")
    w("/// <see cref=\"FileIconData.LightPalette\"/> の添字、<see cref=\"Transform\"/> は")
    w("/// <see cref=\"FileIconData.Transforms\"/> の添字（0 は変換なし）。</summary>")
    w("internal readonly record struct FileIconLayer(")
    w("    string Data, byte Color, byte Transform, double Width, bool RoundCap, bool RoundJoin, bool IsFill);")
    w("")
    w("/// <summary>アイコン 1 個。<see cref=\"FileIconData.Layers\"/> の連続した区間を指す。</summary>")
    w("internal readonly record struct FileIconDef(int Start, int Count);")
    w("")
    w("internal static class FileIconData")
    w("{")
    w("    /// <summary>閉じたフォルダーの索引。</summary>")
    w(f"    public const int FolderIndex = {idx[FOLDER]};")
    w("    /// <summary>開いたフォルダーの索引。</summary>")
    w(f"    public const int FolderOpenIndex = {idx[FOLDER_OPEN]};")
    w("    /// <summary>拡張子・ファイル名のどちらにも当たらなかったときの索引。</summary>")
    w(f"    public const int DefaultFileIndex = {idx[DEFAULT_FILE]};")
    w("")
    w(f"    /// <summary>暗色テーマ用の配色（Catppuccin {DARK_FLAVOR.capitalize()}）。</summary>")
    w("    public static readonly uint[] DarkPalette =")
    w("    [")
    for c in colors:
        w(f'        0xFF{conv.palette[c][0]}, // {c}')
    w("    ];")
    w("")
    w(f"    /// <summary>明色テーマ用の配色（Catppuccin {LIGHT_FLAVOR.capitalize()}）。</summary>")
    w("    public static readonly uint[] LightPalette =")
    w("    [")
    for c in colors:
        w(f'        0xFF{conv.palette[c][1]}, // {c}')
    w("    ];")
    w("")
    w("    /// <summary>レイヤーの座標変換（6 要素ずつ M11,M12,M21,M22,OffsetX,OffsetY）。")
    w("    /// 添字 0 は単位行列で、<see cref=\"FileIconLayer.Transform\"/> が 0 なら変換を省く。</summary>")
    w("    public static readonly double[] Transforms =")
    w("    [")
    for i, m in enumerate(matrices):
        w(f'        {", ".join(fmt(v) for v in m)},')
    w("    ];")
    w("")
    w("    /// <summary>全アイコンの線を平坦に並べたもの。区間は <see cref=\"Defs\"/> が指す。</summary>")
    w("    public static readonly FileIconLayer[] Layers =")
    w("    [")
    for l in flat:
        w(f'        new("{cs_escape(l["d"])}", {cidx[l["color"]]}, {midx[l["m"]]}, {fmt(l["width"])}, '
          f'{"true" if l["cap"] == "round" else "false"}, '
          f'{"true" if l["join"] == "round" else "false"}, '
          f'{"true" if l["fill"] else "false"}),')
    w("    ];")
    w("")
    w("    /// <summary>索引＝アイコン ID。</summary>")
    w("    public static readonly FileIconDef[] Defs =")
    w("    [")
    for n, (start, count) in zip(order, spans):
        w(f'        new({start}, {count}), // {n}')
    w("    ];")
    w("")
    w("    /// <summary>拡張子（先頭の \".\" 込み・小文字）→ <see cref=\"Defs\"/> の索引。</summary>")
    w("    public static readonly Dictionary<string, int> ByExtension = new(StringComparer.OrdinalIgnoreCase)")
    w("    {")
    for k in sorted(by_ext):
        w(f'        ["{cs_escape(k)}"] = {by_ext[k]},')
    w("    };")
    w("")
    w("    /// <summary>ファイル名まるごと（小文字）→ <see cref=\"Defs\"/> の索引。拡張子より優先する。</summary>")
    w("    public static readonly Dictionary<string, int> ByFileName = new(StringComparer.OrdinalIgnoreCase)")
    w("    {")
    for k in sorted(by_name):
        w(f'        ["{cs_escape(k)}"] = {by_name[k]},')
    w("    };")
    w("}")

    out = os.path.normpath(OUTPUT)
    open(out, "w", encoding="utf-8", newline="\r\n").write("\n".join(L) + "\n")
    print(f"icons={len(order)} layers={len(flat)} colors={len(colors)} "
          f"ext={len(by_ext)} name={len(by_name)} bytes={os.path.getsize(out)}")
    if failed:
        print(f"変換できなかったアイコン {len(failed)} 個（該当拡張子は既定アイコンになる）:")
        for n, e in sorted(failed.items())[:40]:
            print(f"  {n}: {e}")


if __name__ == "__main__":
    main()
