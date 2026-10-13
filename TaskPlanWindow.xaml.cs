using System.Windows;
using System.Windows.Input;
namespace Cdiary;

public partial class TaskPlanWindow : Window
{
    private readonly DiaryRepository _repository;
    private readonly DiaryTask _task;
    public TaskPlanWindow(DiaryRepository repository, DiaryTask task)
    {
        _repository = repository; _task = task;
        InitializeComponent();
        TaskTitle.Text = task.Title;
        StartDate.SelectedDate = task.Date; DeadlineDate.SelectedDate = task.Deadline;
        CarryCheck.IsChecked = task.CarryForward;
        if (task.RecurrenceRuleId is not null)
        {
            StartDate.IsEnabled = CarryCheck.IsEnabled = false;
            CarryHint.Text = "重复任务按原规则生成，本次只可设置截止日期。";
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (StartDate.SelectedDate is not { } date) throw new InvalidOperationException("请选择安排日期。");
            _repository.UpdatePlan(_task, date, DeadlineDate.SelectedDate, CarryCheck.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception error) { ErrorText.Text = error.Message; }
    }
    private void ClearDeadline_Click(object sender, RoutedEventArgs e) => DeadlineDate.SelectedDate = null;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Header_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
