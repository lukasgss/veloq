using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Veloq.Data;

internal static class QueryDiagnostics
{
    internal static bool ContainsAsSplitQuery(string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression.TrimEnd().TrimEnd(';'));

        return syntax.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                               memberAccess.Name.Identifier.ValueText == "AsSplitQuery");
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
