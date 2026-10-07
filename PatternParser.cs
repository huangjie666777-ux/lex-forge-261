namespace LexForge261;

/// <summary>
/// Recursive-descent parser for the supported pattern subset:
/// alternation '|' (lowest), concatenation, repetition '*', '+', '?' (highest).
/// Atoms: ASCII literals, backslash escapes (n, r, t, metacharacters),
/// character classes with ascending ranges, and parenthesized groups.
/// </summary>
internal sealed class PatternParser
{
    private readonly string _pattern;
    private readonly string _ruleName;
    private int _pos;

    private PatternParser(string pattern, string ruleName)
    {
        _pattern = pattern;
        _ruleName = ruleName;
    }

    public static Node Parse(string pattern, string ruleName)
        => new PatternParser(pattern, ruleName).ParseAll();

    private LexerCompileException Error(string message, int offset)
        => new($"Rule '{_ruleName}': {message} at pattern offset {offset}.", _ruleName, offset);

    private char Peek => _pos < _pattern.Length ? _pattern[_pos] : '\0';

    private Node ParseAll()
    {
        if (_pattern.Length == 0)
            throw Error("Pattern must not be empty", 0);
        Node node = ParseAlt();
        if (_pos != _pattern.Length)
            throw Error($"Unexpected character '{_pattern[_pos]}'", _pos);
        return node;
    }

    private Node ParseAlt()
    {
        var options = new List<Node> { ParseConcat() };
        while (Peek == '|')
        {
            _pos++;
            options.Add(ParseConcat());
        }
        return options.Count == 1 ? options[0] : new Node.Alt(options);
    }

    private Node ParseConcat()
    {
        var items = new List<Node>();
        while (_pos < _pattern.Length && Peek != '|' && Peek != ')')
            items.Add(ParseRepetition());
        if (items.Count == 0)
            throw Error("Empty branch or group is not allowed", _pos);
        return items.Count == 1 ? items[0] : new Node.Concat(items);
    }

    private Node ParseRepetition()
    {
        Node atom = ParseAtom();
        while (Peek == '*' || Peek == '+' || Peek == '?')
        {
            char op = _pattern[_pos];
            _pos++;
            atom = op switch
            {
                '*' => new Node.Star(atom),
                '+' => new Node.Plus(atom),
                _ => new Node.Opt(atom),
            };
        }
        return atom;
    }

    private Node ParseAtom()
    {
        if (_pos >= _pattern.Length)
            throw Error("Unexpected end of pattern", _pos);
        char c = _pattern[_pos];
        switch (c)
        {
            case '(':
            {
                _pos++;
                Node inner = ParseAlt();
                if (Peek != ')')
                    throw Error("Missing closing ')'", _pos);
                _pos++;
                return inner;
            }
            case ')':
                throw Error("Unmatched ')'", _pos);
            case '[':
                return ParseClass();
            case '\\':
                return new Node.Literal(ParseEscape(_pos));
            case '.':
                throw Error("Wildcard '.' is not supported", _pos);
            case '^' or '$':
                throw Error($"Anchor '{c}' is not supported", _pos);
            case '{' or '}':
                throw Error("Counted repetition is not supported", _pos);
            case '*' or '+' or '?':
                throw Error($"'{c}' has nothing to repeat", _pos);
            default:
                if (c > 0x7F)
                    throw Error("Only ASCII characters are supported in patterns", _pos);
                _pos++;
                return new Node.Literal(c);
        }
    }

    // Parses an escape starting at the backslash position; consumes both chars.
    private char ParseEscape(int slashOffset)
    {
        _pos = slashOffset + 1;
        if (_pos >= _pattern.Length)
            throw Error("Trailing backslash", slashOffset);
        char e = _pattern[_pos];
        _pos++;
        return e switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '\\' => '\\',
            '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '|' or '^' or '$' or '{' or '}' or '-' => e,
            >= '0' and <= '9' => throw Error("Backreferences are not supported", slashOffset),
            _ => throw Error($"Unknown escape '\\{e}'", slashOffset),
        };
    }

    private Node ParseClass()
    {
        int start = _pos;
        _pos++; // consume '['
        if (Peek == '^')
            throw Error("Negated character classes are not supported", _pos);
        var ranges = new List<(char, char)>();
        bool first = true;
        while (true)
        {
            if (_pos >= _pattern.Length)
                throw Error("Unterminated character class", start);
            if (Peek == ']' && !first)
            {
                _pos++;
                break;
            }
            first = false;
            char lo = ReadClassChar(start);
            if (Peek == '-' && _pos + 1 < _pattern.Length && _pattern[_pos + 1] != ']')
            {
                _pos++; // consume '-'
                char hi = ReadClassChar(start);
                if (hi < lo)
                    throw Error($"Range '{lo}-{hi}' is not ascending", _pos - 1);
                ranges.Add((lo, hi));
            }
            else
            {
                ranges.Add((lo, lo));
            }
        }
        if (ranges.Count == 0)
            throw Error("Empty character class", start);
        return new Node.CharClass(ranges);
    }

    private char ReadClassChar(int classStart)
    {
        char c = _pattern[_pos];
        if (c == '\\')
            return ParseEscape(_pos);
        if (c > 0x7F)
            throw Error("Only ASCII characters are supported in character classes", _pos);
        _pos++;
        return c;
    }
}
