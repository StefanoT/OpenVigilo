using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Vigilo.App.ViewModels;
using Vigilo.Core;

namespace Vigilo.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly Dictionary<string, bool> _trackedMessageSectionExpansionStates = new(StringComparer.Ordinal);
    private readonly List<GridViewColumn> _trackedMessageColumns;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.TrackedMessageViewRequested += OpenTrackedMessageWindow;
        _trackedMessageColumns = ((GridView)TrackedMessagesListView.View).Columns.Cast<GridViewColumn>().ToList();
    }

    private void OpenTrackedMessageWindow(TrackedMessageDetail detail)
    {
        var window = new TrackedMessageWindow(
            detail,
            status => _viewModel.UpdateTrackedMessageStatusAsync(detail, status),
            deadline => _viewModel.UpdateTrackedMessageDeadlineAsync(detail, deadline),
            rule => _viewModel.ApplySenderRuleAsync(detail, rule))
        {
            Owner = this
        };
        window.Show();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void TrackedMessageColumns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } contextMenu } button)
        {
            contextMenu.PlacementTarget = button;
            contextMenu.Placement = PlacementMode.Bottom;
            contextMenu.IsOpen = true;
        }
    }

    private void TrackedMessageColumn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Tag: string header } menuItem
            || TrackedMessagesListView.View is not GridView gridView)
        {
            return;
        }

        var column = _trackedMessageColumns.First(candidate => Equals(candidate.Header, header));
        if (menuItem.IsChecked)
        {
            if (gridView.Columns.Contains(column))
            {
                return;
            }

            var originalIndex = _trackedMessageColumns.IndexOf(column);
            var insertionIndex = gridView.Columns.Count(candidate =>
                _trackedMessageColumns.IndexOf(candidate) < originalIndex);
            gridView.Columns.Insert(insertionIndex, column);
            return;
        }

        if (gridView.Columns.Count == 1)
        {
            menuItem.IsChecked = true;
            return;
        }

        gridView.Columns.Remove(column);
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }

    private void SummaryMetric_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string section })
        {
            return;
        }

        TrackedMessagesListView.UpdateLayout();
        var sectionHeader = FindVisualChild<Expander>(
            TrackedMessagesListView,
            expander => string.Equals(expander.Tag as string, section, StringComparison.Ordinal));
        if (sectionHeader is null)
        {
            return;
        }

        sectionHeader.IsExpanded = true;
        _trackedMessageSectionExpansionStates[section] = true;
        sectionHeader.BringIntoView();
        e.Handled = true;
    }

    private void TrackedMessageSectionExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Tag: string section } expander)
        {
            return;
        }

        expander.IsExpanded = !_trackedMessageSectionExpansionStates.TryGetValue(section, out var isExpanded)
            || isExpanded;
    }

    private void TrackedMessageSectionExpander_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is Expander { Tag: string section } expander && ReferenceEquals(e.OriginalSource, expander))
        {
            _trackedMessageSectionExpansionStates[section] = expander.IsExpanded;
        }
    }

    private void CreatorWebsite_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(
                this,
                "Windows could not open www.tommesani.com in the default browser.",
                "Unable to open website",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void TrackedMessagesListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView listView || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        if (System.Windows.Controls.ItemsControl.ContainerFromElement(listView, source)
            is not System.Windows.Controls.ListViewItem { DataContext: TrackedItemView item })
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.ViewTrackedMessageCommand.CanExecute(item))
        {
            _viewModel.ViewTrackedMessageCommand.Execute(item);
        }
    }

    private void TrackedMessagesListView_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView listView || e.OriginalSource is not DependencyObject source)
        {
            e.Handled = true;
            return;
        }

        if (System.Windows.Controls.ItemsControl.ContainerFromElement(listView, source)
            is not System.Windows.Controls.ListViewItem { DataContext: TrackedItemView item })
        {
            e.Handled = true;
            return;
        }

        listView.SelectedItem = item;

        if (listView.ContextMenu is not null)
        {
            foreach (var reclassify in listView.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>()
                         .Where(menuItem => menuItem.Tag as string == "Reclassify"))
            {
                reclassify.Visibility = item.Status == TrackedItemStatus.NeedsReview
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private async void TrackedMessagesListView_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ListView { SelectedItem: TrackedItemView item })
        {
            return;
        }

        e.Handled = true;
        await CopyTrackedMessageToClipboardAsync(item);
    }

    private async Task CopyTrackedMessageToClipboardAsync(TrackedItemView item)
    {
        try
        {
            var text = await _viewModel.BuildTrackedMessageClipboardTextAsync(item, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            System.Windows.Clipboard.SetText(text);
            _viewModel.NotifyTrackedMessageCopied(item);
        }
        catch (Exception ex)
        {
            _viewModel.NotifyTrackedMessageCopyFailed(ex);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            if (FindVisualChild(child, predicate) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
