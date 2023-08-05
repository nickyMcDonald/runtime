﻿//*
#define GC_COLLECT
//*/
//*
#define GC_FINALIZERS
//*/
//*
#define GC_MEMORY_PRESSURE
//*/
/*
#define LOCK_CONTENTION //BUG!? Possibly deadlocks Linux x64
//*/
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
#if LOCK_CONTENTION
using System.Threading;
using System.Threading.Tasks;
#endif //LOCK_CONTENTION

using System.Buffers; // Copied from https://github.com/dotnet/runtime/pull/86370 by @davmason:
{
    // Access ArrayPool.Shared.Rent() before the test to avoid the deadlock reported
    // in https://github.com/dotnet/runtime/issues/86233. This is a real issue,
    // but only seen if you have a short lived EventListener and create EventSources
    // in your OnEventWritten callback so we don't expect customers to hit it.
    byte[] localBuffer = ArrayPool<byte>.Shared.Rent(10);
    Console.WriteLine($"buffer length={localBuffer.Length}");
}

const EventKeywords keywords = EventKeywords.None
#if GC_COLLECT || GC_FINALIZERS || GC_MEMORY_PRESSURE
    | (EventKeywords)0x1
#endif
#if LOCK_CONTENTION
    | (EventKeywords)0x4000
#endif //LOCK_CONTENTION
    ;
