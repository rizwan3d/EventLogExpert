﻿// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Eventing.Helpers;
using EventLogExpert.Eventing.Models;
using Fluxor;
using System.Collections.Immutable;
using static EventLogExpert.UI.Store.EventLog.EventLogState;

namespace EventLogExpert.UI.Store.EventLog;

public class EventLogReducers
{
    /// <summary>The maximum number of new events we will hold in the state before we turn off the watcher.</summary>
    private static readonly int MaxNewEvents = 1000;

    [ReducerMethod]
    public static EventLogState ReduceAddEvent(EventLogState state, EventLogAction.AddEvent action)
    {
        // Sometimes the watcher doesn't stop firing events immediately. Let's
        // make sure the events being added are for a log that is still "open".
        if (!state.ActiveLogs.ContainsKey(action.NewEvent.OwningLog)) { return state; }

        var newEvent = new[] { action.NewEvent };

        var newState = state;

        if (state.ContinuouslyUpdate)
        {
            newState = newState with
            {
                ActiveLogs = DistributeEventsToManyLogs(
                    newState.ActiveLogs,
                    newEvent,
                    state.AppliedFilter,
                    state.OrderBy,
                    state.IsDescending,
                    action.TraceLogger),
                CombinedEvents = AddEventsToCombinedLog(
                        state.CombinedEvents,
                        newEvent,
                        state.AppliedFilter,
                        state.OrderBy,
                        state.IsDescending,
                        action.TraceLogger)
                    .ToList()
                    .AsReadOnly()
            };
        }
        else
        {
            var updatedBuffer = newEvent.Concat(state.NewEventBuffer).ToList().AsReadOnly();
            var full = updatedBuffer.Count >= MaxNewEvents;
            newState = newState with { NewEventBuffer = updatedBuffer, NewEventBufferIsFull = full };
        }

        return newState;
    }

    [ReducerMethod(typeof(EventLogAction.CloseAll))]
    public static EventLogState ReduceCloseAll(EventLogState state) => state with
    {
        ActiveLogs = ImmutableDictionary<string, EventLogData>.Empty,
        CombinedEvents = new List<DisplayEventModel>().AsReadOnly(),
        NewEventBuffer = new List<DisplayEventModel>().AsReadOnly(),
        NewEventBufferIsFull = false
    };

    [ReducerMethod]
    public static EventLogState ReduceCloseLog(EventLogState state, EventLogAction.CloseLog action)
    {
        var newState = state with
        {
            ActiveLogs = state.ActiveLogs.Remove(action.LogName),
            CombinedEvents = state.CombinedEvents.Where(e => e.OwningLog != action.LogName).ToList().AsReadOnly(),
            NewEventBuffer = state.NewEventBuffer
                .Where(e => e.OwningLog != action.LogName)
                .ToList().AsReadOnly()
        };

        newState = newState with
        {
            NewEventBufferIsFull = newState.NewEventBuffer.Count >= MaxNewEvents
        };

        return newState;
    }

    [ReducerMethod]
    public static EventLogState ReduceLoadEvents(EventLogState state, EventLogAction.LoadEvents action)
    {
        var newLogsCollection = state.ActiveLogs;

        if (state.ActiveLogs.ContainsKey(action.LogName))
        {
            newLogsCollection = state.ActiveLogs.Remove(action.LogName);
        }

        // Events collection is always ordered descending by record id
        var sortedEvents = action.Events.SortEvents(state.OrderBy, state.IsDescending).ToList();

        // Filtered events reflects both the filter and sort choice.
        var filteredEvents = TryGetFilteredEvents(
            sortedEvents,
            state.AppliedFilter,
            state.OrderBy,
            state.IsDescending,
            action.TraceLogger,
            out var filtered) ?
            filtered.ToList() :
            sortedEvents;

        newLogsCollection = newLogsCollection.Add(
            action.LogName,
            new EventLogData(
                action.LogName,
                action.Type,
                sortedEvents.AsReadOnly(),
                filteredEvents.AsReadOnly(),
                action.AllEventIds.ToImmutableHashSet(),
                action.AllActivityIds.ToImmutableHashSet(),
                action.AllProviderNames.ToImmutableHashSet(),
                action.AllTaskNames.ToImmutableHashSet(),
                action.AllKeywords.ToImmutableHashSet()
            ));

        var newCombinedEvents = CombineLogs(
            newLogsCollection.Values.Select(l => l.FilteredEvents),
            state.OrderBy,
            state.IsDescending,
            action.TraceLogger);

        return state with { ActiveLogs = newLogsCollection, CombinedEvents = newCombinedEvents.ToList().AsReadOnly() };
    }

