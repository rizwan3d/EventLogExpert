﻿// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Eventing.Helpers;
using EventLogExpert.UI;
using EventLogExpert.UI.Models;
using EventLogExpert.UI.Services;
using EventLogExpert.UI.Store.FilterPane;
using Microsoft.AspNetCore.Components;
using IDispatcher = Fluxor.IDispatcher;

namespace EventLogExpert.Shared.Components.Filters;

public sealed partial class FilterRow
{
    [Parameter] public FilterModel Value { get; set; } = null!;

    [Inject] private IAlertDialogService AlertDialogService { get; init; } = null!;

    [Inject] private IDispatcher Dispatcher { get; init; } = null!;

    private List<string> FilteredItems => Items
        .Where(x => x.Contains(Value.FilterValue ?? string.Empty, StringComparison.CurrentCultureIgnoreCase))
        .ToList();

    private List<string> Items
    {
        get
        {
            switch (Value.FilterType)
            {
                case FilterType.Id :
                    return EventLogState.Value.ActiveLogs.Values.SelectMany(log => log.EventIds)
                        .Distinct().OrderBy(id => id).Select(id => id.ToString()).ToList();
                case FilterType.ActivityId:
                    return EventLogState.Value.ActiveLogs.Values.SelectMany(log => log.EventActivityIds)
                        .Distinct().OrderBy(id => id).Select(activityId => activityId.ToString() ?? string.Empty).ToList();
                case FilterType.Level :
                    List<string> items = [];

                    foreach (SeverityLevel item in Enum.GetValues(typeof(SeverityLevel)))
                    {
                        items.Add(item.ToString());
                    }

                    return items;
                case FilterType.KeywordsDisplayNames :
                    return EventLogState.Value.ActiveLogs.Values.SelectMany(log => log.KeywordNames)
                        .Distinct().OrderBy(name => name).Select(name => name.ToString()).ToList();
                case FilterType.Source :
                    return EventLogState.Value.ActiveLogs.Values.SelectMany(log => log.EventProviderNames)
                        .Distinct().OrderBy(name => name).Select(name => name.ToString()).ToList();
                case FilterType.TaskCategory :
                    return EventLogState.Value.ActiveLogs.Values.SelectMany(log => log.TaskNames)
                        .Distinct().OrderBy(name => name).Select(name => name.ToString()).ToList();
                case FilterType.Description :
                default :
                    return [];
            }
        }
    }

    private void AddSubFilter() => Dispatcher.Dispatch(new FilterPaneAction.AddSubFilter(Value.Id));

    private void EditFilter() => Dispatcher.Dispatch(new FilterPaneAction.ToggleEditFilter(Value.Id));

    private void RemoveFilter() => Dispatcher.Dispatch(new FilterPaneAction.RemoveFilter(Value.Id));

    private async void SaveFilter()
    {
        FilterModel newModel = Value with { };

        if (!FilterMethods.TryParse(newModel, out string? comparisonString))
        {
            await AlertDialogService.ShowAlert("Invalid Filter",
                "The filter you have created is an invalid filter, please adjust and try again.",
                "Ok");

            return;
        }

        newModel.ComparisonString = comparisonString!;
        newModel.IsEditing = false;
        newModel.IsEnabled = true;

        Dispatcher.Dispatch(new FilterPaneAction.SetFilter(newModel));
    }

    private void ToggleFilter() => Dispatcher.Dispatch(new FilterPaneAction.ToggleEnableFilter(Value.Id));
}
