using LexForge261;
using Xunit;

namespace LexForge261.Tests;

public class MigrationTests
{
    private static readonly LexRule[] Rules =
    {
        new("KwLet", "let"),
        new("Ident", "[a-zA-Z_][a-zA-Z0-9_]*"),
        new("Number", "[0-9]+"),
        new("Assign", "="),
        new("Comma", ","),
        new("Semi", ";"),
        new("Ws", "[ \t\r\n]+", Skip: true),
    };

    private static GrammarSymbol T(string name) => GrammarSymbol.Terminal(name);
    private static GrammarSymbol N(string name) => GrammarSymbol.Nonterminal(name);

    // Program -> Stmt      Stmt -> let Ident = Expr ;
    // Expr -> Number Args  Args -> , Number Args | epsilon
    private static Parser BuildParser()
    {
        Lexer lexer = LexerCompiler.Compile(Rules);
        var grammar = new GrammarDefinition("Program", new Production[]
        {
            new("program", "Program", new[] { N("Stmt") }),
            new("stmt", "Stmt", new[] { T("KwLet"), T("Ident"), T("Assign"), N("Expr"), T("Semi") }),
            new("expr", "Expr", new[] { T("Number"), N("Args") }),
            new("args-cons", "Args", new[] { T("Comma"), T("Number"), N("Args") }),
            new("args-empty", "Args", Array.Empty<GrammarSymbol>()),
        });
        return GrammarCompiler.Compile(lexer, grammar);
    }

    private static NodePattern.Production StmtPattern(params NodePattern[] children) =>
        new("stmt", children);

    private static MigrationRule AppendZeroRule() =>
        new("append-zero",
            new NodePattern.Production("args-cons", new NodePattern[]
            {
                new NodePattern.Token("Comma"),
                new NodePattern.Capture("n"),
                new NodePattern.Production("args-empty", Array.Empty<NodePattern>()),
            }),
            new TemplatePart[]
            {
                new TemplatePart.Literal(", "),
                new TemplatePart.Capture("n"),
                new TemplatePart.Literal(", 0"),
            });

