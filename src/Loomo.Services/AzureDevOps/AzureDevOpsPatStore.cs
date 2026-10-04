using System;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace sk0ya.Loomo.Services;

/// <summary>
/// Azure DevOps の PAT（個人用アクセストークン）。TaskAzure と同じ場所・同じ順で探す：
/// <list type="number">
/// <item>環境変数 <c>ADO_PAT</c></item>
/// <item>Windows 資格情報マネージャーの汎用資格情報 <c>ADO_PAT</c></item>
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
    public string? Get()
    {
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return !string.IsNullOrWhiteSpace(env) ? env.Trim() : ReadFromCredentialManager();
    }

    /// <summary>PAT を送るヘッダー（Basic、ユーザー名は空）。</summary>
    public static AuthenticationHeaderValue CreateHeader(string pat)
        => new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));

    private static string? ReadFromCredentialManager()
    {
        if (!NativeMethods.CredRead(CredentialTarget, NativeMethods.CRED_TYPE_GENERIC, 0, out var pointer))
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
