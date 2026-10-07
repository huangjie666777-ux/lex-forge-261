namespace LexForge261;

/// <summary>A single lexer rule. Rules are matched by priority order (index).</summary>
public sealed record LexRule(string Name, string Pattern, bool Skip = false);

/// <summary>A produced token. Offset/Length are 0-based; Line/Column are 1-based.</summary>
public sealed record LexToken(string Name, string Text, int Offset, int Length, int Line, int Column);

/// <summary>Raised when a rule set cannot be compiled.</summary>
public sealed class LexerCompileException : Exception
{
    public string? RuleName { get; }
    public int? PatternOffset { get; }

    public LexerCompileException(string message, string? ruleName = null, int? patternOffset = null)
        : base(message)
    {
        RuleName = ruleName;
        PatternOffset = patternOffset;
    }
}

/// <summary>Raised when input cannot be tokenized.</summary>
public sealed class LexerScanException : Exception
{
    public int Offset { get; }
    public int Line { get; }
    public int Column { get; }

    public LexerScanException(string message, int offset, int line, int column)
        : base(message)
    {
        Offset = offset;
        Line = line;
        Column = column;
    }
}
