using System;
using System.Collections.Generic;
using GuiLabs.FileFormat.PE.Dotnet;
using ListDiff;

namespace GuiLabs.FileFormat;

public class Difference
{
    private List<Node> added = new();
    private List<Node> removed = new();
    private List<(Node left, Node right)> changed = new();

    public IReadOnlyList<Node> Added => added;
    public IReadOnlyList<Node> Removed => removed;
    public IReadOnlyList<(Node left, Node right)> Changed => changed;

    public static Difference Diff(Node leftNode, Node rightNode)
    {
        var difference = new Difference();
        Diff(leftNode, rightNode, difference);
        return difference;
    }

    public static void Diff(Node leftNode, Node rightNode, Difference difference)
    {
        var leftChildren = leftNode.Children;
        var rightChildren = rightNode.Children;

        if (leftNode is EmbeddedPdb leftPdb && rightNode is EmbeddedPdb rightPdb)
        {
            Diff(leftPdb.Metadata, rightPdb.Metadata, difference);
        }

        if (leftChildren.Count == 0 && rightChildren.Count == 0)
        {
            if (leftNode.Length != rightNode.Length)
            {
                difference.ChangedNode(leftNode, rightNode);
            }
            else if (leftNode.GetType() != rightNode.GetType())
            {
                difference.ChangedNode(leftNode, rightNode);
            }
            else
            {
                for (int i = 0; i < leftNode.Length; i++)
                {
                    var leftByte = leftNode.Buffer.ReadByte(leftNode.Start + i);
                    var rightByte = rightNode.Buffer.ReadByte(rightNode.Start + i);
                    if (leftByte != rightByte)
                    {
                        difference.ChangedNode(leftNode, rightNode);
                        return;
                    }
                }
            }

            return;
        }

        var diff = new ListDiff<Node, Node>(leftChildren, rightChildren, (l, r) => StringComparer.Ordinal.Equals(l.Text, r.Text));

        foreach (var action in diff.Actions)
        {
            if (action.ActionType == ListDiffActionType.Update)
            {
                Diff(action.SourceItem, action.DestinationItem, difference);
            }
            else if (action.ActionType == ListDiffActionType.Add)
            {
                difference.AddedNode(action.DestinationItem);
            }
            else
            {
                difference.RemovedNode(action.SourceItem);
            }
        }
    }

    private void ChangedNode(Node leftNode, Node rightNode)
    {
        changed.Add((leftNode, rightNode));
    }

    private void RemovedNode(Node node)
    {
        removed.Add(node);
    }

    private void AddedNode(Node node)
    {
        added.Add(node);
    }
}