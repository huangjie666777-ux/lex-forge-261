namespace LexForge261;

/// <summary>
/// Result of auditing one rule against all earlier rules.
/// When <see cref="CanWin"/> is true, <see cref="WinningWitness"/> is a complete
/// input for which the rule wins (shortest possible, ASCII-lexicographic ties).
/// When false, the rule is fully shadowed: <see cref="ShortestAccepted"/> is its
/// shortest accepted input and <see cref="WinningRule"/> is the earlier rule that
/// actually wins on that input (longest match, declaration order as tie break).
/// </summary>
public sealed record RuleAuditEntry(
    int Index,
    string RuleName,
    bool CanWin,
    string? WinningWitness,
    string? ShortestAccepted,
    string? WinningRule);

/// <summary>A pair of rules whose languages share a complete, non-empty word.</summary>
public sealed record RuleOverlap(string FirstRule, string SecondRule, string Witness);

/// <summary>The full audit of a compiled rule set.</summary>
public sealed record RuleAuditReport(
    IReadOnlyList<RuleOverlap> Overlaps,
    IReadOnlyList<RuleAuditEntry> Entries);

/// <summary>
/// Reachability-based rule-set auditor. All witnesses come from shortest-path
/// searches over the compiled DFA (breadth-first, ASCII ascending); no regexes,
/// random sampling, or bounded-length enumeration are used.
/// </summary>
internal static class RuleAuditor
{
    public static RuleAuditReport Audit(Dfa dfa, string[] names)
    {
        int ruleCount = names.Length;

        // Breadth-first exploration from the start state. Edges are taken in ASCII
        // ascending order, so the first path reaching a state is the shortest word
        // leading to it and, among shortest words, the ASCII-lexicographically
        // smallest one.
        var parent = new int[dfa.Transitions.Length];
        var parentChar = new char[dfa.Transitions.Length];
        var order = new List<int>(dfa.Transitions.Length);
        Array.Fill(parent, -2);
        parent[0] = -1;
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int state = queue.Dequeue();
            order.Add(state);
            int[] row = dfa.Transitions[state];
            for (int c = 0; c < 128; c++)
            {
                int next = row[c];
                if (next >= 0 && parent[next] == -2)
                {
                    parent[next] = state;
                    parentChar[next] = (char)c;
                    queue.Enqueue(next);
                }
            }
        }

        var words = new string?[dfa.Transitions.Length];
        string Word(int state)
        {
            string? cached = words[state];
            if (cached is not null)
                return cached;
            var chars = new List<char>();
            for (int s = state; parent[s] >= 0; s = parent[s])
                chars.Add(parentChar[s]);
            chars.Reverse();
            cached = new string(chars.ToArray());
            words[state] = cached;
            return cached;
        }

        // Returns the first state (in shortest, lex-min discovery order) whose
        // accepted-rule set satisfies the predicate.
        int? FindState(Func<int, int[], bool> predicate)
        {
            foreach (int state in order)
                if (predicate(state, dfa.AcceptSets[state]))
                    return state;
            return null;
        }

        static bool Contains(int[] set, int rule)
            => Array.BinarySearch(set, rule) >= 0;

        // Pairwise intersections, each unordered pair reported once in declaration
        // order: (0,1), (0,2), ..., (1,2), ...
        var overlaps = new List<RuleOverlap>();
        for (int i = 0; i < ruleCount; i++)
        {
            for (int j = i + 1; j < ruleCount; j++)
            {
                int? target = FindState((_, set) => Contains(set, i) && Contains(set, j));
                if (target is int state)
                    overlaps.Add(new RuleOverlap(names[i], names[j], Word(state)));
            }
        }

        // Per-rule win/shadow analysis against the union of all earlier rules.
        var entries = new List<RuleAuditEntry>(ruleCount);
        for (int i = 0; i < ruleCount; i++)
        {
            // The rule wins on a complete input iff it is accepted there and no
            // earlier rule also accepts that complete input: the DFA's per-state
            // accept rule is the lowest-index accepting rule, i.e. exactly the
            // longest-match/order winner for that full input.
            int? winState = FindState((state, set) => Contains(set, i) && dfa.AcceptRule[state] == i);
            if (winState is int win)
            {
                entries.Add(new RuleAuditEntry(i, names[i], true, Word(win), null, null));
                continue;
            }

            // Fully shadowed by the union of preceding rules. Witness with the
            // shortest accepted word of this rule; the actual winner at that
            // point is necessarily an earlier rule.
            int? ownState = FindState((_, set) => Contains(set, i));
            if (ownState is not int own)
                throw new InvalidOperationException($"Rule '{names[i]}' accepts no non-empty input.");
            int winner = dfa.AcceptRule[own];
            entries.Add(new RuleAuditEntry(
                i, names[i], false, null, Word(own), winner >= 0 ? names[winner] : null));
        }

        return new RuleAuditReport(overlaps, entries);
    }
}
