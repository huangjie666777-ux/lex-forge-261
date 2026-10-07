using LexForge261;

var rules = new List<LexRule>
{
    new("Keyword", "if|else|while|return"),
    new("Ident", "[a-zA-Z_][a-zA-Z0-9_]*"),
    new("Number", "[0-9]+(\\.[0-9]+)?"),
    new("Op", "\\+|\\-|\\*|/|==|="),
    new("LParen", "\\("),
    new("RParen", "\\)"),
    new("Semicolon", ";"),
    new("Whitespace", "[ \t\r\n]+", Skip: true),
};

Lexer lexer = LexerCompiler.Compile(rules);

const string source = "if (count == 42) return total + 3.5;\nelse whilex = 0;";
Console.WriteLine($"Source: {source}");
Console.WriteLine();
foreach (LexToken t in lexer.Scan(source))
    Console.WriteLine($"{t.Name,-10} '{t.Text}'  offset={t.Offset} len={t.Length} line={t.Line} col={t.Column}");

Console.WriteLine();
try
{
    lexer.Scan("ok := 1");
}
catch (LexerScanException ex)
{
    Console.WriteLine($"Scan error: {ex.Message}");
}

// ---------------------------------------------------------------------------
// Rule audit: intersections, union shadowing, and winning witnesses
// ---------------------------------------------------------------------------
var auditRules = new List<LexRule>
{
    new("If", "if"),                 // overlaps Ident on "if", but wins ties
    new("Ident", "[a-z]+"),          // can still win on other words
    new("X1", "x1"),
    new("Y2", "y2"),
    new("Both", "x1|y2"),            // covered only by the UNION of X1 and Y2
    new("Num", "[0-9]+"),
    new("Ws", "[ ]+", Skip: true),
};

Lexer audited = LexerCompiler.Compile(auditRules);
RuleAuditReport report = audited.Audit();

Console.WriteLine();
Console.WriteLine("Rule audit");
Console.WriteLine("----------");
Console.WriteLine("Overlapping rule pairs (shared complete words):");
foreach (RuleOverlap overlap in report.Overlaps)
    Console.WriteLine($"  {overlap.FirstRule} <-> {overlap.SecondRule}  e.g. \"{overlap.Witness}\"");

Console.WriteLine();
Console.WriteLine("Per-rule reachability:");
foreach (RuleAuditEntry entry in report.Entries)
{
    if (entry.CanWin)
        Console.WriteLine($"  [{entry.Index}] {entry.RuleName,-6} can win   witness \"{entry.WinningWitness}\"");
    else
        Console.WriteLine($"  [{entry.Index}] {entry.RuleName,-6} SHADOWED  shortest \"{entry.ShortestAccepted}\" won by {entry.WinningRule}");
}

// ---------------------------------------------------------------------------
// LL(1) parser: nullable branches, full-input parse tree, syntax errors,
// and grammar conflicts. Grammar is supplied as objects (no grammar strings).
// ---------------------------------------------------------------------------
var parserRules = new List<LexRule>
{
    new("KwLet", "let"),
    new("Ident", "[a-zA-Z_][a-zA-Z0-9_]*"),
    new("Number", "[0-9]+"),
    new("Assign", "="),
    new("Comma", ","),
    new("Semi", ";"),
    new("Ws", "[ \t\r\n]+", Skip: true),
};
Lexer grammarLexer = LexerCompiler.Compile(parserRules);

// Program -> Stmt      Stmt -> let Ident = Expr ;
// Expr -> Number Args  Args -> , Number Args | epsilon
var grammar = new GrammarDefinition("Program", new Production[]
{
    new("program", "Program", new[] { GrammarSymbol.Nonterminal("Stmt") }),
    new("stmt", "Stmt",
        new[] { GrammarSymbol.Terminal("KwLet"), GrammarSymbol.Terminal("Ident"),
                GrammarSymbol.Terminal("Assign"), GrammarSymbol.Nonterminal("Expr"),
                GrammarSymbol.Terminal("Semi") }),
    new("expr", "Expr", new[] { GrammarSymbol.Terminal("Number"), GrammarSymbol.Nonterminal("Args") }),
    new("args-cons", "Args",
        new[] { GrammarSymbol.Terminal("Comma"), GrammarSymbol.Terminal("Number"),
                GrammarSymbol.Nonterminal("Args") }),
    new("args-empty", "Args", Array.Empty<GrammarSymbol>()),
});
Parser parser = GrammarCompiler.Compile(grammarLexer, grammar);

