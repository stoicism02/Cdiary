using System.Windows;
namespace Cdiary;
public partial class MainWindow
{
    private FocusStore? _focusStore;
    private FocusService? _focus;
    private void InitializeFocus(FocusStore? store,FocusService? focus)
    {
        _focusStore=store;_focus=focus;
        if(store is null||focus is null){TaskFocus.Visibility=Visibility.Collapsed;FocusTab.Visibility=Visibility.Collapsed;return;}
        FocusView.Attach(_repository,store,focus);TaskFocus.Attach(store,focus);TaskFocus.OpenRequested+=OpenFocusTask;
        focus.Changed += FocusCalendarChanged;
        Closed += (_, _) => focus.Changed -= FocusCalendarChanged;
    }
    private void FocusCalendarChanged() { if (CalendarPage.Visibility == Visibility.Visible) RefreshCalendar(); }
    public void ShowFocusPage()
    {
        HomePage.Visibility=CalendarPage.Visibility=SearchPage.Visibility=CompletedPage.Visibility=SettingsPage.Visibility=ScreenshotGallery.Visibility=Visibility.Collapsed;
        FocusView.Visibility=Visibility.Visible;FocusTab.IsChecked=true;
    }
    private void OpenFocusTask(DiaryTask task){ShowFocusPage();FocusView.SelectTask(task);if(!IsVisible)Show();Activate();}
    private void FocusTab_Click(object sender,RoutedEventArgs e)=>ShowFocusPage();
}
