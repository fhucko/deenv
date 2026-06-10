using System.Text;

namespace DeEnv.Code;

// Recursive-descent parser: text → ExpressionNode AST.
// Precedence (lowest to highest): or → and → not → comparison → primary
public static class ExpressionParser
{
    public static ExpressionNode Parse(string text)
    {
        var p = new Parser(text.Trim());
        var node = p.ParseOr();
        p.ExpectEnd();
        return node;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;

        public Parser(string text) { _text = text; }

        public ExpressionNode ParseOr()
        {
            var left = ParseAnd();
            while (TryConsume("||"))
                left = new OrNode(left, ParseAnd());
            return left;
        }

        private ExpressionNode ParseAnd()
        {
            var left = ParseNot();
            while (TryConsume("&&"))
                left = new AndNode(left, ParseNot());
            return left;
        }

        private ExpressionNode ParseNot()
        {
            if (TryConsume("!"))
                return new NotNode(ParseNot());
            return ParseComparison();
        }

        private ExpressionNode ParseComparison()
        {
            var left = ParsePrimary();
            if (TryConsume("=="))  return new EqNode(left, ParsePrimary());
            if (TryConsume("!="))  return new NeqNode(left, ParsePrimary());
            if (TryConsume(">="))  return new GteNode(left, ParsePrimary());
            if (TryConsume("<="))  return new LteNode(left, ParsePrimary());
            if (TryConsume(">"))   return new GtNode(left, ParsePrimary());
            if (TryConsume("<"))   return new LtNode(left, ParsePrimary());
            return left;
        }

        private ExpressionNode ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw new FormatException("Unexpected end of expression.");

            var ch = _text[_pos];

            if (ch == '(')
            {
                _pos++;
                var inner = ParseOr();
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')')
                    throw new FormatException("Expected ')'.");
                _pos++;
                return inner;
            }

            if (ch == '\'')
            {
                _pos++;
                var sb = new StringBuilder();
                while (_pos < _text.Length && _text[_pos] != '\'')
                    sb.Append(_text[_pos++]);
                if (_pos >= _text.Length)
                    throw new FormatException("Unterminated string literal.");
                _pos++;
                return new LiteralNode(sb.ToString());
            }

            if (char.IsDigit(ch))
            {
                var start = _pos;
                while (_pos < _text.Length && char.IsDigit(_text[_pos]))
                    _pos++;
                return new LiteralNode(int.Parse(_text[start.._pos]));
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var ident = ReadIdentifier();
                if (ident == "true")  return new LiteralNode(true);
                if (ident == "false") return new LiteralNode(false);
                var path = new List<string> { ident };
                while (_pos < _text.Length && _text[_pos] == '.')
                {
                    _pos++;
                    path.Add(ReadIdentifier());
                }
                return new FieldNode(path);
            }

            throw new FormatException($"Unexpected character '{ch}' at position {_pos}.");
        }

        private string ReadIdentifier()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
                _pos++;
            if (_pos == start)
                throw new FormatException($"Expected identifier at position {_pos}.");
            return _text[start.._pos];
        }

        private bool TryConsume(string token)
        {
            SkipWhitespace();
            if (_pos + token.Length <= _text.Length &&
                _text.AsSpan(_pos, token.Length).SequenceEqual(token))
            {
                _pos += token.Length;
                return true;
            }
            return false;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        public void ExpectEnd()
        {
            SkipWhitespace();
            if (_pos < _text.Length)
                throw new FormatException($"Unexpected text at position {_pos}: '{_text[_pos..]}'.");
        }
    }
}
