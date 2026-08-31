using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Veloq.Data;

internal static class QueryDiagnostics
{
    private const int MinExcessRowsForExplosion = 50;
    private const int MinFanOutRatioForExplosion = 3;

    internal static bool ContainsAsSplitQuery(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        return syntax.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "AsSplitQuery"
            });
    }

    internal static int CountIncludes(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        return syntax.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Count(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Include" or "ThenInclude"
            });
    }

    internal static int CountCollectionIncludes(string expression, Type? rootType)
    {
        if (rootType is null)
        {
            return 0;
        }

        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        return syntax.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Include"
            })
            .Select(IncludedMemberPath)
            .Where(path => path is { Count: > 0 })
            .Count(path => PathTraversesCollection(rootType, path!));
    }

    private static readonly HashSet<string> FilterMethods =
    [
        "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "Skip", "Take", "Select", "Distinct", "AsQueryable",
    ];

    private static IReadOnlyList<string>? IncludedMemberPath(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count != 1)
        {
            return null;
        }

        ExpressionSyntax argument = invocation.ArgumentList.Arguments[0].Expression;

        if (argument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            string value = literal.Token.ValueText;
            return value.Length == 0 ? null : value.Split('.');
        }

        ExpressionSyntax? body = argument switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body as ExpressionSyntax,
            ParenthesizedLambdaExpressionSyntax paren => paren.Body as ExpressionSyntax,
            _ => argument,
        };

        body = UnwrapFilters(body);

        List<string> segments = [];
        while (body is MemberAccessExpressionSyntax member)
        {
            segments.Insert(0, member.Name.Identifier.ValueText);
            body = member.Expression;
        }

        return segments.Count == 0 ? null : segments;
    }

    private static ExpressionSyntax? UnwrapFilters(ExpressionSyntax? expression)
    {
        while (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } invocation
            && invocation.ArgumentList.Arguments.Count >= 0
            && FilterMethods.Contains(memberAccess.Name.Identifier.ValueText))
        {
            expression = memberAccess.Expression;
        }

        return expression;
    }

    private static bool PathTraversesCollection(Type rootType, IReadOnlyList<string> path)
    {
        Type currentType = rootType;
        bool sawCollection = false;

        foreach (string segment in path)
        {
            PropertyInfo? property = currentType.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
            {
                return false;
            }

            if (IsCollectionType(property.PropertyType))
            {
                sawCollection = true;
                currentType = ElementType(property.PropertyType);
            }
            else
            {
                currentType = property.PropertyType;
            }
        }

        return sawCollection;
    }

    private static Type ElementType(Type collectionType)
    {
        if (collectionType.IsArray)
        {
            return collectionType.GetElementType() ?? typeof(object);
        }

        if (collectionType.IsGenericType)
        {
            return collectionType.GetGenericArguments()[0];
        }

        Type? enumerable = collectionType.GetInterfaces()
            .Where(i => i.IsGenericType)
            .FirstOrDefault(i => i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerable?.GetGenericArguments()[0] ?? typeof(object);
    }

    private static bool IsCollectionType(Type type)
        => type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    internal static bool IsCartesianRisk(int collectionIncludeCount, bool isSplitQuery)
        => !isSplitQuery && collectionIncludeCount >= 2;

    internal static bool IsCartesianExplosion(
        int collectionIncludeCount,
        int rowsFetched,
        int rowsReturned,
        bool isSplitQuery)
    {
        if (!IsCartesianRisk(collectionIncludeCount, isSplitQuery) || rowsReturned <= 0)
        {
            return false;
        }

        return rowsFetched - rowsReturned >= MinExcessRowsForExplosion && rowsFetched >= rowsReturned * MinFanOutRatioForExplosion;
    }

    internal static string AddAsSplitQuery(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        if (syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation)
        {
            ExpressionSyntax splitReceiver = SyntaxFactory.ParseExpression(
                member.Expression.ToFullString() + ".AsSplitQuery()");

            InvocationExpressionSyntax rewritten = invocation.WithExpression(
                member.WithExpression(splitReceiver));

            return rewritten.ToFullString();
        }

        return syntax.ToFullString() + ".AsSplitQuery()";
    }

    internal static bool ContainsAsNoTracking(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        return syntax.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(IsAsNoTracking);
    }

    internal static string RemoveAsNoTracking(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        ExpressionSyntax stripped = syntax.ReplaceNodes(
            syntax.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(IsAsNoTracking),
            (_, rewritten) => ((MemberAccessExpressionSyntax)rewritten.Expression).Expression);

        return stripped.ToFullString();
    }

    private static bool IsAsNoTracking(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Name.Identifier.ValueText: "AsNoTracking"
        };
    }

    internal static string FormatSql(IReadOnlyList<CapturedCommand> commands)
    {
        if (commands.Count == 0)
        {
            return "(query executed client-side — no SQL generated)";
        }

        if (commands.Count == 1)
        {
            return commands[0].Sql;
        }

        StringBuilder output = new();
        for (int index = 0; index < commands.Count; index++)
        {
            if (index > 0)
            {
                output.AppendLine().AppendLine();
            }

            output.AppendLine($"-- Query {index + 1} of {commands.Count}");
            output.Append(commands[index].Sql);
        }

        return output.ToString();
    }
}
