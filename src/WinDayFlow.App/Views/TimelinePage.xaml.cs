using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinDayFlow.Application.Timeline;
using WinDayFlow.Domain;
using WinDayFlow.Presentation.Timeline;

namespace WinDayFlow.App.Views;

public sealed partial class TimelinePage : Page
{
    private static readonly char[] TagSeparators = [',', '，'];

    private const double DetailPanePreferredWidth = 384;
    private const double WideLayoutMinWidth = 800;
    private const double MediumLayoutMinWidth = 680;
    private const double NarrowEntryLayoutMaxWidth = 640;
    private const string EditorFallbackErrorText = "无法保存活动，请稍后重试。";
    private const string DeleteFallbackErrorText = "无法删除活动，请刷新时间线后重试。";

    private bool _isEditingEntry;
    private bool _isEditorOpen;
    private bool _isSubscribed;
    private bool _useNarrowEntryLayout;
    private TimeRange? _editorOriginalRange;
    private TimeSpan _editorInitialStartTime;
    private TimeSpan _editorInitialEndTime;
    private TimelineLayout _responsiveLayout;

    public TimelinePage()
    {
        ViewModel = App.GetService<TimelineViewModel>();
        InitializeComponent();
        CategoryFilter.SelectedIndex = 0;
        ProductivityFilter.SelectedIndex = 0;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateVisualState();
    }

    public TimelineViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnPageSizeChanged;
        SizeChanged += OnPageSizeChanged;
        UpdateResponsiveLayout(ActualWidth);

