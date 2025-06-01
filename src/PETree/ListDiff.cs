// Taken from https://github.com/praeclarum/ListDiff/blob/af9dfcf94c2c8cd43116e0d7ef95829990b49ca3/ListDiff/ListDiff.cs
//
// Copyright (c) Krueger Systems, Inc.
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;

namespace ListDiff
{
    /// <summary>
    /// The type of <see cref="ListDiffAction{S,D}"/>.
    /// </summary>
    public enum ListDiffActionType
    {
        /// <summary>
        /// Update the SourceItem to make it like the DestinationItem
        /// </summary>
        Update,
        /// <summary>
        /// Add the DestinationItem
        /// </summary>
        Add,
        /// <summary>
        /// Remove the SourceItem
        /// </summary>
        Remove,
    }

    /// <summary>
    /// A <see cref="ListDiff{S,D}"/> action that can be one of: Update, Add, or Remove.
    /// </summary>
    /// <typeparam name="S">The type of the source list elements</typeparam>
    /// <typeparam name="D">The type of the destination list elements</typeparam>
    public struct ListDiffAction<S, D>
    {
        /// <summary>
        /// The action to take in order to merge the source list into the destination.
        /// Can be either Update, Add, or Remove.
        /// </summary>
        public ListDiffActionType ActionType;

        /// <summary>
        /// The item from the source list that should be Updated or Removed (depending on <see cref="ActionType"/>).
        /// </summary>
        public S? SourceItem;

        /// <summary>
        /// The item from the destination list that should be Updated or Added (depending on <see cref="ActionType"/>).
        /// </summary>
        public D? DestinationItem;

        /// <summary>
        /// Initializes a new instance of the <see cref="T:ListDiff.ListDiffAction`2"/> class.
        /// </summary>
        /// <param name="type">The <see cref="ActionType"/></param>
        /// <param name="source">The <see cref="SourceItem"/></param>
        /// <param name="dest">The <see cref="DestinationItem"/></param>
        public ListDiffAction(ListDiffActionType type, S? source, D? dest)
        {
            ActionType = type;
            SourceItem = source;
            DestinationItem = dest;
        }

        /// <summary>
        /// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:ListDiff.ListDiffAction`2"/>.
        /// </summary>
        /// <returns>A <see cref="T:System.String"/> that represents the current <see cref="T:ListDiff.ListDiffAction`2"/>.</returns>
        public override string ToString()
        {
            return ActionType switch
            {
                ListDiffActionType.Update => $"Update {SourceItem} to {DestinationItem}",
                ListDiffActionType.Add => $"Add {DestinationItem}",
                _ => $"Remove {SourceItem}",
            };
        }
    }

    /// <summary>
    /// Finds a diff between two lists (that contain possibly different types).
    /// <see cref="Actions"/> are generated such that the order of items in the
    /// destination list is preserved.
    /// The algorithm is from: http://en.wikipedia.org/wiki/Longest_common_subsequence_problem
    /// </summary>
    /// <typeparam name="S">The type of the source list elements</typeparam>
    /// <typeparam name="D">The type of the destination list elements</typeparam>
    public class ListDiff<S, D>
    {
        /// <summary>
        /// The actions needed to transform a source list to a destination list.
        /// </summary>
        public List<ListDiffAction<S, D>> Actions { get; set; }

        /// <summary>
        /// Whether the <see cref="Actions"/> only contain Update actions
        /// (no Adds or Removes).
        /// </summary>
        public bool ContainsOnlyUpdates { get; set; }

        public ListDiff()
        {
        }

        /// <summary>
        /// Construct a new diff that contains all the <see cref="Actions"/> needed to convert
        /// the <see cref="P:sources"/> sequence into the <see cref="P:destinations"/> sequence.
        /// The S.Equals() method is used to match source items to destination items.
        /// </summary>
        /// <param name="source">Source item sequence</param>
        /// <param name="destination">Destination item sequence</param>
        public ListDiff(IEnumerable<S> source, IEnumerable<D> destination)
            : this(source, destination, (a, b) => a!.Equals(b))
        {
        }

        /// <summary>
        /// Construct a new diff that contains all the <see cref="Actions"/> needed to convert
        /// the <see cref="P:sources"/> sequence into the <see cref="P:destinations"/> sequence.
        /// You must provide your own comparison method <see cref="P:match"/> that
        /// will be used to match source items to destination items.
        /// </summary>
        /// <param name="source">Source item sequence</param>
        /// <param name="destination">Destination item sequence</param>
        /// <param name="match">Predicate used to match source and destination items</param>
        public ListDiff(IEnumerable<S> source, IEnumerable<D> destination, Func<S, D, bool> match)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (match == null)
                throw new ArgumentNullException(nameof(match));