using Listener informational = Listener.StartNew("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, keywords);
using Listener verbose = Listener.StartNew("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, keywords);
using Listener logAlways = Listener.StartNew("Microsoft-Windows-DotNETRuntime", EventLevel.LogAlways, keywords);

#if GC_COLLECT
for (int i = 0; i < ushort.MaxValue; i++)
{
    GC.KeepAlive(new());
}
GC.Collect();
#endif //GC_COLLECT

#if GC_FINALIZERS
GC.WaitForPendingFinalizers();
#endif //GC_FINALIZERS

#if GC_MEMORY_PRESSURE
GC.AddMemoryPressure(1L);
GC.RemoveMemoryPressure(1L);
#endif //GC_MEMORY_PRESSURE

#if LOCK_CONTENTION
for (int i = 1; i <= 0x40; i++)
{
    object lockObject = new();
    Task task = new(() =>
    {
        lock (lockObject)
        {
            Thread.Sleep(0);
        }
    });
    lock (lockObject)
    {
        task.Start();
        while (Monitor.LockContentionCount < i)
        {
            Thread.Sleep(0);
        }
    }
    task.Wait();
}
#endif //LOCK_CONTENTION

informational.DumpEvents();
verbose.DumpEvents();
logAlways.DumpEvents();

IEnumerable<Listener.Event> informationalEvents = new Listener.Event[]
{
#if GC_COLLECT

#if false //BUG!? Shouldn’t events be consistent across all platforms?
    new(2,   EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCEnd_V1"),// Linux only?
    new(4,   EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCHeapStats_V2"),// Linux only?
    new(39,  EventLevel.Informational, (EventKeywords)0x0000F00003F00003, "GCDynamicEvent")// Linux arm64 only?,
    new(202, EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCMarkWithType"),// Linux only?
    new(204, EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCPerHeapHistory_V3"),// Linux only?
    new(205, EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCGlobalHeapHistory_V4"),// Linux only?
#endif

    new(3,   EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCRestartEEEnd_V1"),
    new(7,   EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCRestartEEBegin_V1"),
    new(8,   EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCSuspendEEEnd_V1"),
    new(9,   EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCSuspendEEBegin_V1"),
#endif //GC_COLLECT
#if GC_FINALIZERS
    new(13,  EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCFinalizersEnd_V1"),
    new(14,  EventLevel.Informational, (EventKeywords)0x0000F00000000001, "GCFinalizersBegin_V1"),
#endif //GC_FINALIZERS
#if LOCK_CONTENTION
    new(81,  EventLevel.Informational, (EventKeywords)0x0000F00000004000, "ContentionStart_V1"),// Inconsistent name?

#if false //BUG!? Shouldn’t events be consistent across all platforms?
    new(90,  EventLevel.Informational, (EventKeywords)0x0000F00000004000, "ContentionLockCreated"),// Windows/Linux/osx64 only?
#endif

    new(91,  EventLevel.Informational, (EventKeywords)0x0000F00000004000, "ContentionStop_V1"),// Inconsistent name?
#endif //LOCK_CONTENTION
};
IEnumerable<Listener.Event> verboseEvents = new Listener.Event[]
{
#if GC_COLLECT

#if false //BUG!? Shouldn’t events be consistent across all platforms?
    new(10,  EventLevel.Verbose,       (EventKeywords)0x0000F00000000001, "GCAllocationTick_V4"),// Linux only?
    new(33,  EventLevel.Verbose,       (EventKeywords)0x0000F00000000001, "PinObjectAtGCTime"),// Linux only?
#endif

    new(29,  EventLevel.Verbose,       (EventKeywords)0x0000F00000000001, "FinalizeObject"),

#endif //GC_COLLECT
#if GC_MEMORY_PRESSURE
    new(200, EventLevel.Verbose,       (EventKeywords)0x0000F00000000001, "IncreaseMemoryPressure"),
    new(201, EventLevel.Verbose,       (EventKeywords)0x0000F00000000001, "DecreaseMemoryPressure"),
#endif //GC_MEMORY_PRESSURE
};
IEnumerable<Listener.Event> allEvents = informationalEvents.Concat(verboseEvents);

try
{
    ThrowIfEventsFound(informational, verboseEvents);
    ThrowIfEventsNotFound(informational, informationalEvents);
    ThrowIfEventsNotFound(verbose, allEvents);
    ThrowIfEventsNotFound(logAlways, allEvents);
}
finally
{
    informational.DumpEvents();
    verbose.DumpEvents();
    logAlways.DumpEvents();
}

return 100;

static void ThrowIfEventsFound(Listener listener, IEnumerable<Listener.Event> events)
{
    foreach (Listener.Event e in events)
    {
        if (listener.Contains(e))
        {
            throw new Exception($"{listener} contains {e}");
        }
    }
}
static void ThrowIfEventsNotFound(Listener listener, IEnumerable<Listener.Event> events)
{
    foreach (Listener.Event e in events)
    {
        if (!listener.Contains(e))
        {
            throw new Exception($"{listener} doesn't contain {e}");
        }
    }
}

internal sealed class Listener : EventListener, IReadOnlySet<Listener.Event>
{
    public string SourceName { get; private set; } = "";
    public EventLevel Level { get; private set; } = default;
    public EventKeywords Keywords { get; private set; } = default;
    public int Count => events.Count;

    private static string nextSourceName = "";
    private static EventLevel nextLevel = default;
    private static EventKeywords nextKeywords = default;
    private readonly ConcurrentDictionary<Event, int> events = new();
    private readonly SortedSet<Event> keys = new();

    public readonly struct Event : IComparable<Event>
    {
        public readonly int Id;
        public readonly EventLevel Level;
        public readonly EventKeywords Keywords;
        public readonly string Name = "";
        public Event(int id, EventLevel level, EventKeywords keywords, string name)
        {
            Id = id;
            Level = level;
            Keywords = keywords;
            Name = name;
        }
        public int CompareTo(Event other) => Id - other.Id;

        #region BUG!? Shouldn’t event names be consistent across all platforms?
        public override bool Equals([NotNullWhen(true)] object? obj) => GetHashCode() == obj?.GetHashCode();
        public override int GetHashCode() => (Id, Level, Keywords).GetHashCode();
        #endregion

        public override string ToString() => $"{GetType().Name}({Id}, {Level}, {Keywords:x}, {Name})";
    }

    private Listener() => keys.UnionWith(events.Keys);

    public static Listener StartNew(string sourceName, EventLevel level, EventKeywords keywords)
    {
        nextSourceName = sourceName;
        nextLevel = level;
        nextKeywords = keywords;
        return new();
    }
    public void DumpEvents()
    {
        Console.WriteLine($"\n{this} {Count}\n{{");
        Span<char> chars = stackalloc char[0x80];
        foreach (Event e in this)
        {
            chars.Fill(' ');
            "    new(".TryCopyTo(chars);
            $"{e.Id},".TryCopyTo(chars[8..]);
            $"{e.Level.GetType().Name}.{e.Level},".TryCopyTo(chars[13..]);
            $"({e.Keywords.GetType().Name})0x{e.Keywords:x}, \"{e.Name}\"),".TryCopyTo(chars[39..]);
            Console.WriteLine(chars.TrimEnd(' ').ToString());
        }
        Console.WriteLine("};");
    }
    public bool Contains(Event item) => Events.Contains(item);
    public bool IsProperSubsetOf(IEnumerable<Event> other) => Events.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<Event> other) => Events.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<Event> other) => Events.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<Event> other) => Events.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<Event> other) => Events.Overlaps(other);
    public bool SetEquals(IEnumerable<Event> other) => Events.SetEquals(other);
    public IEnumerator<Event> GetEnumerator() => Events.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => Events.GetEnumerator();
    public override string ToString() => $"{GetType().Name}({SourceName}, {Level}, {Keywords:x})";

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (string.IsNullOrEmpty(SourceName) && (source.Name == nextSourceName))
        {
            SourceName = source.Name;
            Level = nextLevel;
            Keywords = nextKeywords;
            EnableEvents(source, Level, Keywords);
        }
    }
    protected override void OnEventWritten(EventWrittenEventArgs e) => events[new(e.EventId, e.Level, e.Keywords, e.EventName ?? "")] = 0;

    private IReadOnlySet<Event> Events
    {
        get
        {
            if (Count > keys.Count)
            {
                keys.UnionWith(events.Keys);
            }
            return keys;
        }
    }
}