    [Fact]
    public void RewritesMatch_PreservingSurroundingAndInteriorText()
    {
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), new[] { AppendZeroRule() });
        const string source = "let answer = 42,   7;";

        MigrationResult result = migrator.Migrate(source);

        Assert.True(result.Changed);
        Assert.Equal("let answer = 42, 7, 0;", result.Source);
        MigrationEdit edit = Assert.Single(result.Edits);
        Assert.Equal("append-zero", edit.RuleId);
        // Range spans the matched Args node: from ',' to the end of '7'.
        Assert.Equal(source.IndexOf(','), edit.Offset);
        Assert.Equal(",   7", source.Substring(edit.Offset, edit.Length));
        Assert.Equal(", 7, 0", edit.Replacement); // template literals, capture = token text
    }

    [Fact]
    public void NoMatch_ReturnsSourceUnchanged()
    {
        var rule = new MigrationRule("never",
            StmtPattern(
                new NodePattern.Token("KwLet", "var"), // text constraint never holds
                new NodePattern.Capture("id"),
                new NodePattern.Token("Assign"),
                new NodePattern.Capture("expr"),
                new NodePattern.Token("Semi")),
            new TemplatePart[] { new TemplatePart.Capture("id") });
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), new[] { rule });

        MigrationResult result = migrator.Migrate("let x = 1;");

        Assert.False(result.Changed);
        Assert.Equal("let x = 1;", result.Source);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public void FirstMatchingRuleWins_AndMatchedSubtreeIsNotTraversed()
    {
        // Rule 1 matches the whole statement; rule 2 would match the inner Args.
        var outer = new MigrationRule("outer",
            StmtPattern(
                new NodePattern.Token("KwLet"),
                new NodePattern.Capture("id"),
                new NodePattern.Token("Assign"),
                new NodePattern.Capture("expr"),
                new NodePattern.Token("Semi")),
            new TemplatePart[]
            {
                new TemplatePart.Literal("let "),
                new TemplatePart.Capture("id"),
                new TemplatePart.Literal(" = "),
                new TemplatePart.Capture("expr"),
                new TemplatePart.Literal(";"),
            });
        SourceMigrator migrator = MigrationCompiler.Compile(
            BuildParser(), new MigrationRule[] { outer, AppendZeroRule() });

        MigrationResult result = migrator.Migrate("let   x = 1,   2;");

        MigrationEdit edit = Assert.Single(result.Edits);
        Assert.Equal("outer", edit.RuleId);
        // Template literals normalize outer spacing; the capture keeps its own
        // interior skipped text verbatim.
        Assert.Equal("let x = 1,   2;", result.Source);
    }

    [Fact]
    public void CaptureOfEmptySubtree_IsNotAMatch()
    {
        // The tail Args of "let x = 1;" is epsilon: capturing it must fail.
        var rule = new MigrationRule("cap-empty",
            new NodePattern.Production("expr", new NodePattern[]
            {
                new NodePattern.Token("Number"),
                new NodePattern.Capture("args"),
            }),
            new TemplatePart[] { new TemplatePart.Capture("args") });
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), new[] { rule });

        MigrationResult result = migrator.Migrate("let x = 1;");

        Assert.False(result.Changed);
        Assert.Equal("let x = 1;", result.Source);
    }

    [Fact]
    public void MultipleEdits_AreSortedByOriginalOffset()
    {
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), new[] { AppendZeroRule() });

        MigrationResult result = migrator.Migrate("let x = 1, 2, 3;");

        Assert.Equal("let x = 1, 2, 3, 0;", result.Source);
        MigrationEdit edit = Assert.Single(result.Edits); // only the tail Args matches args-empty
        Assert.Equal("append-zero", edit.RuleId);
    }

    [Fact]
    public void InvalidRewrite_ThrowsAndReturnsNothing()
    {
        var bad = new MigrationRule("bad",
            StmtPattern(
                new NodePattern.Token("KwLet"),
                new NodePattern.Capture("id"),
                new NodePattern.Token("Assign"),
                new NodePattern.Capture("expr"),
                new NodePattern.Token("Semi")),
            new TemplatePart[]
            {
                new TemplatePart.Literal("let = ;"), // not parseable
            });
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), new[] { bad });

        MigrationValidationException ex = Assert.Throws<MigrationValidationException>(
            () => migrator.Migrate("let x = 1;"));
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void InputSyntaxError_SurfacesUnchanged()
    {
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), new[] { AppendZeroRule() });
        Assert.Throws<SyntaxException>(() => migrator.Migrate("let x = ;"));
    }

    [Fact]
    public void Compile_RejectsUnknownProduction()
    {
        var rule = new MigrationRule("r",
            new NodePattern.Production("nope", Array.Empty<NodePattern>()),
            Array.Empty<TemplatePart>());
        MigrationCompileException ex = Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { rule }));
        Assert.Equal("r", ex.RuleId);
    }

    [Fact]
    public void Compile_RejectsChildShapeMismatch()
    {
        var wrongCount = new MigrationRule("r",
            StmtPattern(new NodePattern.Token("KwLet")),
            Array.Empty<TemplatePart>());
        Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { wrongCount }));

        var wrongKind = new MigrationRule("r",
            StmtPattern(
                new NodePattern.Capture("c"),
                new NodePattern.Capture("id"),
                new NodePattern.Token("Assign"),
                new NodePattern.Capture("expr"),
                new NodePattern.Token("Comma")), // grammar expects Semi here
            Array.Empty<TemplatePart>());
        Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { wrongKind }));

        var tokenOnNonterminal = new MigrationRule("r",
            StmtPattern(
                new NodePattern.Token("KwLet"),
                new NodePattern.Token("Ident"),
                new NodePattern.Token("Assign"),
                new NodePattern.Token("Number"), // Expr is a nonterminal
                new NodePattern.Token("Semi")),
            Array.Empty<TemplatePart>());
        Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { tokenOnNonterminal }));
    }

    [Fact]
    public void Compile_RejectsDuplicateCaptureNames()
    {
        var rule = new MigrationRule("r",
            StmtPattern(
                new NodePattern.Token("KwLet"),
                new NodePattern.Capture("x"),
                new NodePattern.Token("Assign"),
                new NodePattern.Capture("x"),
                new NodePattern.Token("Semi")),
            Array.Empty<TemplatePart>());
        Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { rule }));
    }

    [Fact]
    public void Compile_RejectsUndefinedTemplateReference()
    {
        var rule = new MigrationRule("r",
            StmtPattern(
                new NodePattern.Token("KwLet"),
                new NodePattern.Capture("id"),
                new NodePattern.Token("Assign"),
                new NodePattern.Capture("expr"),
                new NodePattern.Token("Semi")),
            new TemplatePart[] { new TemplatePart.Capture("missing") });
        Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { rule }));
    }

    [Fact]
    public void Compile_RejectsDuplicateRuleIds()
    {
        MigrationRule rule = AppendZeroRule();
        Assert.Throws<MigrationCompileException>(
            () => MigrationCompiler.Compile(BuildParser(), new[] { rule, rule }));
    }

    [Fact]
    public void Compile_SnapshotsRules_LaterMutationHasNoEffect()
    {
        var rules = new List<MigrationRule> { AppendZeroRule() };
        SourceMigrator migrator = MigrationCompiler.Compile(BuildParser(), rules);
        rules.Clear();
        rules.Add(new MigrationRule("other",
            new NodePattern.Production("nope", Array.Empty<NodePattern>()),
            Array.Empty<TemplatePart>()));

        MigrationResult result = migrator.Migrate("let x = 1, 2;");
        Assert.Equal("let x = 1, 2, 0;", result.Source);
        Assert.Equal(new[] { "append-zero" }, migrator.RuleIds);
    }
}
