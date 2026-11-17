using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MioReel.Desktop.Services;
using MioReel.Desktop.ViewModels;
using ReactiveUI.Primitives;

namespace MioReel.Desktop.Views;

public partial class ExploreView : UserControl
{
    private ScrollViewer? _listScrollViewer;
    private MainWindowViewModel? _observedViewModel;
    private bool _isCheckingPrefetch;
    private bool _isCheckQueued;
    private readonly CatalogPrefetchGate _prefetchGate = new();
    private bool _isDraggingScrollbar;
    private Point _lastScrollbarPosition;

    public ExploreView()
    {
        InitializeComponent();
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y < 0) RequestPrefetch();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Down or Key.PageDown or Key.End or Key.Space) RequestPrefetch();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            _isDraggingScrollbar = e.Source is Visual visual &&
                (visual is ScrollBar || visual.FindAncestorOfType<ScrollBar>() is not null);
            _lastScrollbarPosition = e.GetPosition(this);
            if (_isDraggingScrollbar) RequestPrefetch();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            var position = e.GetPosition(this);
            if (_isDraggingScrollbar && position != _lastScrollbarPosition
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) RequestPrefetch();
            _lastScrollbarPosition = position;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _isDraggingScrollbar = false,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        SizeChanged += (_, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.UpdateExploreColumns(Math.Max(180, e.NewSize.Width - 20));
            QueuePrefetchCheck();
        };
        DataContextChanged += (_, _) => ObserveViewModel();
        AttachedToVisualTree += (_, _) =>
        {
            ObserveViewModel();
            AttachScrollViewer();
        };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) QueuePrefetchCheck(); };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_observedViewModel is not null) _observedViewModel.PropertyChanged -= OnCatalogStateChanged;
            _observedViewModel = null;
            if (_listScrollViewer is not null) _listScrollViewer.ScrollChanged -= OnExploreScrollChanged;
            _listScrollViewer = null;
        };
        ExploreVirtualizedList.TemplateApplied += (_, _) => AttachScrollViewer();
        ExploreVirtualizedList.Loaded += (_, _) => AttachScrollViewer();
    }

    private void AttachScrollViewer()
    {
        var sv = ExploreVirtualizedList.FindDescendantOfType<ScrollViewer>();
        if (sv is null || ReferenceEquals(_listScrollViewer, sv))
            return;

        if (_listScrollViewer is not null)
            _listScrollViewer.ScrollChanged -= OnExploreScrollChanged;

        _listScrollViewer = sv;
        _listScrollViewer.ScrollChanged += OnExploreScrollChanged;
        QueuePrefetchCheck();
    }

    private void ObserveViewModel()
    {
        if (ReferenceEquals(_observedViewModel, DataContext)) return;
        if (_observedViewModel is not null) _observedViewModel.PropertyChanged -= OnCatalogStateChanged;
        _observedViewModel = DataContext as MainWindowViewModel;
        _prefetchGate.Reset();
        if (_observedViewModel is not null) _observedViewModel.PropertyChanged += OnCatalogStateChanged;
        QueuePrefetchCheck();
    }

    private void OnCatalogStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoadingCatalog) && _observedViewModel?.IsLoadingCatalog == true)
            _prefetchGate.Reset();
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoadingMoreCatalog)
            && _observedViewModel is { IsLoadingMoreCatalog: true, IsLoadingCatalog: false })
            _prefetchGate.ConsumeRequest();
        if (e.PropertyName is nameof(MainWindowViewModel.IsLoadingCatalog) or nameof(MainWindowViewModel.IsLoadingMoreCatalog)
            or nameof(MainWindowViewModel.HasMoreCatalog)) QueuePrefetchCheck();
    }

    private void OnExploreScrollChanged(object? sender, ScrollChangedEventArgs e) => QueuePrefetchCheck();

    private void RequestPrefetch()
    {
        _prefetchGate.RequestLoad();
        QueuePrefetchCheck();
    }

    private void QueuePrefetchCheck()
    {
        if (_isCheckQueued) return;
        _isCheckQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _isCheckQueued = false;
            CheckPrefetchAsync();
        }, DispatcherPriority.Background);
    }

    private async void CheckPrefetchAsync()
    {
        if (_isCheckingPrefetch || _listScrollViewer is not { } sv || _observedViewModel is not { } vm || !IsEffectivelyVisible) return;
        if (!vm.HasMoreCatalog || vm.IsLoadingCatalog || vm.IsLoadingMoreCatalog) return;
        if (!_prefetchGate.TryStart(sv.Extent.Height, sv.Offset.Y, sv.Viewport.Height)) return;
        _isCheckingPrefetch = true;
        try
        {
            await vm.LoadMoreCatalogCommand.Execute().ToTask();
        }
        finally
        {
            _isCheckingPrefetch = false;
            // Recheck any input received during the request; completion never grants another request.
            QueuePrefetchCheck();
        }
    }
}
