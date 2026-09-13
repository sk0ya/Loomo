using System.Diagnostics;
using sk0ya.Loomo.Completion.Host;
using sk0ya.Loomo.Core.Completion;

// 入力の先読みを回すワーカー。stdin から 1 行 1 依頼の JSON を読み、stdout へ応答を書く。
//
// 読み取りと生成を別スレッドにしてあるのが肝で、生成中でも次の依頼を読み取れる——読めなければ
// 「古くなった依頼を取り消す」こともできず、本体が待たされる。依頼が来たら走っている生成を
// 即座に取り消し、常に<b>最新の 1 件だけ</b>を処理する。打鍵のたびに依頼が来るので、
// 並べて待たせると手を止めた頃には何世代も前の答えが返ることになる。
//
// 使い方: sk0ya.Loomo.Completion.Host.exe <modelPath> [decodeThreads] [prefillThreads]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: sk0ya.Loomo.Completion.Host <modelPath> [decodeThreads] [prefillThreads]");
    return 2;
}

var modelPath = args[0];
int decodeThreads = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 0;
int prefillThreads = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 0;

using var engine = new FimEngine(modelPath, decodeThreads, prefillThreads);

var pending = new object();
FimProtocol.Request? next = null;
CancellationTokenSource? running = null;
long runningId = 0;
var signal = new SemaphoreSlim(0);
var stdout = Console.Out;

var worker = new Thread(() =>
{
    while (true)
    {
        signal.Wait();

        FimProtocol.Request request;
        CancellationTokenSource cts;
        lock (pending)
        {
            if (next is not { } queued) continue;   // 取り消しで空になった
            request = queued;
            next = null;
            cts = running = new CancellationTokenSource();
            runningId = request.Id;
        }

        var response = engine.Complete(request, cts.Token);
        try { stdout.WriteLine(FimProtocol.Serialize(response)); stdout.Flush(); }
        catch { return; }   // 本体が閉じた

        lock (pending)
        {
            if (ReferenceEquals(running, cts))
            {
                running = null;
                runningId = 0;
            }
        }
        cts.Dispose();
    }
})
{ IsBackground = true, Name = "FimGenerate" };
worker.Start();

Console.Error.WriteLine($"[fim] ready pid={Environment.ProcessId} model={Path.GetFileName(modelPath)}");

while (Console.ReadLine() is { } line)
{
    if (FimProtocol.ReadCancellation(line) is { } cancellation)
    {
        lock (pending)
        {
            if (next is { } queued && queued.Id == cancellation.Id)
                next = null;
            if (runningId == cancellation.Id)
                running?.Cancel();
        }
        continue;
    }

    if (FimProtocol.ReadRequest(line) is not { } request) continue;

    lock (pending)
    {
        // 走っている生成は、もう答えても遅い。先に止めてから新しい依頼を積む。
        running?.Cancel();
        bool wasWaiting = next is not null;
        next = request;
        if (!wasWaiting) signal.Release();
    }
}

return 0;
