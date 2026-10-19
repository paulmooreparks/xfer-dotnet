using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ParksComputing.XferLang.Tests.Harness;

/// <summary>
/// Helpers for the suite's tagged JSON form (suite/tests/README.md). Comparison with
/// <see cref="JsonNode.DeepEquals"/> ignores the order of object members, which the specification
/// makes normative, so <see cref="MemberOrder"/> is compared as well.
/// </summary>
internal static class Tagged
{
    /// <summary>
    /// The member names of every object in the tree, in the order a depth-first walk meets the
    /// objects, each object's names in order. Two trees have the same member order exactly when
    /// these lists are equal. Iterative, as every walk in this project must be.
    /// </summary>
    public static IReadOnlyList<string> MemberOrder(JsonNode root)
    {
        var order = new List<string>();
        var stack = new Stack<JsonNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            JsonNode node = stack.Pop();
            if (node is JsonObject obj)
            {
                order.Add("{");
                var children = new List<JsonNode>();
                foreach (KeyValuePair<string, JsonNode?> member in obj)
                {
                    order.Add(member.Key);
                    if (member.Value is not null)
                    {
                        children.Add(member.Value);
                    }
                }

                for (int i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
            else if (node is JsonArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is { } item)
                    {
                        stack.Push(item);
                    }
                }
            }
        }

        return order;
    }

    /// <summary>Structural equality of two tagged trees, including member order.</summary>
    public static bool Equal(JsonNode? expected, JsonNode? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        IReadOnlyList<string> a = MemberOrder(expected);
        IReadOnlyList<string> b = MemberOrder(actual);
        if (!JsonNode.DeepEquals(expected, actual) || a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
