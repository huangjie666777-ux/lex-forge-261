using LexForge261;
using Xunit;

namespace LexForge261.Tests;

public class LexerTests
{
    private static readonly LexRule[] ExprRules =
    {
        new("KeywordIf", "if"),
        new("Ident", "[a-z][a-z0-9]*"),
        new("Number", "[0-9]+(\\.[0-9]+)?"),
        new("Plus", "\\+"),
        new("Whitespace", "[ \t\n]+", Skip: true),
    };

    [Fact]
    public void LongestMatchWins_OverKeywordPrefix()
    {
        var lexer = LexerCompiler.Compile(ExprRules);
        var tokens = lexer.Scan("if iffy");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("KeywordIf", tokens[0].Name);
        Assert.Equal("if", tokens[0].Text);
        Assert.Equal("Ident", tokens[1].Name);
        Assert.Equal("iffy", tokens[1].Text);
    }

    [Fact]
    public void EqualLength_PrefersEarlierRule()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("First", "ab"),
            new("Second", "a(b|c)"),
        });
        var tokens = lexer.Scan("ab");
        Assert.Single(tokens);
        Assert.Equal("First", tokens[0].Name);
    }

    [Fact]
    public void TracksOffsetsLinesAndColumns()
    {
        var lexer = LexerCompiler.Compile(ExprRules);
        var tokens = lexer.Scan("if x1\n  42 + y");
        Assert.Equal(
            new[] { ("KeywordIf", 0, 2, 1, 1), ("Ident", 3, 2, 1, 4), ("Number", 8, 2, 2, 3), ("Plus", 11, 1, 2, 6), ("Ident", 13, 1, 2, 8) },
            tokens.Select(t => (t.Name, t.Offset, t.Length, t.Line, t.Column)).ToArray());
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        var lexer = LexerCompiler.Compile(ExprRules);
        Assert.Empty(lexer.Scan(""));
    }

    [Fact]
    public void UnrecognizedCharacter_ReportsFirstPositionAndStops()
    {
        var lexer = LexerCompiler.Compile(ExprRules);
        var ex = Assert.Throws<LexerScanException>(() => lexer.Scan("12 @ 34"));
        Assert.Equal(3, ex.Offset);
        Assert.Equal(1, ex.Line);
        Assert.Equal(4, ex.Column);
    }

    [Fact]
    public void NonAsciiCharacter_ReportsPosition()
    {
        var lexer = LexerCompiler.Compile(ExprRules);
        var ex = Assert.Throws<LexerScanException>(() => lexer.Scan("ab\u00e9"));
        Assert.Equal(2, ex.Offset);
        Assert.Contains("Non-ASCII", ex.Message);
    }

    [Fact]
    public void FallbackToLastAccept_RemainingCharsRejoin()
    {
        // "ab" is a rule, "abc" is not; scanning "abcab" must yield ab + error-free re-lex.
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("AB", "ab"),
            new("C", "c"),
        });
        var tokens = lexer.Scan("abcab");
        Assert.Equal(new[] { "AB", "C", "AB" }, tokens.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void SkipRulesAdvancePosition()
    {
        var lexer = LexerCompiler.Compile(ExprRules);
        var tokens = lexer.Scan("  \n\t7");
        Assert.Single(tokens);
        Assert.Equal(4, tokens[0].Offset);
        Assert.Equal(2, tokens[0].Line);
        Assert.Equal(2, tokens[0].Column);
    }

    [Fact]
    public void ScanIsStateless_AndRulesSnapshot()
    {
        var rules = new List<LexRule>(ExprRules);
        var lexer = LexerCompiler.Compile(rules);
        var first = lexer.Scan("if 1");
        rules.Clear();
        rules.Add(new("Only", "z"));
        var second = lexer.Scan("if 1");
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("a*", "can match the empty string")]
    [InlineData("(ab)?", "can match the empty string")]
    [InlineData("a|", "Empty branch")]
    [InlineData("()", "Empty branch")]
    [InlineData("a.", "Wildcard '.' is not supported")]
    [InlineData("[^a]", "Negated character classes are not supported")]
    [InlineData("[z-a]", "not ascending")]
    [InlineData("a{2}", "Counted repetition is not supported")]
    [InlineData("(a)\\1", "Backreferences are not supported")]
    [InlineData("^abc", "Anchor")]
    [InlineData("ab", null)] // valid control case handled separately
    public void InvalidPatterns_ThrowWithRuleNameAndOffset(string pattern, string? expected)
    {
        if (expected is null)
        {
            LexerCompiler.Compile(new[] { new LexRule("R", pattern) });
            return;
        }
        var ex = Assert.Throws<LexerCompileException>(() => LexerCompiler.Compile(new[] { new LexRule("R", pattern) }));
        Assert.Equal("R", ex.RuleName);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Escapes_Work()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("NL", "\n"),
            new("Tab", "\t"),
            new("Lit", "a\\+b"),
        });
        var tokens = lexer.Scan("\na+b\t");
        Assert.Equal(new[] { "NL", "Lit", "Tab" }, tokens.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void DuplicateNames_Rejected()
    {
        Assert.Throws<LexerCompileException>(() =>
            LexerCompiler.Compile(new[] { new LexRule("A", "x"), new LexRule("A", "y") }));
    }

    [Fact]
    public void GroupsAlternationAndRepetition()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("Kw", "if|else|while"),
            new("Num", "[0-9]+"),
            new("Ws", " +", Skip: true),
        });
        var tokens = lexer.Scan("if else 123 while");
        Assert.Equal(new[] { "Kw", "Kw", "Num", "Kw" }, tokens.Select(t => t.Name).ToArray());
    }
}
