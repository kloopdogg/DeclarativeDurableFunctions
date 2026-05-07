using System.Text.Json;
using System.Text.RegularExpressions;
using DeclarativeDurableFunctions.Exceptions;

namespace DeclarativeDurableFunctions.Engine;

static partial class ExpressionEvaluator
{
    // Matches a YAML value that is entirely a single {{...}} expression.
    static readonly Regex SingleExpr = SingleExprRegex();

    // Matches any {{...}} token within a string for interpolation.
    static readonly Regex AllTokens = AllTokensRegex();

    /// <summary>
    /// Evaluates a YAML value that may contain {{...}} expressions.
    /// If the entire value is one {{expr}}, the result preserves the resolved type.
    /// If {{expr}} tokens are embedded in surrounding text, result is an interpolated string.
    /// </summary>
    public static object? Evaluate(string expression, WorkflowExecutionContext ctx)
    {
        var single = SingleExpr.Match(expression);
        return single.Success
            ? EvaluateInner(single.Groups[1].Value.Trim(), expression, ctx)
            : !expression.Contains("{{")
            ? expression
            : AllTokens.Replace(expression, m =>
            Stringify(EvaluateInner(m.Groups[1].Value.Trim(), expression, ctx)));
    }

    /// <summary>
    /// Evaluates a condition expression (should be "{{expr}}" wrapping a boolean expression).
    /// Returns true if the result is truthy.
    /// </summary>
    public static bool EvaluateBool(string expression, WorkflowExecutionContext ctx)
    {
        var single = SingleExpr.Match(expression);
        string inner = single.Success ? single.Groups[1].Value.Trim() : expression.Trim();
        object? result = EvaluateInner(inner, expression, ctx, isCondition: true);
        return IsTruthy(result);
    }

    /// <summary>
    /// Resolves an input template (string expression, nested dictionary, or list) against the context.
    /// </summary>
    public static object? ResolveInputTemplate(object? inputTemplate, WorkflowExecutionContext ctx) => inputTemplate switch
    {
        null => null,
        string s => Evaluate(s, ctx),
        Dictionary<object, object> dict => ResolveDict(dict, ctx),
        List<object> list => list.Select(item => ResolveInputTemplate(item, ctx)).ToList(),
        _ => inputTemplate
    };

    static Dictionary<string, object?> ResolveDict(Dictionary<object, object> dict, WorkflowExecutionContext ctx)
    {
        var result = new Dictionary<string, object?>(dict.Count);
        foreach (var (key, value) in dict)
        {
            result[key.ToString()!] = ResolveInputTemplate(value, ctx);
        }

        return result;
    }

    static object? EvaluateInner(string inner, string fullExpression, WorkflowExecutionContext ctx, bool isCondition = false)
    {
        try
        {
            var lexer = new ExprLexer(inner);
            var parser = new ExprParser(lexer, fullExpression);
            var node = parser.Parse();
            return EvalNode(node, ctx, fullExpression, isCondition);
        }
        catch (WorkflowExpressionException) { throw; }
        catch (Exception ex)
        {
            throw new WorkflowExpressionException(fullExpression, ex.Message, ex);
        }
    }

    static object? EvalNode(ExprNode node, WorkflowExecutionContext ctx, string expr, bool isCondition)
        => node switch
        {
            LiteralNode lit   => lit.Value,
            PathNode path     => ResolvePath(path.Segments, ctx, expr, isCondition),
            UnaryOpNode unary => EvalUnary(unary, ctx, expr),
            BinaryOpNode bin  => EvalBinary(bin, ctx, expr),
            _ => throw new WorkflowExpressionException(expr, "Unknown AST node type")
        };

    static object? ResolvePath(string[] segments, WorkflowExecutionContext ctx, string expr, bool isCondition)
    {
        if (segments.Length == 0)
        {
            return null;
        }

        string root = segments[0];

        switch (root)
        {
            case "input":
                return TraverseJsonElement(ctx.Input, segments, 1, expr, isCondition);

            case "$item":
                if (!ctx.IterationItem.HasValue)
                {
                    throw new WorkflowExpressionException(expr, "$item is not available outside a foreach step");
                }

                return TraverseJsonElement(ctx.IterationItem.Value, segments, 1, expr, isCondition);

            case "$index":
                if (!ctx.IterationIndex.HasValue)
                {
                    throw new WorkflowExpressionException(expr, "$index is not available outside a foreach step");
                }

                return ctx.IterationIndex.Value;

            case "orchestration":
                if (segments.Length < 2)
                {
                    throw new WorkflowExpressionException(expr, "'orchestration' requires a property name");
                }

                return segments[1] switch
                {
                    "instanceId"       => ctx.InstanceId,
                    "parentInstanceId" => (object?)ctx.ParentInstanceId,
                    _ => throw new WorkflowExpressionException(expr, $"Unknown orchestration property '{segments[1]}'")
                };

            default:
                if (!ctx.HasOutput(root))
                {
                    return isCondition ? null : throw new WorkflowExpressionException(expr, $"Unknown variable '{root}'");
                }
                object? output = ctx.GetOutput(root);
                if (segments.Length == 1)
                {
                    return output;
                }

                if (output is JsonElement je)
                {
                    return TraverseJsonElement(je, segments, 1, expr, isCondition);
                }

                return null;
        }
    }

