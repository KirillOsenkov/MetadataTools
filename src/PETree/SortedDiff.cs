using System;
using System.Collections.Generic;
using System.Linq;
using ListDiff;

static class SortedDiff
{
    public static ListDiff<T, T> SortAndDiff<T>(
        this IEnumerable<T> left,
        IEnumerable<T> right,
        IComparer<T> comparer,
        bool reportSame = false)
    {
        var sortedLeft = left.ToArray();
        var sortedRight = right.ToArray();

        Array.Sort(sortedLeft, comparer);
        Array.Sort(sortedRight, comparer);

        var diff = GetSortedSequencesDiff<T>(sortedLeft, sortedRight, comparer, reportSame);
        return diff;
    }

    public static ListDiff<T, T> GetSortedSequencesDiff<T>(
        this IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        IComparer<T> comparer,
        bool reportSame = false)
    {
        var difference = new ListDiff<T, T>();
        var actions = new List<ListDiffAction<T, T>>();
        difference.Actions = actions;

        DiffSortedSequences(
            left,
            right,
            comparer,
            a =>
            {
                actions.Add(new ListDiffAction<T, T>(ListDiffActionType.Add, default, a));
            },
            r =>
            {
                actions.Add(new ListDiffAction<T, T>(ListDiffActionType.Remove, r, default));
            },
            reportSame ?
                (l, r) => actions.Add(new ListDiffAction<T, T>(ListDiffActionType.Update, l, r)) :
                null);
        difference.ContainsOnlyUpdates = actions.All(a => a.ActionType == ListDiffActionType.Update);

        return difference;
    }

    public static void DiffSortedSequences<T>(
        this IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        IComparer<T> comparer,
        Action<T> added = null,
        Action<T> removed = null,
        Action<T, T> same = null)
    {
        left ??= Array.Empty<T>();
        right ??= Array.Empty<T>();

        for (int l = 0, r = 0; l < left.Count || r < right.Count;)
        {
            if (l >= left.Count)
            {
                added?.Invoke(right[r]);
                r++;
                continue;
            }
            else if (r >= right.Count)
            {
                removed?.Invoke(left[l]);
                l++;
                continue;
            }

            var leftItem = left[l];
            var rightItem = right[r];

            int comparison = comparer.Compare(leftItem, rightItem);
            if (comparison < 0)
            {
                removed?.Invoke(leftItem);
                l++;
            }
            else if (comparison > 0)
            {
                added?.Invoke(rightItem);
                r++;
            }
            else
            {
                same?.Invoke(leftItem, rightItem);
                l++;
                r++;
            }
        }
    }
}