            IList<S> x = source as IList<S> ?? source.ToArray();
            IList<D> y = destination as IList<D> ?? destination.ToArray();

            Actions = new List<ListDiffAction<S, D>>();

            var m = x.Count;
            var n = y.Count;

            var start = 0;

            while (start < m && start < n && match(x[start], y[start]))
            {
                start++;
            }

            while (start < m && start < n && match(x[m - 1], y[n - 1]))
            {
                m--;
                n--;
            }

            //
            // Construct the C matrix
            //
            var c = new int[m - start + 1, n - start + 1];
            for (var i = 1; i <= m - start; i++)
            {
                for (var j = 1; j <= n - start; j++)
                {
                    if (match(x[i - 1], y[j - 1]))
                    {
                        c[i, j] = c[i - 1, j - 1] + 1;
                    }
                    else
                    {
                        c[i, j] = Math.Max(c[i, j - 1], c[i - 1, j]);
                    }
                }
            }

            //
            // Generate the actions
            //
            for (int i = 0; i < start; i++)
            {
                Actions.Add(new ListDiffAction<S, D>(ListDiffActionType.Update, x[i], y[i]));
            }

            ContainsOnlyUpdates = true;
            GenDiff(c, x, y, start, m, n, match);

            for (int i = 0; i < x.Count - m; i++)
            {
                Actions.Add(new ListDiffAction<S, D>(ListDiffActionType.Update, x[m + i], y[n + i]));
            }
        }

        struct GenDiffFrame
        {
            public int I;
            public int J;
            public int State;
        }

        void GenDiff(int[,] c, IList<S> x, IList<D> y, int start, int ii, int ij, Func<S, D, bool> match)
        {
            var stack = new List<GenDiffFrame> {
                new GenDiffFrame { I = ii, J = ij }
            };
            while (stack.Count > 0)
            {
                var fp = stack.Count - 1;
                var frame = stack[fp];
                var fi = frame.I;
                var fj = frame.J;
                switch (frame.State)
                {
                    case 0:
                        if (fi > start && fj > start && match(x[fi - 1], y[fj - 1]))
                        {
                            frame.State = 1;
                            stack[fp] = frame;
                            stack.Add(new GenDiffFrame { I = fi - 1, J = fj - 1 });
                        }
                        else
                        {
                            if (fj > start && (fi == start || c[fi - start, fj - start - 1] >= c[fi - start - 1, fj - start]))
                            {
                                frame.State = 2;
                                stack[fp] = frame;
                                stack.Add(new GenDiffFrame { I = fi, J = fj - 1 });
                                ContainsOnlyUpdates = false;
                            }
                            else if (fi > start && (fj == start || c[fi - start, fj - start - 1] < c[fi - start - 1, fj - start]))
                            {
                                frame.State = 3;
                                stack[fp] = frame;
                                stack.Add(new GenDiffFrame { I = fi - 1, J = fj });
                                ContainsOnlyUpdates = false;
                            }
                            else
                            {
                                stack.RemoveAt(fp);
                            }
                        }
                        break;
                    case 1:
                        Actions.Add(new ListDiffAction<S, D>(ListDiffActionType.Update, x[fi - 1], y[fj - 1]));
                        stack.RemoveAt(fp);
                        break;
                    case 2:
                        Actions.Add(new ListDiffAction<S, D>(ListDiffActionType.Add, default, y[fj - 1]));
                        stack.RemoveAt(fp);
                        break;
                    case 3:
                        Actions.Add(new ListDiffAction<S, D>(ListDiffActionType.Remove, x[fi - 1], default));
                        stack.RemoveAt(fp);
                        break;
                    default:
                        throw new InvalidOperationException("Invalid diff generator state");
                }
            }
        }

