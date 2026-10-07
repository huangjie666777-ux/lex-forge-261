namespace LexForge261;

/// <summary>
/// A pattern matched against a parse-tree node. Patterns mirror the caller's
/// grammar: nonterminals match by production identifier and ordered child
/// patterns, terminals match by token name (optionally constrained to exact
/// source text), and named captures match any single subtree.
/// </summary>
public abstract record TreePattern
{
    private TreePattern() { }

    /// <summary>
    /// Matches a nonterminal node derived by the production
    /// <paramref name="productionId"/>; <paramref name="children"/> must have
    /// exactly one pattern per right-hand-side symbol of that production, in
    /// order.
    /// </summary>
    public sealed record Nonterminal(string ProductionId, IReadOnlyList<TreePattern> Children) : TreePattern;

    /// <summary>
    /// Matches a terminal leaf with token name <paramref name="tokenName"/>;
    /// when <paramref name="text"/> is non-null the token's original source
    /// text must equal it exactly.
    /// </summary>
    public sealed record Terminal(string TokenName, string? Text = null) : TreePattern;

    /// <summary>Matches any single subtree and binds it to <paramref name="name"/>.</summary>
    public sealed record Capture(string Name) : TreePattern;
}

/// <summary>A piece of a replacement template: literal text or a capture reference.</summary>
public abstract record TemplatePart
{
    private TemplatePart() { }

    /// <summary>Literal text emitted verbatim into the replacement.</summary>
    public sealed record Literal(string Text) : TemplatePart;

    /// <summary>
    /// Emits the original source text of the subtree captured under
    /// <paramref name="captureName"/> (no reformatting).
    /// </summary>
    public sealed record CaptureReference(string CaptureName) : TemplatePart;
}

/// <summary>
/// One ordered migration rule: a unique identifier, a tree pattern selecting
/// the nodes to rewrite, and the replacement template.
/// </summary>
public sealed record MigrationRule(
    string Id,
    TreePattern Pattern,
    IReadOnlyList<TemplatePart> Replacement);

/// <summary>
/// A single applied rewrite. <see cref="Offset"/>/<see cref="Length"/> refer to
/// the original source; <see cref="Replacement"/> is the substituted text.
/// </summary>
public sealed record SourceEdit(string RuleId, int Offset, int Length, string Replacement);

/// <summary>The migrated source plus the edits that produced it, ordered by original offset.</summary>
public sealed record MigrationResult(string Source, IReadOnlyList<SourceEdit> Edits)
{
    /// <summary>True when at least one rule matched.</summary>
    public bool Changed => Edits.Count > 0;
}

/// <summary>Raised when a migration rule set cannot be compiled.</summary>
public sealed class MigrationCompileException : Exception
{
    /// <summary>The rule involved, when applicable.</summary>
    public string? RuleId { get; }

    public MigrationCompileException(string message, string? ruleId = null)
        : base(message)
    {
        RuleId = ruleId;
    }
}

/// <summary>
/// Raised when a migration produced source that the bound parser cannot
/// re-parse completely. No partial result is returned in this case.
/// </summary>
public sealed class MigrationValidationException : Exception
{
    /// <summary>The rule whose replacement triggered the failure, when known.</summary>
    public string? RuleId { get; }

    public MigrationValidationException(string message, string? ruleId, Exception innerException)
        : base(message, innerException)
    {
        RuleId = ruleId;
    }
}

/// <summary>
/// A compiled, immutable set of ordered migration rules bound to a
/// <see cref="Parser"/>. Matching always runs against the original parse tree;
/// a single <see cref="Migrate"/> call never rewrites text produced by an
/// earlier rewrite in the same call.
/// </summary>
public sealed class SourceMigrator
{
    private readonly Parser _parser;
    private readonly CompiledRule[] _rules;

    private SourceMigrator(Parser parser, CompiledRule[] rules)
    {
        _parser = parser;
        _rules = rules;
    }