    static object? TraverseJsonElement(
        JsonElement element, string[] segments, int startIndex, string expr, bool isCondition)
    {
        for (int i = startIndex; i < segments.Length; i++)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                return isCondition
                    ? null
                    : throw new WorkflowExpressionException(
                    expr, $"Cannot access property '{segments[i]}' on a non-object");
            }
            if (!TryGetProperty(element, segments[i], out var child))
            {
                if (isCondition)
                {
                    return null;
                }

                string path = string.Join(".", segments[..i]);
                throw new WorkflowExpressionException(
                    expr, $"Property '{segments[i]}' not found on '{path}'");
            }
            element = child;
        }
        return UnboxJsonElement(element);
    }

    static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    static object? UnboxJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String  => el.GetString(),
        JsonValueKind.Number  => el.TryGetInt64(out long l) ? (object)l : el.GetDouble(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        JsonValueKind.Undefined => null,
        _                     => el   // Object or Array — preserve as JsonElement
    };

    static object? EvalUnary(UnaryOpNode node, WorkflowExecutionContext ctx, string expr)
    {
        object? operand = EvalNode(node.Operand, ctx, expr, isCondition: true);
        return node.Op == ExprTokenKind.Not ? !IsTruthy(operand)
            : throw new WorkflowExpressionException(expr, $"Unknown unary op {node.Op}");
    }

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    static object? EvalBinary(BinaryOpNode node, WorkflowExecutionContext ctx, string expr)
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
    {
        // Short-circuit &&
        if (node.Op == ExprTokenKind.And)
        {
            object? l = EvalNode(node.Left, ctx, expr, isCondition: true);
            return IsTruthy(l) && IsTruthy(EvalNode(node.Right, ctx, expr, isCondition: true));
        }
        // Short-circuit ||
        if (node.Op == ExprTokenKind.Or)
        {
            object? l = EvalNode(node.Left, ctx, expr, isCondition: true);
            return IsTruthy(l) || IsTruthy(EvalNode(node.Right, ctx, expr, isCondition: true));
        }

        object? left = EvalNode(node.Left, ctx, expr, isCondition: true);
        object? right = EvalNode(node.Right, ctx, expr, isCondition: true);

        return node.Op switch
        {
            ExprTokenKind.Eq  => AreEqual(left, right),
            ExprTokenKind.Neq => !AreEqual(left, right),
            ExprTokenKind.Lt  => Compare(left, right) < 0,
            ExprTokenKind.Gt  => Compare(left, right) > 0,
            ExprTokenKind.Lte => Compare(left, right) <= 0,
            ExprTokenKind.Gte => Compare(left, right) >= 0,
            _ => throw new WorkflowExpressionException(expr, $"Unknown binary op {node.Op}")
        };
    }

    static bool AreEqual(object? a, object? b) => (a == null && b == null) || (a != null && b != null && (TryGetDouble(a, out double da) && TryGetDouble(b, out double db) ? da == db : a.ToString() == b.ToString()));

    static int Compare(object? a, object? b) => TryGetDouble(a, out double da) && TryGetDouble(b, out double db)
            ? da.CompareTo(db)
            : string.Compare(a?.ToString(), b?.ToString(), StringComparison.Ordinal);

    static bool TryGetDouble(object? value, out double result)
    {
        switch (value)
        {
            case int i:    result = i; return true;
            case long l:   result = l; return true;
            case double d: result = d; return true;
            case float f:  result = f; return true;
            case JsonElement je when je.ValueKind == JsonValueKind.Number:
                result = je.GetDouble(); return true;
            default: result = 0; return false;
        }
    }

    static bool IsTruthy(object? value) => value switch
    {
        null       => false,
        bool b     => b,
        int i      => i != 0,
        long l     => l != 0,
        double d   => d != 0,
        string s   => s.Length > 0,
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.Null  => false,
            JsonValueKind.False => false,
            JsonValueKind.Number => je.GetDouble() != 0,
            JsonValueKind.String => (je.GetString()?.Length ?? 0) > 0,
            _ => true
        },
        _ => true
    };

    internal static string Stringify(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
        JsonElement je when je.ValueKind == JsonValueKind.Null   => "",
        JsonElement je when je.ValueKind == JsonValueKind.True   => "true",
        JsonElement je when je.ValueKind == JsonValueKind.False  => "false",
        JsonElement je => je.ToString(),
        _ => value.ToString() ?? ""
    };

    [GeneratedRegex(@"^\s*\{\{(.+?)\}\}\s*$", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex SingleExprRegex();

    [GeneratedRegex(@"\{\{(.+?)\}\}", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex AllTokensRegex();
}

// ---- Token types ----

enum ExprTokenKind
{
    Ident, StringLit, NumberLit, True, False, Null,
    Dot, LParen, RParen,
    Not, And, Or,
    Eq, Neq, Lt, Gt, Lte, Gte,
    EOF
}

sealed class ExprToken(ExprTokenKind kind, string? text = null, object? value = null)
{
    public ExprTokenKind Kind { get; } = kind;
    public string? Text { get; } = text;
    public object? Value { get; } = value;
}

// ---- Lexer ----

sealed class ExprLexer(string text)
{
#pragma warning disable IDE1006 // Naming Styles
    int _pos;
#pragma warning restore IDE1006 // Naming Styles

    public ExprToken Next()
    {
        while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
        {
            _pos++;
        }

        if (_pos >= text.Length)
        {
            return new ExprToken(ExprTokenKind.EOF);
        }

        char c = text[_pos];

        if (c is '"' or '\'')
        {
            char quote = c;
            _pos++;
            var sb = new System.Text.StringBuilder();
            while (_pos < text.Length && text[_pos] != quote)
            {
                if (text[_pos] == '\\' && _pos + 1 < text.Length)
                {
                    _pos++;
                    _ = sb.Append(text[_pos] switch { 'n' => '\n', 't' => '\t', _ => text[_pos] });
                }
                else
                {
                    _ = sb.Append(text[_pos]);
                }

                _pos++;
            }
            if (_pos < text.Length)
            {
                _pos++;
            }

            return new ExprToken(ExprTokenKind.StringLit, null, sb.ToString());
        }

        if (char.IsDigit(c) || (c == '-' && _pos + 1 < text.Length && char.IsDigit(text[_pos + 1])))
        {
            int start = _pos;
            if (c == '-')
            {
                _pos++;
            }

            while (_pos < text.Length && char.IsDigit(text[_pos]))
            {
                _pos++;
            }

            bool isFloat = _pos < text.Length && text[_pos] == '.';
            if (isFloat) { _pos++; while (_pos < text.Length && char.IsDigit(text[_pos]))
                {
                    _pos++;
                }
            }
            string num = text[start.._pos];
#pragma warning disable CA1305 // Specify IFormatProvider
            return isFloat
                ? new ExprToken(ExprTokenKind.NumberLit, null, double.Parse(num, System.Globalization.CultureInfo.InvariantCulture))
                : new ExprToken(ExprTokenKind.NumberLit, null, long.Parse(num));
#pragma warning restore CA1305 // Specify IFormatProvider
        }

        if (char.IsLetter(c) || c is '_' or '$')
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '$'))
            {
                _pos++;
            }

            string ident = text[start.._pos];
            return ident switch
            {
                "true"  => new ExprToken(ExprTokenKind.True, ident, true),
                "false" => new ExprToken(ExprTokenKind.False, ident, false),
                "null"  => new ExprToken(ExprTokenKind.Null, ident, null),
                _       => new ExprToken(ExprTokenKind.Ident, ident)
            };
        }

        if (c == '&' && _pos + 1 < text.Length && text[_pos + 1] == '&') { _pos += 2; return new ExprToken(ExprTokenKind.And, "&&"); }
        if (c == '|' && _pos + 1 < text.Length && text[_pos + 1] == '|') { _pos += 2; return new ExprToken(ExprTokenKind.Or, "||"); }
        if (c == '=' && _pos + 1 < text.Length && text[_pos + 1] == '=') { _pos += 2; return new ExprToken(ExprTokenKind.Eq, "=="); }
        if (c == '!' && _pos + 1 < text.Length && text[_pos + 1] == '=') { _pos += 2; return new ExprToken(ExprTokenKind.Neq, "!="); }
        if (c == '<' && _pos + 1 < text.Length && text[_pos + 1] == '=') { _pos += 2; return new ExprToken(ExprTokenKind.Lte, "<="); }
        if (c == '>' && _pos + 1 < text.Length && text[_pos + 1] == '=') { _pos += 2; return new ExprToken(ExprTokenKind.Gte, ">="); }

        _pos++;
        return c switch
        {
            '.' => new ExprToken(ExprTokenKind.Dot, "."),
            '(' => new ExprToken(ExprTokenKind.LParen, "("),
            ')' => new ExprToken(ExprTokenKind.RParen, ")"),
            '!' => new ExprToken(ExprTokenKind.Not, "!"),
            '<' => new ExprToken(ExprTokenKind.Lt, "<"),
            '>' => new ExprToken(ExprTokenKind.Gt, ">"),
            _   => throw new InvalidOperationException($"Unexpected character '{c}'")
        };
    }
}

