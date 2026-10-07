namespace LexForge261;

/// <summary>
/// A node pattern matched against the parse tree. Patterns are supplied as
/// objects and compiled by <see cref="MigrationCompiler.Compile"/>.
/// </summary>
public abstract record NodePattern
{
    private NodePattern() { }

    /// <summary>
    /// Matches a nonterminal derived by the production <paramref name="ProductionId"/>;
    /// <paramref name="Children"/> are matched in order against the node's children
    /// and must mirror the production's right-hand side exactly.
    /// </summary>
    public sealed record Production(string ProductionId, IReadOnlyList<NodePattern> Children) : NodePattern;

    /// <summary>
    /// Matches a terminal with the given lexer rule name; when
    /// <paramref name="Text"/> is non-null the token's original text must equal it.
    /// </summary>
    public sealed record Token(string TokenName, string? Text = null) : NodePattern;

    /// <summary>Matches any single subtree and binds it to <paramref name="Name"/>.</summary>
    public sealed record Capture(string Name) : NodePattern;
}

/// <summary>A piece of a replacement template: literal text or a capture reference.</summary>
public abstract record TemplatePart
{
    private TemplatePart() { }

    /// <summary>Literal text emitted verbatim.</summary>
    public sealed record Literal(string Text) : TemplatePart;

    /// <summary>Emits the original source text of the named capture.</summary>
    public sealed record Capture(string Name) : TemplatePart;
}

/// <summary>
/// One ordered migration rule: a unique identifier, a tree pattern, and the
/// replacement template used when the pattern matches.
/// </summary>
public sealed record MigrationRule(
    string Id,
    NodePattern Pattern,
    IReadOnlyList<TemplatePart> Replacement);

/// <summary>A single applied rewrite, reported in original source offsets.</summary>
public sealed record MigrationEdit(string RuleId, int Offset, int Length, string Replacement);

/// <summary>The migrated source plus the ordered list of applied edits.</summary>
public sealed record MigrationResult(string Source, IReadOnlyList<MigrationEdit> Edits)
{
    /// <summary>True when at least one rule rewrote the input.</summary>
    public bool Changed => Edits.Count > 0;
}

/// <summary>Raised when a migration rule set cannot be compiled.</summary>
public sealed class MigrationCompileException : Exception
{
    /// <summary>The offending rule identifier, when applicable.</summary>
    public string? RuleId { get; }

    public MigrationCompileException(string message, string? ruleId = null)
        : base(message)
    {
        RuleId = ruleId;
    }
}

/// <summary>
/// Raised when the rewritten source fails to re-parse with the same parser.
/// No partial result is produced in this case.
/// </summary>
public sealed class MigrationValidationException : Exception
{
    public MigrationValidationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Compiles caller-supplied migration rules against a parser's grammar.</summary>
public static class MigrationCompiler
{
    /// <summary>
    /// Validates and snapshots the rules. Unknown production identifiers, child
    /// shapes that do not mirror the production's right-hand side, duplicate
    /// capture names, undefined template references, and duplicate rule
    /// identifiers are rejected. Later caller mutations of the inputs never
    /// affect the compiled migrator.
    /// </summary>
    public static SourceMigrator Compile(Parser parser, IReadOnlyList<MigrationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(rules);

        var productionsById = new Dictionary<string, Production>(StringComparer.Ordinal);
        foreach (Production p in parser.Grammar.Productions)
            productionsById[p.Id] = p;

        var snapshot = rules.ToArray();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new SourceMigrator.CompiledRule[snapshot.Length];

        for (int i = 0; i < snapshot.Length; i++)
        {
            MigrationRule rule = snapshot[i]
                ?? throw new MigrationCompileException($"Rule at index {i} is null.");
            if (string.IsNullOrEmpty(rule.Id))
                throw new MigrationCompileException($"Rule at index {i} has an empty identifier.");
            if (!seenIds.Add(rule.Id))
                throw new MigrationCompileException(
                    $"Duplicate migration rule identifier '{rule.Id}'.", rule.Id);
            if (rule.Pattern is null)
                throw new MigrationCompileException($"Rule '{rule.Id}' has a null pattern.", rule.Id);
            if (rule.Replacement is null)
                throw new MigrationCompileException($"Rule '{rule.Id}' has a null replacement.", rule.Id);
            if (rule.Pattern is not NodePattern.Production)
                throw new MigrationCompileException(
                    $"Rule '{rule.Id}' must be rooted at a production pattern.", rule.Id);

            var captures = new HashSet<string>(StringComparer.Ordinal);
            NodePattern pattern = SnapshotPattern(rule.Pattern, rule.Id, productionsById, captures);

            var replacement = new TemplatePart[rule.Replacement.Count];
            for (int j = 0; j < replacement.Length; j++)
            {
                TemplatePart part = rule.Replacement[j]
                    ?? throw new MigrationCompileException(
                        $"Rule '{rule.Id}' has a null template part at index {j}.", rule.Id);
                switch (part)
                {
                    case TemplatePart.Literal lit:
                        if (lit.Text is null)
                            throw new MigrationCompileException(
                                $"Rule '{rule.Id}' has a literal template part with null text.", rule.Id);
                        replacement[j] = lit;
                        break;
                    case TemplatePart.Capture cap:
                        if (string.IsNullOrEmpty(cap.Name))
                            throw new MigrationCompileException(
                                $"Rule '{rule.Id}' has a template reference with an empty name.", rule.Id);
                        if (!captures.Contains(cap.Name))
                            throw new MigrationCompileException(
                                $"Rule '{rule.Id}' template references undefined capture '{cap.Name}'.", rule.Id);
                        replacement[j] = cap;
                        break;
                    default:
                        throw new MigrationCompileException(
                            $"Rule '{rule.Id}' has an unknown template part kind.", rule.Id);
                }
            }

            compiled[i] = new SourceMigrator.CompiledRule(rule.Id, pattern, replacement);
        }

        return new SourceMigrator(parser, compiled);
    }