    [ReducerMethod]
    public static EventLogState ReduceLoadNewEvents(EventLogState state, EventLogAction.LoadNewEvents action) =>
        ProcessNewEventBuffer(state, action.TraceLogger);

    [ReducerMethod]
    public static EventLogState ReduceOpenLog(EventLogState state, EventLogAction.OpenLog action) => state with
    {
        ActiveLogs = state.ActiveLogs.Add(action.LogName, GetEmptyLogData(action.LogName, action.LogType))
    };

    [ReducerMethod]
    public static EventLogState ReduceSelectEvent(EventLogState state, EventLogAction.SelectEvent action)
    {
        if (state.SelectedEvent == action.SelectedEvent) { return state; }

        return state with { SelectedEvent = action.SelectedEvent };
    }

    [ReducerMethod]
    public static EventLogState ReduceSelectLog(EventLogState state, EventLogAction.SelectLog action) =>
        state with { SelectedLogName = action.LogName };

    [ReducerMethod]
    public static EventLogState ReduceSetContinouslyUpdate(
        EventLogState state,
        EventLogAction.SetContinouslyUpdate action)
    {
        var newState = state with { ContinuouslyUpdate = action.ContinuouslyUpdate };

        if (action.ContinuouslyUpdate)
        {
            newState = ProcessNewEventBuffer(newState, action.TraceLogger);
        }

        return newState;
    }

    [ReducerMethod]
    public static EventLogState ReduceSetEventsLoading(EventLogState state, EventLogAction.SetEventsLoading action)
    {
        var newEventsLoading = state.EventsLoading;

        if (newEventsLoading.ContainsKey(action.ActivityId))
        {
            newEventsLoading = newEventsLoading.Remove(action.ActivityId);
        }

        if (action.Count == 0)
        {
            return state with { EventsLoading = newEventsLoading };
        }

        return state with { EventsLoading = newEventsLoading.Add(action.ActivityId, action.Count) };
    }

    [ReducerMethod]
    public static EventLogState ReduceSetFilters(EventLogState state, EventLogAction.SetFilters action)
    {
        if (!HasFilteringChanged(action.EventFilter, state.AppliedFilter))
        {
            return state;
        }

        var newState = state;

        foreach (var entry in state.ActiveLogs.Values)
        {
            var newLogData = TryGetFilteredEvents(
                entry.Events,
                action.EventFilter,
                state.OrderBy,
                state.IsDescending,
                action.TraceLogger,
                out var filteredEvents) ?
                entry with { FilteredEvents = filteredEvents.ToList().AsReadOnly() } :
                entry with { FilteredEvents = entry.Events };

            newState = newState with
            {
                ActiveLogs = newState.ActiveLogs
                    .Remove(entry.Name)
                    .Add(entry.Name, newLogData)
            };
        }

        var newCombinedEvents = CombineLogs(
            newState.ActiveLogs.Values.Select(l => l.FilteredEvents),
            state.OrderBy,
            state.IsDescending,
            action.TraceLogger);

        newState = newState with
        {
            CombinedEvents = newCombinedEvents.ToList().AsReadOnly(),
            AppliedFilter = action.EventFilter
        };

        return newState;
    }

    [ReducerMethod]
    public static EventLogState ReduceSetOrderBy(EventLogState state, EventLogAction.SetOrderBy action) =>
        state.OrderBy.Equals(action.OrderBy) ?
            OrderAndSortActiveLogs(state, null, true, action.TraceLogger) :
            OrderAndSortActiveLogs(state, action.OrderBy, state.IsDescending, action.TraceLogger);

    [ReducerMethod]
    public static EventLogState ReduceToggleSorting(EventLogState state, EventLogAction.ToggleSorting action) =>
        OrderAndSortActiveLogs(state, state.OrderBy, !state.IsDescending, action.TraceLogger);