// ---- AST Nodes ----

abstract class ExprNode { }
sealed class LiteralNode(object? value) : ExprNode { public readonly object? Value = value; }
sealed class PathNode(string[] segments) : ExprNode { public readonly string[] Segments = segments; }
sealed class UnaryOpNode(ExprTokenKind op, ExprNode operand) : ExprNode
{
    public readonly ExprTokenKind Op = op;
    public readonly ExprNode Operand = operand;
}
sealed class BinaryOpNode(ExprTokenKind op, ExprNode left, ExprNode right) : ExprNode
{
    public readonly ExprTokenKind Op = op;
    public readonly ExprNode Left = left;
    public readonly ExprNode Right = right;
}

// ---- Recursive descent parser ----

sealed class ExprParser
{
    readonly string expression;
    readonly ExprLexer lexer;
    ExprToken cur;

    public ExprParser(ExprLexer lexer, string expression)
    {
        this.lexer = lexer;
        this.expression = expression;
        cur = this.lexer.Next();
    }

    public ExprNode Parse()
    {
        var node = ParseOr();
        return cur.Kind != ExprTokenKind.EOF ? throw new WorkflowExpressionException(expression, $"Unexpected token '{cur.Text}'") : node;
    }

    ExprNode ParseOr()
    {
        var left = ParseAnd();
        while (cur.Kind == ExprTokenKind.Or)
        {
            Advance();
            left = new BinaryOpNode(ExprTokenKind.Or, left, ParseAnd());
        }
        return left;
    }

