using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace sk0ya.Loomo.App.Services;

/// <summary>crx（Chrome/Edge の拡張機能パッケージ）を展開した結果。</summary>
/// <param name="PublicKey">パッケージに署名した公開鍵（DER, base64）。manifest の <c>key</c> に書き戻すと、
/// 展開して読み込んでも<b>ストアと同じ拡張機能 ID</b> になる（<see cref="CrxArchive"/> の注記）。</param>
public sealed record CrxExtractResult(string Directory, string? PublicKey);

/// <summary>
/// crx（CRX2 / CRX3）を素の ZIP に戻して展開する。
///
/// <para>WebView2 の <c>AddBrowserExtensionAsync</c> は<b>展開済みフォルダーしか受け付けない</b>
/// （＝ストアからの導入は crx を自分で剥がすしかない）。crx は「小さなヘッダ＋ZIP」なので、
/// ヘッダの長さぶん読み飛ばした残りを <see cref="ZipArchive"/> に渡せばよい。</para>
///
/// <para><b>公開鍵を manifest の <c>key</c> へ書き戻す</b>のが要点。展開フォルダーから読み込んだ拡張機能の
/// ID は、既定ではフォルダーのパスから作られる——つまり置き場所を変えるたびに別人になり、保存した設定も
/// 権限も失う。crx のヘッダには署名の公開鍵が入っていて、これを manifest に書いておくとストアで配られている
/// のと同じ ID に固定される。</para>
///
/// <para>ZIP の展開はパス脱出（<c>../</c> や絶対パス）を弾き、<c>_metadata/</c>（ストアが署名検証用に
/// 入れる内容で、展開済み読み込みでは検証に失敗して読み込めなくなる）は捨てる。</para>
/// </summary>
public static class CrxArchive
{
    private static readonly byte[] Magic = "Cr24"u8.ToArray();

    /// <summary>展開中の置き場に付ける接尾辞（<c>&lt;ID&gt;.tmp</c>）。
    /// <see cref="BrowserExtensionStore.CleanOrphanFolders"/> はこれを取り残しと見なさない。</summary>
    public const string StagingSuffix = ".tmp";

    /// <summary>crx を <paramref name="destination"/> へ展開する（中身は事前に空にする）。
    ///
    /// <para><b>展開が済むまで既存の中身に触らない</b>。隣の <c>&lt;destination&gt;.tmp</c> へ出し切ってから
    /// 置き換える——先に消すと、ダウンロードが途中で切れた crx（ZIP が壊れていて
    /// <see cref="ZipArchive"/> が投げる）で<b>入れ直しのつもりが今まで動いていた拡張機能を失う</b>ことになる。
    /// WebView2 には登録されたまま実体だけ消えるので、いちばん困る形の壊れ方をする。</para></summary>
    public static CrxExtractResult Extract(Stream crx, string destination)
    {
        var buffer = ReadAll(crx);
        var (zipOffset, publicKey) = ParseHeader(buffer);

        var staging = destination.TrimEnd(Path.DirectorySeparatorChar) + StagingSuffix;
        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            using (var zip = new ZipArchive(
                new MemoryStream(buffer, zipOffset, buffer.Length - zipOffset, writable: false),
                ZipArchiveMode.Read))
            {
                ExtractEntries(zip, staging);
            }

            if (publicKey is not null)
                TryWriteManifestKey(staging, publicKey);

            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
        }
        finally
        {
            // 途中で失敗したときの置き場を残さない（消せなくても実害は無い）。
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new CrxExtractResult(destination, publicKey);
    }

