namespace LexForge261;

/// <summary>
/// A compiled LL(1) parser bound to a <see cref="Lexer"/>. Immutable and
/// stateless with respect to calls: repeated parses never share state, and the
/// grammar definition was snapshotted at compile time.
/// </summary>
public sealed class Parser
{
    private readonly Lexer _lexer;
    private readonly CompiledGrammar _grammar;

    internal Parser(Lexer lexer, CompiledGrammar grammar)
    {
        _lexer = lexer;
        _grammar = grammar;
    }

    /// <summary>The compiled lexer tokens are drawn from.</summary>
    public Lexer Lexer => _lexer;

    /// <summary>The start nonterminal.</summary>
    public string Start => _grammar.Start;

    /// <summary>The compiled grammar, for structured tools bound to this parser.</summary>
    internal CompiledGrammar Grammar => _grammar;

    /// <summary>Tokenizes <paramref name="text"/> using the bound lexer.</summary>
    public IReadOnlyList<LexToken> Scan(string text) => _lexer.Scan(text);

    /// <summary>Delegates to the bound lexer's rule audit.</summary>
    public RuleAuditReport Audit() => _lexer.Audit();

    /// <summary>
    /// Tokenizes and parses the complete text. A successful parse consumes every
    /// token (the lookahead must be exactly EOF after deriving the start symbol);
    /// a legal prefix followed by extra input fails. Lexical errors surface as
    /// the original <see cref="LexerScanException"/>.
    /// </summary>
    public NonterminalNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        IReadOnlyList<LexToken> tokens = _lexer.Scan(text);
        return ParseTokens(tokens, text);
    }

    /// <summary>
    /// Parses a previously produced token stream. <paramref name="sourceText"/> is
    /// used only to locate EOF when the final tokens were followed by skipped text.
    /// </summary>
    public NonterminalNode ParseTokens(IReadOnlyList<LexToken> tokens, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(sourceText);

        int index = 0;
        NonterminalNode root = null!;

        LexToken? Current() => index < tokens.Count ? tokens[index] : null;
        string CurrentName() => Current() is { } t ? t.Name : LookaheadSymbols.EndOfInput;

        // Stable expected-set calculation for a failure point. `left` is the
        // nonterminal whose production is being expanded and `remaining` is the
        // unconsumed suffix of that production (including the offending symbol):
        // FIRST(remaining), extended with FOLLOW(left) when the whole suffix is
        // nullable.
        string[] Expecting(string left, IReadOnlyList<GrammarSymbol> remaining)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            bool allNullable = true;
            foreach (GrammarSymbol s in remaining)
            {
                if (s.IsTerminal)
                {
                    set.Add(s.Name);
                    allNullable = false;
                    break;
                }
                set.UnionWith(_grammar.First[s.Name]);
                if (!_grammar.Nullable.Contains(s.Name))
                {
                    allNullable = false;
                    break;
                }
            }
            if (allNullable)
                set.UnionWith(_grammar.Follow[left]);
            return set.OrderBy(name => name, Comparer<string>.Create(CompareLookahead))
                .Select(LookaheadSymbols.Display)
                .ToArray();
        }

        // Explicit stack frames keep parsing table-driven while preserving the
        // ordered tree. Each frame is one production expansion walked left to
        // right. The initial frame expands the start symbol.
        var stack = new Stack<Frame>();
        stack.Push(BeginExpansion(_grammar.Start, Current(), sourceText));

        while (stack.Count > 0)
        {
            Frame frame = stack.Peek();

            if (frame.IsComplete)
            {
                var node = new NonterminalNode(frame.Nonterminal, frame.ProductionId, frame.Children);
                stack.Pop();
                if (stack.Count > 0)
                    stack.Peek().AddChild(node);
                else
                    root = node;
                continue;
            }

            GrammarSymbol sym = frame.NextSymbol();
            if (sym.IsTerminal)
            {
                LexToken? tok = Current();
                if (tok is null || tok.Name != sym.Name)
                {
                    throw SyntaxError(
                        Expecting(frame.Nonterminal, frame.Remaining), Current(), sourceText);
                }
                index++;
                frame.AddChild(new TerminalNode(tok));
                frame.Advance();
                continue;
            }

            // Nonterminal: consult the predictive table.
            string lookahead = CurrentName();
            Dictionary<string, Production> row = _grammar.Table[sym.Name];
            if (!row.TryGetValue(lookahead, out Production? production))
                throw SyntaxError(Expecting(frame.Nonterminal, frame.Remaining), Current(), sourceText);

            frame.Advance();
            stack.Push(new Frame(sym.Name, production));
        }

        // Complete input only: the start symbol derived, but tokens may remain.
        if (Current() is { } leftover)
        {
            var expected = new[] { GrammarSymbols.Eof };
            throw new SyntaxException(
                leftover.Name, expected, leftover.Offset, leftover.Line, leftover.Column, leftover.Text);
        }

        return root;
    }

    private Frame BeginExpansion(string nonterminal, LexToken? current, string sourceText)
    {
        string lookahead = current?.Name ?? LookaheadSymbols.EndOfInput;
        if (!_grammar.Table[nonterminal].TryGetValue(lookahead, out Production? production))
            throw SyntaxError(
                ExpectTop(nonterminal), current, sourceText);
        return new Frame(nonterminal, production);
    }

    private string[] ExpectTop(string nonterminal)
    {
        // Failure while choosing the start production: expected is the full start
        // prediction set (FIRST(start), plus EOF if the start is nullable).
        var set = new HashSet<string>(_grammar.First[nonterminal], StringComparer.Ordinal);
        if (_grammar.Nullable.Contains(nonterminal))
            set.UnionWith(_grammar.Follow[nonterminal]);
        return set.OrderBy(name => name, Comparer<string>.Create(CompareLookahead))
            .Select(LookaheadSymbols.Display)
            .ToArray();
    }

    private static SyntaxException SyntaxError(
        string[] expected, LexToken? actual, string sourceText)
    {
        if (actual is { } tok)
        {
            return new SyntaxException(
                tok.Name, expected, tok.Offset, tok.Line, tok.Column, tok.Text);
        }

        var (offset, line, column) = EndPosition(sourceText);
        return new SyntaxException(GrammarSymbols.Eof, expected, offset, line, column, null);
    }

    private static int CompareLookahead(string? a, string? b)
    {
        if (a == b) return 0;
        if (a == LookaheadSymbols.EndOfInput) return 1;
        if (b == LookaheadSymbols.EndOfInput) return -1;
        return string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// Position immediately after all source text. Since skipped tokens never
    /// appear in the token stream, the end must be computed from the source so
    /// that trailing skipped text is included. Only LF advances the line.
    /// </summary>
    private static (int Offset, int Line, int Column) EndPosition(string text)
    {
        int line = 1, col = 1;
        foreach (char c in text)
        {
            if (c == '\n') { line++; col = 1; }
            else col++;
        }
        return (text.Length, line, col);
    }

    private sealed class Frame
    {
        public string Nonterminal { get; }
        public string ProductionId { get; }
        private readonly IReadOnlyList<GrammarSymbol> _symbols;
        private int _cursor;
        private readonly List<ParseNode> _children = new();

        public Frame(string nonterminal, Production production)
        {
            Nonterminal = nonterminal;
            ProductionId = production.Id;
            _symbols = production.Right;
        }

        public IReadOnlyList<ParseNode> Children => _children;
        public bool IsComplete => _cursor >= _symbols.Count;

        public GrammarSymbol NextSymbol() => _symbols[_cursor];
        public void Advance() => _cursor++;
        public void AddChild(ParseNode node) => _children.Add(node);
        public IReadOnlyList<GrammarSymbol> Remaining => _symbols.Skip(_cursor).ToArray();
    }
}