    ExprNode ParseAnd()
    {
        var left = ParseEquality();
        while (cur.Kind == ExprTokenKind.And)
        {
            Advance();
            left = new BinaryOpNode(ExprTokenKind.And, left, ParseEquality());
        }
        return left;
    }

    ExprNode ParseEquality()
    {
        var left = ParseComparison();
        if (cur.Kind is ExprTokenKind.Eq or ExprTokenKind.Neq)
        {
            var op = cur.Kind; Advance();
            return new BinaryOpNode(op, left, ParseComparison());
        }
        return left;
    }

    ExprNode ParseComparison()
    {
        var left = ParseUnary();
        if (cur.Kind is ExprTokenKind.Lt or ExprTokenKind.Gt or ExprTokenKind.Lte or ExprTokenKind.Gte)
        {
            var op = cur.Kind; Advance();
            return new BinaryOpNode(op, left, ParseUnary());
        }
        return left;
    }

    ExprNode ParseUnary()
    {
        if (cur.Kind == ExprTokenKind.Not)
        {
            Advance();
            return new UnaryOpNode(ExprTokenKind.Not, ParseUnary());
        }
        return ParsePrimary();
    }

    ExprNode ParsePrimary()
    {
        if (cur.Kind == ExprTokenKind.LParen)
        {
            Advance();
            var inner = ParseOr();
            if (cur.Kind != ExprTokenKind.RParen)
            {
                throw new WorkflowExpressionException(expression, "Expected ')'");
            }

            Advance();
            return inner;
        }

        if (cur.Kind == ExprTokenKind.Ident)
        {
            string name = cur.Text!; Advance();
            return ParsePathContinuation(name);
        }

        if (cur.Kind is ExprTokenKind.True or ExprTokenKind.False or ExprTokenKind.Null
                      or ExprTokenKind.StringLit or ExprTokenKind.NumberLit)
        {
            object? val = cur.Value; Advance();
            return new LiteralNode(val);
        }

        throw new WorkflowExpressionException(expression,
            $"Unexpected token '{cur.Text ?? cur.Kind.ToString()}'");
    }

#pragma warning disable CA1859 // Use concrete types when possible for improved performance
    ExprNode ParsePathContinuation(string first)
#pragma warning restore CA1859 // Use concrete types when possible for improved performance
    {
        var segments = new List<string> { first };
        while (cur.Kind == ExprTokenKind.Dot)
        {
            Advance();
            if (cur.Kind != ExprTokenKind.Ident)
            {
                throw new WorkflowExpressionException(expression, "Expected identifier after '.'");
            }

            segments.Add(cur.Text!); Advance();
        }
        return new PathNode([.. segments]);
    }

    void Advance() => cur = lexer.Next();
}