    /// <summary>ヘッダを読み、ZIP の開始位置と（あれば）署名の公開鍵を返す。</summary>
    private static (int ZipOffset, string? PublicKey) ParseHeader(byte[] buffer)
    {
        if (buffer.Length < 16 || !buffer.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("crx ファイルではありません（先頭が Cr24 ではありません）。");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
        return version switch
        {
            2 => ParseCrx2(buffer),
            3 => ParseCrx3(buffer),
            _ => throw new InvalidDataException($"未対応の crx バージョンです（{version}）。"),
        };
    }

    /// <summary>CRX2: Cr24 | version | 公開鍵長 | 署名長 | 公開鍵 | 署名 | ZIP。</summary>
    private static (int, string?) ParseCrx2(byte[] buffer)
    {
        // 長さは <c>uint</c>。int へ落としてから足すと桁あふれで負になり、範囲検査をすり抜けたまま
        // MemoryStream の生成で <c>ArgumentOutOfRangeException</c> になる——例外の型が変わるので
        // <see cref="BrowserExtensionStore.DownloadAsync"/> の Chrome→Edge の乗り換えも効かなくなる
        // （CRX3 側と同じ用心）。
        long keyLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
        long signatureLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4));
        var offset = 16 + keyLength + signatureLength;
        if (offset > buffer.Length)
            throw new InvalidDataException("crx のヘッダが壊れています。");
        return ((int)offset, keyLength > 0 ? Convert.ToBase64String(buffer, 16, (int)keyLength) : null);
    }

    /// <summary>CRX3: Cr24 | version | ヘッダ長 | ヘッダ(protobuf) | ZIP。
    /// ヘッダは <c>CrxFileHeader</c> で、公開鍵はフィールド2/3（署名の一覧）の中のフィールド1。</summary>
    private static (int, string?) ParseCrx3(byte[] buffer)
    {
        // 長さは <c>uint</c>。int へ落としてから足すと桁あふれで負になり、範囲検査をすり抜ける。
        long headerLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4));
        var offset = 12 + headerLength;
        if (offset > buffer.Length)
            throw new InvalidDataException("crx のヘッダが壊れています。");
        // 鍵が読めなくても展開自体は続ける（ID がパス由来になるだけで、拡張機能としては動く）。
        string? publicKey = null;
        try { publicKey = ReadCrx3PublicKey(buffer.AsSpan(12, (int)headerLength)); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { }
        return ((int)offset, publicKey);
    }

    /// <summary>ID を決めている公開鍵を選ぶ。
    /// <b>crx には署名が複数入る</b>——ストアの crx なら発行元の鍵も一緒に載っているので、
    /// 先頭の鍵をそのまま採ると<b>ストアと違う ID</b>になる（実機で確認済み）。
    /// ヘッダの <c>signed_header_data</c> が持つ <c>crx_id</c>（＝公開鍵の SHA256 の先頭16バイト）と
    /// 突き合わせて、当たったものを返す。</summary>
    private static string? ReadCrx3PublicKey(ReadOnlySpan<byte> header)
    {
        var keys = new List<byte[]>();
        byte[]? crxId = null;
        var reader = new ProtobufReader(header);
        while (reader.TryReadField(out var field, out var value))
        {
            switch (field)
            {
                // 2 = sha256_with_rsa, 3 = sha256_with_ecdsa。どちらも AsymmetricKeyProof で、
                // その中のフィールド1が公開鍵。
                case 2 or 3:
                    var proof = new ProtobufReader(value);
                    while (proof.TryReadField(out var proofField, out var proofValue))
                        if (proofField == 1 && proofValue.Length > 0)
                            keys.Add(proofValue.ToArray());
                    break;
                // 10000 = signed_header_data（SignedData）。そのフィールド1が crx_id。
                case 10000:
                    var signed = new ProtobufReader(value);
                    while (signed.TryReadField(out var signedField, out var signedValue))
                        // crx_id は公開鍵の SHA256 の先頭16バイト。長さが合わないものは見なかったことにする
                        // （SHA256 より長い値を渡されると、下の突き合わせが範囲外で落ちる）。
                        if (signedField == 1 && signedValue.Length is > 0 and <= 32)
                            crxId = signedValue.ToArray();
                    break;
            }
        }
        if (crxId is not null)
            foreach (var key in keys)
                if (SHA256.HashData(key).AsSpan(0, crxId.Length).SequenceEqual(crxId))
                    return Convert.ToBase64String(key);
        // crx_id が読めないときは先頭の鍵で妥協する（ID は変わり得るが、拡張機能としては動く）。
        return keys.Count > 0 ? Convert.ToBase64String(keys[0]) : null;
    }

    private static void ExtractEntries(ZipArchive zip, string destination)
    {
        var root = Path.GetFullPath(destination);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/'))
                continue;
            // ストアが入れる署名検証用の資産。展開済み読み込みでは検証が通らず、拡張機能ごと読み込めなくなる。
            if (entry.FullName.StartsWith("_metadata/", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            // ZIP の中の名前は信用しない（`../` や絶対パスで外へ書き出せてしまう）。
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>展開した manifest.json に <c>key</c> を書き戻して、ID をストアと同じに固定する。
    /// 既に <c>key</c> があるものはそのまま（自分で ID を宣言している拡張機能）。</summary>
    private static void TryWriteManifestKey(string directory, string publicKey)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
            return;
        try
        {
            // manifest.json は Chromium 側がコメント付きを許すので、読むときも許して落とさない
            // （書き戻しでコメントは消えるが、拡張機能の動作には影響しない）。
            var node = JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is not JsonObject manifest || manifest.ContainsKey("key"))
                return;
            manifest["key"] = publicKey;
            File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // ID がパス由来になるだけなので、ここで導入を止めない。
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memory && memory.TryGetBuffer(out var segment) && segment.Offset == 0)
            return segment.Array!.Length == segment.Count ? segment.Array! : memory.ToArray();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>protobuf の最小読み取り（長さ付きフィールドだけ返し、他は読み飛ばす）。
    /// crx のヘッダを読むためだけのもので、protobuf ランタイムを持ち込むほどの用は無い。</summary>
    private ref struct ProtobufReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _data = data;
        private int _position = 0;

        public bool TryReadField(out int fieldNumber, out ReadOnlySpan<byte> value)
        {
            fieldNumber = 0;
            value = default;
            while (_position < _data.Length)
            {
                var tag = ReadVarint();
                var wireType = (int)(tag & 7);
                var field = (int)(tag >> 3);
                switch (wireType)
                {
                    case 0:
                        ReadVarint();
                        break;
                    case 1:
                        Skip(8);
                        break;
                    case 2:
                        var length = (int)ReadVarint();
                        if (length < 0 || _position + length > _data.Length)
                            throw new InvalidDataException("protobuf の長さが範囲外です。");
                        value = _data.Slice(_position, length);
                        _position += length;
                        fieldNumber = field;
                        return true;
                    case 5:
                        Skip(4);
                        break;
                    default:
                        throw new InvalidDataException($"未対応の wire type です（{wireType}）。");
                }
            }
            return false;
        }

        private void Skip(int count)
        {
            if (_position + count > _data.Length)
                throw new InvalidDataException("protobuf が途中で終わっています。");
            _position += count;
        }

        private ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length)
                    throw new InvalidDataException("protobuf が途中で終わっています。");
                var b = _data[_position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
            }
            throw new InvalidDataException("varint が長すぎます。");
        }
    }
}