    /// <summary>Add new events to a "combined" log view</summary>
    /// <param name="combinedLog"></param>
    /// <param name="eventsToAdd">
    ///     It is assumed that these events are already sorted in descending order. This value should be
    ///     coming from NewEventBuffer, where new events are inserted at the top of the list as they come in.
    /// </param>
    /// <param name="filter"></param>
    /// <param name="orderBy"></param>
    /// <param name="isDescending"></param>
    /// <param name="traceLogger"></param>
    /// <returns></returns>
    private static IEnumerable<DisplayEventModel> AddEventsToCombinedLog(
        IEnumerable<DisplayEventModel> combinedLog,
        IEnumerable<DisplayEventModel> eventsToAdd,
        EventFilter filter,
        ColumnName? orderBy,
        bool isDescending,
        ITraceLogger traceLogger)
    {
        TryGetFilteredEvents(eventsToAdd, filter, orderBy, isDescending, traceLogger, out var filteredEvents);

        return isDescending ? filteredEvents.Concat(combinedLog) : combinedLog.Concat(filteredEvents);
    }

    /// <summary>Adds new events to the currently opened log</summary>
    private static EventLogData AddEventsToOneLog(
        EventLogData logData,
        IEnumerable<DisplayEventModel> eventsToAdd,
        EventFilter filter,
        ColumnName? orderBy,
        bool isDescending,
        ITraceLogger traceLogger)
    {
        var newEvents = eventsToAdd
            .Concat(logData.Events)
            .SortEvents(orderBy, isDescending)
            .ToList()
            .AsReadOnly();

        var filteredEvents =
            TryGetFilteredEvents(newEvents, filter, orderBy, isDescending, traceLogger, out var filtered) ?
                filtered.ToList().AsReadOnly() :
                newEvents;

        var updatedEventIds = logData.EventIds.Union(newEvents.Select(e => e.Id));
        var updatedProviderNames = logData.EventProviderNames.Union(newEvents.Select(e => e.Source));
        var updatedTaskNames = logData.TaskNames.Union(newEvents.Select(e => e.TaskCategory));

        var updatedLogData = logData with
        {
            Events = newEvents,
            FilteredEvents = filteredEvents,
            EventIds = updatedEventIds,
            EventProviderNames = updatedProviderNames,
            TaskNames = updatedTaskNames
        };

        return updatedLogData;
    }

    /// <summary>
    ///     This should be used to combine events from multiple logs into a combined log. The sort key changes depending
    ///     on how many logs are present.
    /// </summary>
    private static IEnumerable<DisplayEventModel> CombineLogs(
        IEnumerable<IEnumerable<DisplayEventModel>> eventData,
        ColumnName? orderBy,
        bool isDescending,
        ITraceLogger traceLogger)
    {
        var events = eventData.ToList();

        traceLogger.Trace($"{nameof(CombineLogs)} was called for {events.Count} logs.");

        if (events.Count > 1)
        {
            return orderBy is null ?
                events.SelectMany(l => l).SortEvents(ColumnName.DateAndTime, isDescending) :
                events.SelectMany(l => l).SortEvents(orderBy, isDescending);
        }

        return events.FirstOrDefault()?.SortEvents(orderBy, isDescending) ?? Enumerable.Empty<DisplayEventModel>();
    }

    private static ImmutableDictionary<string, EventLogData> DistributeEventsToManyLogs(
        ImmutableDictionary<string, EventLogData> logsToUpdate,
        IEnumerable<DisplayEventModel> eventsToDistribute,
        EventFilter filter,
        ColumnName? orderBy,
        bool isDescending,
        ITraceLogger traceLogger)
    {
        var newLogs = logsToUpdate;
        var events = eventsToDistribute.ToList();

        foreach (var log in logsToUpdate.Values)
        {
            var newEventsForThisLog = events.Where(e => e.OwningLog == log.Name).ToList();

            if (newEventsForThisLog.Any())
            {
                var newLogData = AddEventsToOneLog(log, newEventsForThisLog, filter, orderBy, isDescending, traceLogger);
                newLogs = newLogs.Remove(log.Name).Add(log.Name, newLogData);
            }
        }

        return newLogs;
    }

