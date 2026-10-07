namespace LexForge261;

/// <summary>A validated, immutable LL(1) grammar plus its predictive table.</summary>
internal sealed class CompiledGrammar
{
    public required string Start { get; init; }
    public required IReadOnlyList<Production> Productions { get; init; }
    public required IReadOnlySet<string> Nonterminals { get; init; }
    /// <summary>Productions grouped by left-hand side, in declared order.</summary>
    public required IReadOnlyDictionary<string, List<Production>> ByLeft { get; init; }
    /// <summary>Predictive table: nonterminal -> (terminal/EOF -> production).</summary>
    public required IReadOnlyDictionary<string, Dictionary<string, Production>> Table { get; init; }
    public required IReadOnlySet<string> Nullable { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlySet<string>> First { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlySet<string>> Follow { get; init; }
}

/// <summary>Compiles grammars into LL(1) predictive parsers.</summary>
public static class GrammarCompiler
{
    /// <summary>
    /// Validates the grammar against the lexer's token metadata, computes
    /// nullable/FIRST/FOLLOW fixed points, rejects left recursion, and builds the
    /// predictive table (including EOF). The grammar definition is snapshotted.
    /// </summary>
    public static Parser Compile(Lexer lexer, GrammarDefinition grammar)
    {
        ArgumentNullException.ThrowIfNull(lexer);
        ArgumentNullException.ThrowIfNull(grammar);
        ArgumentNullException.ThrowIfNull(grammar.Start);
        ArgumentNullException.ThrowIfNull(grammar.Productions);

        // Snapshot: later caller mutations of the definition never leak in.
        var productions = grammar.Productions
            .Select((p, i) => p is null
                ? throw new GrammarCompileException($"Production at index {i} is null.")
                : new Production(p.Id, p.Left, p.Right.ToArray()))
            .ToArray();

        var terminals = new HashSet<string>(lexer.TokenRuleNames, StringComparer.Ordinal);
        var nonterminals = new HashSet<string>(StringComparer.Ordinal);
        foreach (Production p in productions)
        {
            if (string.IsNullOrEmpty(p.Id))
                throw new GrammarCompileException("A production has an empty identifier.");
            if (string.IsNullOrEmpty(p.Left))
                throw new GrammarCompileException(
                    $"Production '{p.Id}' has an empty left-hand side.");
            if (p.Right is null)
                throw new GrammarCompileException(
                    $"Production '{p.Id}' has a null right-hand side.");
            nonterminals.Add(p.Left);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Production p in productions)
        {
            if (!ids.Add(p.Id))
                throw new GrammarCompileException($"Duplicate production identifier '{p.Id}'.");
        }

        if (!nonterminals.Contains(grammar.Start))
            throw new GrammarCompileException(
                $"Start symbol '{grammar.Start}' is not a defined nonterminal.", grammar.Start);

        // Every symbol on every right-hand side must be defined; terminals must
        // name a non-skip lexer rule (a skip rule reference is rejected).
        var skipNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string t in lexer.SkipRuleNames)
            skipNames.Add(t);
        foreach (Production p in productions)
        {
            foreach (GrammarSymbol sym in p.Right)
            {
                if (string.IsNullOrEmpty(sym.Name))
                    throw new GrammarCompileException(
                        $"Production '{p.Id}' contains a symbol with an empty name.");
                if (sym.IsTerminal)
                {
                    if (skipNames.Contains(sym.Name))
                        throw new GrammarCompileException(
                            $"Production '{p.Id}' references skipped lexer rule '{sym.Name}'; " +
                            "terminals may only reference non-skip rules.");
                    if (!terminals.Contains(sym.Name))
                        throw new GrammarCompileException(
                            $"Production '{p.Id}' references undefined terminal '{sym.Name}'.");
                }
                else if (!nonterminals.Contains(sym.Name))
                {
                    throw new GrammarCompileException(
                        $"Production '{p.Id}' references undefined nonterminal '{sym.Name}'.");
                }
            }
        }

        var byLeft = new Dictionary<string, List<Production>>(StringComparer.Ordinal);
        foreach (Production p in productions)
        {
            if (!byLeft.TryGetValue(p.Left, out var list))
                {
                    list = new List<Production>();
                    byLeft[p.Left] = list;
                }
            list.Add(p);
        }

        // ------------------------------------------------------------------
        // Nullable fixed point: a nonterminal is nullable if some production
        // derives epsilon.
        // ------------------------------------------------------------------
        var nullable = new HashSet<string>(StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Production p in productions)
            {
                if (nullable.Contains(p.Left))
                    continue;
                if (p.Right.All(s => !s.IsTerminal && nullable.Contains(s.Name)))
                {
                    nullable.Add(p.Left);
                    changed = true;
                }
            }
        }

        // Direct/indirect left recursion: A derives a nonempty nullable prefix
        // followed by A. Detected via fixed-point reachability of "prefix" sets.
        // For each nonterminal X, prefix[X] is the set of nonterminals Y such
        // that X =>+ Y... through a (possibly empty) nullable-prefixed chain.
        var prefix = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string nt in nonterminals)
            prefix[nt] = new HashSet<string>(StringComparer.Ordinal);

