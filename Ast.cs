namespace LexForge261;

internal abstract record Node
{
    public sealed record Literal(char C) : Node;
    public sealed record CharClass(List<(char Lo, char Hi)> Ranges) : Node;
    public sealed record Concat(List<Node> Items) : Node;
    public sealed record Alt(List<Node> Options) : Node;
    public sealed record Star(Node Inner) : Node;
    public sealed record Plus(Node Inner) : Node;
    public sealed record Opt(Node Inner) : Node;

    public static bool IsNullable(Node n) => n switch
    {
        Literal => false,
        CharClass => false,
        Concat c => c.Items.All(IsNullable),
        Alt a => a.Options.Any(IsNullable),
        Star => true,
        Plus p => IsNullable(p.Inner),
        Opt => true,
        _ => false,
    };
}