    private static NodePattern SnapshotPattern(
        NodePattern pattern,
        string ruleId,
        IReadOnlyDictionary<string, Production> productionsById,
        HashSet<string> captures)
    {
        switch (pattern)
        {
            case NodePattern.Token token:
            {
                if (string.IsNullOrEmpty(token.TokenName))
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' has a token pattern with an empty name.", ruleId);
                return token;
            }
            case NodePattern.Capture capture:
            {
                if (string.IsNullOrEmpty(capture.Name))
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' has a capture with an empty name.", ruleId);
                if (!captures.Add(capture.Name))
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' duplicates capture name '{capture.Name}'.", ruleId);
                return capture;
            }
            case NodePattern.Production production:
            {
                if (string.IsNullOrEmpty(production.ProductionId))
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' has a production pattern with an empty identifier.", ruleId);
                if (!productionsById.TryGetValue(production.ProductionId, out Production? def))
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' references unknown production '{production.ProductionId}'.", ruleId);
                if (production.Children is null)
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' production pattern '{production.ProductionId}' has null children.", ruleId);
                if (production.Children.Count != def.Right.Count)
                    throw new MigrationCompileException(
                        $"Rule '{ruleId}' production pattern '{production.ProductionId}' has " +
                        $"{production.Children.Count} child patterns but the production has " +
                        $"{def.Right.Count} right-hand side symbols.", ruleId);

                var children = new NodePattern[production.Children.Count];
                for (int i = 0; i < children.Length; i++)
                {
                    NodePattern child = production.Children[i]
                        ?? throw new MigrationCompileException(
                            $"Rule '{ruleId}' production pattern '{production.ProductionId}' " +
                            $"has a null child at index {i}.", ruleId);
                    GrammarSymbol symbol = def.Right[i];
                    switch (child)
                    {
                        case NodePattern.Token when !symbol.IsTerminal:
                            throw new MigrationCompileException(
                                $"Rule '{ruleId}' child {i} of '{production.ProductionId}' is a token " +
                                $"pattern but the grammar symbol is nonterminal '{symbol.Name}'.", ruleId);
                        case NodePattern.Token token when symbol.Name != token.TokenName:
                            throw new MigrationCompileException(
                                $"Rule '{ruleId}' child {i} of '{production.ProductionId}' matches token " +
                                $"'{token.TokenName}' but the grammar symbol is terminal '{symbol.Name}'.", ruleId);
                        case NodePattern.Production when symbol.IsTerminal:
                            throw new MigrationCompileException(
                                $"Rule '{ruleId}' child {i} of '{production.ProductionId}' is a production " +
                                $"pattern but the grammar symbol is terminal '{symbol.Name}'.", ruleId);
                        case NodePattern.Production nested
                            when productionsById.TryGetValue(nested.ProductionId, out Production? nestedDef)
                                 && nestedDef.Left != symbol.Name:
                            throw new MigrationCompileException(
                                $"Rule '{ruleId}' child {i} of '{production.ProductionId}' uses production " +
                                $"'{nested.ProductionId}' of '{nestedDef.Left}' where nonterminal " +
                                $"'{symbol.Name}' is required.", ruleId);
                    }
                    children[i] = SnapshotPattern(child, ruleId, productionsById, captures);
                }
                return production with { Children = children };
            }
            default:
                throw new MigrationCompileException(
                    $"Rule '{ruleId}' contains an unknown pattern kind.", ruleId);
        }
    }
}

/// <summary>
/// A compiled, immutable migrator bound to a <see cref="Parser"/>. Safe to reuse
/// across sources; no state is retained between calls.
/// </summary>
public sealed class SourceMigrator
{
    internal sealed record CompiledRule(string Id, NodePattern Pattern, TemplatePart[] Replacement);

    private readonly Parser _parser;
    private readonly CompiledRule[] _rules;

