using System.Text;
using sk0ya.Loomo.Core.Git;

// git・ssh の資格情報の問いを Loomo へ取り次ぐ（§24.101）。
// 引数 1 つ目が問いの文面。答えが得られたら標準出力へ書いて 0、取り消し・Loomo 不在なら何も書かずに 1。
// 失敗で終われば git は「could not read Password…」で止まり、ssh は認証をあきらめる——どちらも今までと同じ止まり方。
var pipe = Environment.GetEnvironmentVariable(GitAskPass.PipeVariable);
if (string.IsNullOrEmpty(pipe))
    return 1;

var request = new GitAskPass.Request(
    args.Length > 0 ? args[0] : "",
    Environment.GetEnvironmentVariable(GitAskPass.SshPromptVariable),
    Environment.GetEnvironmentVariable(GitAskPass.ContextVariable),
    Environment.GetEnvironmentVariable(GitAskPass.InvocationVariable));
var reply = await GitAskPass.AskAsync(pipe, request);
if (!reply.Answered)
    return 1;

// コンソールの符号化に任せない（cp932 だと日本語や記号を含む合言葉が化ける）。git も ssh も UTF-8 で読む。
using var stdout = Console.OpenStandardOutput();
var bytes = new UTF8Encoding(false).GetBytes((reply.Answer ?? "") + "\n");
stdout.Write(bytes);
stdout.Flush();
return 0;