    /// <summary>
    /// Validates and snapshots the rule set. Compilation fails on duplicate
    /// rule identifiers, unknown production identifiers, child patterns that
    /// do not match the production's right-hand-side shape, duplicate capture
    /// names within a rule, or template references to undefined captures.
    /// </summary>
    public static SourceMigrator Compile(Parser parser, IReadOnlyList<MigrationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(rules);

        // Snapshot so later caller mutations cannot affect the compiled migrator.
        var snapshot = rules.ToArray();
        var byId = parser.Grammar.Productions.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<CompiledRule>(snapshot.Length);

        for (int i = 0; i < snapshot.Length; i++)
        {
            MigrationRule rule = snapshot[i]
                ?? throw new MigrationCompileException($"Migration rule at index {i} is null.");
            if (string.IsNullOrEmpty(rule.Id))
                throw new MigrationCompileException($"Migration rule at index {i} has an empty identifier.");
            if (!seen.Add(rule.Id))
                throw new MigrationCompileException(
                    $"Duplicate migration rule identifier '{rule.Id}'.", rule.Id);
            if (rule.Pattern is null)
                throw new MigrationCompileException($"Migration rule '{rule.Id}' has a null pattern.", rule.Id);
            if (rule.Replacement is null)
                throw new MigrationCompileException($"Migration rule '{rule.Id}' has a null replacement.", rule.Id);
            if (rule.Pattern is not TreePattern.Nonterminal root)
                throw new MigrationCompileException(
                    $"Migration rule '{rule.Id}' must be rooted at a nonterminal pattern.", rule.Id);

            var captures = new HashSet<string>(StringComparer.Ordinal);
            ValidateNodePattern(root, rule.Id, byId, captures, expectedNonterminal: null);

            var replacement = rule.Replacement.ToArray();
            foreach (TemplatePart part in replacement)
            {
                switch (part)
                {
                    case null:
                        throw new MigrationCompileException(
                            $"Migration rule '{rule.Id}' has a null template part.", rule.Id);
                    case TemplatePart.Literal lit when lit.Text is null:
                        throw new MigrationCompileException(
                            $"Migration rule '{rule.Id}' has a literal template part with null text.", rule.Id);
                    case TemplatePart.CaptureReference reference:
                        if (string.IsNullOrEmpty(reference.CaptureName))
                            throw new MigrationCompileException(
                                $"Migration rule '{rule.Id}' has a template reference with an empty name.", rule.Id);
                        if (!captures.Contains(reference.CaptureName))
                            throw new MigrationCompileException(
                                $"Migration rule '{rule.Id}' template references undefined capture " +
                                $"'{reference.CaptureName}'.", rule.Id);
                        break;
                }
            }

            compiled.Add(new CompiledRule(rule.Id, root, replacement));
        }

        return new SourceMigrator(parser, compiled.ToArray());
    }

    private static void ValidateNodePattern(
        TreePattern.Nonterminal pattern,
        string ruleId,
        IReadOnlyDictionary<string, Production> byId,
        HashSet<string> captures,
        string? expectedNonterminal)
    {
        if (string.IsNullOrEmpty(pattern.ProductionId))
            throw new MigrationCompileException(
                $"Migration rule '{ruleId}' contains a nonterminal pattern with an empty production identifier.",
                ruleId);
        if (!byId.TryGetValue(pattern.ProductionId, out Production? production))
            throw new MigrationCompileException(
                $"Migration rule '{ruleId}' references unknown production '{pattern.ProductionId}'.", ruleId);
        if (expectedNonterminal is not null && production.Left != expectedNonterminal)
            throw new MigrationCompileException(
                $"Migration rule '{ruleId}' uses production '{pattern.ProductionId}' " +
                $"(left-hand side '{production.Left}') where nonterminal '{expectedNonterminal}' is required.",
                ruleId);
        if (pattern.Children is null)
            throw new MigrationCompileException(
                $"Migration rule '{ruleId}' pattern for production '{pattern.ProductionId}' has null children.",
                ruleId);
        if (pattern.Children.Count != production.Right.Count)
            throw new MigrationCompileException(
                $"Migration rule '{ruleId}' pattern for production '{pattern.ProductionId}' has " +
                $"{pattern.Children.Count} child pattern(s) but the production has " +
                $"{production.Right.Count} right-hand-side symbol(s).", ruleId);

        for (int i = 0; i < production.Right.Count; i++)
        {
            GrammarSymbol symbol = production.Right[i];
            TreePattern child = pattern.Children[i]
                ?? throw new MigrationCompileException(
                    $"Migration rule '{ruleId}' pattern for production '{pattern.ProductionId}' " +
                    $"has a null child pattern at position {i}.", ruleId);
            switch (child)
            {
                case TreePattern.Capture capture:
                    if (string.IsNullOrEmpty(capture.Name))
                        throw new MigrationCompileException(
                            $"Migration rule '{ruleId}' contains a capture with an empty name.", ruleId);
                    if (!captures.Add(capture.Name))
                        throw new MigrationCompileException(
                            $"Migration rule '{ruleId}' contains duplicate capture name '{capture.Name}'.", ruleId);
                    break;
                case TreePattern.Terminal terminal:
                    if (!symbol.IsTerminal)
                        throw new MigrationCompileException(
                            $"Migration rule '{ruleId}' pattern for production '{pattern.ProductionId}' " +
                            $"uses a terminal pattern at position {i} where nonterminal '{symbol.Name}' is required.",
                            ruleId);
                    if (terminal.TokenName != symbol.Name)
                        throw new MigrationCompileException(
                            $"Migration rule '{ruleId}' pattern for production '{pattern.ProductionId}' " +
                            $"uses terminal '{terminal.TokenName}' at position {i} where '{symbol.Name}' is required.",
                            ruleId);
                    break;
                case TreePattern.Nonterminal nonterminal:
                    if (symbol.IsTerminal)
                        throw new MigrationCompileException(
                            $"Migration rule '{ruleId}' pattern for production '{pattern.ProductionId}' " +
                            $"uses a nonterminal pattern at position {i} where terminal '{symbol.Name}' is required.",
                            ruleId);
                    ValidateNodePattern(nonterminal, ruleId, byId, captures, symbol.Name);
                    break;
            }
        }
    }

