# LexForge261

A lexer-rule compilation library for .NET 8 / C# 12. Rules are compiled once into a shared DFA and the resulting lexer can scan any number of texts repeatedly. No `System.Text.RegularExpressions` and no external generator: patterns are parsed by a hand-written recursive-descent parser, compiled to a Thompson NFA, then determinized via subset construction with epsilon closures.

## Public API

```csharp
var lexer = LexerCompiler.Compile(new LexRule[]
{
    new("Keyword", "if|else|while"),
    new("Ident",   "[a-zA-Z_][a-zA-Z0-9_]*"),
    new("Number",  "[0-9]+(\\.[0-9]+)?"),
    new("Ws",      "[ \\t\\n]+", Skip: true),
});
IReadOnlyList<LexToken> tokens = lexer.Scan("if x1\n  42");
```

- `LexRule(Name, Pattern, Skip)` - rules are matched in list order (priority).
- `LexToken(Name, Text, Offset, Length, Line, Column)` - offset/length are 0-based, line/column are 1-based; only LF advances the line.
- `Lexer.Scan(text)` - stateless; safe to reuse across texts and threads. Empty text returns an empty result.
- `LexerCompileException` - carries `RuleName` and `PatternOffset` for syntax errors.
- `LexerScanException` - carries `Offset`, `Line`, `Column` of the first unrecognized position; scanning stops (no character skipping).

## Rule audit

`Lexer.Audit()` reuses the compiled DFA to report how the rules compete. It is a pure computation: call it any number of times, interleave it with scans, and nothing is mutated.

```csharp
RuleAuditReport report = lexer.Audit();

foreach (RuleOverlap overlap in report.Overlaps)
    Console.WriteLine($"{overlap.FirstRule} <-> {overlap.SecondRule}: \"{overlap.Witness}\"");

foreach (RuleAuditEntry entry in report.Entries)
    // entry.CanWin ? entry.WinningWitness : entry.ShortestAccepted / entry.WinningRule
    ;
```

- `Overlaps` - every unordered pair of rules whose languages share a complete non-empty word, each pair reported once, in declaration order (`(0,1)`, `(0,2)`, ..., `(1,2)`, ...). `Witness` is a word both rules accept in full; sharing only a prefix is not an intersection. The witness is the shortest such word, and the ASCII-lexicographically smallest among equal lengths.
- `Entries[i].CanWin` - whether rule `i` can ever win from the scan start, considering the union of all earlier rules (including skipped rules). When true, `WinningWitness` is the shortest input (ASCII-lex-min tie break) for which this rule is the actual longest-match/order winner.
- When `CanWin` is false the rule is **fully shadowed**: `ShortestAccepted` is its shortest accepted word and `WinningRule` names the earlier rule that actually wins on that word. Shadowing is decided against the union of all predecessors - e.g. `x1`, `y2` together fully shadow `x1|y2` even though neither predecessor alone covers it.
- All answers come from exact automaton reachability: a breadth-first search over the compiled DFA with edges taken in ASCII order (the first path reaching a state is its shortest, lex-smallest word). No `Regex`, random sampling, or bounded word-length enumeration is involved.

## LL(1) parsing

The compiled lexer is paired with an explicitly modeled grammar (no grammar strings, no generator). `GrammarCompiler.Compile(lexer, grammar)` validates the grammar, computes nullable/FIRST/FOLLOW fixed points, rejects left recursion, and builds a predictive table including `EOF`; the returned `Parser` parses the **complete** text into an ordered parse tree using the existing lexer.

```csharp
GrammarSymbol T(string name) => GrammarSymbol.Terminal(name);
GrammarSymbol N(string name) => GrammarSymbol.Nonterminal(name);

// Program -> Stmt                       Stmt -> let Ident = Expr ;
// Expr    -> Number Args                Args -> , Number Args | epsilon
var grammar = new GrammarDefinition("Program", new Production[]
{
    new("program", "Program", new[] { N("Stmt") }),
    new("stmt", "Stmt", new[] { T("KwLet"), T("Ident"), T("Assign"), N("Expr"), T("Semi") }),
    new("expr", "Expr", new[] { T("Number"), N("Args") }),
    new("args-cons", "Args", new[] { T("Comma"), T("Number"), N("Args") }),
    new("args-empty", "Args", Array.Empty<GrammarSymbol>()), // empty RHS = epsilon
});
Parser parser = GrammarCompiler.Compile(lexer, grammar);
NonterminalNode tree = parser.Parse("let answer = 42, 7;");
```

