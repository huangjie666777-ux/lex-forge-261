using LexForge261;
using Xunit;

namespace LexForge261.Tests;

public class MigrationTests
{
    private static readonly LexRule[] Rules =
    {
        new("Ident", "[a-z][a-z0-9]*"),
        new("Number", "[0-9]+"),
        new("Assign", "="),
        new("Semi", ";"),
        new("Comma", ","),
        new("Ws", "[ \t\n\r]+", Skip: true),
    };

    private static GrammarSymbol T(string name) => GrammarSymbol.Terminal(name);
    private static GrammarSymbol N(string name) => GrammarSymbol.Nonterminal(name);

    private static TreePattern NT(string id, params TreePattern[] children) =>
        new TreePattern.Nonterminal(id, children);

    private static Parser BuildParser()
    {
        // S -> Stmt Tail   Stmt -> Ident = E ;   Tail -> Stmt Tail | epsilon
        // E -> Number Args   Args -> , Number Args | epsilon
        var grammar = new GrammarDefinition("S", new Production[]
        {
            new("s-prog", "S", new[] { N("Stmt"), N("Tail") }),
            new("s-assign", "Stmt", new[] { T("Ident"), T("Assign"), N("E"), T("Semi") }),
            new("t-cons", "Tail", new[] { N("Stmt"), N("Tail") }),
            new("t-empty", "Tail", Array.Empty<GrammarSymbol>()),
            new("e-num", "E", new[] { T("Number"), N("Args") }),
            new("a-cons", "Args", new[] { T("Comma"), T("Number"), N("Args") }),
            new("a-empty", "Args", Array.Empty<GrammarSymbol>()),
        });
        return GrammarCompiler.Compile(LexerCompiler.Compile(Rules), grammar);
    }

    private static MigrationRule RenumberRule(string id, string from, string to) =>
        new(id,
            NT("e-num", new TreePattern.Terminal("Number", from), new TreePattern.Capture("rest")),
            new TemplatePart[] { new TemplatePart.Literal(to), new TemplatePart.CaptureReference("rest") });

    [Fact]
    public void RewritesMatchedNodes_PreservingSkippedTextInsideCaptures()
    {
        SourceMigrator migrator = SourceMigrator.Compile(BuildParser(), new[] { RenumberRule("one-to-two", "1", "2") });

        MigrationResult result = migrator.Migrate("x = 1,\t2 ,  3;");

        Assert.True(result.Changed);
        Assert.Equal("x = 2,\t2 ,  3;", result.Source);
        SourceEdit edit = Assert.Single(result.Edits);
        Assert.Equal("one-to-two", edit.RuleId);
        Assert.Equal(4, edit.Offset);
        Assert.Equal("1,\t2 ,  3", "x = 1,\t2 ,  3;".Substring(edit.Offset, edit.Length));
        Assert.Equal("2,\t2 ,  3", edit.Replacement);
    }