    /// <summary>
    /// Parses <paramref name="source"/> with the bound parser, applies the
    /// first matching rule per node in pre-order (a matched node's descendants
    /// are not visited), splices the replacements using original offsets, and
    /// re-parses the result for full validation. When nothing matches the
    /// original source is returned unchanged.
    /// </summary>
    /// <exception cref="MigrationValidationException">
    /// The rewritten source failed to parse; no partial result is returned.
    /// </exception>
    public MigrationResult Migrate(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        NonterminalNode root = _parser.Parse(source);

        var edits = new List<SourceEdit>();
        Visit(root, source, edits);

        if (edits.Count == 0)
            return new MigrationResult(source, Array.Empty<SourceEdit>());

        edits.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

        var builder = new System.Text.StringBuilder(source.Length);
        int cursor = 0;
        foreach (SourceEdit edit in edits)
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
                $"Migration produced source that failed to re-parse: {ex.Message}",
                edits[^1].RuleId, ex);
        }

        return new MigrationResult(migrated, edits);
    }

    private void Visit(ParseNode node, string source, List<SourceEdit> edits)
    {
        // Only nodes covering at least one token are rewrite targets.
        if (!node.TryGetTokenRange(out int offset, out int length))
            return;

        if (node is NonterminalNode nonterminal)
        {
            foreach (CompiledRule rule in _rules)
            {
                var bindings = new Dictionary<string, ParseNode>(StringComparer.Ordinal);
                if (!Match(rule.Pattern, nonterminal, bindings))
                    continue;

                string replacement = BuildReplacement(rule, bindings, source);
                edits.Add(new SourceEdit(rule.Id, offset, length, replacement));
                return; // first matching rule wins; descendants are not visited
            }
        }

        foreach (ParseNode child in node.Children)
            Visit(child, source, edits);
    }

    private static bool Match(
        TreePattern.Nonterminal pattern, ParseNode node, Dictionary<string, ParseNode> bindings)
    {
        if (node is not NonterminalNode nonterminal ||
            nonterminal.ProductionId != pattern.ProductionId ||
            nonterminal.Children.Count != pattern.Children.Count)
            return false;

        // Snapshot bindings so a failed branch leaves no partial captures.
        var local = new Dictionary<string, ParseNode>(bindings, StringComparer.Ordinal);
        for (int i = 0; i < pattern.Children.Count; i++)
        {
            if (!MatchChild(pattern.Children[i], nonterminal.Children[i], local))
                return false;
        }

        foreach (KeyValuePair<string, ParseNode> pair in local)
            bindings[pair.Key] = pair.Value;
        return true;
    }

    private static bool MatchChild(
        TreePattern pattern, ParseNode node, Dictionary<string, ParseNode> bindings)
    {
        switch (pattern)
        {
            case TreePattern.Terminal terminal:
                return node is TerminalNode leaf &&
                       leaf.Name == terminal.TokenName &&
                       (terminal.Text is null || leaf.Token.Text == terminal.Text);
            case TreePattern.Capture capture:
                // A capture must bind a subtree that covers at least one token.
                if (!node.TryGetTokenRange(out _, out _))
                    return false;
                bindings[capture.Name] = node;
                return true;
            case TreePattern.Nonterminal nonterminal:
                return Match(nonterminal, node, bindings);
            default:
                return false;
        }
    }

    private static string BuildReplacement(
        CompiledRule rule, IReadOnlyDictionary<string, ParseNode> bindings, string source)
    {
        var builder = new System.Text.StringBuilder();
        foreach (TemplatePart part in rule.Replacement)
        {
            switch (part)
            {
                case TemplatePart.Literal literal:
                    builder.Append(literal.Text);
                    break;
                case TemplatePart.CaptureReference reference:
                    ParseNode captured = bindings[reference.CaptureName];
                    captured.TryGetTokenRange(out int offset, out int length);
                    builder.Append(source, offset, length);
                    break;
            }
        }
        return builder.ToString();
    }

    private sealed record CompiledRule(string Id, TreePattern.Nonterminal Pattern, TemplatePart[] Replacement);
}

