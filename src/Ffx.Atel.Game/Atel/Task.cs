// SPDX-License-Identifier: MIT

using System;
using System.Runtime.CompilerServices;

namespace Ffx.Atel;

// The task-like types of requests between workers (docs/plan.md, "Requests between workers"):
// `async Task NameAsync()` declares a requestable entry, `_ = W.NameAsync();` queues it (req),
// `await W.NameAsync()` / `.Finished` waits until it ends (reqe), `.Started` until it begins
// (reqw). They only describe the game to the C# compiler; nothing here runs.

/// <summary>A request to another worker's entry.</summary>
[AsyncMethodBuilder(typeof(TaskBuilder))]
public sealed class Task {
    private Task() { }

    /// <summary>Completes when the requested entry has ended (<c>reqe</c>).</summary>
    public Awaitable Finished => throw Runtime.NotRunnable();

    /// <summary>Completes when the requested entry has begun (<c>reqw</c>).</summary>
    public Awaitable Started => throw Runtime.NotRunnable();

    public Awaitable.Awaiter GetAwaiter() => throw Runtime.NotRunnable();
}

/// <summary>A request to another worker's entry that returns a value.</summary>
[AsyncMethodBuilder(typeof(TaskBuilder<>))]
public sealed class Task<T> {
    private Task() { }

    /// <summary>Completes with the result when the requested entry has ended.</summary>
    public Awaitable<T> Finished => throw Runtime.NotRunnable();

    /// <summary>Completes when the requested entry has begun (<c>reqw</c>).</summary>
    public Awaitable Started => throw Runtime.NotRunnable();

    public Awaitable<T>.Awaiter GetAwaiter() => throw Runtime.NotRunnable();
}

/// <summary>Something to <c>await</c>.</summary>
public readonly struct Awaitable {
    public Awaiter GetAwaiter() => throw Runtime.NotRunnable();

    public readonly struct Awaiter : INotifyCompletion {
        public bool IsCompleted => throw Runtime.NotRunnable();
        public void GetResult() => throw Runtime.NotRunnable();
        public void OnCompleted(Action continuation) => throw Runtime.NotRunnable();
    }
}

/// <summary>Something to <c>await</c> for a value.</summary>
public readonly struct Awaitable<T> {
    public Awaiter GetAwaiter() => throw Runtime.NotRunnable();

    public readonly struct Awaiter : INotifyCompletion {
        public bool IsCompleted => throw Runtime.NotRunnable();
        public T GetResult() => throw Runtime.NotRunnable();
        public void OnCompleted(Action continuation) => throw Runtime.NotRunnable();
    }
}

/// <summary>The builder C# uses for <c>async Task</c> methods.</summary>
public struct TaskBuilder {
    public static TaskBuilder Create() => throw Runtime.NotRunnable();
    public readonly Task Task => throw Runtime.NotRunnable();
    public readonly void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => throw Runtime.NotRunnable();
    public readonly void SetStateMachine(IAsyncStateMachine stateMachine) => throw Runtime.NotRunnable();
    public readonly void SetResult() => throw Runtime.NotRunnable();
    public readonly void SetException(Exception exception) => throw Runtime.NotRunnable();
    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine => throw Runtime.NotRunnable();
    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine => throw Runtime.NotRunnable();
}

/// <summary>The builder C# uses for <c>async Task&lt;T&gt;</c> methods.</summary>
public struct TaskBuilder<T> {
    public static TaskBuilder<T> Create() => throw Runtime.NotRunnable();
    public readonly Task<T> Task => throw Runtime.NotRunnable();
    public readonly void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => throw Runtime.NotRunnable();
    public readonly void SetStateMachine(IAsyncStateMachine stateMachine) => throw Runtime.NotRunnable();
    public readonly void SetResult(T result) => throw Runtime.NotRunnable();
    public readonly void SetException(Exception exception) => throw Runtime.NotRunnable();
    public readonly void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine => throw Runtime.NotRunnable();
    public readonly void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine => throw Runtime.NotRunnable();
}