- `GrammarDefinition(Start, Productions)` and `Production(Id, Left, Right)` are snapshotted at compile time; mutating them afterwards never affects the compiled parser. Production identifiers must be unique.
- `GrammarSymbol.Terminal(name)` / `.Nonterminal(name)` distinguish symbol kinds explicitly. Terminals name **non-skip** lexer rules; referencing a skip rule or an undefined name fails compilation, as does an undefined symbol or an invalid start symbol.
- Nullable and FIRST are fixed points; nullable prefixes are propagated fully, so direct **and** indirect left recursion (including through epsilon-deriving prefixes) are rejected before table construction.
- FOLLOW is a fixed point seeded with `EOF` at the start symbol. Table cell `(nonterminal, lookahead)`: FIRST of a nullable production contributes FOLLOW. When two productions land in one cell compilation fails with `GrammarCompileException` carrying `Nonterminal`, `Lookahead`, and the stably ordered `ProductionIds`; no production is chosen silently.
- `Parse(text)` first calls the existing lexer; lexical errors surface unchanged as `LexerScanException`. Parsing consumes every token: a legal prefix followed by extra input fails with actual token vs expected `EOF`.
- Parse nodes: `NonterminalNode.Name`/`ProductionId` with ordered `Children`; `TerminalNode.Token` is the original `LexToken`; an epsilon production yields a nonterminal with no children.
- The first syntax error stops the parse. `SyntaxException` reports `Actual` (token name or `EOF`), `ActualText`, `Expected` (the prediction set of the remaining suffix, stably sorted ordinal with `EOF` last), and `Offset`/`Line`/`Column`. Missing input is `EOF` at the end position, which **includes trailing skipped text** computed from the source; only LF advances the line.
- `Parser` preserves the existing surface: `parser.Scan(text)`, `parser.Audit()`, and compilation via `GrammarCompiler.Compile` (the lexer's own `Compile`/`Scan`/`Audit` are unchanged). Repeated parses are independent and never share state.
- A lexer rule may literally be named `EOF`: internally the end-of-input marker is a sentinel that cannot collide with a token name, so such a terminal parses and reports like any other.

## Source migration

`MigrationCompiler.Compile(parser, rules)` builds a reusable `SourceMigrator` from caller-ordered rules. The grammar stays the caller's own; no script language is built in. Each `MigrationRule(Id, Pattern, Replacement)` pairs a tree pattern with a replacement template:

- `NodePattern.Production(ProductionId, Children)` matches a nonterminal derived by that production; child patterns are matched in order and must mirror the production's right-hand side exactly (token patterns on terminal positions with the same name, production patterns on nonterminal positions).
- `NodePattern.Token(TokenName, Text?)` matches a terminal, optionally constrained to its original text.
- `NodePattern.Capture(Name)` matches any single subtree and binds it.
- The replacement is a list of `TemplatePart.Literal(Text)` and `TemplatePart.Capture(Name)`; capture references emit the captured subtree's original source text verbatim.

Compilation rejects unknown production identifiers, child shapes that do not match the production, duplicate capture names, undefined template references, and duplicate rule identifiers (`MigrationCompileException`). The rule set is snapshotted; later caller mutations never affect the compiled migrator.

`SourceMigrator.Migrate(source)` parses with the bound parser, then matches **preorder from the root**: the first matching rule per node wins and its subtree is not descended into; unmatched nodes are traversed further. Only nodes spanning at least one token are targets, and a capture of an empty (epsilon) subtree is a non-match. All matching uses the original tree - a single call never rewrites its own output.

Target and capture ranges run from the first token's offset to the end of the last token, in original offsets, covering skipped text inside the span. Characters outside every edit are copied verbatim. The result is a `MigrationResult(Source, Edits)` with edits sorted by original offset, each carrying `RuleId`, `Offset`, `Length`, and `Replacement`. With no matches the source is returned unchanged.

Every rewrite is validated by re-parsing with the same parser; a failure raises `MigrationValidationException` with the underlying `SyntaxException`/`LexerScanException` as `InnerException` and no partial result is produced.

## Pattern syntax

- ASCII literals and escapes: `\n`, `\r`, `\t`, and escaped metacharacters (`\\ . * + ? ( ) [ ] | ^ $ { } -`).
- Character classes with ascending ranges: `[a-z0-9_]`.
- Grouping `(...)`, concatenation, alternation `|`, repetition `*` `+` `?`.
- Precedence (high to low): repetition, concatenation, alternation.
- Not supported (compile-time error with rule name and pattern offset): wildcard `.`, negated classes `[^...]`, anchors `^` `$`, counted repetition `{m,n}`, backreferences.
- Repetition operators may be applied to subexpressions that themselves can match empty text: patterns such as `(a?)*b` compile (the mandatory `b` keeps the overall language non-empty). Only the rule pattern as a whole must reject the empty string; patterns such as `(a?)*` or `(a|b)?` are still rejected.

## Matching semantics

- Longest match wins at the current position; ties break by rule order.
- On a dead end or end of input the scanner falls back to the last accepting position; remaining characters take part in the next token, nothing is lost.
- `Skip` rules produce no token but still advance position/line/column.
- Non-ASCII input is an error reported at the first offending position.

## Limits

- NFA and DFA are each capped at 4096 states; exceeding either fails compilation with a clear error (no partial lexer is produced).
- Compilation snapshots the rule list; later caller mutations have no effect.

## Layout

- `Abstractions.cs` - public types (`LexRule`, `LexToken`, exceptions).
- `PatternParser.cs` / `Ast.cs` - pattern syntax tree and validation.
- `Nfa.cs` - Thompson construction.
- `Dfa.cs` - subset construction with epsilon closure, per-state accept sets and accept priorities.
- `RuleAudit.cs` - exact DFA reachability analysis (intersections, winning witnesses, union shadowing).
- `Lexer.cs` - `LexerCompiler`, the scanning loop, and `Lexer.Audit()`.
- `Grammar.cs` / `ParseTree.cs` - grammar model (`Production`, `GrammarSymbol`, `GrammarDefinition`), exceptions, and parse tree nodes.
- `GrammarCompiler.cs` - validation, nullable/FIRST/FOLLOW fixed points, nullable-prefix left-recursion rejection, and the EOF-aware predictive table.
- `Parser.cs` - table-driven full-input parsing, ordered tree construction, and first-error reporting.
- `Migration.cs` - tree-pattern/template migration rules, `MigrationCompiler`, and `SourceMigrator`.
- `LexForge261.Tests` - xUnit tests; `LexForge261.Demo` - runnable example.

## Build, test, demo

```sh
dotnet build LexForge261.csproj
dotnet test LexForge261.Tests
dotnet run --project LexForge261.Demo
```