Console.WriteLine();
Console.WriteLine("LL(1) parse");
Console.WriteLine("-----------");

static void PrintTree(ParseNode node, string indent)
{
    if (node is TerminalNode leaf)
        Console.WriteLine($"{indent}{leaf.Name} '{leaf.Token.Text}' @{leaf.Token.Offset}");
    else
    {
        var nt = (NonterminalNode)node;
        Console.WriteLine($"{indent}{nt.Name} -> {nt.ProductionId}");
        foreach (ParseNode child in nt.Children)
            PrintTree(child, indent + "  ");
    }
}

// 1. Legal script exercising the nullable epsilon branch.
const string script = "let answer = 42, 7;";
Console.WriteLine($"Script: {script}");
PrintTree(parser.Parse(script), "  ");

// 2. First syntax error stops parsing; expected terminals are sorted, EOF last.
foreach (string bad in new[] { "let answer = boom;", "let answer = 42" })
{
    try
    {
        parser.Parse(bad);
    }
    catch (SyntaxException ex)
    {
        Console.WriteLine(
            $"Syntax error in '{bad}': got {ex.Actual}, expected [{string.Join(", ", ex.Expected)}] " +
            $"at offset {ex.Offset} (line {ex.Line}, col {ex.Column})");
    }
}

// 3. A legal prefix followed by extra input is NOT a success.
try
{
    parser.Parse("let answer = 42; let extra = 1;");
}
catch (SyntaxException ex)
{
    Console.WriteLine($"Trailing input rejected: got {ex.Actual}, expected [{string.Join(", ", ex.Expected)}]");
}

// 4. LL(1) table conflict: two alternatives predict the same lookahead.
var ambiguous = new GrammarDefinition("S", new Production[]
{
    new("alt-a", "S", new[] { GrammarSymbol.Terminal("Ident"), GrammarSymbol.Terminal("Semi") }),
    new("alt-b", "S", new[] { GrammarSymbol.Terminal("Ident"), GrammarSymbol.Terminal("Comma") }),
});
try
{
    GrammarCompiler.Compile(grammarLexer, ambiguous);
}
catch (GrammarCompileException ex)
{
    Console.WriteLine(
        $"Grammar rejected: {ex.Nonterminal} on {ex.Lookahead} conflicts " +
        $"[{string.Join(", ", ex.ProductionIds)}]");
}

// ---------------------------------------------------------------------------
// Structured source migration: ordered tree-pattern rules with capture-based
// replacement templates, applied over the original parse tree and validated
// by re-parsing with the same parser.
// ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Source migration");
Console.WriteLine("----------------");

static TreePattern Nt(string id, params TreePattern[] children) =>
    new TreePattern.Nonterminal(id, children);

// expr -> Number Args: rewrite the leading literal, keep the rest verbatim.
var migrationRules = new MigrationRule[]
{
    new("zero-leading-number",
        Nt("expr", new TreePattern.Terminal("Number"), new TreePattern.Capture("rest")),
        new TemplatePart[] { new TemplatePart.Literal("0"), new TemplatePart.CaptureReference("rest") }),
};
SourceMigrator migrator = SourceMigrator.Compile(parser, migrationRules);

const string oldScript = "let answer = 42,\t7;";
MigrationResult migrated = migrator.Migrate(oldScript);
Console.WriteLine($"Before: {oldScript}");
Console.WriteLine($"After:  {migrated.Source}");
foreach (SourceEdit edit in migrated.Edits)
    Console.WriteLine($"  rule {edit.RuleId}: [{edit.Offset}..{edit.Offset + edit.Length}) -> \"{edit.Replacement}\"");

// A rule whose replacement breaks the grammar is rejected: nothing is returned.
var brokenRules = new MigrationRule[]
{
    new("drop-leading-number",
        Nt("expr", new TreePattern.Terminal("Number"), new TreePattern.Capture("rest")),
        new TemplatePart[] { new TemplatePart.CaptureReference("rest") }),
};
SourceMigrator broken = SourceMigrator.Compile(parser, brokenRules);
try
{
    broken.Migrate(oldScript);
}
catch (MigrationValidationException ex)
{
    Console.WriteLine($"Invalid rewrite rejected: {ex.Message}");
}