    internal SourceMigrator(Parser parser, CompiledRule[] rules)
    {
        _parser = parser;
        _rules = rules;
    }

    /// <summary>The parser whose lexer and grammar drive parsing and validation.</summary>
    public Parser Parser => _parser;

    /// <summary>The compiled rule identifiers, in caller-supplied order.</summary>
    public IReadOnlyList<string> RuleIds => _rules.Select(r => r.Id).ToArray();

    /// <summary>
    /// Parses <paramref name="source"/>, rewrites every match (preorder from the
    /// root; first matching rule per node wins and its subtree is not descended
    /// into), and validates the result with the same parser. All matching uses
    /// the original tree; a single call never rewrites its own output. When
    /// nothing matches the source is returned unchanged. Lexical and syntax
    /// errors of the input surface unchanged; a rewritten text that fails to
    /// re-parse raises <see cref="MigrationValidationException"/> and no partial
    /// result is returned.
    /// </summary>
    public MigrationResult Migrate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        NonterminalNode root = _parser.Parse(source);

        var edits = new List<MigrationEdit>();
        var captures = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        Visit(root, source, edits, captures);

        if (edits.Count == 0)
            return new MigrationResult(source, Array.Empty<MigrationEdit>());

        edits.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        var builder = new System.Text.StringBuilder(source.Length);
        int cursor = 0;
        foreach (MigrationEdit edit in edits)
        {
            builder.Append(source, cursor, edit.Offset - cursor);
            builder.Append(edit.Replacement);
            cursor = edit.Offset + edit.Length;
        }
        builder.Append(source, cursor, source.Length - cursor);
        string migrated = builder.ToString();

        try
        {
            _parser.Parse(migrated);
        }
        catch (Exception ex) when (ex is SyntaxException or LexerScanException)
        {
            throw new MigrationValidationException(
                $"The migrated source failed to re-parse: {ex.Message}", ex);
        }

        return new MigrationResult(migrated, edits);
    }

    private void Visit(
        ParseNode node,
        string source,
        List<MigrationEdit> edits,
        Dictionary<string, (int Start, int End)> captures)
    {
        if (node is TerminalNode)
            return;

        // Only nodes spanning at least one token are rewrite targets.
        if (TryGetTokenRange(node, out int start, out int end))
        {
            foreach (CompiledRule rule in _rules)
            {
                captures.Clear();
                if (Match(rule.Pattern, node, captures))
                {
                    edits.Add(new MigrationEdit(
                        rule.Id, start, end - start, Render(rule.Replacement, captures, source)));
                    return; // first matching rule wins; matched subtrees are not descended into
                }
            }
        }

        foreach (ParseNode child in node.Children)
            Visit(child, source, edits, captures);
    }

    private static bool Match(
        NodePattern pattern, ParseNode node, Dictionary<string, (int Start, int End)> captures)
    {
        switch (pattern)
        {
            case NodePattern.Token token:
                return node is TerminalNode leaf
                       && leaf.Name == token.TokenName
                       && (token.Text is null || leaf.Token.Text == token.Text);
            case NodePattern.Capture capture:
                // A capture of a tokenless (epsilon) subtree is a non-match.
                if (!TryGetTokenRange(node, out int captureStart, out int captureEnd))
                    return false;
                captures[capture.Name] = (captureStart, captureEnd);
                return true;
            case NodePattern.Production production:
                if (node is not NonterminalNode nt || nt.ProductionId != production.ProductionId)
                    return false;
                for (int i = 0; i < production.Children.Count; i++)
                {
                    if (!Match(production.Children[i], nt.Children[i], captures))
                        return false;
                }
                return true;
            default:
                return false;
        }
    }

    private static string Render(
        TemplatePart[] replacement,
        Dictionary<string, (int Start, int End)> captures,
        string source)
    {
        var builder = new System.Text.StringBuilder();
        foreach (TemplatePart part in replacement)
        {
            switch (part)
            {
                case TemplatePart.Literal literal:
                    builder.Append(literal.Text);
                    break;
                case TemplatePart.Capture capture:
                    var (captureStart, captureEnd) = captures[capture.Name];
                    builder.Append(source, captureStart, captureEnd - captureStart);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Token span of a subtree: from the first token's offset to the end of the
    /// last token, in original offsets. Skipped text inside the span is covered.
    /// </summary>
    private static bool TryGetTokenRange(ParseNode node, out int start, out int end)
    {
        start = int.MaxValue;
        end = int.MinValue;
        var stack = new Stack<ParseNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            ParseNode current = stack.Pop();
            if (current is TerminalNode leaf)
            {
                start = Math.Min(start, leaf.Token.Offset);
                end = Math.Max(end, leaf.Token.Offset + leaf.Token.Length);
            }
            else
            {
                for (int i = current.Children.Count - 1; i >= 0; i--)
                    stack.Push(current.Children[i]);
            }
        }
        return start <= end;
    }
}

