namespace sk0ya.Loomo.Core.Tasks;

/// <summary>
/// 終わらないタスク（watch）の出力を1行ずつ受け取り、matcher の区切り（<see cref="ProblemMatcherBackground"/>）で
/// サイクルが閉じるたびに、そのタスクの問題をまとめて差し替える（設計書 §31.17.5）。
///
/// <para><b>サイクル単位で差し替える</b>のは VS Code と同じ考え方で、watch は「直したら次のコンパイルで消える」ことが
/// 本体だから。行ごとに足していくと、直した問題がいつまでも残る。begins で集め直しを始め、ends で出そろった分を
/// 渡す（0件なら空＝きれいになった）。区切りの無い matcher は1回のサイクルとみなし、タスクの終わりで当てる。</para>
///
/// <para>行は Terminal の購読（<c>SubscribeOutputLines</c>）から来る<b>論理行</b>で、画面幅の折り返しは既に継がれている
/// ので、<see cref="ProblemMatcherEngine.RejoinSoftWraps"/> は使わない。</para>
/// </summary>
public sealed class BackgroundProblemCollector
{
    /// <summary>1サイクルで溜める行の上限。ends が来ない（パターン違い）まま延々と出力されても膨らみ続けない。</summary>
    internal const int MaxLinesPerCycle = 20000;

    private readonly ProblemMatchContext _context;
    private readonly Action<IReadOnlyList<TaskProblem>> _publish;
    private readonly List<Cycle> _cycles;

    private sealed class Cycle(ProblemMatcher matcher)
    {
        public ProblemMatcher Matcher { get; } = matcher;
        public List<string> Lines { get; } = [];
        public bool Active { get; set; } = matcher.Background is not { } bg || bg.ActiveOnStart || bg.BeginsPattern is null;
        public IReadOnlyList<TaskProblem> Latest { get; set; } = [];
    }

    /// <param name="publish">サイクルが閉じるたびに、全 matcher の最新サイクル分を合わせた問題で呼ぶ。</param>
    public BackgroundProblemCollector(IReadOnlyList<ProblemMatcher> matchers, ProblemMatchContext context,
        Action<IReadOnlyList<TaskProblem>> publish)
    {
        _context = context;
        _publish = publish;
        _cycles = matchers.Select(m => new Cycle(m)).ToList();
    }

    public void OnLine(string line)
    {
        var changed = false;
        foreach (var cycle in _cycles)
        {
            if (cycle.Matcher.Background is not { } background) { Add(cycle, line); continue; }

            if (background.BeginsPattern?.IsMatch(line) == true)
            {
                cycle.Lines.Clear();
                cycle.Active = true;
                // begins と ends が同じ行に乗る書式もあるので、続けて ends も見る。
            }
            else if (cycle.Active)
            {
                Add(cycle, line);
            }

            if (cycle.Active && background.EndsPattern?.IsMatch(line) == true)
            {
                Close(cycle);
                changed = true;
            }
        }
        if (changed) Publish();
    }

    /// <summary>タスクが終わった（Ctrl+C・異常終了）。区切りの無い matcher はここで1回だけ当てる。
    /// 区切りのある matcher の閉じていないサイクルは捨てる——途中までの出力で問題を減らすと、直っていない
    /// ものまで消えて見えるため（前のサイクルの結果を残す）。</summary>
    public void Complete()
    {
        var changed = false;
        foreach (var cycle in _cycles.Where(c => c.Matcher.Background is null))
        {
            Close(cycle);
            changed = true;
        }
        if (changed) Publish();
    }

    private static void Add(Cycle cycle, string line)
    {
        if (cycle.Lines.Count < MaxLinesPerCycle) cycle.Lines.Add(line);
    }

    private void Close(Cycle cycle)
    {
        cycle.Latest = ProblemMatcherEngine.Match(string.Join('\n', cycle.Lines), [cycle.Matcher], _context,
            rejoinSoftWraps: false);
        cycle.Lines.Clear();
        cycle.Active = cycle.Matcher.Background is { BeginsPattern: null };
    }

    private void Publish()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<TaskProblem>();
        foreach (var problem in _cycles.SelectMany(c => c.Latest))
        {
            if (seen.Add($"{problem.FilePath}|{problem.Line}|{problem.Column}|{problem.Severity}|{problem.Code}|{problem.Message}"))
                all.Add(problem);
        }
        _publish(all);
    }
}
