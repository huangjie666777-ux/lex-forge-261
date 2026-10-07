namespace LexForge261;

/// <summary>The end-of-input lookahead symbol used in FIRST/FOLLOW tables.</summary>
public static class GrammarSymbols
{
    public const string Eof = "EOF";
}

/// <summary>A grammar symbol: either a nonterminal or a terminal (lexer rule name).</summary>
public readonly record struct GrammarSymbol(string Name, bool IsTerminal)
{
    public static GrammarSymbol Nonterminal(string name) => new(name, false);
    public static GrammarSymbol Terminal(string name) => new(name, true);

    public override string ToString() => IsTerminal ? $"'{Name}'" : Name;
}

/// <summary>
/// An ordered production. <paramref name="Id"/> must be unique within a grammar,
/// <paramref name="Left"/> is the nonterminal it rewrites, and <paramref name="Right"/>
/// is the (possibly empty) symbol sequence; an empty sequence denotes epsilon.
/// Terminals reference non-skip lexer rule names.
/// </summary>
public sealed record Production(string Id, string Left, IReadOnlyList<GrammarSymbol> Right);

/// <summary>A grammar paired with the lexer whose token names its terminals refer to.</summary>
public sealed record GrammarDefinition(
    string Start,
    IReadOnlyList<Production> Productions);

/// <summary>Raised when a grammar cannot be compiled into an LL(1) parser.</summary>
public sealed class GrammarCompileException : Exception
{
    /// <summary>The nonterminal involved, when applicable.</summary>
    public string? Nonterminal { get; }

    /// <summary>The lookahead terminal involved (or "EOF"), when applicable.</summary>
    public string? Lookahead { get; }

    /// <summary>Production identifiers involved (e.g. a table conflict), stably ordered.</summary>
    public IReadOnlyList<string> ProductionIds { get; }

    public GrammarCompileException(
        string message,
        string? nonterminal = null,
        string? lookahead = null,
        IReadOnlyList<string>? productionIds = null)
        : base(message)
    {
        Nonterminal = nonterminal;
        Lookahead = lookahead;
        ProductionIds = productionIds ?? Array.Empty<string>();
    }
}

/// <summary>Raised when the token stream does not match the compiled grammar.</summary>
public sealed class SyntaxException : Exception
{
    /// <summary>Actual token name, or "EOF" when input was exhausted.</summary>
    public string Actual { get; }

    /// <summary>Expected terminal names, stably sorted; "EOF" is sorted last.</summary>
    public IReadOnlyList<string> Expected { get; }

    /// <summary>0-based source offset of the actual token (or of the end position).</summary>
    public int Offset { get; }

    /// <summary>1-based source line.</summary>
    public int Line { get; }

    /// <summary>1-based source column (only LF advances the line).</summary>
    public int Column { get; }

    /// <summary>Text of the actual token; null for EOF.</summary>
    public string? ActualText { get; }

    public SyntaxException(
        string actual,
        IReadOnlyList<string> expected,
        int offset,
        int line,
        int column,
        string? actualText)
        : base(BuildMessage(actual, expected, offset, line, column, actualText))
    {
        Actual = actual;
        Expected = expected;
        Offset = offset;
        Line = line;
        Column = column;
        ActualText = actualText;
    }

    private static string BuildMessage(
        string actual, IReadOnlyList<string> expected, int offset, int line, int column, string? actualText)
    {
        string shownActual = actual == GrammarSymbols.Eof
            ? "EOF"
            : $"{actual} ('{actualText}')";
        return $"Syntax error at offset {offset} (line {line}, column {column}): " +
               $"unexpected {shownActual}; expected {string.Join(", ", expected)}.";
    }
}
