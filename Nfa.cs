namespace LexForge261;

/// <summary>Thompson NFA over ASCII bytes with epsilon transitions.</summary>
internal sealed class Nfa
{
    public const int MaxStates = 4096;

    public sealed class State
    {
        public readonly List<(int Lo, int Hi, State Target)> Transitions = new();
        public readonly List<State> Epsilon = new();
        public int AcceptRule = -1; // rule index, or -1
    }

    public State Start { get; } = new();
    public int StateCount { get; private set; } = 1;

    private State NewState()
    {
        if (StateCount >= MaxStates)
            throw new LexerCompileException($"NFA state limit of {MaxStates} exceeded.");
        StateCount++;
        return new State();
    }

    /// <summary>Adds a rule; returns the fragment attached to the start state.</summary>
    public void AddRule(Node node, int ruleIndex)
    {
        State accept = NewState();
        accept.AcceptRule = ruleIndex;
        State entry = Build(node, accept);
        Start.Epsilon.Add(entry);
    }

    // Builds a fragment whose match ends in 'outState'; returns fragment entry.
    private State Build(Node node, State outState)
    {
        switch (node)
        {
            case Node.Literal lit:
            {
                State s = NewState();
                s.Transitions.Add((lit.C, lit.C, outState));
                return s;
            }
            case Node.CharClass cc:
            {
                State s = NewState();
                foreach (var (lo, hi) in cc.Ranges)
                    s.Transitions.Add((lo, hi, outState));
                return s;
            }
            case Node.Concat cat:
            {
                State next = outState;
                for (int i = cat.Items.Count - 1; i >= 0; i--)
                    next = Build(cat.Items[i], next);
                return next;
            }
            case Node.Alt alt:
            {
                State s = NewState();
                foreach (Node option in alt.Options)
                    s.Epsilon.Add(Build(option, outState));
                return s;
            }
            case Node.Star star:
            {
                State s = NewState();
                State body = Build(star.Inner, s);
                s.Epsilon.Add(body);
                s.Epsilon.Add(outState);
                return s;
            }
            case Node.Plus plus:
            {
                State loop = NewState();
                State body = Build(plus.Inner, loop);
                loop.Epsilon.Add(body);
                loop.Epsilon.Add(outState);
                return body;
            }
            case Node.Opt opt:
            {
                State s = NewState();
                s.Epsilon.Add(Build(opt.Inner, outState));
                s.Epsilon.Add(outState);
                return s;
            }
            default:
                throw new InvalidOperationException("Unknown node.");
        }
    }
}
