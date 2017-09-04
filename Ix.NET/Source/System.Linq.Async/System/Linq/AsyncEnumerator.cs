﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information. 

using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace System.Collections.Generic
{
    /// <summary>
    /// Provides a set of extension methods for <see cref="IAsyncEnumerator{T}"/>.
    /// </summary>
    public static class AsyncEnumerator
    {
        public static IAsyncEnumerator<T> Create<T>(Func<Task<bool>> moveNext, Func<T> current, Func<Task> dispose)
        {
            if (moveNext == null)
                throw new ArgumentNullException(nameof(moveNext));

            // Note: Many methods pass null in for the second two params. We're assuming
            // That the caller is responsible and knows what they're doing
            return new AnonymousAsyncIterator<T>(moveNext, current, dispose);
        }

        internal static IAsyncEnumerator<T> Create<T>(Func<TaskCompletionSource<bool>, Task<bool>> moveNext, Func<T> current, Func<Task> dispose)
        {
            return new AnonymousAsyncIterator<T>(
                async () =>
                {
                    var tcs = new TaskCompletionSource<bool>();

                    return await moveNext(tcs).ConfigureAwait(false);
                },
                current,
                dispose
            );
        }

        /// <summary>
        /// Advances the enumerator to the next element in the sequence, returning the result asynchronously.
        /// </summary>
        /// <param name="source">The enumerator to advance.</param>
        /// <param name="cancellationToken">Cancellation token that can be used to cancel the operation.</param>
        /// <returns>
        /// Task containing the result of the operation: true if the enumerator was successfully advanced
        /// to the next element; false if the enumerator has passed the end of the sequence.
        /// </returns>
        public static Task<bool> MoveNextAsync<T>(this IAsyncEnumerator<T> source, CancellationToken cancellationToken)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            cancellationToken.ThrowIfCancellationRequested();

            return source.MoveNextAsync();
        }

        private sealed class AnonymousAsyncIterator<T> : AsyncIterator<T>
        {
            private readonly Func<T> currentFunc;
            private readonly Func<Task> dispose;
            private readonly Func<Task<bool>> moveNext;

            public AnonymousAsyncIterator(Func<Task<bool>> moveNext, Func<T> currentFunc, Func<Task> dispose)
            {
                Debug.Assert(moveNext != null);

                this.moveNext = moveNext;
                this.currentFunc = currentFunc;
                this.dispose = dispose;

                // Explicit call to initialize enumerator mode
                GetAsyncEnumerator();
            }

            public override AsyncIterator<T> Clone()
            {
                throw new NotSupportedException("AnonymousAsyncIterator cannot be cloned. It is only intended for use as an iterator.");
            }

            public override async Task DisposeAsync()
            {
                if (dispose != null)
                {
                    await dispose().ConfigureAwait(false);
                }

                await base.DisposeAsync().ConfigureAwait(false);
            }

            protected override async Task<bool> MoveNextCore()
            {
                switch (state)
                {
                    case AsyncIteratorState.Allocated:
                        state = AsyncIteratorState.Iterating;
                        goto case AsyncIteratorState.Iterating;

                    case AsyncIteratorState.Iterating:
                        if (await moveNext().ConfigureAwait(false))
                        {
                            current = currentFunc();
                            return true;
                        }

                        await DisposeAsync().ConfigureAwait(false);
                        break;
                }

                return false;
            }
        }
    }
}
