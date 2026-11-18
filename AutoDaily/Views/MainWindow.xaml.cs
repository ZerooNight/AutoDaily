using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AutoDaily.Models;
using AutoDaily.Services;
using AutoDaily.ViewModels;

namespace AutoDaily.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _forceClose;

    // 拖曳排序狀態
    private GameTask? _dragItem;
    private Point _dragStart;
    private double _grabOffsetY;
    private double _dragHeight;
    private bool _dragging;
    private DragAdorner? _adorner;
    private AdornerLayer? _adornerLayer;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // 小螢幕（例如 1080p + 125% 縮放）時不要超出工作區
        var maxHeight = SystemParameters.WorkArea.Height - 16;
        if (Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);

        vm.Logs.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add) LogScroll.ScrollToEnd();
        };
        StateChanged += (_, _) => UpdateMaximizedState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.Apply(this);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_forceClose || _vm.IsIdle)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (!MessageDialog.Show(this, "任務執行中", "目前仍有任務正在執行，確定要中止並關閉嗎？\n正在執行的自動化程式會被關閉。",
                "中止並關閉", "繼續執行", danger: true))
            return;

        _vm.Stop();
        await _vm.WaitIdleAsync();
        _forceClose = true;
        Close();
    }

    /// <summary>從另一個實例收到「顯示視窗」的要求。</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // ───────────────────────── 標題列 ─────────────────────────

    private void UpdateMaximizedState()
    {
        // 無邊框視窗最大化時會超出螢幕邊緣，用內距補回來
        RootBorder.Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_vm.Settings) { Owner = this };
        if (dialog.ShowDialog() == true) _vm.Save();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Logger.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Logger.LogDir}\"") { UseShellExecute = true });
    }

    // ───────────────────────── 任務 ─────────────────────────

    private void AddTask_Click(object sender, RoutedEventArgs e) => OpenEditor(new GameTask(), isNew: true);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameTask task }) OpenEditor(task, isNew: false);
    }

    private void RunOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GameTask task }) _ = _vm.RunAsync(task);
    }

    private void OpenEditor(GameTask task, bool isNew)
    {
        if (!_vm.IsIdle) return;
        var copy = task.Clone();
        var dialog = new TaskEditWindow(copy, isNew) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (dialog.Deleted)
        {
            _vm.RemoveTask(task);
            Logger.Write($"已刪除任務：{task.Name}");
            return;
        }

        task.CopyFrom(copy);
        if (isNew)
        {
            _vm.AddTask(task);
            Logger.Write($"已新增任務：{task.Name}");
        }
        else
        {
            _vm.Save();
        }
        _vm.UpdateSummary();
    }

    // ───────────────────────── 拖曳排序 ─────────────────────────

    private void TaskList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.IsIdle || e.OriginalSource is not DependencyObject source || IsInteractive(source)) return;
        if (ItemsControl.ContainerFromElement(TaskList, source) is not FrameworkElement container ||
            container.DataContext is not GameTask task)
            return;

        if (e.ClickCount == 2)
        {
            OpenEditor(task, isNew: false);
            e.Handled = true;
            return;
        }

        _dragItem = task;
        _dragStart = e.GetPosition(TaskList);
        _grabOffsetY = e.GetPosition(container).Y;
        _dragHeight = container.ActualHeight;
    }

    private void TaskList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var p = e.GetPosition(TaskList);
        if (!_dragging)
        {
            if (Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance &&
                Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance)
                return;
            if (!BeginDrag()) return;
        }

        UpdateDrag(p);
        AutoScroll(e.GetPosition(TaskScroll));
    }

    private void TaskList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragItem != null) EndDrag();
    }

    private void TaskList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) EndDrag();
    }

    private bool BeginDrag()
    {
        if (_dragItem == null || ContainerOf(_dragItem) is not FrameworkElement container) return false;

        _adornerLayer = AdornerLayer.GetAdornerLayer(TaskList);
        if (_adornerLayer == null) return false;

        _adorner = new DragAdorner(TaskList, DragAdorner.Snapshot(container),
            new Size(container.ActualWidth, container.ActualHeight));
        _adornerLayer.Add(_adorner);

        _dragging = true;
        _dragItem.IsDragging = true;
        TaskList.CaptureMouse();
        Mouse.OverrideCursor = Cursors.SizeAll;
        return true;
    }

    private void UpdateDrag(Point p)
    {
        if (_dragItem == null || _adorner == null) return;

        var maxY = Math.Max(0, TaskList.ActualHeight - _dragHeight);
        var y = Math.Clamp(p.Y - _grabOffsetY, -8, maxY + 8);
        _adorner.SetPosition(0, y);

        // 以拖曳中卡片的中心點，跟其他卡片的中線比較，決定插入位置
        var center = y + _dragHeight / 2;
        var from = _vm.Tasks.IndexOf(_dragItem);
        var to = 0;
        for (var i = 0; i < _vm.Tasks.Count; i++)
        {
            if (i == from || ContainerOf(_vm.Tasks[i]) is not FrameworkElement c) continue;
            if (center > LayoutY(c) + c.ActualHeight / 2) to++;
        }

        if (to != from) MoveAnimated(from, to);
    }

    private void MoveAnimated(int from, int to)
    {
        // 先記錄每張卡片目前看起來的位置，移動後再從舊位置滑到新位置
        var before = new Dictionary<GameTask, double>();
        foreach (var t in _vm.Tasks)
        {
            if (t != _dragItem && ContainerOf(t) is FrameworkElement c)
                before[t] = LayoutY(c) + CurrentOffset(c);
        }

        _vm.Tasks.Move(from, to);
        TaskList.UpdateLayout();

        foreach (var (t, oldVisualY) in before)
        {
            if (ContainerOf(t) is not FrameworkElement c) continue;
            var delta = oldVisualY - LayoutY(c);
            if (Math.Abs(delta) < 0.5) continue;

            if (c.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
            {
                tt = new TranslateTransform();
                c.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        _vm.Renumber();
    }

    private void EndDrag()
    {
        var wasDragging = _dragging;
        _dragging = false;

        if (_adorner != null)
        {
            _adornerLayer?.Remove(_adorner);
            _adorner = null;
        }
        if (_dragItem != null) _dragItem.IsDragging = false;
        _dragItem = null;

        Mouse.OverrideCursor = null;
        if (TaskList.IsMouseCaptured) TaskList.ReleaseMouseCapture();

        if (wasDragging)
        {
            _vm.Renumber();
            _vm.Save();
        }
    }

    private void AutoScroll(Point inScroll)
    {
        const double edge = 28;
        if (inScroll.Y < edge)
            TaskScroll.ScrollToVerticalOffset(TaskScroll.VerticalOffset - 10);
        else if (inScroll.Y > TaskScroll.ActualHeight - edge)
            TaskScroll.ScrollToVerticalOffset(TaskScroll.VerticalOffset + 10);
    }

    private FrameworkElement? ContainerOf(GameTask task) =>
        TaskList.ItemContainerGenerator.ContainerFromItem(task) as FrameworkElement;

    /// <summary>容器在清單中的版面位置（扣掉正在播放的位移動畫）。</summary>
    private double LayoutY(FrameworkElement c) =>
        c.TranslatePoint(new Point(0, 0), TaskList).Y - CurrentOffset(c);

    private static double CurrentOffset(FrameworkElement c) =>
        c.RenderTransform is TranslateTransform tt ? tt.Y : 0;

    /// <summary>點在按鈕、開關上時不要開始拖曳。</summary>
    private bool IsInteractive(DependencyObject? d)
    {
        while (d != null && d != TaskList)
        {
            if (d is ButtonBase or TextBoxBase) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }
}
