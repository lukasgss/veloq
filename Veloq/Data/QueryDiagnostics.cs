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
            .Select(invocation => IncludedMemberName(invocation))
            .Where(name => name is not null)
            .Select(name => rootType.GetProperty(name!, BindingFlags.Public | BindingFlags.Instance))
            .Count(property => property is not null && IsCollectionType(property.PropertyType));
    }

    private static string? IncludedMemberName(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count != 1)
        {
            return null;
        }

        ExpressionSyntax body = invocation.ArgumentList.Arguments[0].Expression switch
        {
            SimpleLambdaExpressionSyntax simple => simple.Body as ExpressionSyntax,
            ParenthesizedLambdaExpressionSyntax paren => paren.Body as ExpressionSyntax,
            _ => null,
        } ?? invocation.ArgumentList.Arguments[0].Expression;

        return body is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : null;
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
