using System.Windows;
using System.Windows.Controls;
using Curfew.Models;
using Curfew.Services;

namespace Curfew;

public partial class MainWindow : Window
{
    private const int FirstHour = 6;

    private readonly WindowsScheduleService _service = new();
    private readonly ScheduleStore _store = new();
    private readonly Dictionary<string, ChildSchedule> _schedules = new();

    private ChildSchedule? _schedule;
    private bool _updating; // suppresses change handlers during programmatic UI updates

    public MainWindow()
    {
        InitializeComponent();

        // "From" offers 6am..11pm; "until" offers the same plus 12am (24).
        var hours = Enumerable.Range(FirstHour, 18).ToList();
        FromBox.ItemsSource = hours.Select(FormatHour).ToList();
        ToBox.ItemsSource = hours.Append(24).Select(FormatHour).ToList();

        HoursGrid.Changed += UpdateWeeklyHours;
        Loaded += async (_, _) => await LoadAccountsAsync();
    }

    private static string FormatHour(int h) => h switch
    {
        0 or 24 => "12am",
        12 => "12pm",
        > 12 => $"{h - 12}pm",
        _ => $"{h}am",
    };

    private async Task LoadAccountsAsync()
    {
        try
        {
            var accounts = await _service.ListLocalAccountsAsync();
            foreach (var account in accounts)
                _schedules[account.ToLowerInvariant()] = await _store.LoadAsync(account) ?? new ChildSchedule(account);

            if (accounts.Count == 0)
            {
                MessageText.Text = "No other local accounts to manage.";
                return;
            }

            MessageText.Visibility = Visibility.Collapsed;
            MainGrid.Visibility = Visibility.Visible;
            AccountList.ItemsSource = accounts;
            AccountList.SelectedIndex = 0;
        }
        catch (Exception e)
        {
            MessageText.Text = $"Could not load accounts: {e.Message}";
        }
    }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AccountList.SelectedItem is not string name) return;
        _schedule = _schedules[name.ToLowerInvariant()];
        StatusText.Text = "";
        Refresh();
    }

    /// <summary>Pushes the current schedule into every control.</summary>
    private void Refresh()
    {
        if (_schedule is null) return;
        _updating = true;

        var same = _schedule.Mode == ScheduleMode.SameEveryDay;
        TitleText.Text = $"{_schedule.Username}'s schedule";
        ModeSame.IsChecked = same;
        ModeDiff.IsChecked = !same;
        SameView.Visibility = same ? Visibility.Visible : Visibility.Collapsed;
        HoursGrid.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
        FromBox.SelectedIndex = Math.Clamp(_schedule.SameRange.From - FirstHour, 0, 17);
        ToBox.SelectedIndex = Math.Clamp(_schedule.SameRange.To - FirstHour, 0, 18);
        HoursGrid.Schedule = _schedule;

        _updating = false;
        UpdateWeeklyHours();
    }

    private void UpdateWeeklyHours() =>
        WeeklyHoursText.Text = $"Allowed this week: {_schedule?.TotalWeeklyHours ?? 0} hrs";

    private void SameRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _schedule is null || FromBox.SelectedIndex < 0 || ToBox.SelectedIndex < 0) return;
        _schedule.SameRange = new TimeRange(FromBox.SelectedIndex + FirstHour, ToBox.SelectedIndex + FirstHour);
        UpdateWeeklyHours();
    }

    private void ModeSame_Click(object sender, RoutedEventArgs e) => ChangeMode(ScheduleMode.SameEveryDay);
    private void ModeDiff_Click(object sender, RoutedEventArgs e) => ChangeMode(ScheduleMode.DifferentEachDay);

    private void ChangeMode(ScheduleMode newMode)
    {
        if (_schedule is null || newMode == _schedule.Mode)
        {
            Refresh();
            return;
        }

        if (newMode == ScheduleMode.SameEveryDay && _schedule.HasCustomData)
        {
            var result = MessageBox.Show(
                this,
                "Switching to \"Same every day\" will replace your custom schedule for each day. Continue?",
                "Switch to same every day?",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK)
            {
                Refresh(); // snap the toggle back to the old mode
                return;
            }
            _schedule.CustomHours.Clear();
        }

        _schedule.Mode = newMode;
        Refresh();
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_schedule is null) return;
        var name = _schedule.Username;
        _schedule = _schedules[name.ToLowerInvariant()] = await _store.LoadAsync(name) ?? new ChildSchedule(name);
        StatusText.Text = "";
        Refresh();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_schedule is null) return;

        SaveButton.IsEnabled = false;
        SaveButton.Content = "Saving...";
        StatusText.Text = "";
        try
        {
            await _service.ApplyScheduleAsync(_schedule);
            await _store.SaveAsync(_schedule);
            StatusText.Text = "Schedule saved.";
        }
        catch (ScheduleValidationException ex)
        {
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
            SaveButton.Content = "Save schedule";
        }
    }
}
