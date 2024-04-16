// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.ResourcesGridColumns;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Extensions;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Pages;

public partial class Resources : ComponentBase, IAsyncDisposable, IPageWithSessionAndUrlState<Resources.ResourcesViewModel, Resources.ResourcesPageState>
{
    private const string TypeColumn = nameof(TypeColumn);
    private const string NameColumn = nameof(NameColumn);
    private const string StateColumn = nameof(StateColumn);
    private const string StartTimeColumn = nameof(StartTimeColumn);
    private const string SourceColumn = nameof(SourceColumn);
    private const string EndpointsColumn = nameof(EndpointsColumn);
    private const string ActionsColumn = nameof(ActionsColumn);

    private Subscription? _logsSubscription;
    private IList<GridColumn>? _gridColumns;
    private Dictionary<ApplicationKey, int>? _applicationUnviewedErrorCounts;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }
    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }
    [Inject]
    public required NavigationManager NavigationManager { get; init; }
    [Inject]
    public required IDialogService DialogService { get; init; }
    [Inject]
    public required IToastService ToastService { get; init; }
    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }
    [Inject]
    public required IJSRuntime JS { get; init; }


    public string BasePath => DashboardUrls.ResourcesBasePath;
    public string SessionStorageKey => "Resources_PageState";
    public ResourcesViewModel PageViewModel { get; set; } = null!;

    [Parameter]
    [SupplyParameterFromQuery(Name = "view")]
    public string? ViewKindName { get; set; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public string? VisibleTypes { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "resource")]
    public string? ResourceName { get; set; }

    private ResourceViewModel? SelectedResource { get; set; }

    private readonly CancellationTokenSource _watchTaskCancellationTokenSource = new();
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly ConcurrentDictionary<string, bool> _allResourceTypes = [];
    private readonly ConcurrentDictionary<string, bool> _visibleResourceTypes = new(StringComparers.ResourceName);
    private readonly HashSet<string> _expandedResourceNames = [];
    private string _filter = "";
    private bool _isTypeFilterVisible;
    private Task? _resourceSubscriptionTask;
    private bool _isLoading = true;
    private string? _elementIdBeforeDetailsViewOpened;
    private FluentDataGrid<ResourceGridViewModel> _dataGrid = null!;
    private GridColumnManager _manager = null!;
    private int _maxHighlightedCount;
    private DotNetObjectReference<ResourcesInterop>? _resourcesInteropReference;
    private IJSObjectReference? _jsModule;
    private AspirePageContentLayout? _contentLayout;

    private bool Filter(ResourceViewModel resource) => _visibleResourceTypes.ContainsKey(resource.ResourceType) && (_filter.Length == 0 || resource.MatchesFilter(_filter)) && !resource.IsHiddenState();

    private async Task OnAllResourceTypesCheckedChangedAsync(bool? areAllTypesVisible)
    {
        AreAllTypesVisible = areAllTypesVisible;
        await _dataGrid.SafeRefreshDataAsync();
    }

    private async Task OnResourceTypeVisibilityChangedAsync(string resourceType, bool isVisible)
    {
        if (isVisible)
        {
            _visibleResourceTypes[resourceType] = true;
        }
        else
        {
            _visibleResourceTypes.TryRemove(resourceType, out _);
        }

        await UpdateResourceGraphResourcesAsync();
        await ClearSelectedResourceAsync();
        await _dataGrid.SafeRefreshDataAsync();
    }

    private async Task HandleSearchFilterChangedAsync()
    {
        await UpdateResourceGraphResourcesAsync();
        await ClearSelectedResourceAsync();
    }

    private bool? AreAllTypesVisible
    {
        get
        {
            static bool SetEqualsKeys(ConcurrentDictionary<string, bool> left, ConcurrentDictionary<string, bool> right)
            {
                // PERF: This is inefficient since Keys locks and copies the keys.
                var keysLeft = left.Keys;
                var keysRight = right.Keys;

                return keysLeft.Count == keysRight.Count && keysLeft.OrderBy(key => key, StringComparers.ResourceType).SequenceEqual(keysRight.OrderBy(key => key, StringComparers.ResourceType), StringComparers.ResourceType);
            }

            return SetEqualsKeys(_visibleResourceTypes, _allResourceTypes)
                ? true
                : _visibleResourceTypes.IsEmpty
                    ? false
                    : null;
        }
        set
        {
            static bool UnionWithKeys(ConcurrentDictionary<string, bool> left, ConcurrentDictionary<string, bool> right)
            {
                // .Keys locks and copies the keys so avoid it here.
                foreach (var (key, _) in right)
                {
                    left[key] = true;
                }

                return true;
            }

            if (value is true)
            {
                UnionWithKeys(_visibleResourceTypes, _allResourceTypes);
            }
            else if (value is false)
            {
                _visibleResourceTypes.Clear();
            }

            _ = UpdateResourceGraphResourcesAsync();

            StateHasChanged();
        }
    }

    private readonly GridSort<ResourceGridViewModel> _nameSort = GridSort<ResourceGridViewModel>.ByAscending(p => p.Resource, ResourceViewModelNameComparer.Instance);
    private readonly GridSort<ResourceGridViewModel> _stateSort = GridSort<ResourceGridViewModel>.ByAscending(p => p.Resource.State).ThenAscending(p => p.Resource, ResourceViewModelNameComparer.Instance);
    private readonly GridSort<ResourceGridViewModel> _startTimeSort = GridSort<ResourceGridViewModel>.ByDescending(p => p.Resource.StartTimeStamp).ThenAscending(p => p.Resource, ResourceViewModelNameComparer.Instance);
    private readonly GridSort<ResourceGridViewModel> _typeSort = GridSort<ResourceGridViewModel>.ByAscending(p => p.Resource.ResourceType).ThenAscending(p => p.Resource, ResourceViewModelNameComparer.Instance);

    protected override async Task OnInitializedAsync()
    {
        _gridColumns = [
            new GridColumn(Name: NameColumn, DesktopWidth: "1.5fr", MobileWidth: "1.5fr"),
            new GridColumn(Name: StateColumn, DesktopWidth: "1.25fr", MobileWidth: "1.25fr"),
            new GridColumn(Name: StartTimeColumn, DesktopWidth: "1fr"),
            new GridColumn(Name: TypeColumn, DesktopWidth: "1fr"),
            new GridColumn(Name: SourceColumn, DesktopWidth: "2.25fr"),
            new GridColumn(Name: EndpointsColumn, DesktopWidth: "2.25fr", MobileWidth: "2fr"),
            new GridColumn(Name: ActionsColumn, DesktopWidth: "minmax(150px, 1.5fr)", MobileWidth: "1fr")
        ];

        PageViewModel = new ResourcesViewModel
        {
            SelectedViewKind = ResourceViewKind.Table
        };

        _applicationUnviewedErrorCounts = TelemetryRepository.GetApplicationUnviewedErrorLogsCount();

        if (DashboardClient.IsEnabled)
        {
            await SubscribeResourcesAsync();
        }

        _logsSubscription = TelemetryRepository.OnNewLogs(null, SubscriptionType.Other, async () =>
        {
            var newApplicationUnviewedErrorCounts = TelemetryRepository.GetApplicationUnviewedErrorLogsCount();

            // Only update UI if the error counts have changed.
            if (ApplicationErrorCountsChanged(newApplicationUnviewedErrorCounts))
            {
                _applicationUnviewedErrorCounts = newApplicationUnviewedErrorCounts;
                await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
            }
        });

        _isLoading = false;

        async Task SubscribeResourcesAsync()
        {
            var preselectedVisibleResourceTypes = VisibleTypes?.Split(',').ToHashSet();

            var (snapshot, subscription) = await DashboardClient.SubscribeResourcesAsync(_watchTaskCancellationTokenSource.Token);

            // Apply snapshot.
            foreach (var resource in snapshot)
            {
                var added = _resourceByName.TryAdd(resource.Name, resource);

                _allResourceTypes.TryAdd(resource.ResourceType, true);

                if (preselectedVisibleResourceTypes is null || preselectedVisibleResourceTypes.Contains(resource.ResourceType))
                {
                    _visibleResourceTypes.TryAdd(resource.ResourceType, true);
                }

                Debug.Assert(added, "Should not receive duplicate resources in initial snapshot data.");
            }

            UpdateMaxHighlightedCount();
            await _dataGrid.SafeRefreshDataAsync();

            // Listen for updates and apply.
            _resourceSubscriptionTask = Task.Run(async () =>
            {
                await foreach (var changes in subscription.WithCancellation(_watchTaskCancellationTokenSource.Token).ConfigureAwait(false))
                {
                    foreach (var (changeType, resource) in changes)
                    {
                        if (changeType == ResourceViewModelChangeType.Upsert)
                        {
                            _resourceByName[resource.Name] = resource;
                            if (string.Equals(SelectedResource?.Name, resource.Name, StringComparisons.ResourceName))
                            {
                                SelectedResource = resource;
                            }

                            if (_allResourceTypes.TryAdd(resource.ResourceType, true))
                            {
                                // If someone has filtered out a resource type then don't remove filter because an update was received.
                                // Only automatically set resource type to visible if it is a new resource.
                                _visibleResourceTypes[resource.ResourceType] = true;
                            }
                        }
                        else if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            var removed = _resourceByName.TryRemove(resource.Name, out _);
                            Debug.Assert(removed, "Cannot remove unknown resource.");
                        }
                    }

                    UpdateMaxHighlightedCount();
                    await UpdateResourceGraphResourcesAsync();
                    await InvokeAsync(StateHasChanged);
                    await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
                }
            });
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (PageViewModel.SelectedViewKind == ResourceViewKind.Graph && _jsModule == null)
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "/js/app-resourcegraph.js");

            _resourcesInteropReference = DotNetObjectReference.Create(new ResourcesInterop(this));

            await _jsModule.InvokeVoidAsync("initializeResourcesGraph", _resourcesInteropReference);
            await UpdateResourceGraphResourcesAsync();
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        await this.InitializeViewModelAsync();
    }

    private async Task UpdateResourceGraphResourcesAsync()
    {
        if (PageViewModel.SelectedViewKind != ResourceViewKind.Graph || _jsModule == null)
        {
            return;
        }

        var databaseIcon = GetIconPathData(new Icons.Filled.Size24.Database());
        var containerIcon = GetIconPathData(new Icons.Filled.Size24.Box());
        var executableIcon = GetIconPathData(new Icons.Filled.Size24.SettingsCogMultiple());
        var projectIcon = GetIconPathData(new Icons.Filled.Size24.CodeCircle());

        var activeResources = _resourceByName.Values.Where(Filter).OrderBy(e => e.ResourceType).ThenBy(e => e.Name).ToList();
        var resources = activeResources.Select(MapDto).ToList();
        await _jsModule.InvokeVoidAsync("updateResourcesGraph", resources);

        ResourceDto MapDto(ResourceViewModel r)
        {
            var referencedNames = new List<string>();
            if (r.Environment.SingleOrDefault(e => e.Name == "hack_resource_references") is { } references)
            {
                referencedNames = references.Value!.Split(',').ToList();
            }

            var resolvedNames = new List<string>();
            for (var i = 0; i < referencedNames.Count; i++)
            {
                var name = referencedNames[i];
                foreach (var targetResource in activeResources.Where(r => string.Equals(r.DisplayName, name, StringComparisons.ResourceName)))
                {
                    resolvedNames.Add(targetResource.Name);
                }
            }

            var endpoint = GetDisplayedEndpoints(r, out _).FirstOrDefault();
            var resolvedEndpointText = ResolvedEndpointText(endpoint);
            var resourceName = ResourceViewModel.GetResourceName(r, _resourceByName);
            var color = ColorGenerator.Instance.GetColorHexByKey(resourceName);

            var icon = r.ResourceType switch
            {
                KnownResourceTypes.Executable => executableIcon,
                KnownResourceTypes.Project => projectIcon,
                KnownResourceTypes.Container => containerIcon,
                string t => t.Contains("database", StringComparison.OrdinalIgnoreCase) ? databaseIcon : executableIcon
            };

            var stateIcon = StateColumnDisplay.GetStateIcon(r, ColumnsLoc);

            var dto = new ResourceDto
            {
                Name = r.Name,
                ResourceType = r.ResourceType,
                DisplayName = ResourceViewModel.GetResourceName(r, _resourceByName),
                Uid = r.Uid,
                ResourceIcon = new IconDto
                {
                    Path = icon,
                    Color = color,
                    Tooltip = r.ResourceType
                },
                StateIcon = new IconDto
                {
                    Path = GetIconPathData(stateIcon.Icon),
                    Color = stateIcon.Color.ToAttributeValue()!,
                    Tooltip = stateIcon.Tooltip ?? r.State
                },
                ReferencedNames = resolvedNames.ToImmutableArray(),
                EndpointUrl = endpoint?.Url,
                EndpointText = resolvedEndpointText
            };

            return dto;
        }
    }

    private static string ResolvedEndpointText(DisplayedEndpoint? endpoint)
    {
        var text = endpoint?.Text ?? endpoint?.Url;
        if (string.IsNullOrEmpty(text))
        {
            return "No endpoints";
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return $"{uri.Host}:{uri.Port}";
        }

        return text;
    }

    private static string GetIconPathData(Icon icon)
    {
        var p = icon.Content;
        var e = XElement.Parse(p);
        return e.Attribute("d")!.Value;
    }

    private class ResourcesInterop(Resources resources)
    {
        [JSInvokable]
        public async Task SelectResource(string id)
        {
            if (resources._resourceByName.TryGetValue(id, out var resource))
            {
                await resources.InvokeAsync(async () =>
                {
                    await resources.ShowResourceDetailsAsync(resource, null!);
                    resources.StateHasChanged();
                });
            }
        }
    }

    private class ResourceDto
    {
        public required string Name { get; init; }
        public required string ResourceType { get; init; }
        public required string DisplayName { get; init; }
        public required string Uid { get; init; }
        public required IconDto ResourceIcon { get; init; }
        public required IconDto StateIcon { get; init; }
        public required string? EndpointUrl { get; init; }
        public required string? EndpointText { get; init; }
        public required ImmutableArray<string> ReferencedNames { get; init; }
    }

    private class IconDto
    {
        public required string Path { get; init; }
        public required string Color { get; init; }
        public required string? Tooltip { get; init; }
    }

    private ValueTask<GridItemsProviderResult<ResourceGridViewModel>> GetData(GridItemsProviderRequest<ResourceGridViewModel> request)
    {
        // Get filtered and ordered resources.
        var filteredResources = _resourceByName.Values
            .Where(Filter)
            .Select(r => new ResourceGridViewModel { Resource = r })
            .AsQueryable();
        filteredResources = request.ApplySorting(filteredResources);

        // Rearrange resources based on parent information.
        // This must happen after resources are ordered so nested resources are in the right order.
        // Collapsed resources are filtered out of results.
        var orderedResources = ResourceGridViewModel.OrderNestedResources(filteredResources.ToList(), r => !_expandedResourceNames.Contains(r.Name))
            .Where(r => !r.IsHidden)
            .ToList();

        // Paging visible resources.
        var query = orderedResources
            .Skip(request.StartIndex)
            .Take(request.Count ?? DashboardUIHelpers.DefaultDataGridResultCount);

        return ValueTask.FromResult(GridItemsProviderResult.From(query.ToList(), orderedResources.Count));
    }

    private void UpdateMaxHighlightedCount()
    {
        var maxHighlightedCount = 0;
        foreach (var kvp in _resourceByName)
        {
            var resourceHighlightedCount = 0;
            foreach (var command in kvp.Value.Commands)
            {
                if (command.IsHighlighted && command.State != CommandViewModelState.Hidden)
                {
                    resourceHighlightedCount++;
                }
            }
            maxHighlightedCount = Math.Max(maxHighlightedCount, resourceHighlightedCount);
        }

        // Don't attempt to display more than 2 highlighted commands. Many commands will take up too much space.
        // Extra highlighted commands are still available in the menu.
        _maxHighlightedCount = Math.Min(maxHighlightedCount, 2);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (ResourceName is not null)
        {
            if (_resourceByName.TryGetValue(ResourceName, out var selectedResource))
            {
                await ShowResourceDetailsAsync(selectedResource, buttonId: null);
            }

            // Navigate to remove ?resource=xxx in the URL.
            NavigationManager.NavigateTo(DashboardUrls.ResourcesUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }

    private bool ApplicationErrorCountsChanged(Dictionary<ApplicationKey, int> newApplicationUnviewedErrorCounts)
    {
        if (_applicationUnviewedErrorCounts == null || _applicationUnviewedErrorCounts.Count != newApplicationUnviewedErrorCounts.Count)
        {
            return true;
        }

        foreach (var (application, count) in newApplicationUnviewedErrorCounts)
        {
            if (!_applicationUnviewedErrorCounts.TryGetValue(application, out var oldCount) || oldCount != count)
            {
                return true;
            }
        }

        return false;
    }

    private async Task ShowResourceDetailsAsync(ResourceViewModel resource, string? buttonId)
    {
        _elementIdBeforeDetailsViewOpened = buttonId;

        if (string.Equals(SelectedResource?.Name, resource.Name, StringComparisons.ResourceName))
        {
            await ClearSelectedResourceAsync();
        }
        else
        {
            SelectedResource = resource;

            // Ensure that the selected resource is visible in the grid. All parents must be expanded.
            var current = resource;
            while (current != null)
            {
                if (current.GetResourcePropertyValue(KnownProperties.Resource.ParentName) is { Length: > 0 } value)
                {
                    if (_resourceByName.TryGetValue(value, out current))
                    {
                        _expandedResourceNames.Add(value);
                        continue;
                    }
                }

                break;
            }

            await _dataGrid.SafeRefreshDataAsync();
        }
    }

    private async Task ClearSelectedResourceAsync(bool causedByUserAction = false)
    {
        SelectedResource = null;

        await InvokeAsync(StateHasChanged);

        if (PageViewModel.SelectedViewKind == ResourceViewKind.Graph)
        {
            await UpdateResourceGraphSelectedAsync();
        }

        if (_elementIdBeforeDetailsViewOpened is not null && causedByUserAction)
        {
            await JS.InvokeVoidAsync("focusElement", _elementIdBeforeDetailsViewOpened);
        }

        _elementIdBeforeDetailsViewOpened = null;
    }

    private string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

    private bool HasMultipleReplicas(ResourceViewModel resource)
    {
        var count = 0;
        foreach (var (_, item) in _resourceByName)
        {
            if (item.IsHiddenState())
            {
                continue;
            }

            if (string.Equals(item.DisplayName, resource.DisplayName, StringComparisons.ResourceName))
            {
                count++;
                if (count >= 2)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string GetRowClass(ResourceViewModel resource)
        => string.Equals(resource.Name, SelectedResource?.Name, StringComparisons.ResourceName) ? "selected-row resource-row" : "resource-row";

    private async Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command)
    {
        if (!string.IsNullOrWhiteSpace(command.ConfirmationMessage))
        {
            var dialogReference = await DialogService.ShowConfirmationAsync(command.ConfirmationMessage);
            var result = await dialogReference.Result;
            if (result.Cancelled)
            {
                return;
            }
        }

        var messageResourceName = GetResourceName(resource);

        var toastParameters = new ToastParameters<CommunicationToastContent>()
        {
            Id = Guid.NewGuid().ToString(),
            Intent = ToastIntent.Progress,
            Title = string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Resources.ResourceCommandStarting)], messageResourceName, command.DisplayName),
            Content = new CommunicationToastContent()
        };

        // Show a toast immediately to indicate the command is starting.
        ToastService.ShowCommunicationToast(toastParameters);

        var response = await DashboardClient.ExecuteResourceCommandAsync(resource.Name, resource.ResourceType, command, CancellationToken.None);

        // Update toast with the result;
        if (response.Kind == ResourceCommandResponseKind.Succeeded)
        {
            toastParameters.Title = string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Resources.ResourceCommandSuccess)], messageResourceName, command.DisplayName);
            toastParameters.Intent = ToastIntent.Success;
            toastParameters.Icon = GetIntentIcon(ToastIntent.Success);
        }
        else
        {
            toastParameters.Title = string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Resources.ResourceCommandFailed)], messageResourceName, command.DisplayName);
            toastParameters.Intent = ToastIntent.Error;
            toastParameters.Icon = GetIntentIcon(ToastIntent.Error);
            toastParameters.Content.Details = response.ErrorMessage;
            toastParameters.PrimaryAction = Loc[nameof(Dashboard.Resources.Resources.ResourceCommandToastViewLogs)];
            toastParameters.OnPrimaryAction = EventCallback.Factory.Create<ToastResult>(this, () => NavigationManager.NavigateTo(DashboardUrls.ConsoleLogsUrl(resource: resource.Name)));
        }

        ToastService.UpdateToast(toastParameters.Id, toastParameters);
    }

    // Copied from FluentUI.
    private static (Icon Icon, Color Color)? GetIntentIcon(ToastIntent intent)
    {
        return intent switch
        {
            ToastIntent.Success => (new Icons.Filled.Size24.CheckmarkCircle(), Color.Success),
            ToastIntent.Warning => (new Icons.Filled.Size24.Warning(), Color.Warning),
            ToastIntent.Error => (new Icons.Filled.Size24.DismissCircle(), Color.Error),
            ToastIntent.Info => (new Icons.Filled.Size24.Info(), Color.Info),
            ToastIntent.Progress => (new Icons.Regular.Size24.Flash(), Color.Neutral),
            ToastIntent.Upload => (new Icons.Regular.Size24.ArrowUpload(), Color.Neutral),
            ToastIntent.Download => (new Icons.Regular.Size24.ArrowDownload(), Color.Neutral),
            ToastIntent.Event => (new Icons.Regular.Size24.CalendarLtr(), Color.Neutral),
            ToastIntent.Mention => (new Icons.Regular.Size24.Person(), Color.Neutral),
            ToastIntent.Custom => null,
            _ => throw new InvalidOperationException()
        };
    }

    private static (string Value, string? ContentAfterValue, string ValueToCopy, string Tooltip)? GetSourceColumnValueAndTooltip(ResourceViewModel resource)
    {
        // NOTE projects are also executables, so we have to check for projects first
        if (resource.IsProject() && resource.TryGetProjectPath(out var projectPath))
        {
            return (Value: Path.GetFileName(projectPath), ContentAfterValue: null, ValueToCopy: projectPath, Tooltip: projectPath);
        }

        if (resource.TryGetExecutablePath(out var executablePath))
        {
            resource.TryGetExecutableArguments(out var arguments);
            var argumentsString = arguments.IsDefaultOrEmpty ? "" : string.Join(" ", arguments);
            var fullCommandLine = $"{executablePath} {argumentsString}";

            return (Value: Path.GetFileName(executablePath), ContentAfterValue: argumentsString, ValueToCopy: fullCommandLine, Tooltip: fullCommandLine);
        }

        if (resource.TryGetContainerImage(out var containerImage))
        {
            return (Value: containerImage, ContentAfterValue: null, ValueToCopy: containerImage, Tooltip: containerImage);
        }

        if (resource.Properties.TryGetValue(KnownProperties.Resource.Source, out var property) && property.Value is { HasStringValue: true, StringValue: var value })
        {
            return (Value: value, ContentAfterValue: null, ValueToCopy: value, Tooltip: value);
        }

        return null;
    }

    private static string GetEndpointsTooltip(ResourceViewModel resource)
    {
        var displayedEndpoints = GetDisplayedEndpoints(resource);

        if (displayedEndpoints.Count == 0)
        {
            return string.Empty;
        }

        if (displayedEndpoints.Count == 1)
        {
            return displayedEndpoints[0].Text;
        }

        var maxShownEndpoints = 3;
        var tooltipBuilder = new StringBuilder(string.Join(", ", displayedEndpoints.Take(maxShownEndpoints).Select(endpoint => endpoint.Text)));

        if (displayedEndpoints.Count > maxShownEndpoints)
        {
            tooltipBuilder.Append(CultureInfo.CurrentCulture, $" + {displayedEndpoints.Count - maxShownEndpoints}");
        }

        return tooltipBuilder.ToString();
    }

    private async Task OnToggleCollapse(ResourceGridViewModel viewModel)
    {
        // View model data is recreated if data updates.
        // Persist the collapsed state in a separate list.
        if (viewModel.IsCollapsed)
        {
            viewModel.IsCollapsed = false;
            _expandedResourceNames.Add(viewModel.Resource.Name);
        }
        else
        {
            viewModel.IsCollapsed = true;
            _expandedResourceNames.Remove(viewModel.Resource.Name);
        }

        await _dataGrid.SafeRefreshDataAsync();
    }

    private static List<DisplayedEndpoint> GetDisplayedEndpoints(ResourceViewModel resource)
    {
        return ResourceEndpointHelpers.GetEndpoints(resource, includeInternalUrls: false);
    }

    private Task OnTabChangeAsync(FluentTab newTab)
    {
        var id = newTab.Id?.Substring("tab-".Length);

        if (id is null
            || !Enum.TryParse(typeof(ResourceViewKind), id, out var o)
            || o is not ResourceViewKind viewKind)
        {
            return Task.CompletedTask;
        }

        return OnViewChangedAsync(viewKind);
    }

    private async Task OnViewChangedAsync(ResourceViewKind newView)
    {
        PageViewModel.SelectedViewKind = newView;
        await this.AfterViewModelChangedAsync(_contentLayout, isChangeInToolbar: true);

        if (newView == ResourceViewKind.Graph)
        {
            await UpdateResourceGraphResourcesAsync();
            await UpdateResourceGraphSelectedAsync();
        }
    }

    private async Task UpdateResourceGraphSelectedAsync()
    {
        if (_jsModule != null)
        {
            await _jsModule.InvokeVoidAsync("updateResourcesGraphSelected", SelectedResource?.Name);
        }
    }

    public sealed class ResourcesViewModel
    {
        public required ResourceViewKind SelectedViewKind { get; set; }
    }

    public class ResourcesPageState
    {
        public required string? ViewKind { get; set; }
    }

    public enum ResourceViewKind
    {
        Table,
        Graph
    }

    public async ValueTask DisposeAsync()
    {
        _resourcesInteropReference?.Dispose();
        _watchTaskCancellationTokenSource.Cancel();
        _watchTaskCancellationTokenSource.Dispose();
        _logsSubscription?.Dispose();

        await JSInteropHelpers.SafeDisposeAsync(_jsModule);

        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
    }

    public void UpdateViewModelFromQuery(ResourcesViewModel viewModel)
    {
        if (Enum.TryParse(typeof(ResourceViewKind), ViewKindName, out var view) && view is ResourceViewKind vk)
        {
            viewModel.SelectedViewKind = vk;
        }
    }

    public string GetUrlFromSerializableViewModel(ResourcesPageState serializable)
    {
        return DashboardUrls.ResourcesUrl(view: serializable.ViewKind);
    }

    public ResourcesPageState ConvertViewModelToSerializable()
    {
        return new ResourcesPageState
        {
            ViewKind = (PageViewModel.SelectedViewKind != ResourceViewKind.Table) ? PageViewModel.SelectedViewKind.ToString() : null
        };
    }
}
