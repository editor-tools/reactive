﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information. 

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace System.Linq
{
    public static partial class AsyncEnumerable
    {
        public static IAsyncEnumerable<TSource> Append<TSource>(this IAsyncEnumerable<TSource> source, TSource element)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (source is AppendPrependAsyncIterator<TSource> appendable)
            {
                return appendable.Append(element);
            }

            return new AppendPrepend1AsyncIterator<TSource>(source, element, true);
        }

        public static IAsyncEnumerable<TSource> Prepend<TSource>(this IAsyncEnumerable<TSource> source, TSource element)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (source is AppendPrependAsyncIterator<TSource> appendable)
            {
                return appendable.Prepend(element);
            }

            return new AppendPrepend1AsyncIterator<TSource>(source, element, false);
        }

        private abstract class AppendPrependAsyncIterator<TSource> : AsyncIterator<TSource>, IAsyncIListProvider<TSource>
        {
            protected readonly IAsyncEnumerable<TSource> source;
            protected IAsyncEnumerator<TSource> enumerator;

            protected AppendPrependAsyncIterator(IAsyncEnumerable<TSource> source)
            {
                Debug.Assert(source != null);

                this.source = source;
            }

            protected void GetSourceEnumerator()
            {
                Debug.Assert(enumerator == null);
                enumerator = source.GetAsyncEnumerator();
            }

            public abstract AppendPrependAsyncIterator<TSource> Append(TSource item);
            public abstract AppendPrependAsyncIterator<TSource> Prepend(TSource item);

            protected async Task<bool> LoadFromEnumeratorAsync()
            {
                if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    current = enumerator.Current;
                    return true;
                }

                if (enumerator != null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                    enumerator = null;
                }

                return false;
            }

            public override async Task DisposeAsync()
            {
                if (enumerator != null)
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                    enumerator = null;
                }

                await base.DisposeAsync().ConfigureAwait(false);
            }

            public abstract Task<TSource[]> ToArrayAsync(CancellationToken cancellationToken);
            public abstract Task<List<TSource>> ToListAsync(CancellationToken cancellationToken);
            public abstract Task<int> GetCountAsync(bool onlyIfCheap, CancellationToken cancellationToken);
        }

        private sealed class AppendPrepend1AsyncIterator<TSource> : AppendPrependAsyncIterator<TSource>
        {
            private readonly TSource item;
            private readonly bool appending;

            bool hasEnumerator;

            public AppendPrepend1AsyncIterator(IAsyncEnumerable<TSource> source, TSource item, bool appending)
                : base(source)
            {
                this.item = item;
                this.appending = appending;
            }

            public override AsyncIterator<TSource> Clone()
            {
                return new AppendPrepend1AsyncIterator<TSource>(source, item, appending);
            }

            protected override async Task<bool> MoveNextCore()
            {
                switch (state)
                {
                    case AsyncIteratorState.Allocated:
                        hasEnumerator = false;
                        state = AsyncIteratorState.Iterating;
                        if (!appending)
                        {
                            current = item;
                            return true;
                        }

                        goto case AsyncIteratorState.Iterating;

                    case AsyncIteratorState.Iterating:
                        if (!hasEnumerator)
                        {
                            GetSourceEnumerator();
                            hasEnumerator = true;
                        }

                        if (enumerator != null)
                        {
                            if (await LoadFromEnumeratorAsync()
                                .ConfigureAwait(false))
                            {
                                return true;
                            }

                            if (appending)
                            {
                                current = item;
                                return true;
                            }
                        }

                        break;
                }

                await DisposeAsync().ConfigureAwait(false);
                return false;
            }

            public override AppendPrependAsyncIterator<TSource> Append(TSource element)
            {
                if (appending)
                {
                    return new AppendPrependNAsyncIterator<TSource>(source, null, new SingleLinkedNode<TSource>(item, element));
                }

                return new AppendPrependNAsyncIterator<TSource>(source, new SingleLinkedNode<TSource>(item), new SingleLinkedNode<TSource>(element));
            }

            public override AppendPrependAsyncIterator<TSource> Prepend(TSource element)
            {
                if (appending)
                {
                    return new AppendPrependNAsyncIterator<TSource>(source, new SingleLinkedNode<TSource>(element), new SingleLinkedNode<TSource>(item));
                }

                return new AppendPrependNAsyncIterator<TSource>(source, new SingleLinkedNode<TSource>(item, element), null);
            }

            public override async Task<TSource[]> ToArrayAsync(CancellationToken cancellationToken)
            {
                var count = await GetCountAsync(onlyIfCheap: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (count == -1)
                {
                    return await AsyncEnumerableHelpers.ToArray(this, cancellationToken).ConfigureAwait(false);
                }

                var array = new TSource[count];
                int index;
                if (appending)
                {
                    index = 0;
                }
                else
                {
                    array[0] = item;
                    index = 1;
                }

                if (source is ICollection<TSource> sourceCollection)
                {
                    sourceCollection.CopyTo(array, index);
                }
                else
                {
                    var en = source.GetAsyncEnumerator();

                    try
                    {
                        while (await en.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                        {
                            array[index] = en.Current;
                            ++index;
                        }
                    }
                    finally
                    {
                        await en.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (appending)
                {
                    array[array.Length - 1] = item;
                }

                return array;
            }

            public override async Task<List<TSource>> ToListAsync(CancellationToken cancellationToken)
            {
                var count = await GetCountAsync(onlyIfCheap: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                var list = count == -1 ? new List<TSource>() : new List<TSource>(count);

                if (!appending)
                {
                    list.Add(item);
                }


                var en = source.GetAsyncEnumerator();

                try
                {
                    while (await en.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        list.Add(en.Current);
                    }
                }
                finally
                {
                    await en.DisposeAsync().ConfigureAwait(false);
                }

                if (appending)
                {
                    list.Add(item);
                }

                return list;
            }

            public override async Task<int> GetCountAsync(bool onlyIfCheap, CancellationToken cancellationToken)
            {
                if (source is IAsyncIListProvider<TSource> listProv)
                {
                    var count = await listProv.GetCountAsync(onlyIfCheap, cancellationToken).ConfigureAwait(false);
                    return count == -1 ? -1 : count + 1;
                }

                return !onlyIfCheap || source is ICollection<TSource> ? await source.Count(cancellationToken).ConfigureAwait(false) + 1 : -1;
            }
        }

        private sealed class SingleLinkedNode<TSource>
        {
            public SingleLinkedNode(TSource first, TSource second)
            {
                Linked = new SingleLinkedNode<TSource>(first);
                Item = second;
                Count = 2;
            }

            public SingleLinkedNode(TSource item)
            {
                Item = item;
                Count = 1;
            }

            private SingleLinkedNode(SingleLinkedNode<TSource> linked, TSource item)
            {
                Debug.Assert(linked != null);
                Linked = linked;
                Item = item;
                Count = linked.Count + 1;
            }

            public TSource Item { get; }

            public SingleLinkedNode<TSource> Linked { get; }

            public int Count { get; }

            public SingleLinkedNode<TSource> Add(TSource item) => new SingleLinkedNode<TSource>(this, item);

            public IEnumerator<TSource> GetEnumerator()
            {
                var array = new TSource[Count];
                var index = Count;
                for (var n = this; n != null; n = n.Linked)
                {
                    --index;
                    array[index] = n.Item;
                }

                Debug.Assert(index == 0);
                return ((IEnumerable<TSource>)array).GetEnumerator();
            }
        }

        private sealed class AppendPrependNAsyncIterator<TSource> : AppendPrependAsyncIterator<TSource>
        {
            private readonly SingleLinkedNode<TSource> prepended;
            private readonly SingleLinkedNode<TSource> appended;

            private SingleLinkedNode<TSource> node;

            public AppendPrependNAsyncIterator(IAsyncEnumerable<TSource> source, SingleLinkedNode<TSource> prepended, SingleLinkedNode<TSource> appended)
                : base(source)
            {
                Debug.Assert(prepended != null || appended != null);

                this.prepended = prepended;
                this.appended = appended;
            }

            public override AsyncIterator<TSource> Clone()
            {
                return new AppendPrependNAsyncIterator<TSource>(source, prepended, appended);
            }

            int mode;
            IEnumerator<TSource> appendedEnumerator;

            public override async Task DisposeAsync()
            {
                if (appendedEnumerator != null)
                {
                    appendedEnumerator.Dispose();
                    appendedEnumerator = null;
                }

                await base.DisposeAsync().ConfigureAwait(false);
            }

            protected override async Task<bool> MoveNextCore()
            {
                switch (state)
                {
                    case AsyncIteratorState.Allocated:
                        mode = 1;
                        state = AsyncIteratorState.Iterating;
                        goto case AsyncIteratorState.Iterating;

                    case AsyncIteratorState.Iterating:
                        switch (mode)
                        {
                            case 1:
                                node = prepended;
                                mode = 2;
                                goto case 2;

                            case 2:
                                if (node != null)
                                {
                                    current = node.Item;
                                    node = node.Linked;
                                    return true;
                                }

                                GetSourceEnumerator();
                                mode = 3;
                                goto case 3;

                            case 3:
                                if (await LoadFromEnumeratorAsync().ConfigureAwait(false))
                                {
                                    return true;
                                }

                                if (appended != null)
                                {
                                    appendedEnumerator = appended.GetEnumerator();
                                    mode = 4;
                                    goto case 4;
                                }

                                break;


                            case 4:
                                if (appendedEnumerator.MoveNext())
                                {
                                    current = appendedEnumerator.Current;
                                    return true;
                                }
                                break;
                        }

                        break;
                }

                await DisposeAsync().ConfigureAwait(false);
                return false;
            }

            public override AppendPrependAsyncIterator<TSource> Append(TSource item)
            {
                return new AppendPrependNAsyncIterator<TSource>(source, prepended, appended != null ? appended.Add(item) : new SingleLinkedNode<TSource>(item));
            }

            public override AppendPrependAsyncIterator<TSource> Prepend(TSource item)
            {
                return new AppendPrependNAsyncIterator<TSource>(source, prepended != null ? prepended.Add(item) : new SingleLinkedNode<TSource>(item), appended);
            }

            public override async Task<TSource[]> ToArrayAsync(CancellationToken cancellationToken)
            {
                var count = await GetCountAsync(onlyIfCheap: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (count == -1)
                {
                    return await AsyncEnumerableHelpers.ToArray(this, cancellationToken).ConfigureAwait(false);
                }

                var array = new TSource[count];
                var index = 0;
                for (var n = prepended; n != null; n = n.Linked)
                {
                    array[index] = n.Item;
                    ++index;
                }

                if (source is ICollection<TSource> sourceCollection)
                {
                    sourceCollection.CopyTo(array, index);
                }
                else
                {
                    var en = source.GetAsyncEnumerator();

                    try
                    {
                        while (await en.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                        {
                            array[index] = en.Current;
                            ++index;
                        }
                    }
                    finally
                    {
                        await en.DisposeAsync().ConfigureAwait(false);
                    }
                }

                index = array.Length;
                for (var n = appended; n != null; n = n.Linked)
                {
                    --index;
                    array[index] = n.Item;
                }

                return array;
            }

            public override async Task<List<TSource>> ToListAsync(CancellationToken cancellationToken)
            {
                var count = await GetCountAsync(onlyIfCheap: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                var list = count == -1 ? new List<TSource>() : new List<TSource>(count);
                for (var n = prepended; n != null; n = n.Linked)
                {
                    list.Add(n.Item);
                }

                var en = source.GetAsyncEnumerator();

                try
                {
                    while (await en.MoveNextAsync(cancellationToken).ConfigureAwait(false))
                    {
                        list.Add(en.Current);
                    }
                }
                finally
                {
                    await en.DisposeAsync().ConfigureAwait(false);
                }

                if (appended != null)
                {
                    using (var en2 = appended.GetEnumerator())
                    {
                        while (en2.MoveNext())
                        {
                            list.Add(en2.Current);
                        }
                    }
                }

                return list;
            }

            public override async Task<int> GetCountAsync(bool onlyIfCheap, CancellationToken cancellationToken)
            {
                if (source is IAsyncIListProvider<TSource> listProv)
                {
                    var count = await listProv.GetCountAsync(onlyIfCheap, cancellationToken).ConfigureAwait(false);
                    return count == -1 ? -1 : count + (appended == null ? 0 : appended.Count) + (prepended == null ? 0 : prepended.Count);
                }

                return !onlyIfCheap || source is ICollection<TSource> ? await source.Count(cancellationToken).ConfigureAwait(false) + (appended == null ? 0 : appended.Count) + (prepended == null ? 0 : prepended.Count) : -1;
            }
        }
    }
}