    [Fact]
    public void NoMatch_ReturnsSourceUnchanged()
    {
        SourceMigrator migrator = SourceMigrator.Compile(BuildParser(), new[] { RenumberRule("nine-to-ten", "9", "10") });

        MigrationResult result = migrator.Migrate("x = 1;");

        Assert.False(result.Changed);
        Assert.Equal("x = 1;", result.Source);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void EmptySubtreeCapture_DoesNotMatch()
    {
        // "rest" captures Args; with no trailing args the epsilon node binds nothing.
        SourceMigrator migrator = SourceMigrator.Compile(BuildParser(), new[] { RenumberRule("one-to-two", "1", "2") });

        Assert.False(migrator.Migrate("x = 1;").Changed);
        Assert.True(migrator.Migrate("x = 1, 2;").Changed);
    }

    [Fact]
    public void FirstMatchingRuleWins_AndDescendantsAreNotVisited()
    {
        Parser parser = BuildParser();
        var rules = new MigrationRule[]
        {
            // Matches the whole statement and rewrites it wholesale.
            new("stmt",
                NT("s-assign", new TreePattern.Capture("name"), new TreePattern.Terminal("Assign"),
                    new TreePattern.Capture("value"), new TreePattern.Terminal("Semi")),
                new TemplatePart[]
                {
                    new TemplatePart.CaptureReference("name"), new TemplatePart.Literal(" = "),
                    new TemplatePart.CaptureReference("value"), new TemplatePart.Literal(";"),
                }),
            // Would also match the nested E, but the statement rule already won.
            RenumberRule("one-to-two", "1", "2"),
        };
        SourceMigrator migrator = SourceMigrator.Compile(parser, rules);

        MigrationResult result = migrator.Migrate("x = 1;");

        Assert.Equal("x = 1;", result.Source);
        SourceEdit edit = Assert.Single(result.Edits);
        Assert.Equal("stmt", edit.RuleId);
        Assert.Equal(0, edit.Offset);
        Assert.Equal("x = 1;".Length, edit.Length);
    }

    [Fact]
    public void MultipleMatches_ProduceEditsSortedByOriginalOffset()
    {
        SourceMigrator migrator =
            SourceMigrator.Compile(BuildParser(), new[] { RenumberRule("one-to-two", "1", "2") });

        MigrationResult result = migrator.Migrate("x = 1, 3; y = 1, 3;");

        Assert.Equal("x = 2, 3; y = 2, 3;", result.Source);
        Assert.Equal(2, result.Edits.Count);
        Assert.True(result.Edits[0].Offset < result.Edits[1].Offset);
        Assert.All(result.Edits, edit => Assert.Equal("one-to-two", edit.RuleId));
        Assert.Equal("2, 3", result.Edits[0].Replacement);
    }

    [Fact]
    public void InvalidRewrite_ThrowsAndReturnsNothing()
    {
        Parser parser = BuildParser();
        // Replaces the value with an identifier the grammar does not allow.
        var bad = new MigrationRule("bad",
            NT("e-num", new TreePattern.Terminal("Number"), new TreePattern.Capture("rest")),
            new TemplatePart[] { new TemplatePart.Literal("oops"), new TemplatePart.CaptureReference("rest") });
        SourceMigrator migrator = SourceMigrator.Compile(parser, new[] { bad });

        MigrationValidationException ex =
            Assert.Throws<MigrationValidationException>(() => migrator.Migrate("x = 1, 3;"));
        Assert.Equal("bad", ex.RuleId);
        Assert.IsType<SyntaxException>(ex.InnerException);
    }

    [Fact]
    public void Compile_RejectsUnknownProduction()
    {
        var rule = new MigrationRule("r", NT("nope"), Array.Empty<TemplatePart>());
        MigrationCompileException ex =
            Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { rule }));
        Assert.Equal("r", ex.RuleId);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Compile_RejectsChildShapeMismatch()
    {
        // e-num has 2 RHS symbols; only one child pattern supplied.
        var rule = new MigrationRule("r", NT("e-num", new TreePattern.Terminal("Number")),
            Array.Empty<TemplatePart>());
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { rule }));

        // Terminal pattern where the nonterminal Args belongs.
        var wrongKind = new MigrationRule("r",
            NT("e-num", new TreePattern.Terminal("Number"), new TreePattern.Terminal("Args")),
            Array.Empty<TemplatePart>());
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { wrongKind }));

        // Wrong terminal name at a terminal position.
        var wrongName = new MigrationRule("r",
            NT("e-num", new TreePattern.Terminal("Ident"), new TreePattern.Capture("a")),
            new TemplatePart[] { new TemplatePart.CaptureReference("a") });
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { wrongName }));

        // Nonterminal pattern whose production belongs to another nonterminal.
        var wrongLeft = new MigrationRule("r",
            NT("s-assign", new TreePattern.Capture("a"), new TreePattern.Terminal("Assign"),
                NT("a-cons", new TreePattern.Terminal("Comma"), new TreePattern.Terminal("Number"),
                    new TreePattern.Capture("b")),
                new TreePattern.Terminal("Semi")),
            new TemplatePart[] { new TemplatePart.CaptureReference("a") });
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { wrongLeft }));
    }

    [Fact]
    public void Compile_RejectsDuplicateCaptureNames()
    {
        var rule = new MigrationRule("r",
            NT("s-assign", new TreePattern.Capture("x"), new TreePattern.Terminal("Assign"),
                new TreePattern.Capture("x"), new TreePattern.Terminal("Semi")),
            new TemplatePart[] { new TemplatePart.CaptureReference("x") });
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { rule }));
    }

    [Fact]
    public void Compile_RejectsUndefinedTemplateReference()
    {
        var rule = new MigrationRule("r",
            NT("e-num", new TreePattern.Terminal("Number"), new TreePattern.Capture("rest")),
            new TemplatePart[] { new TemplatePart.CaptureReference("missing") });
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), new[] { rule }));
    }

    [Fact]
    public void Compile_RejectsDuplicateRuleIds()
    {
        var rules = new[] { RenumberRule("dup", "1", "2"), RenumberRule("dup", "3", "4") };
        Assert.Throws<MigrationCompileException>(() => SourceMigrator.Compile(BuildParser(), rules));
    }

    [Fact]
    public void Compile_SnapshotsRules_LaterMutationsHaveNoEffect()
    {
        Parser parser = BuildParser();
        var list = new List<MigrationRule> { RenumberRule("one-to-two", "1", "2") };
        SourceMigrator migrator = SourceMigrator.Compile(parser, list);
        list.Clear();
        list.Add(RenumberRule("nine-to-ten", "9", "10"));

        Assert.True(migrator.Migrate("x = 1, 3;").Changed);
        Assert.False(migrator.Migrate("x = 9, 3;").Changed);
    }

    [Fact]
    public void TokenNamedEof_IsNotConfusedWithEndOfInput()
    {
        var rules = new[]
        {
            new LexRule("EOF", "end"),
            new LexRule("Semi", ";"),
            new LexRule("Ws", "[ \t\n\r]+", Skip: true),
        };
        var grammar = new GrammarDefinition("S", new Production[]
        {
            new("s-end", "S", new[] { T("EOF"), T("Semi") }),
        });
        Parser parser = GrammarCompiler.Compile(LexerCompiler.Compile(rules), grammar);

        NonterminalNode root = parser.Parse("end;");
        var leaf = Assert.IsType<TerminalNode>(root.Children[0]);
        Assert.Equal("EOF", leaf.Name);
        Assert.Equal("end", leaf.Token.Text);

        // Missing the 'end' keyword still reports the token, not end-of-input.
        SyntaxException ex = Assert.Throws<SyntaxException>(() => parser.Parse(";"));
        Assert.Equal("Semi", ex.Actual);
        Assert.Contains("EOF", ex.Expected);
    }
}
