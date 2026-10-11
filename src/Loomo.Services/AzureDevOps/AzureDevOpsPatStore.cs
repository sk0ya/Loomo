using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace sk0ya.Loomo.Services;

/// <summary>PAT の候補1つと、どこから取ったか（人に見せる一言）。</summary>
public sealed record PatCandidate(string Pat, string Source);

/// <summary>
/// Azure DevOps の PAT（個人用アクセストークン）。TaskAzure と同じ場所・同じ順で探す：
/// <list type="number">
/// <item>環境変数（TaskAzure の設定の PatEnvVarName、既定 <c>ADO_PAT</c>）</item>
/// <item>Windows 資格情報マネージャーの汎用資格情報（同じ名前）</item>
/// </list>
/// 置き場所を TaskAzure と揃えてあるので、TaskAzure で保存した PAT がそのまま使える。
/// <para><b>読むだけ。書かない</b>——<c>ADO_PAT</c> は TaskAzure と共有している資格情報なので、Loomo から上書きすると
/// TaskAzure の PAT まで書き換わる。PAT の登録・更新は TaskAzure（か資格情報マネージャー）で行う。</para>
/// <para>はじめは Git Credential Manager から借りる方式にしたが、職場の環境で通らなかったため、
/// 実際に動いている TaskAzure の方式に合わせた。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AzureDevOpsPatStore
{
    public const string EnvironmentVariable = "ADO_PAT";
    public const string CredentialTarget = "ADO_PAT";

    /// <summary>PAT。どこにも無ければ null。</summary>
    public string? Get() => FindCandidates().FirstOrDefault()?.Pat;

    /// <summary>
    /// PAT の候補を試す順に（同じ値は1つにまとめる）。TaskAzure の順（環境変数 → 資格情報マネージャー）を
    /// 名前ごとに、<paramref name="taskAzureEnvVarName"/>（TaskAzure の設定の PatEnvVarName）を先にして並べる。
    /// <para>環境変数は<b>プロセスが起動した時点の値</b>なので、長く起動している Loomo は古い PAT を握ったままのことがある
    /// （起動し直したばかりの TaskAzure は新しい値を読む）。だから保存されている今の値（ユーザー・システムの環境変数）も候補に入れ、
    /// 受け付けられなかったら次を試す。</para>
    /// </summary>
    public IReadOnlyList<PatCandidate> FindCandidates(string? taskAzureEnvVarName = null)
    {
        var names = new[] { taskAzureEnvVarName, EnvironmentVariable }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<PatCandidate>();
        foreach (var name in names)
        {
            Add(Environment.GetEnvironmentVariable(name), $"環境変数 {name}（Loomo の起動時の値）");
            Add(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User), $"環境変数 {name}（ユーザー）");
            Add(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine), $"環境変数 {name}（システム）");
            Add(ReadFromCredentialManager(name), $"Windows 資格情報マネージャーの {name}");
        }
        return result;

        void Add(string? pat, string source)
        {
            if (string.IsNullOrWhiteSpace(pat)) return;
            pat = pat.Trim();
            if (result.All(c => c.Pat != pat)) result.Add(new PatCandidate(pat, source));
        }
    }

    /// <summary>PAT を送るヘッダー（Basic、ユーザー名は空）。</summary>
    public static AuthenticationHeaderValue CreateHeader(string pat)
        => new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));

    private static string? ReadFromCredentialManager(string target)
    {
        if (!NativeMethods.CredRead(target, NativeMethods.CRED_TYPE_GENERIC, 0, out var pointer))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var pat = Encoding.Unicode.GetString(bytes).Trim();
            return pat.Length > 0 ? pat : null;
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    private static class NativeMethods
    {
        public const uint CRED_TYPE_GENERIC = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, int reserved, out IntPtr credential);

        [DllImport("advapi32.dll")]
        public static extern void CredFree(IntPtr credential);
    }
}
