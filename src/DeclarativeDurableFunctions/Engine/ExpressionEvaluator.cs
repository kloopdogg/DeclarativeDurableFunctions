using System.Text.Json;
using System.Text.RegularExpressions;
using DeclarativeDurableFunctions.Exceptions;

namespace DeclarativeDurableFunctions.Engine;

internal static class ExpressionEvaluator
{
    // Matches a YAML value that is entirely a single {{...}} expression.
    private static readonly Regex SingleExpr =
        new(@"^\s*\{\{(.+?)\}\}\s*$", RegexOptions.Compiled | RegexOptions.Singleline);

    // Matches any {{...}} token within a string for interpolation.
    private static readonly Regex AllTokens =
        new(@"\{\{(.+?)\}\}", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Evaluates a YAML value that may contain {{...}} expressions.
    /// If the entire value is one {{expr}}, the result preserves the resolved type.
    /// If {{expr}} tokens are embedded in surrounding text, result is an interpolated string.
    /// </summary>
    public static object? Evaluate(string expression, WorkflowExecutionContext ctx)
    {
        var single = SingleExpr.Match(expression);
        if (single.Success)
            return EvaluateInner(single.Groups[1].Value.Trim(), expression, ctx);

        if (!expression.Contains("{{"))
            return expression;

        return AllTokens.Replace(expression, m =>
            Stringify(EvaluateInner(m.Groups[1].Value.Trim(), expression, ctx)));
    }

    /// <summary>
    /// Evaluates a condition expression (should be "{{expr}}" wrapping a boolean expression).
    /// Returns true if the result is truthy.
    /// </summary>
    public static bool EvaluateBool(string expression, WorkflowExecutionContext ctx)
    {
        var single = SingleExpr.Match(expression);
        var inner = single.Success ? single.Groups[1].Value.Trim() : expression.Trim();
        var result = EvaluateInner(inner, expression, ctx, isCondition: true);
        return IsTruthy(result);
    }

    /// <summary>
    /// Resolves an input template (string expression, nested dictionary, or list) against the context.
    /// </summary>
    public static object? ResolveInputTemplate(object? inputTemplate, WorkflowExecutionContext ctx)
    {
        return inputTemplate switch
        {
            null => null,
            string s => Evaluate(s, ctx),
            Dictionary<object, object> dict => ResolveDict(dict, ctx),
            List<object> list => list.Select(item => ResolveInputTemplate(item, ctx)).ToList(),
            _ => inputTemplate
        };
    }

    private static Dictionary<string, object?> ResolveDict(Dictionary<object, object> dict, WorkflowExecutionContext ctx)
    {
        var result = new Dictionary<string, object?>(dict.Count);
        foreach (var (key, value) in dict)
            result[key.ToString()!] = ResolveInputTemplate(value, ctx);
        return result;
    }

    private static object? EvaluateInner(string inner, string fullExpression, WorkflowExecutionContext ctx, bool isCondition = false)
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

    private static object? EvalNode(ExprNode node, WorkflowExecutionContext ctx, string expr, bool isCondition)
        => node switch
        {
            LiteralNode lit   => lit.Value,
            PathNode path     => ResolvePath(path.Segments, ctx, expr, isCondition),
            UnaryOpNode unary => EvalUnary(unary, ctx, expr),
            BinaryOpNode bin  => EvalBinary(bin, ctx, expr),
            _ => throw new WorkflowExpressionException(expr, "Unknown AST node type")
        };

    private static object? ResolvePath(string[] segments, WorkflowExecutionContext ctx, string expr, bool isCondition)
    {
        if (segments.Length == 0) return null;
        var root = segments[0];

        switch (root)
        {
            case "input":
                return TraverseJsonElement(ctx.Input, segments, 1, expr, isCondition);

            case "$item":
                if (!ctx.IterationItem.HasValue)
                    throw new WorkflowExpressionException(expr, "$item is not available outside a foreach step");
                return TraverseJsonElement(ctx.IterationItem.Value, segments, 1, expr, isCondition);

            case "$index":
                if (!ctx.IterationIndex.HasValue)
                    throw new WorkflowExpressionException(expr, "$index is not available outside a foreach step");
                return ctx.IterationIndex.Value;

            case "orchestration":
                if (segments.Length < 2)
                    throw new WorkflowExpressionException(expr, "'orchestration' requires a property name");
                return segments[1] switch
                {
                    "instanceId"       => ctx.InstanceId,
                    "parentInstanceId" => (object?)ctx.ParentInstanceId,
                    _ => throw new WorkflowExpressionException(expr, $"Unknown orchestration property '{segments[1]}'")
                };

            default:
                if (!ctx.HasOutput(root))
                {
                    if (isCondition) return null;
                    throw new WorkflowExpressionException(expr, $"Unknown variable '{root}'");
                }
                var output = ctx.GetOutput(root);
                if (segments.Length == 1) return output;
                if (output is JsonElement je)
                    return TraverseJsonElement(je, segments, 1, expr, isCondition);
                return null;
        }
    }

    private static object? TraverseJsonElement(
        JsonElement element, string[] segments, int startIndex, string expr, bool isCondition)
    {
        for (var i = startIndex; i < segments.Length; i++)
        {
            if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;
            if (element.ValueKind != JsonValueKind.Object)
            {
                if (isCondition) return null;
                throw new WorkflowExpressionException(
                    expr, $"Cannot access property '{segments[i]}' on a non-object");
            }
            if (!element.TryGetProperty(segments[i], out var child))
            {
                if (isCondition) return null;
                var path = string.Join(".", segments[..i]);
                throw new WorkflowExpressionException(
                    expr, $"Property '{segments[i]}' not found on '{path}'");
            }
            element = child;
        }
        return UnboxJsonElement(element);
    }

    private static object? UnboxJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String  => el.GetString(),
        JsonValueKind.Number  => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.True    => true,
        JsonValueKind.False   => false,
        JsonValueKind.Null    => null,
        JsonValueKind.Undefined => null,
        _                     => el   // Object or Array — preserve as JsonElement
    };

