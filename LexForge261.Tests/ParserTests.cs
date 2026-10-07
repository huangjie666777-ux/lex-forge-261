using LexForge261;
using Xunit;

namespace LexForge261.Tests;

public class ParserTests
{
    private static readonly LexRule[] Rules =
    {
        new("If", "if"),
        new("Then", "then"),
        new("Else", "else"),
        new("Ident", "[a-z][a-z0-9]*"),
        new("Number", "[0-9]+"),
        new("LParen", "\\("),
        new("RParen", "\\)"),
        new("Assign", "="),
        new("Semi", ";"),
        new("Comma", ","),
        new("Ws", "[ 	\n\r]+", Skip: true),
    };

    private static GrammarSymbol T(string name) => GrammarSymbol.Terminal(name);
    private static GrammarSymbol N(string name) => GrammarSymbol.Nonterminal(name);

    private static Parser BuildWithNullable(Lexer lexer)
    {
        // S -> Ident = E ;    E -> Number Args    Args -> , Number Args | epsilon
        var grammar = new GrammarDefinition("S", new Production[]
        {
            new("s-assign", "S", new[] { T("Ident"), T("Assign"), N("E"), T("Semi") }),
            new("e-num", "E", new[] { T("Number"), N("Args") }),
            new("a-cons", "Args", new[] { T("Comma"), T("Number"), N("Args") }),
            new("a-empty", "Args", Array.Empty<GrammarSymbol>()),
        });
        return GrammarCompiler.Compile(lexer, grammar);
    }

    [Fact]
    public void ParsesLegalScript_WithNullableBranch()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        NonterminalNode root = parser.Parse("x = 1, 2;");

        Assert.Equal("S", root.Name);
        Assert.Equal("s-assign", root.ProductionId);
        Assert.Equal(4, root.Children.Count);

        var e = Assert.IsType<NonterminalNode>(root.Children[2]);
        Assert.Equal("E", e.Name);
        Assert.Equal("e-num", e.ProductionId);

        var args = Assert.IsType<NonterminalNode>(e.Children[1]);
        Assert.Equal("Args", args.Name);
        Assert.Equal("a-cons", args.ProductionId);
        var tail = Assert.IsType<NonterminalNode>(args.Children[2]);
        Assert.Equal("a-empty", tail.ProductionId);
        Assert.Empty(tail.Children);