    private static EventLogData GetEmptyLogData(string logName, LogType logType) => new(
        logName,
        logType,
        new List<DisplayEventModel>().AsReadOnly(),
        new List<DisplayEventModel>().AsReadOnly(),
        ImmutableHashSet<int>.Empty,
        ImmutableHashSet<Guid?>.Empty,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty);

    /// <summary>Tries to filter and sort <paramref name="events" /> by <paramref name="eventFilter" /></summary>
    private static bool TryGetFilteredEvents(
        IEnumerable<DisplayEventModel> events,
        EventFilter eventFilter,
        ColumnName? orderBy,
        bool isDescending,
        ITraceLogger traceLogger,
        out IEnumerable<DisplayEventModel> filteredEvents)
    {
        traceLogger.Trace($"{nameof(TryGetFilteredEvents)} was called to filter {events.Count()} events.");

        if (!IsFilteringEnabled(eventFilter))
        {
            filteredEvents = events;
            return false;
        }

        List<Func<DisplayEventModel, bool>> filters = new();

        if (eventFilter.DateFilter?.IsEnabled is true)
        {
            filters.Add(e =>
                e.TimeCreated >= eventFilter.DateFilter.After &&
                e.TimeCreated <= eventFilter.DateFilter.Before);
        }

        if (eventFilter.AdvancedFilter?.IsEnabled is true)
        {
            filters.Add(e => eventFilter.AdvancedFilter.Comparison(e));
        }

        if (eventFilter.Filters.Any())
        {
            filters.Add(e => eventFilter.Filters
                .All(filter => filter.Comparison(e)));
        }

        if (eventFilter.CachedFilters.Any())
        {
            filters.Add(e => eventFilter.CachedFilters
                .All(filter => filter.Comparison(e)));
        }

        filteredEvents = events.AsParallel()
            .Where(e => filters
                .All(filter => filter(e)))
            .SortEvents(orderBy, isDescending);

        return true;
    }

    private static bool HasFilteringChanged(EventFilter updated, EventFilter original) =>
        updated.AdvancedFilter?.Equals(original.AdvancedFilter) is false ||
        updated.DateFilter?.Equals(original.DateFilter) is false ||
        updated.CachedFilters.Equals(original.CachedFilters) is false ||
        updated.Filters.Equals(original.Filters) is false;

    private static bool IsFilteringEnabled(EventFilter eventFilter) =>
        eventFilter.AdvancedFilter?.IsEnabled is true ||
        eventFilter.CachedFilters.Any() ||
        eventFilter.DateFilter?.IsEnabled is true ||
        eventFilter.Filters.Any();

    private static EventLogState OrderAndSortActiveLogs(
        EventLogState state,
        ColumnName? orderBy,
        bool isDescending,
        ITraceLogger traceLogger)
    {
        var newActiveLogs = state.ActiveLogs;

        foreach (var logData in newActiveLogs.Values)
        {
            newActiveLogs = newActiveLogs
                .Remove(logData.Name)
                .Add(logData.Name,
                    logData with
                    {
                        FilteredEvents = logData.FilteredEvents
                            .SortEvents(orderBy, isDescending)
                            .ToList()
                            .AsReadOnly()
                    });
        }

        var newCombinedEvents = CombineLogs(
            newActiveLogs.Values.Select(l => l.FilteredEvents),
            orderBy,
            isDescending,
            traceLogger);

        return state with
        {
            ActiveLogs = newActiveLogs,
            CombinedEvents = newCombinedEvents.ToList().AsReadOnly(),
            OrderBy = orderBy,
            IsDescending = isDescending
        };
    }

    private static EventLogState ProcessNewEventBuffer(EventLogState state, ITraceLogger traceLogger)
    {
        var newState = state with
        {
            ActiveLogs = DistributeEventsToManyLogs(
                state.ActiveLogs,
                state.NewEventBuffer,
                state.AppliedFilter,
                state.OrderBy,
                state.IsDescending,
                traceLogger)
        };

        var newCombinedEvents = CombineLogs(
            newState.ActiveLogs.Values.Select(l => l.FilteredEvents),
            state.OrderBy,
            state.IsDescending,
            traceLogger);

        newState = newState with
        {
            CombinedEvents = newCombinedEvents.ToList().AsReadOnly(),
            NewEventBuffer = new List<DisplayEventModel>().AsReadOnly(),
            NewEventBufferIsFull = false
        };

        return newState;
    }
}