    private static object? EvalUnary(UnaryOpNode node, WorkflowExecutionContext ctx, string expr)
    {
        var operand = EvalNode(node.Operand, ctx, expr, isCondition: true);
        return node.Op == ExprTokenKind.Not ? !IsTruthy(operand)
            : throw new WorkflowExpressionException(expr, $"Unknown unary op {node.Op}");
    }

    private static object? EvalBinary(BinaryOpNode node, WorkflowExecutionContext ctx, string expr)
    {
        // Short-circuit &&
        if (node.Op == ExprTokenKind.And)
        {
            var l = EvalNode(node.Left, ctx, expr, isCondition: true);
            return !IsTruthy(l) ? false : IsTruthy(EvalNode(node.Right, ctx, expr, isCondition: true));
        }
        // Short-circuit ||
        if (node.Op == ExprTokenKind.Or)
        {
            var l = EvalNode(node.Left, ctx, expr, isCondition: true);
            return IsTruthy(l) ? true : IsTruthy(EvalNode(node.Right, ctx, expr, isCondition: true));
        }

        var left = EvalNode(node.Left, ctx, expr, isCondition: true);
        var right = EvalNode(node.Right, ctx, expr, isCondition: true);

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

    private static bool AreEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (TryGetDouble(a, out var da) && TryGetDouble(b, out var db))
            return da == db;
        return a.ToString() == b.ToString();
    }

    private static int Compare(object? a, object? b)
    {
        if (TryGetDouble(a, out var da) && TryGetDouble(b, out var db))
            return da.CompareTo(db);
        return string.Compare(a?.ToString(), b?.ToString(), StringComparison.Ordinal);
    }

    private static bool TryGetDouble(object? value, out double result)
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

    private static bool IsTruthy(object? value) => value switch
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

    private static string Stringify(object? value) => value switch
    {
        null       => "",
        JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString() ?? "",
        JsonElement je => je.ToString(),
        _ => value.ToString() ?? ""
    };
}

// ---- Token types ----

internal enum ExprTokenKind
{
    Ident, StringLit, NumberLit, True, False, Null,
    Dot, LParen, RParen,
    Not, And, Or,
    Eq, Neq, Lt, Gt, Lte, Gte,
    EOF
}

internal sealed class ExprToken(ExprTokenKind kind, string? text = null, object? value = null)
{
    public ExprTokenKind Kind { get; } = kind;
    public string? Text { get; } = text;
    public object? Value { get; } = value;
}

// ---- Lexer ----

internal sealed class ExprLexer(string text)
{
    private int _pos;

    public ExprToken Next()
    {
        while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        if (_pos >= text.Length) return new ExprToken(ExprTokenKind.EOF);

        var c = text[_pos];

        if (c == '"')
        {
            _pos++;
            var sb = new System.Text.StringBuilder();
            while (_pos < text.Length && text[_pos] != '"')
            {
                if (text[_pos] == '\\' && _pos + 1 < text.Length)
                {
                    _pos++;
                    sb.Append(text[_pos] switch { 'n' => '\n', 't' => '\t', _ => text[_pos] });
                }
                else sb.Append(text[_pos]);
                _pos++;
            }
            if (_pos < text.Length) _pos++;
            return new ExprToken(ExprTokenKind.StringLit, null, sb.ToString());
        }

        if (char.IsDigit(c) || (c == '-' && _pos + 1 < text.Length && char.IsDigit(text[_pos + 1])))
        {
            var start = _pos;
            if (c == '-') _pos++;
            while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++;
            var isFloat = _pos < text.Length && text[_pos] == '.';
            if (isFloat) { _pos++; while (_pos < text.Length && char.IsDigit(text[_pos])) _pos++; }
            var num = text[start.._pos];
            return isFloat
                ? new ExprToken(ExprTokenKind.NumberLit, null, double.Parse(num, System.Globalization.CultureInfo.InvariantCulture))
                : new ExprToken(ExprTokenKind.NumberLit, null, long.Parse(num));
        }

        if (char.IsLetter(c) || c is '_' or '$')
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '$'))
                _pos++;
            var ident = text[start.._pos];
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

