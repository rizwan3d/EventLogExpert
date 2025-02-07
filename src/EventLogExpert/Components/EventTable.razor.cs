// // Copyright (c) Microsoft Corporation.
// // Licensed under the MIT License.

using EventLogExpert.Eventing.Helpers;
using EventLogExpert.Eventing.Models;
using EventLogExpert.Services;
using EventLogExpert.UI;
using EventLogExpert.UI.Store.EventLog;
using Fluxor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using System.Collections.Immutable;
using static EventLogExpert.UI.Store.EventLog.EventLogState;
using IDispatcher = Fluxor.IDispatcher;

namespace EventLogExpert.Components;

public sealed partial class EventTable
{
    private string? _activeLog;

    [Inject]
    private IStateSelection<EventLogState, IImmutableDictionary<string, EventLogData>> ActiveLogState { get; init; } = null!;

    [Inject] private IClipboardService ClipboardService { get; init; } = null!;

    [Inject] private IDispatcher Dispatcher { get; init; } = null!;

    [Inject] private IJSRuntime JSRuntime { get; init; } = null!;

    [Inject]
    private IStateSelection<EventLogState, DisplayEventModel?> SelectedEventState { get; init; } = null!;

    [Inject] private ITraceLogger TraceLogger { get; init; } = null!;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await JSRuntime.InvokeVoidAsync("registerTableColumnResizers");
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    protected override void OnInitialized()
    {
        MaximumStateChangedNotificationsPerSecond = 2;

        ActiveLogState.Select(s => s.ActiveLogs);

        ActiveLogState.StateChanged += async (sender, activeLog) =>
        {
            await JSRuntime.InvokeVoidAsync("registerTableColumnResizers");
        };

        SelectedEventState.Select(s => s.SelectedEvent);

        base.OnInitialized();
    }

    private static string GetLevelClass(string level) => level switch
    {
        nameof(SeverityLevel.Error) => "bi bi-exclamation-circle error",
        nameof(SeverityLevel.Warning) => "bi bi-exclamation-triangle warning",
        nameof(SeverityLevel.Information) => "bi bi-info-circle",
        _ => string.Empty,
    };

    private string GetCss(DisplayEventModel @event) => SelectedEventState.Value?.RecordId == @event.RecordId ?
        "table-row selected" : "table-row";

    private void HandleKeyUp(KeyboardEventArgs args)
    {
        if (args is { CtrlKey: true, Code: "KeyC" })
        {
            ClipboardService.CopySelectedEvent(SelectedEventState.Value, SettingsState.Value.Config.CopyType);
        }
    }

    private async Task InvokeContextMenu(MouseEventArgs args) =>
        await JSRuntime.InvokeVoidAsync("invokeContextMenu", args.ClientX, args.ClientY);

    private async Task InvokeTableColumnMenu(MouseEventArgs args) =>
        await JSRuntime.InvokeVoidAsync("invokeTableColumnMenu", args.ClientX, args.ClientY);

    private bool IsColumnHidden(ColumnName columnName)
    {
        if (!SettingsState.Value.EventTableColumns.TryGetValue(columnName, out var enabled)) { return true; }

        return !enabled;
    }

    private void SelectEvent(DisplayEventModel @event) => Dispatcher.Dispatch(new EventLogAction.SelectEvent(@event));

    private void ToggleSorting() =>
        Dispatcher.Dispatch(new EventLogAction.ToggleSorting(TraceLogger));
}
