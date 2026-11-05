namespace Seiko;

// Minimal SPDX expression shape check: identifiers, AND/OR/WITH, parentheses,
// and LicenseRef-/DocumentRef- ids. The list of known license ids is not
// bundled; a typo in an id passes here and is caught by the SPDX tooling the
// maintainer already runs.
internal static class SpdxExpression
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = Tokenize(value);
        if (tokens is null || tokens.Count == 0)
        {
            return false;
        }

        var position = 0;
        var ok = ParseOr(tokens, ref position);
        return ok && position == tokens.Count;
    }

    private static bool ParseOr(List<string> tokens, ref int position)
    {
        if (!ParseAnd(tokens, ref position))
        {
            return false;
        }

        while (Peek(tokens, position) == "OR")
        {
            position++;
            if (!ParseAnd(tokens, ref position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseAnd(List<string> tokens, ref int position)
    {
        if (!ParseWith(tokens, ref position))
        {
            return false;
        }

        while (Peek(tokens, position) == "AND")
        {
            position++;
            if (!ParseWith(tokens, ref position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseWith(List<string> tokens, ref int position)
    {
        if (!ParseAtom(tokens, ref position))
        {
            return false;
        }

        if (Peek(tokens, position) == "WITH")
        {
            position++;
            if (!ParseAtom(tokens, ref position))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ParseAtom(List<string> tokens, ref int position)
    {
        var token = Peek(tokens, position);
        if (token == "(")
        {
            position++;
            if (!ParseOr(tokens, ref position))
            {
                return false;
            }

            if (Peek(tokens, position) != ")")
            {
                return false;
            }

            position++;
            return true;
        }

        if (token is null or "AND" or "OR" or "WITH" or ")")
        {
            return false;
        }

        position++;
        return true;
    }

    private static string? Peek(List<string> tokens, int position)
        => position < tokens.Count ? tokens[position] : null;

    private static List<string>? Tokenize(string value)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
                continue;
            }

            if (character is '(' or ')')
            {
                tokens.Add(character.ToString());
                index++;
                continue;
            }

            var start = index;
            while (index < value.Length
                && !char.IsWhiteSpace(value[index])
                && value[index] is not ('(' or ')'))
            {
                index++;
            }

            var token = value[start..index];
            if (token is "AND" or "OR" or "WITH")
            {
                tokens.Add(token);
                continue;
            }

            if (!token.All(IsIdentifierChar))
            {
                return null;
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private static bool IsIdentifierChar(char value)
        => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '+' or ':';
}