internal abstract class ExprNode { }
internal sealed class LiteralNode(object? value) : ExprNode { public readonly object? Value = value; }
internal sealed class PathNode(string[] segments) : ExprNode { public readonly string[] Segments = segments; }
internal sealed class UnaryOpNode(ExprTokenKind op, ExprNode operand) : ExprNode
{
    public readonly ExprTokenKind Op = op;
    public readonly ExprNode Operand = operand;
}
internal sealed class BinaryOpNode(ExprTokenKind op, ExprNode left, ExprNode right) : ExprNode
{
    public readonly ExprTokenKind Op = op;
    public readonly ExprNode Left = left;
    public readonly ExprNode Right = right;
}

// ---- Recursive descent parser ----

internal sealed class ExprParser
{
    private readonly string _expression;
    private readonly ExprLexer _lexer;
    private ExprToken _cur;

    public ExprParser(ExprLexer lexer, string expression)
    {
        _lexer = lexer;
        _expression = expression;
        _cur = _lexer.Next();
    }

    public ExprNode Parse()
    {
        var node = ParseOr();
        if (_cur.Kind != ExprTokenKind.EOF)
            throw new WorkflowExpressionException(_expression, $"Unexpected token '{_cur.Text}'");
        return node;
    }

    private ExprNode ParseOr()
    {
        var left = ParseAnd();
        while (_cur.Kind == ExprTokenKind.Or)
        {
            Advance();
            left = new BinaryOpNode(ExprTokenKind.Or, left, ParseAnd());
        }
        return left;
    }

    private ExprNode ParseAnd()
    {
        var left = ParseEquality();
        while (_cur.Kind == ExprTokenKind.And)
        {
            Advance();
            left = new BinaryOpNode(ExprTokenKind.And, left, ParseEquality());
        }
        return left;
    }

    private ExprNode ParseEquality()
    {
        var left = ParseComparison();
        if (_cur.Kind is ExprTokenKind.Eq or ExprTokenKind.Neq)
        {
            var op = _cur.Kind; Advance();
            return new BinaryOpNode(op, left, ParseComparison());
        }
        return left;
    }

    private ExprNode ParseComparison()
    {
        var left = ParseUnary();
        if (_cur.Kind is ExprTokenKind.Lt or ExprTokenKind.Gt or ExprTokenKind.Lte or ExprTokenKind.Gte)
        {
            var op = _cur.Kind; Advance();
            return new BinaryOpNode(op, left, ParseUnary());
        }
        return left;
    }

    private ExprNode ParseUnary()
    {
        if (_cur.Kind == ExprTokenKind.Not)
        {
            Advance();
            return new UnaryOpNode(ExprTokenKind.Not, ParseUnary());
        }
        return ParsePrimary();
    }

    private ExprNode ParsePrimary()
    {
        if (_cur.Kind == ExprTokenKind.LParen)
        {
            Advance();
            var inner = ParseOr();
            if (_cur.Kind != ExprTokenKind.RParen)
                throw new WorkflowExpressionException(_expression, "Expected ')'");
            Advance();
            return inner;
        }

        if (_cur.Kind == ExprTokenKind.Ident)
        {
            var name = _cur.Text!; Advance();
            return ParsePathContinuation(name);
        }

        if (_cur.Kind is ExprTokenKind.True or ExprTokenKind.False or ExprTokenKind.Null
                      or ExprTokenKind.StringLit or ExprTokenKind.NumberLit)
        {
            var val = _cur.Value; Advance();
            return new LiteralNode(val);
        }

        throw new WorkflowExpressionException(_expression,
            $"Unexpected token '{_cur.Text ?? _cur.Kind.ToString()}'");
    }

    private ExprNode ParsePathContinuation(string first)
    {
        var segments = new List<string> { first };
        while (_cur.Kind == ExprTokenKind.Dot)
        {
            Advance();
            if (_cur.Kind != ExprTokenKind.Ident)
                throw new WorkflowExpressionException(_expression, "Expected identifier after '.'");
            segments.Add(_cur.Text!); Advance();
        }
        return new PathNode(segments.ToArray());
    }

    private void Advance() => _cur = _lexer.Next();
}
