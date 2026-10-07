namespace LexForge261;

/// <summary>
/// A compiled, immutable lexer. Safe to reuse across threads and texts;
/// no scanning state is retained between calls.
/// </summary>
public sealed class Lexer
{
    private readonly Dfa _dfa;
    private readonly string[] _names;
    private readonly bool[] _skip;

    /// <summary>Names of rules that emit tokens (i.e. non-skip rules), in declaration order.</summary>
    internal string[] TokenRuleNames => _names.Where((_, i) => !_skip[i]).ToArray();

    /// <summary>Names of skip rules, in declaration order.</summary>
    internal string[] SkipRuleNames => _names.Where((_, i) => _skip[i]).ToArray();

    internal Lexer(Dfa dfa, string[] names, bool[] skip)
    {
        _dfa = dfa;
        _names = names;
        _skip = skip;
    }

    /// <summary>
    /// Audits rule intersections and per-rule win/shadow status. Pure: repeated
    /// calls are independent and never affect scanning or the caller's rules.
    /// </summary>
    public RuleAuditReport Audit() => RuleAuditor.Audit(_dfa, _names);

    /// <summary>Tokenizes the entire text. Empty text yields an empty result.</summary>
    public IReadOnlyList<LexToken> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<LexToken>();
        int pos = 0, line = 1, col = 1;
        while (pos < text.Length)
        {
            int state = 0;
            int lastAcceptPos = -1, lastAcceptRule = -1;
            int cursor = pos;
            while (cursor < text.Length)
            {
                char c = text[cursor];
                if (c > 0x7F)
                    break; // handled below if no accept was reached
                int next = _dfa.Transitions[state][c];
                if (next < 0)
                    break;
                state = next;
                cursor++;
                int rule = _dfa.AcceptRule[state];
                if (rule >= 0)
                {
                    lastAcceptPos = cursor;
                    lastAcceptRule = rule;
                }
            }
            if (lastAcceptPos < 0)
            {
                char bad = text[pos];
                string why = bad > 0x7F
                    ? $"Non-ASCII character U+{(int)bad:X4}"
                    : $"Unrecognized character '{bad}'";
                throw new LexerScanException($"{why} at offset {pos} (line {line}, column {col}).", pos, line, col);
            }
            string lexeme = text.Substring(pos, lastAcceptPos - pos);
            if (!_skip[lastAcceptRule])
                tokens.Add(new LexToken(_names[lastAcceptRule], lexeme, pos, lexeme.Length, line, col));
            foreach (char c in lexeme)
            {
                if (c == '\n') { line++; col = 1; }
                else col++;
            }
            pos = lastAcceptPos;
        }
        return tokens;
    }
}

/// <summary>Compiles prioritized rules into a reusable <see cref="Lexer"/>.</summary>
public static class LexerCompiler
{
    public static Lexer Compile(IReadOnlyList<LexRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
            throw new LexerCompileException("At least one rule is required.");

        // Snapshot so later caller mutations cannot affect the compiled lexer.
        var snapshot = rules.ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nfa = new Nfa();
        var names = new string[snapshot.Length];
        var skip = new bool[snapshot.Length];

        for (int i = 0; i < snapshot.Length; i++)
        {
            LexRule rule = snapshot[i] ?? throw new LexerCompileException($"Rule at index {i} is null.");
            if (string.IsNullOrEmpty(rule.Name))
                throw new LexerCompileException($"Rule at index {i} has an empty name.");
            if (!seen.Add(rule.Name))
                throw new LexerCompileException($"Duplicate rule name '{rule.Name}'.", rule.Name);
            if (rule.Pattern is null)
                throw new LexerCompileException($"Rule '{rule.Name}' has a null pattern.", rule.Name);

            Node ast = PatternParser.Parse(rule.Pattern, rule.Name);
            if (Node.IsNullable(ast))
                throw new LexerCompileException($"Rule '{rule.Name}' can match the empty string.", rule.Name);
            nfa.AddRule(ast, i);
            names[i] = rule.Name;
            skip[i] = rule.Skip;
        }

        Dfa dfa = Dfa.Build(nfa);
        return new Lexer(dfa, names, skip);
    }
}
