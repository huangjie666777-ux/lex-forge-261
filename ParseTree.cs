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