        if (!_isSubscribed)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.Entries.CollectionChanged += OnEntriesChanged;
            _isSubscribed = true;
        }

        if (!ViewModel.IsInitialized)
        {
            await ViewModel.InitializeCommand.ExecuteAsync(null);
        }

        UpdateVisualState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnPageSizeChanged;

        if (_isSubscribed)
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.Entries.CollectionChanged -= OnEntriesChanged;
            _isSubscribed = false;
        }

        ViewModel.Dispose();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void OnEntryLayoutLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control entryLayoutRoot)
        {
            ApplyEntryLayoutState(entryLayoutRoot);
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.SearchText = sender.Text;
        }
    }

    private void OnCategoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedCategory = ParseSelection<ActivityCategory>(CategoryFilter);
    }

    private void OnProductivityFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedProductivity = ParseSelection<ProductivityKind>(ProductivityFilter);
    }

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DetailSplitView.IsPaneOpen = ViewModel.SelectedEntry is not null;
        UpdateDetailState();

        if (DetailSplitView.IsPaneOpen)
        {
            DispatcherQueue.TryEnqueue(() =>
                EditActivityButton.Focus(FocusState.Programmatic));
        }
    }

    private void OnDetailPaneClosed(SplitView sender, object args)
    {
        if (ViewModel.SelectedEntry is not null)
        {
            TimelineList.SelectedItem = null;
        }

        TimelineList.Focus(FocusState.Programmatic);
    }

    private void OnCloseDetails(object sender, RoutedEventArgs e)
    {
        DetailSplitView.IsPaneOpen = false;
        TimelineList.SelectedItem = null;
        TimelineList.Focus(FocusState.Programmatic);
    }

    private async void OnCreateActivity(object sender, RoutedEventArgs e)
    {
        if (_isEditorOpen || ViewModel.IsSaving)
        {
            return;
        }

        PrepareCreateEditor();
        await ShowActivityEditorAsync();
    }

    private async void OnEditActivity(object sender, RoutedEventArgs e)
    {
        if (_isEditorOpen || ViewModel.IsSaving || ViewModel.SelectedEntry is not { } entry)
        {
            return;
        }

        PrepareEditEditor(entry);
        await ShowActivityEditorAsync();
    }

    private async void OnDeleteActivity(object sender, RoutedEventArgs e)
    {
        if (_isEditorOpen || ViewModel.IsSaving || ViewModel.SelectedEntry is not { } entry)
        {
            return;
        }

        ViewModel.ClearMutationError();
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除活动？",
            Content = new TextBlock
            {
                MaxWidth = 440,
                Text = $"“{entry.Title}”将从 {ViewModel.SelectedDateText} 的时间线中删除。此操作无法撤销。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        ContentDialogResult result;
        try
        {
            result = await confirmation.ShowAsync();
        }
        catch (Exception)
        {
            ShowMutationError("无法打开删除确认，请稍后重试。");
            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (!await ViewModel.DeleteSelectedEntryAsync())
            {
                ShowMutationError(GetMutationErrorOrFallback(DeleteFallbackErrorText));
                return;
            }

            DetailSplitView.IsPaneOpen = false;
            TimelineList.SelectedItem = null;
            TimelineList.Focus(FocusState.Programmatic);
        }
        catch (Exception)
        {
            ShowMutationError(DeleteFallbackErrorText);
        }
    }

    private async void OnActivityEditorPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (!TryBuildEditorDraft(out var draft))
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        SetEditorSavingState(true);
        try
        {
            var saved = _isEditingEntry
                ? await ViewModel.UpdateSelectedEntryAsync(draft)
                : await ViewModel.CreateManualEntryAsync(draft);
            if (!saved)
            {
                args.Cancel = true;
                ShowEditorError(GetMutationErrorOrFallback(EditorFallbackErrorText), "保存失败");
            }
            else if (!_isEditingEntry
                && ViewModel.SelectedEntry is null
                && ViewModel.HasActiveFilters)
            {
                ShowMutationMessage(
                    "活动已保存，但当前筛选条件将它隐藏了。清除筛选后即可查看。",
                    "活动已保存");
            }
        }
        catch (Exception)
        {
            args.Cancel = true;
            ShowEditorError(EditorFallbackErrorText, "保存失败");
        }
        finally
        {
            SetEditorSavingState(false);
            deferral.Complete();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateVisualState();

        if (e.PropertyName == nameof(TimelineViewModel.SearchText)
            && SearchBox.Text != ViewModel.SearchText)
        {
            SearchBox.Text = ViewModel.SearchText;
        }

        if (e.PropertyName == nameof(TimelineViewModel.SelectedCategory)
            && ViewModel.SelectedCategory is null)
        {
            CategoryFilter.SelectedIndex = 0;
        }

        if (e.PropertyName == nameof(TimelineViewModel.SelectedProductivity)
            && ViewModel.SelectedProductivity is null)
        {
            ProductivityFilter.SelectedIndex = 0;
        }

        if (e.PropertyName == nameof(TimelineViewModel.SelectedEntry))
        {
            DetailSplitView.IsPaneOpen = ViewModel.SelectedEntry is not null;
            UpdateDetailState();
        }

        if (e.PropertyName == nameof(TimelineViewModel.MutationErrorMessage))
        {
            if (!ViewModel.HasMutationError)
            {
                MutationInfoBar.IsOpen = false;
            }
            else if (_isEditorOpen)
            {
                ShowEditorError(ViewModel.MutationErrorMessage, "保存失败");
            }
            else
            {
                ShowMutationError(ViewModel.MutationErrorMessage);
            }
        }
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateVisualState();
    }

    private void UpdateVisualState()
    {
        var showTimelineContent = !ViewModel.IsLoading && !ViewModel.HasError;
        var showUnprocessedStatus = showTimelineContent
            && (ViewModel.HasUnprocessedIntervals
                || ViewModel.HasUnprocessedIntervalLoadError);

        LoadingPanel.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        TimelineList.Visibility = showTimelineContent && !ViewModel.IsEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnprocessedStatusBand.Visibility = showUnprocessedStatus
            ? Visibility.Visible
            : Visibility.Collapsed;
        UnprocessedStatusList.Visibility = showUnprocessedStatus
            && ViewModel.HasUnprocessedIntervals
                ? Visibility.Visible
                : Visibility.Collapsed;
        UnprocessedStatusError.Visibility = showUnprocessedStatus
            && ViewModel.HasUnprocessedIntervalLoadError
                ? Visibility.Visible
                : Visibility.Collapsed;

        ErrorInfoBar.Message = ViewModel.ErrorMessage;
        EmptyState.Title = ViewModel.HasActiveFilters
            ? "没有匹配的活动"
            : ViewModel.HasUnprocessedIntervals
                ? "尚无已分析活动"
                : "当天没有活动";
        EmptyState.Description = ViewModel.HasActiveFilters
            ? "调整或清除筛选条件后再试。"
            : ViewModel.HasUnprocessedIntervals
                ? "录制内容尚未生成可查看的时间线活动。"
                : "你可以手工新建活动；录制内容分析完成后也会显示在这里。";

        CreateActivityButton.IsEnabled = !ViewModel.IsSaving;
        UpdateDetailState();
    }

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        DetailSplitView.OpenPaneLength = Math.Min(DetailPanePreferredWidth, width);

        var layout = width >= WideLayoutMinWidth
            ? TimelineLayout.Wide
            : width >= MediumLayoutMinWidth
                ? TimelineLayout.Medium
                : TimelineLayout.Narrow;

        if (_responsiveLayout != layout)
        {
            ApplyFilterLayout(layout);
            ApplyToolbarLayout(layout);
            _responsiveLayout = layout;
        }

        var useNarrowEntryLayout = width < NarrowEntryLayoutMaxWidth;
        if (_useNarrowEntryLayout != useNarrowEntryLayout)
        {
            _useNarrowEntryLayout = useNarrowEntryLayout;
            ApplyRealizedEntryLayoutStates(TimelineList);
        }
    }

    private void ApplyRealizedEntryLayoutStates(DependencyObject root)
    {
        if (root is Control { Name: "EntryLayoutRoot" } entryLayoutRoot)
        {
            ApplyEntryLayoutState(entryLayoutRoot);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            ApplyRealizedEntryLayoutStates(VisualTreeHelper.GetChild(root, index));
        }
    }

    private void ApplyEntryLayoutState(Control entryLayoutRoot)
    {
        var entryGrid = FindNamedDescendant<Grid>(entryLayoutRoot, "EntryGrid");
        var timeStack = FindNamedDescendant<StackPanel>(entryLayoutRoot, "TimeStack");
        var contentStack = FindNamedDescendant<StackPanel>(entryLayoutRoot, "ContentStack");
        var metadataStack = FindNamedDescendant<StackPanel>(entryLayoutRoot, "MetadataStack");
        var categoryText = FindNamedDescendant<TextBlock>(entryLayoutRoot, "CategoryTextBlock");
        var productivityText = FindNamedDescendant<TextBlock>(
            entryLayoutRoot,
            "ProductivityTextBlock");

        if (entryGrid is null
            || timeStack is null
            || contentStack is null
            || metadataStack is null
            || categoryText is null
            || productivityText is null)
        {
            return;
        }

        var zero = new GridLength(0);
        var star = new GridLength(1, GridUnitType.Star);
        entryGrid.ColumnSpacing = _useNarrowEntryLayout ? 0 : 16;
        entryGrid.RowSpacing = _useNarrowEntryLayout ? 8 : 0;
        entryGrid.ColumnDefinitions[0].Width = _useNarrowEntryLayout
            ? star
            : new GridLength(104);
        entryGrid.ColumnDefinitions[1].Width = _useNarrowEntryLayout ? zero : star;
        entryGrid.ColumnDefinitions[2].Width = _useNarrowEntryLayout
            ? zero
            : new GridLength(136);

        Grid.SetRow(timeStack, 0);
        Grid.SetColumn(timeStack, 0);
        Grid.SetColumnSpan(timeStack, _useNarrowEntryLayout ? 3 : 1);
        timeStack.Orientation = _useNarrowEntryLayout
            ? Orientation.Horizontal
            : Orientation.Vertical;
        timeStack.Spacing = _useNarrowEntryLayout ? 8 : 2;

        Grid.SetRow(contentStack, _useNarrowEntryLayout ? 1 : 0);
        Grid.SetColumn(contentStack, _useNarrowEntryLayout ? 0 : 1);
        Grid.SetColumnSpan(contentStack, _useNarrowEntryLayout ? 3 : 1);

        Grid.SetRow(metadataStack, _useNarrowEntryLayout ? 2 : 0);
        Grid.SetColumn(metadataStack, _useNarrowEntryLayout ? 0 : 2);
        Grid.SetColumnSpan(metadataStack, _useNarrowEntryLayout ? 3 : 1);
        metadataStack.HorizontalAlignment = _useNarrowEntryLayout
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        metadataStack.Orientation = _useNarrowEntryLayout
            ? Orientation.Horizontal
            : Orientation.Vertical;
        metadataStack.Spacing = _useNarrowEntryLayout ? 12 : 4;

        var textAlignment = _useNarrowEntryLayout
            ? TextAlignment.Left
            : TextAlignment.Right;
        categoryText.TextAlignment = textAlignment;
        productivityText.TextAlignment = textAlignment;
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        if (root is T { Name: var elementName } element
            && string.Equals(elementName, name, StringComparison.Ordinal))
        {
            return element;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var match = FindNamedDescendant<T>(VisualTreeHelper.GetChild(root, index), name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private void ApplyFilterLayout(TimelineLayout layout)
    {
        var zero = new GridLength(0);
        var star = new GridLength(1, GridUnitType.Star);
        var isWide = layout == TimelineLayout.Wide;
        var isNarrow = layout == TimelineLayout.Narrow;

        FilterGrid.RowSpacing = isWide ? 0 : 8;
        FilterSearchRow.Height = GridLength.Auto;
        FilterSecondaryRow.Height = isWide ? zero : GridLength.Auto;
        FilterTertiaryRow.Height = isNarrow ? GridLength.Auto : zero;
        FilterSearchColumn.Width = star;
        FilterCategoryColumn.Width = isWide
            ? new GridLength(176)
            : isNarrow
                ? zero
                : star;
        FilterProductivityColumn.Width = isWide
            ? new GridLength(152)
            : zero;

        Grid.SetRow(SearchBox, 0);
        Grid.SetColumn(SearchBox, 0);
        Grid.SetColumnSpan(SearchBox, isWide ? 1 : 4);

        Grid.SetRow(CategoryFilter, isWide ? 0 : 1);
        Grid.SetColumn(CategoryFilter, isWide ? 1 : 0);
        Grid.SetColumnSpan(CategoryFilter, isNarrow ? 3 : 1);

        Grid.SetRow(ProductivityFilter, isNarrow ? 2 : isWide ? 0 : 1);
        Grid.SetColumn(ProductivityFilter, isWide ? 2 : isNarrow ? 0 : 1);
        Grid.SetColumnSpan(ProductivityFilter, isNarrow ? 4 : 1);

        Grid.SetRow(ClearFiltersButton, isWide ? 0 : 1);
        Grid.SetColumn(ClearFiltersButton, 3);
    }

    private void ApplyToolbarLayout(TimelineLayout layout)
    {
        var isNarrow = layout == TimelineLayout.Narrow;
        ToolbarSecondaryRow.Height = isNarrow ? GridLength.Auto : new GridLength(0);
        TimelineToolbarGrid.RowSpacing = isNarrow ? 8 : 0;
        TimelineToolbarGrid.ColumnSpacing = isNarrow ? 0 : 12;
        SelectedDateLabel.MinWidth = isNarrow ? 136 : 168;

        Grid.SetRow(TimelineActionPanel, isNarrow ? 1 : 0);
        Grid.SetColumn(TimelineActionPanel, isNarrow ? 0 : 1);
        Grid.SetColumnSpan(TimelineActionPanel, isNarrow ? 2 : 1);
        TimelineActionPanel.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private void PrepareCreateEditor()
    {
        ViewModel.ClearMutationError();
        _isEditingEntry = false;
        _editorOriginalRange = null;
        ActivityEditorDialog.Title = "新建活动";
        ActivityEditorDialog.PrimaryButtonText = "创建";
        EditorTitleTextBox.Text = string.Empty;
        EditorSummaryTextBox.Text = string.Empty;
        EditorTagsTextBox.Text = string.Empty;

        var start = GetDefaultStartTime();
        EditorStartTimePicker.Time = start;
        EditorEndTimePicker.Time = start.Add(TimeSpan.FromHours(1));
        _editorInitialStartTime = EditorStartTimePicker.Time;
        _editorInitialEndTime = EditorEndTimePicker.Time;
        SelectComboBoxValue(EditorCategoryPicker, ActivityCategory.Unknown);
        SelectComboBoxValue(EditorProductivityPicker, ProductivityKind.Neutral);
        ResetEditorState();
    }

    private void PrepareEditEditor(TimelineEntryItemViewModel entry)
    {
        ViewModel.ClearMutationError();
        _isEditingEntry = true;
        _editorOriginalRange = entry.Entry.Range;
        ActivityEditorDialog.Title = "编辑活动";
        ActivityEditorDialog.PrimaryButtonText = "保存";
        EditorTitleTextBox.Text = entry.Title;
        EditorSummaryTextBox.Text = entry.Summary;
        EditorStartTimePicker.Time = new TimeSpan(entry.Start.Hour, entry.Start.Minute, 0);
        EditorEndTimePicker.Time = new TimeSpan(entry.End.Hour, entry.End.Minute, 0);
        _editorInitialStartTime = EditorStartTimePicker.Time;
        _editorInitialEndTime = EditorEndTimePicker.Time;
        EditorTagsTextBox.Text = string.Join(", ", entry.Tags);
        SelectComboBoxValue(EditorCategoryPicker, entry.Category);
        SelectComboBoxValue(EditorProductivityPicker, entry.Productivity);
        ResetEditorState();
    }

    private async Task ShowActivityEditorAsync()
    {
        _isEditorOpen = true;
        MutationInfoBar.IsOpen = false;
        try
        {
            ActivityEditorDialog.XamlRoot = XamlRoot;
            await ActivityEditorDialog.ShowAsync();
        }
        catch (Exception)
        {
            ShowMutationError("无法打开活动编辑器，请稍后重试。");
        }
        finally
        {
            _isEditorOpen = false;
            SetEditorSavingState(false);
        }
    }

    private bool TryBuildEditorDraft(out TimelineEntryDraft draft)
    {
        draft = null!;
        var title = EditorTitleTextBox.Text.Trim();
        if (title.Length == 0)
        {
            ShowEditorError("请输入活动标题。");
            EditorTitleTextBox.Focus(FocusState.Programmatic);
            return false;
        }

        TimeRange range;
        if (_editorOriginalRange is not null
            && EditorStartTimePicker.Time == _editorInitialStartTime
            && EditorEndTimePicker.Time == _editorInitialEndTime)
        {
            range = _editorOriginalRange;
        }
        else
        {
            if (!TryCreateEditorDateTimeOffset(
                    EditorStartTimePicker.Time,
                    isStart: true,
                    out var start)
                || !TryCreateEditorDateTimeOffset(
                    EditorEndTimePicker.Time,
                    isStart: false,
                    out var end))
            {
                return false;
            }

            if (end <= start)
            {
                ShowEditorError("结束时间必须晚于开始时间。");
                EditorEndTimePicker.Focus(FocusState.Programmatic);
                return false;
            }

            var lastIncludedInstant = end.AddTicks(-1);
            if (DateOnly.FromDateTime(start.DateTime)
                != DateOnly.FromDateTime(lastIncludedInstant.DateTime))
            {
                ShowEditorError("活动不能跨越自然日，请拆分为两条记录。");
                return false;
            }

            range = new TimeRange(start, end);
        }

        var category = ParseSelection<ActivityCategory>(EditorCategoryPicker);
        var productivity = ParseSelection<ProductivityKind>(EditorProductivityPicker);
        if (!category.HasValue || !productivity.HasValue)
        {
            ShowEditorError("请选择活动类别和效率。");
            return false;
        }

        try
        {
            draft = new TimelineEntryDraft(
                range,
                title,
                EditorSummaryTextBox.Text,
                category.Value,
                productivity.Value,
                NormalizeTags(EditorTagsTextBox.Text));
            EditorErrorInfoBar.IsOpen = false;
            return true;
        }
        catch (ArgumentException)
        {
            ShowEditorError("活动信息无效，请检查后重试。");
            return false;
        }
    }

    private bool TryCreateEditorDateTimeOffset(
        TimeSpan time,
        bool isStart,
        out DateTimeOffset value)
    {
        if (_editorOriginalRange is not null)
        {
            var original = isStart ? _editorOriginalRange.Start : _editorOriginalRange.End;
            var originalDate = DateOnly.FromDateTime(original.DateTime);
            value = new DateTimeOffset(
                originalDate.ToDateTime(TimeOnly.FromTimeSpan(time), DateTimeKind.Unspecified),
                original.Offset);
            return true;
        }

        var localDateTime = ViewModel.SelectedDate.ToDateTime(
            TimeOnly.FromTimeSpan(time),
            DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(localDateTime)
            || TimeZoneInfo.Local.IsAmbiguousTime(localDateTime))
        {
            ShowEditorError("所选时间位于夏令时切换区间，请选择其他时间。");
            value = default;
            return false;
        }

        value = new DateTimeOffset(
            localDateTime,
            TimeZoneInfo.Local.GetUtcOffset(localDateTime));
        return true;
    }

    private TimeSpan GetDefaultStartTime()
    {
        var now = DateTime.Now;
        if (ViewModel.SelectedDate != DateOnly.FromDateTime(now))
        {
            return TimeSpan.FromHours(9);
        }

        var rounded = new TimeSpan(now.Hour, (now.Minute / 15) * 15, 0);
        return rounded > TimeSpan.FromHours(22.5)
            ? TimeSpan.FromHours(22.5)
            : rounded;
    }

    private static string[] NormalizeTags(string text)
    {
        return text
            .Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void SelectComboBoxValue<TEnum>(ComboBox comboBox, TEnum value)
        where TEnum : struct, Enum
    {
        var target = value.ToString();
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, target, StringComparison.Ordinal));
    }

    private void ResetEditorState()
    {
        EditorErrorInfoBar.IsOpen = false;
        EditorErrorInfoBar.Message = string.Empty;
        SetEditorSavingState(false);
    }

    private void SetEditorSavingState(bool isSaving)
    {
        EditorTitleTextBox.IsEnabled = !isSaving;
        EditorSummaryTextBox.IsEnabled = !isSaving;
        EditorStartTimePicker.IsEnabled = !isSaving;
        EditorEndTimePicker.IsEnabled = !isSaving;
        EditorCategoryPicker.IsEnabled = !isSaving;
        EditorProductivityPicker.IsEnabled = !isSaving;
        EditorTagsTextBox.IsEnabled = !isSaving;
        ActivityEditorDialog.IsPrimaryButtonEnabled = !isSaving;
        ActivityEditorDialog.IsSecondaryButtonEnabled = !isSaving;
        EditorProgressBar.Visibility = isSaving ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEditorError(string message, string title = "请检查活动信息")
    {
        EditorErrorInfoBar.Title = title;
        EditorErrorInfoBar.Message = message;
        EditorErrorInfoBar.IsOpen = true;
    }

    private void ShowMutationError(string message)
    {
        MutationInfoBar.Severity = InfoBarSeverity.Error;
        MutationInfoBar.Title = "无法完成操作";
        MutationInfoBar.Message = message;
        MutationInfoBar.IsOpen = true;
    }

    private void ShowMutationMessage(string message, string title)
    {
        MutationInfoBar.Severity = InfoBarSeverity.Success;
        MutationInfoBar.Title = title;
        MutationInfoBar.Message = message;
        MutationInfoBar.IsOpen = true;
    }

    private string GetMutationErrorOrFallback(string fallback)
    {
        return string.IsNullOrWhiteSpace(ViewModel.MutationErrorMessage)
            ? fallback
            : ViewModel.MutationErrorMessage;
    }

    private void UpdateDetailState()
    {
        var entry = ViewModel.SelectedEntry;
        var canMutate = entry is not null && !ViewModel.IsSaving;
        EditActivityButton.IsEnabled = canMutate;
        DeleteActivityButton.IsEnabled = canMutate;
        ManualEvidenceInfoBar.Visibility = entry is { HasEvidence: false }
            ? Visibility.Visible
            : Visibility.Collapsed;
        AnalyzedEvidenceInfoBar.Visibility = entry is { HasEvidence: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
        AnalyzedEvidenceMetadataPanel.Visibility = entry is { HasEvidence: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static TEnum? ParseSelection<TEnum>(ComboBox comboBox)
        where TEnum : struct, Enum
    {
        return comboBox.SelectedItem is ComboBoxItem { Tag: string value }
            && Enum.TryParse<TEnum>(value, out var parsed)
                ? parsed
                : null;
    }

    private enum TimelineLayout
    {
        Unset,
        Narrow,
        Medium,
        Wide,
    }
}
