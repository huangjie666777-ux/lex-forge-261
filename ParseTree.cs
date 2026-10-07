namespace LexForge261;

/// <summary>
/// A node in the ordered parse tree. Walk <see cref="Children"/> in order to
/// reproduce the derivation. Discriminate with <see cref="IsTerminal"/>.
/// </summary>
public abstract class ParseNode
{
    /// <summary>Symbol name: the nonterminal or the terminal (lexer rule) name.</summary>
    public string Name { get; }

    /// <summary>Ordered child nodes (empty for terminals and epsilon productions).</summary>
    public IReadOnlyList<ParseNode> Children { get; }

    public bool IsTerminal => this is TerminalNode;

    private protected ParseNode(string name, IReadOnlyList<ParseNode> children)
    {
        Name = name;
        Children = children;
    }
}

/// <summary>A terminal leaf; <see cref="Token"/> is the original token from the lexer.</summary>
public sealed class TerminalNode : ParseNode
{
    public LexToken Token { get; }

    internal TerminalNode(LexToken token)
        : base(token.Name, Array.Empty<ParseNode>())
    {
        Token = token;
    }
}

/// <summary>A nonterminal node labeled with the production used to derive it.</summary>
public sealed class NonterminalNode : ParseNode
{
    /// <summary>Identifier of the applied production.</summary>
    public string ProductionId { get; }

    internal NonterminalNode(string nonterminal, string productionId, IReadOnlyList<ParseNode> children)
        : base(nonterminal, children)
    {
        ProductionId = productionId;
    }
}

/// <summary>Source-range helpers over parse trees, based on original token offsets.</summary>
public static class ParseNodeExtensions
{
    /// <summary>
    /// Computes the source range covered by the node: from the offset of its
    /// first token to the end of its last token (skipped text in between is
    /// included). Returns false for subtrees that contain no token at all
    /// (e.g. a nullable nonterminal derived from epsilon).
    /// </summary>
    public static bool TryGetTokenRange(this ParseNode node, out int offset, out int length)
    {
        ArgumentNullException.ThrowIfNull(node);
        LexToken? first = null;
        LexToken? last = null;
        Walk(node);
        if (first is null)
        {
            offset = 0;
            length = 0;
            return false;
        }
        offset = first.Offset;
        length = last!.Offset + last.Length - first.Offset;
        return true;

        void Walk(ParseNode current)
        {
            if (current is TerminalNode leaf)
            {
                first ??= leaf.Token;
                last = leaf.Token;
                return;
            }
            foreach (ParseNode child in current.Children)
                Walk(child);
        }
    }
}
