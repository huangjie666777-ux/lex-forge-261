namespace LexForge261;

/// <summary>Shared DFA built by subset construction with epsilon closure.</summary>
internal sealed class Dfa
{
    public const int MaxStates = 4096;
    private const int Alphabet = 128;

    public readonly int[][] Transitions; // [state][char] -> state, -1 = dead
    public readonly int[] AcceptRule;    // best (lowest-index) rule per state, -1 = none
    public readonly int[][] AcceptSets;  // sorted rule indices accepted by each state

    private Dfa(int[][] transitions, int[] acceptRule, int[][] acceptSets)
    {
        Transitions = transitions;
        AcceptRule = acceptRule;
        AcceptSets = acceptSets;
    }

    public static Dfa Build(Nfa nfa)
    {
        var closureCache = new Dictionary<Nfa.State, List<Nfa.State>>();
        var dfaStates = new List<HashSet<Nfa.State>>();
        var ids = new Dictionary<HashSet<Nfa.State>, int>(HashSet<Nfa.State>.CreateSetComparer());
        var transitions = new List<int[]>();
        var accept = new List<int>();
        var acceptSets = new List<int[]>();

        int AddState(HashSet<Nfa.State> set)
        {
            if (ids.TryGetValue(set, out int existing))
                return existing;
            if (dfaStates.Count >= MaxStates)
                throw new LexerCompileException($"DFA state limit of {MaxStates} exceeded.");
            int id = dfaStates.Count;
            dfaStates.Add(set);
            ids.Add(set, id);
            transitions.Add(new int[Alphabet]);
            Array.Fill(transitions[id], -1);
            int best = -1;
            var accepted = new SortedSet<int>();
            foreach (Nfa.State s in set)
                if (s.AcceptRule >= 0)
                {
                    accepted.Add(s.AcceptRule);
                    if (best < 0 || s.AcceptRule < best)
                        best = s.AcceptRule;
                }
            accept.Add(best);
            acceptSets.Add(accepted.ToArray());
            return id;
        }

        AddState(EpsilonClosure(new[] { nfa.Start }, closureCache));
        for (int i = 0; i < dfaStates.Count; i++)
        {
            // Collect per-character moves by expanding ranges.
            var moves = new Dictionary<int, HashSet<Nfa.State>>();
            foreach (Nfa.State s in dfaStates[i])
            {
                foreach (var (lo, hi, target) in s.Transitions)
                {
                    for (int c = lo; c <= hi; c++)
                    {
                        if (!moves.TryGetValue(c, out var bucket))
                            moves[c] = bucket = new HashSet<Nfa.State>();
                        bucket.Add(target);
                    }
                }
            }
            foreach (var (c, targets) in moves)
                transitions[i][c] = AddState(EpsilonClosure(targets, closureCache));
        }
        return new Dfa(transitions.ToArray(), accept.ToArray(), acceptSets.ToArray());
    }

    private static HashSet<Nfa.State> EpsilonClosure(IEnumerable<Nfa.State> seeds,
        Dictionary<Nfa.State, List<Nfa.State>> cache)
    {
        var result = new HashSet<Nfa.State>();
        var stack = new Stack<Nfa.State>();
        foreach (Nfa.State s in seeds)
        {
            if (!cache.TryGetValue(s, out var memo))
            {
                memo = ComputeClosure(s);
                cache[s] = memo;
            }
            foreach (Nfa.State m in memo)
                if (result.Add(m))
                    stack.Push(m);
        }
        return result;
    }

    private static List<Nfa.State> ComputeClosure(Nfa.State seed)
    {
        var list = new List<Nfa.State>();
        var seen = new HashSet<Nfa.State>();
        var stack = new Stack<Nfa.State>();
        stack.Push(seed);
        while (stack.Count > 0)
        {
            Nfa.State s = stack.Pop();
            if (!seen.Add(s))
                continue;
            list.Add(s);
            foreach (Nfa.State e in s.Epsilon)
                stack.Push(e);
        }
        return list;
    }
}
