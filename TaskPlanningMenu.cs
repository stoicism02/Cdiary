using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
namespace Cdiary;

internal static class TaskPlanningMenu
{
    public static ContextMenu Create(Window owner, DiaryTask task, DiaryRepository repository, Func<Task> flush, DateTime? day = null)
    {
        var menu = new ContextMenu();
        void Add(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                try { await flush(); action(); }
                catch (Exception error) { System.Windows.MessageBox.Show(owner, error.Message, "小岛日记"); }
            };
            menu.Items.Add(item);
        }
        Add("日期与推进…", () => new TaskPlanWindow(repository, task) { Owner = owner }.ShowDialog());
        var today = DateTime.Today;
        Add(task.IsShelvedOn(today) ? "继续处理" : "今天先到这里",
            () => repository.SetDayShelved(task, DateTime.Today, !task.IsShelvedOn(DateTime.Today)),
            !task.IsDone && task.Date <= today && (day is null || day.Value.Date == today));
        return menu;
    }
}