        changed = true;
        while (changed)
        {
            changed = false;
            foreach (Production p in productions)
            {
                HashSet<string> addTo = prefix[p.Left];
                foreach (GrammarSymbol sym in p.Right)
                {
                    if (sym.IsTerminal)
                        break;
                    // sym is a nonterminal: p.Left can start with sym.
                    if (addTo.Add(sym.Name))
                        changed = true;
                    // Propagation: if p.Left can start with sym, it can also
                    // start with whatever sym can start with (full nullable-prefix
                    // propagation). Stop scanning once a symbol cannot derive
                    // epsilon, since later symbols are not in a nullable prefix.
                    foreach (string transitively in prefix[sym.Name].ToArray())
                    {
                        if (addTo.Add(transitively))
                            changed = true;
                    }
                    if (!nullable.Contains(sym.Name))
                        break;
                }
            }
        }

        foreach (string nt in nonterminals)
        {
            if (prefix[nt].Contains(nt))
                throw new GrammarCompileException(
                    $"Nonterminal '{nt}' is left-recursive (directly or indirectly); the grammar is not LL(1).",
                    nt);
        }

        // ------------------------------------------------------------------
        // FIRST fixed point over nonterminals.
        // ------------------------------------------------------------------
        var first = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string nt in nonterminals)
            first[nt] = new HashSet<string>(StringComparer.Ordinal);

        changed = true;
        while (changed)
        {
            changed = false;
            foreach (Production p in productions)
            {
                HashSet<string> target = first[p.Left];
                foreach (GrammarSymbol sym in p.Right)
                {
                    if (sym.IsTerminal)
                    {
                        if (target.Add(sym.Name))
                            changed = true;
                        break;
                    }
                    int before = target.Count;
                    target.UnionWith(first[sym.Name]);
                    if (target.Count != before)
                        changed = true;
                    if (!nullable.Contains(sym.Name))
                        break;
                }
            }
        }

        // ------------------------------------------------------------------
        // FOLLOW fixed point (EOF belongs to the start symbol).
        // ------------------------------------------------------------------
        var follow = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string nt in nonterminals)
            follow[nt] = new HashSet<string>(StringComparer.Ordinal);
        follow[grammar.Start].Add(GrammarSymbols.Eof);

        changed = true;
        while (changed)
        {
            changed = false;
            foreach (Production p in productions)
            {
                for (int i = 0; i < p.Right.Count; i++)
                {
                    GrammarSymbol sym = p.Right[i];
                    if (sym.IsTerminal)
                        continue;
                    HashSet<string> target = follow[sym.Name];

                    // FIRST of the suffix after sym.
                    bool suffixNullable = true;
                    for (int j = i + 1; j < p.Right.Count; j++)
                    {
                        GrammarSymbol next = p.Right[j];
                        if (next.IsTerminal)
                        {
                            if (target.Add(next.Name))
                                changed = true;
                            suffixNullable = false;
                            break;
                        }
                        int before = target.Count;
                        target.UnionWith(first[next.Name]);
                        if (target.Count != before)
                            changed = true;
                        if (!nullable.Contains(next.Name))
                        {
                            suffixNullable = false;
                            break;
                        }
                    }

                    if (suffixNullable)
                    {
                        int before = target.Count;
                        target.UnionWith(follow[p.Left]);
                        if (target.Count != before)
                            changed = true;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Predictive table with conflict detection. Conflicts fail the build
        // with all colliding production identifiers (no arbitrary choice).
        // ------------------------------------------------------------------
        var table = new Dictionary<string, Dictionary<string, Production>>(StringComparer.Ordinal);
        foreach (string nt in nonterminals)
            table[nt] = new Dictionary<string, Production>(StringComparer.Ordinal);

        foreach (Production p in productions)
        {
            var predicting = new HashSet<string>(StringComparer.Ordinal);
            bool allNullable = true;
            foreach (GrammarSymbol sym in p.Right)
            {
                if (sym.IsTerminal)
                {
                    predicting.Add(sym.Name);
                    allNullable = false;
                    break;
                }
                predicting.UnionWith(first[sym.Name]);
                if (!nullable.Contains(sym.Name))
                {
                    allNullable = false;
                    break;
                }
            }
            if (allNullable)
                predicting.UnionWith(follow[p.Left]);

            foreach (string lookahead in predicting)
            {
                Dictionary<string, Production> row = table[p.Left];
                if (row.TryGetValue(lookahead, out Production? existing))
                {
                    var conflicting = new[] { existing.Id, p.Id }
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .ToArray();
                    throw new GrammarCompileException(
                        $"LL(1) conflict for nonterminal '{p.Left}' on lookahead '{lookahead}': " +
                        $"productions {string.Join(", ", conflicting)} all apply.",
                        p.Left, lookahead, conflicting);
                }
                row[lookahead] = p;
            }
        }

        var compiled = new CompiledGrammar
        {
            Start = grammar.Start,
            Productions = productions,
            Nonterminals = nonterminals,
            ByLeft = byLeft,
            Table = table,
            Nullable = nullable,
            First = first.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value),
            Follow = follow.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value),
        };
        return new Parser(lexer, compiled);
    }
}