        /// <summary>
        /// Returns a <see cref="T:System.String"/> that represents the current <see cref="T:ListDiff.ListDiff`2"/>.
        /// </summary>
        /// <returns>A <see cref="T:System.String"/> that represents the current <see cref="T:ListDiff.ListDiff`2"/>.</returns>
        public override string ToString()
        {
            var sb = new StringBuilder();

            foreach (var action in Actions)
            {
                if (action.ActionType == ListDiffActionType.Update)
                {
                    sb.Append(Convert.ToString(action.SourceItem));
                }
                else if (action.ActionType == ListDiffActionType.Add)
                {
                    sb.Append("+(");
                    sb.Append(Convert.ToString(action.DestinationItem));
                    sb.Append(")");
                }
                else if (action.ActionType == ListDiffActionType.Remove)
                {
                    sb.Append("-(");
                    sb.Append(Convert.ToString(action.SourceItem));
                    sb.Append(")");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Returns two sets of operations to perform on the source list
        /// in order to turn it into the destination list.
        /// You should apply all of the removes first followed by the adds
        /// in order for the indexing to be correct.
        /// </summary>
        /// <returns>An array of the indices of items to remove and an array of items to add.</returns>
        public ((int Index, int Count)[] Removes, (int Index, D[] Items)[] Adds) GetRemovesAndAdds()
        {
            var rems = new List<int>();
            var adds = new List<(int Index, D Item)>();

            //
            // Calculate indices assuming all removes
            // happend before the adds
            //
            var remIndex = 0;
            var addIndex = 0;
            foreach (var action in Actions)
            {
                switch (action.ActionType)
                {
                    case ListDiffActionType.Update:
                        remIndex++;
                        addIndex++;
                        break;
                    case ListDiffActionType.Add:
                        adds.Add((addIndex, action.DestinationItem!));
                        addIndex++;
                        break;
                    case ListDiffActionType.Remove:
                        rems.Add(remIndex);
                        break;
                }
            }

            //
            // Group the removes and adds into ranges
            //
            var orems = new List<(int Index, int Count)>();
            var rn = rems.Count;
            for (var ri = 0; ri < rn;)
            {
                var index = rems[ri];
                var sri = ri;
                ri++;
                while (ri < rn && rems[ri] == index)
                {
                    ri++;
                }
                orems.Add((index, ri - sri));
            }
            var oadds = new List<(int Index, D[] Items)>();
            var an = adds.Count;
            for (var ai = 0; ai < an;)
            {
                var (index, item) = adds[ai];
                var items = new List<D> { item };
                ai++;
                var eindex = index + 1;
                while (ai < an && adds[ai].Index == eindex)
                {
                    items.Add(adds[ai].Item);
                    eindex++;
                    ai++;
                }
                oadds.Add((index, items.ToArray()));
            }

            return (orems.ToArray(), oadds.ToArray());
        }

        /// <summary>
        /// Returns two sets of operations (removes and adds) to perform on the source list
        /// in order to turn it into the destination list.
        /// The indices of the removes are relative to the source list.
        /// The indices of the adds are relative to the source list with all
        /// of the removes applied.
        /// <para>
        /// It should be noted that these indices do not account for previous
        /// operations in their set. All removes are relative to the source collection
        /// (ignoring other removes) and all adds are relative to the collection
        /// after the deletes (ignoring other adds).
        /// This allows the order of any removes or any adds to be arbitrary
        /// (but all the removes have to happen before any of the adds).
        /// </para>
        /// <para>
        /// This is weird, I know. But very useful when updating NSCollectionViews.
        /// </para>
        /// </summary>
        /// <returns>An array of the indices of items to remove and an array of items to add.</returns>
        public ((int Index, int Count)[] Removes, (int Index, D[] Items)[] Adds) GetBatchRemovesAndAdds()
        {
            var rems = new List<int>();
            var adds = new List<(int Index, D Item)>();

            //
            // Calculate indices assuming all removes
            // happend before the adds
            //
            var remIndex = 0;
            var addIndex = 0;
            foreach (var action in Actions)
            {
                switch (action.ActionType)
                {
                    case ListDiffActionType.Update:
                        remIndex++;
                        addIndex++;
                        break;
                    case ListDiffActionType.Add:
                        adds.Add((addIndex, action.DestinationItem!));
                        addIndex++;
                        break;
                    case ListDiffActionType.Remove:
                        rems.Add(remIndex);
                        remIndex++;
                        break;
                }
            }

            //
            // Group the removes and adds into ranges
            //
            var orems = new List<(int Index, int Count)>();
            var rn = rems.Count;
            for (var ri = 0; ri < rn;)
            {
                var index = rems[ri];
                var sri = ri;
                ri++;
                var eindex = index + 1;
                while (ri < rn && rems[ri] == eindex)
                {
                    eindex++;
                    ri++;
                }
                orems.Add((index, ri - sri));
            }
            var oadds = new List<(int Index, D[] Items)>();
            var an = adds.Count;
            for (var ai = 0; ai < an;)
            {
                var (index, item) = adds[ai];
                var items = new List<D> { item };
                ai++;
                var eindex = index + 1;
                while (ai < an && adds[ai].Index == eindex)
                {
                    items.Add(adds[ai].Item);
                    eindex++;
                    ai++;
                }
                oadds.Add((index, items.ToArray()));
            }

            return (orems.ToArray(), oadds.ToArray());
        }
    }

    /// <summary>
    /// Extensions for IEnumerable and IList to generate diffs and to merge lists.
    /// </summary>
    public static class ListDiffExtensions
    {
        /// <summary>
        /// Merges <see cref="P:source"/> into <see cref="P:destination"/> by first creating a diff
        /// and then changing <see cref="P:source"/> in place by calling Insert and RemoveAt.
        /// </summary>
        /// <returns>The diff used to merge.</returns>
        /// <param name="source">Source item list</param>
        /// <param name="destination">Destination item sequence</param>
        /// <param name="match">Predicate used to match source and destination items</param>
        /// <typeparam name="T">The type of items in the list</typeparam>
        public static ListDiff<T, T> MergeInto<T>(this IList<T> source, IEnumerable<T> destination, Func<T, T, bool> match)
        {
            var diff = new ListDiff<T, T>(source, destination, match);

            var p = 0;

            foreach (var a in diff.Actions)
            {
                if (a.ActionType == ListDiffActionType.Add)
                {
                    source.Insert(p, a.DestinationItem!);
                    p++;
                }
                else if (a.ActionType == ListDiffActionType.Remove)
                {
                    source.RemoveAt(p);
                }
                else
                {
                    p++;
                }
            }

            return diff;
        }

        /// <summary>
        /// Merges <see cref="P:source"/> into <see cref="P:destination"/> by first creating a diff
        /// and then changing <see cref="P:source"/> in place by calling Insert and RemoveAt.
        /// The <see cref="P:create"/> function is used to create new <see cref="T:TSource"/> items
        /// from <see cref="T:TDestination"/> items.
        /// The <see cref="P:update"/> action is called whenever items already match between the
        /// source and destination.
        /// The <see cref="P:delete"/> action is called whenever items are removed from the source list.
        /// </summary>
        /// <returns>The diff used to merge.</returns>
        /// <param name="source">Source item list</param>
        /// <param name="destination">Destination item sequence</param>
        /// <param name="match">Predicate used to match source and destination items</param>
        /// <param name="create">Function used to create new source items from destination items when they need to be inserted</param>
        /// <param name="update">Action invoked when source and destination items match</param>
        /// <param name="delete">Action invoked when source items are removed</param>
        /// <typeparam name="TSource">The type of items in the source list</typeparam>
        /// <typeparam name="TDestination">The type of items in the destination sequence</typeparam>
        public static ListDiff<TSource, TDestination> MergeInto<TSource, TDestination>(this IList<TSource> source, IEnumerable<TDestination> destination, Func<TSource, TDestination, bool> match, Func<TDestination, TSource> create, Action<TSource, TDestination> update, Action<TSource> delete)
        {
            var diff = new ListDiff<TSource, TDestination>(source, destination, match);

            var p = 0;

            foreach (var a in diff.Actions)
            {
                if (a.ActionType == ListDiffActionType.Add)
                {
                    source.Insert(p, create(a.DestinationItem!));
                    p++;
                }
                else if (a.ActionType == ListDiffActionType.Remove)
                {
                    delete(a.SourceItem!);
                    source.RemoveAt(p);
                }
                else
                {
                    update(a.SourceItem!, a.DestinationItem!);
                    p++;
                }
            }

            return diff;
        }

        /// <summary>
        /// Construct a new diff that contains all the actions needed to convert
        /// the <see cref="P:source"/> sequence into the <see cref="P:destination"/> sequence.
        /// The TSource.Equals() method is used to match source items to destination items.
        /// </summary>
        /// <param name="source">Source item sequence</param>
        /// <param name="destination">Destination item sequence</param>
        public static ListDiff<TSource, TDestination> Diff<TSource, TDestination>(this IEnumerable<TSource> source, IEnumerable<TDestination> destination)
        {
            return new ListDiff<TSource, TDestination>(source, destination);
        }

        /// <summary>
        /// Construct a new diff that contains all the actions needed to convert
        /// the <see cref="P:source"/> sequence into the <see cref="P:destination"/> sequence.
        /// You must provide your own comparison method <see cref="P:match"/> that
        /// will be used to match source items to destination items.
        /// </summary>
        /// <param name="source">Source item sequence</param>
        /// <param name="destination">Destination item sequence</param>
        /// <param name="match">Predicate used to match source and destination items</param>
        public static ListDiff<TSource, TDestination> Diff<TSource, TDestination>(this IEnumerable<TSource> source, IEnumerable<TDestination> destination, Func<TSource, TDestination, bool> match)
        {
            return new ListDiff<TSource, TDestination>(source, destination, match);
        }
    }
}