using LexForge261;
using Xunit;

namespace LexForge261.Tests;

public class RuleAuditTests
{
    [Fact]
    public void NullableBodyRepetition_IsAccepted()
    {
        // The overall language does not contain the empty string; only the
        // top-level rule may be nullable.
        Lexer lexer = LexerCompiler.Compile(new[] { new LexRule("R", "(a?)*b") });
        Assert.Equal("R", lexer.Scan("b")[0].Name);
        Assert.Equal("R", lexer.Scan("aaab")[0].Name);
    }

    [Theory]
    [InlineData("(a?)*")]
    [InlineData("(a*)+")]
    [InlineData("(a|b)?")]
    public void WholePatternNullable_StillRejected(string pattern)
    {
        LexerCompileException ex = Assert.Throws<LexerCompileException>(() =>
            LexerCompiler.Compile(new[] { new LexRule("R", pattern) }));
        Assert.Contains("can match the empty string", ex.Message);
    }

    [Fact]
    public void Overlaps_ShortestLexMinWitness_StableOrder()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("A", "ab|ac"),
            new("B", "ac|ad"),
            new("C", "ax"),
        });

        RuleAuditReport report = lexer.Audit();

        RuleOverlap only = Assert.Single(report.Overlaps);
        Assert.Equal("A", only.FirstRule);
        Assert.Equal("B", only.SecondRule);
        Assert.Equal("ac", only.Witness);

        RuleAuditReport again = lexer.Audit();
        Assert.Equal(report.Overlaps.Select(o => (o.FirstRule, o.SecondRule, o.Witness)).ToArray(),
            again.Overlaps.Select(o => (o.FirstRule, o.SecondRule, o.Witness)).ToArray());
    }

    [Fact]
    public void PrefixSharing_IsNotIntersection()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("A", "ab"),
            new("B", "abc"),
        });
        Assert.Empty(lexer.Audit().Overlaps);
    }

    [Fact]
    public void CanWin_ReturnsShortestLexMinWitness()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("If", "if"),
            new("Ident", "[a-z]+"),
        });

        RuleAuditEntry ifEntry = lexer.Audit().Entries[0];
        Assert.True(ifEntry.CanWin);
        Assert.Equal("if", ifEntry.WinningWitness);

        RuleAuditEntry ident = lexer.Audit().Entries[1];
        Assert.True(ident.CanWin);
        Assert.Equal("a", ident.WinningWitness); // shortest identifier not stolen by 'if'

        // The witness agrees with the actual scanner on the full input.
        Assert.Equal("Ident", lexer.Scan("a")[0].Name);
        Assert.Equal("If", lexer.Scan("if")[0].Name);
    }

    [Fact]
    public void UnionShadowing_IsDetectedAcrossMultiplePredecessors()
    {
        // No single predecessor contains C's whole language, but their union
        // covers both alternatives. This must be reported as full shadowing.
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("P", "ab"),
            new("Q", "cd"),
            new("C", "ab|cd"),
        });

        RuleAuditEntry cEntry = lexer.Audit().Entries[2];
        Assert.False(cEntry.CanWin);
        Assert.Equal("ab", cEntry.ShortestAccepted);
        Assert.Equal("P", cEntry.WinningRule);
    }

    [Fact]
    public void WinnerReflectsLongestMatch_NotJustAcceptance()
    {
        // C shares the exact word "ab" with earlier B, but C also accepts "abc"
        // where no earlier rule reaches as far; hence C can still win.
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("A", "a"),
            new("B", "ab"),
            new("C", "ab|abc"),
        });

        RuleAuditEntry cEntry = lexer.Audit().Entries[2];
        Assert.True(cEntry.CanWin);
        Assert.Equal("abc", cEntry.WinningWitness);
    }

    [Fact]
    public void SkipRulesCompete_AndAuditDoesNotPolluteScans()
    {
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("Ws", "[ ]+", Skip: true),
            new("Spaces", "[ ]"),
            new("Word", "x"),
        });

        RuleAuditReport report = lexer.Audit();
        Assert.False(report.Entries[1].CanWin);
        Assert.Equal(" ", report.Entries[1].ShortestAccepted);
        Assert.Equal("Ws", report.Entries[1].WinningRule);
        Assert.True(report.Entries[2].CanWin);
        Assert.Equal("x", report.Entries[2].WinningWitness);

        // Auditing must not change scanning behavior.
        Assert.Empty(lexer.Scan("  "));
        Assert.Equal("Word", lexer.Scan(" x").Single().Name);
    }
}