        var leaf = Assert.IsType<TerminalNode>(root.Children[0]);
        Assert.Equal("x", leaf.Token.Text);
    }

    [Fact]
    public void EpsilonBranch_TerminalLeafPreservesLexToken()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        NonterminalNode root = parser.Parse("a = 7;");
        var e = (NonterminalNode)root.Children[2];
        var args = (NonterminalNode)e.Children[1];
        Assert.Equal("a-empty", args.ProductionId);
        Assert.Empty(args.Children);

        var semi = Assert.IsType<TerminalNode>(root.Children[3]);
        Assert.Equal("Semi", semi.Token.Name);
        Assert.Equal(5, semi.Token.Offset);
    }

    [Fact]
    public void ConsumesCompleteInput_LegalPrefixFails()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        // "x = 1;" is a valid prefix but "2" remains.
        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse("x = 1; 2"));
        Assert.Equal("Number", ex.Actual);
        Assert.Equal(new[] { "EOF" }, ex.Expected);
    }

    [Fact]
    public void SyntaxError_ReportsActualExpectedAndPosition_AndStopsAtFirst()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse("x = if;"));
        Assert.Equal("If", ex.Actual);
        Assert.Equal("if", ex.ActualText);
        Assert.Equal(new[] { "Number" }, ex.Expected);
        Assert.Equal(4, ex.Offset);
        Assert.Equal(1, ex.Line);
        Assert.Equal(5, ex.Column);
    }

    [Fact]
    public void MissingInput_EofPositionIncludesTrailingSkippedText()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse("x = 1,  \n  "));
        Assert.Equal("EOF", ex.Actual);
        Assert.Equal(new[] { "Number" }, ex.Expected);
        Assert.Equal(11, ex.Offset);
        Assert.Equal(2, ex.Line);
        Assert.Equal(3, ex.Column);
    }

    [Fact]
    public void OnlyLfAdvancesLine()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse("\nx\r = if"));
        Assert.Equal(2, ex.Line);
        Assert.Equal(6, ex.Column);
    }

    [Fact]
    public void LexicalError_IsPreservedUnchanged()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var parser = BuildWithNullable(lexer);
        Assert.Throws<LexerScanException>(() => parser.Parse("x = 1 @"));
    }

    [Fact]
    public void RejectsDuplicateProductionIds()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var grammar = new GrammarDefinition("S", new Production[]
        {
            new("p", "S", new[] { T("Ident") }),
            new("p", "S", new[] { T("Number") }),
        });
        GrammarCompileException ex =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, grammar));
        Assert.Contains("Duplicate production identifier", ex.Message);
    }

    [Fact]
    public void RejectsUndefinedReferences_InvalidStart_AndSkipRuleTerminals()
    {
        var lexer = LexerCompiler.Compile(Rules);

        var badStart = new GrammarDefinition("Missing", new Production[]
        {
            new("p", "S", new[] { T("Ident") }),
        });
        GrammarCompileException ex1 =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, badStart));
        Assert.Equal("Missing", ex1.Nonterminal);

        var undefinedNt = new GrammarDefinition("S", new Production[]
        {
            new("p", "S", new[] { N("Ghost") }),
        });
        Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, undefinedNt));

        var undefinedT = new GrammarDefinition("S", new Production[]
        {
            new("p", "S", new[] { T("Ghost") }),
        });
        Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, undefinedT));

        var skipRef = new GrammarDefinition("S", new Production[]
        {
            new("p", "S", new[] { T("Ws") }),
        });
        GrammarCompileException ex2 =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, skipRef));
        Assert.Contains("skipped lexer rule", ex2.Message);
    }

    [Fact]
    public void RejectsDirectAndIndirectLeftRecursion()
    {
        var lexer = LexerCompiler.Compile(Rules);

        var direct = new GrammarDefinition("A", new Production[]
        {
            new("a-rec", "A", new[] { N("A"), T("Ident") }),
            new("a-base", "A", new[] { T("Ident") }),
        });
        GrammarCompileException ex1 =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, direct));
        Assert.Equal("A", ex1.Nonterminal);

        var indirect = new GrammarDefinition("B", new Production[]
        {
            new("b", "B", new[] { N("C"), T("Ident") }),
            new("c-rec", "C", new[] { N("B"), T("Number") }),
            new("c-base", "C", new[] { T("Ident") }),
        });
        GrammarCompileException ex2 =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, indirect));
        Assert.Contains("left-recursive", ex2.Message);
    }

    [Fact]
    public void NullablePrefixRecursionIsStillRejected()
    {
        var lexer = LexerCompiler.Compile(Rules);
        // A -> B A ; B -> epsilon | Ident  => A derives A via a nullable prefix.
        var grammar = new GrammarDefinition("A", new Production[]
        {
            new("a", "A", new[] { N("B"), N("A") }),
            new("a-base", "A", new[] { T("Ident") }),
            new("b-empty", "B", Array.Empty<GrammarSymbol>()),
            new("b-ident", "B", new[] { T("Ident") }),
        });
        GrammarCompileException ex =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, grammar));
        Assert.Equal("A", ex.Nonterminal);
    }

    [Fact]
    public void GrammarConflict_ReportsNonterminalLookaheadAndProductions()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var grammar = new GrammarDefinition("S", new Production[]
        {
            new("p-one", "S", new[] { T("Ident"), T("Semi") }),
            new("p-two", "S", new[] { T("Ident"), T("Comma") }),
        });
        GrammarCompileException ex =
            Assert.Throws<GrammarCompileException>(() => GrammarCompiler.Compile(lexer, grammar));
        Assert.Equal("S", ex.Nonterminal);
        Assert.Equal("Ident", ex.Lookahead);
        Assert.Equal(new[] { "p-one", "p-two" }, ex.ProductionIds);
    }

    [Fact]
    public void RepeatedParses_DoNotShareState_AndGrammarIsSnapshotted()
    {
        var lexer = LexerCompiler.Compile(Rules);
        var productions = new List<Production>
        {
            new("s-assign", "S", new[] { T("Ident"), T("Assign"), N("E"), T("Semi") }),
            new("e-num", "E", new[] { T("Number") }),
        };
        var grammar = new GrammarDefinition("S", productions);
        Parser parser = GrammarCompiler.Compile(lexer, grammar);

        productions.Add(new("extra", "E", new[] { T("Ident") }));

        NonterminalNode r1 = parser.Parse("a = 1;");
        NonterminalNode r2 = parser.Parse("b = 2;");
        Assert.Equal("a", ((TerminalNode)r1.Children[0]).Token.Text);
        Assert.Equal("b", ((TerminalNode)r2.Children[0]).Token.Text);
        Assert.Equal(4, r1.Children.Count);
    }

    [Fact]
    public void ExpectedSet_IsStablySortedWithEofLast()
    {
        var lexer = LexerCompiler.Compile(Rules);
        // Two start alternatives with distinct FIRST sets; a Number matches
        // neither and surfaces the stably sorted union of the prediction set.
        var grammar = new GrammarDefinition("Stmt", new Production[]
        {
            new("st-if", "Stmt", new[] { T("If"), T("Ident") }),
            new("st-simple", "Stmt", new[] { T("Ident"), T("Semi") }),
        });
        Parser parser = GrammarCompiler.Compile(lexer, grammar);
        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse("123"));
        Assert.Equal(new[] { "Ident", "If" }, ex.Expected);
        Assert.Equal("Number", ex.Actual);
    }

    [Fact]
    public void TokenNamedEof_DoesNotCollideWithEndOfInput()
    {
        // A lexer rule literally named "EOF" is an ordinary terminal; it must
        // not be confused with the end-of-input marker.
        var lexer = LexerCompiler.Compile(new LexRule[]
        {
            new("EOF", "end"),
            new("Semi", ";"),
            new("Ws", "[ \t\r\n]+", Skip: true),
        });
        // S -> A ;    A -> EOF | epsilon
        // The nullable alternative must not conflict with the EOF terminal.
        var grammar = new GrammarDefinition("S", new Production[]
        {
            new("s", "S", new[] { N("A"), T("Semi") }),
            new("a-end", "A", new[] { T("EOF") }),
            new("a-empty", "A", Array.Empty<GrammarSymbol>()),
        });
        Parser parser = GrammarCompiler.Compile(lexer, grammar);

        NonterminalNode withEnd = parser.Parse("end;");
        var a1 = (NonterminalNode)withEnd.Children[0];
        Assert.Equal("a-end", a1.ProductionId);
        Assert.Equal("EOF", ((TerminalNode)a1.Children[0]).Name);

        NonterminalNode withoutEnd = parser.Parse(";");
        Assert.Equal("a-empty", ((NonterminalNode)withoutEnd.Children[0]).ProductionId);

        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse("end end;"));
        Assert.Equal("EOF", ex.Actual);
        Assert.Equal("end", ex.ActualText);
    }
}
