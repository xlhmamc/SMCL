using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using SMCL.ViewModels;

namespace SMCL.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 监听 DataContext 挂载事件
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            // 取消旧订阅，防止反复绑定
            vm.DisplayLogs.CollectionChanged -= OnDisplayLogsChanged;
            // 订阅 DisplayLogs 的更新事件
            vm.DisplayLogs.CollectionChanged += OnDisplayLogsChanged;
        }
    }

    private void OnDisplayLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 只要有新日志插入，就在 UI 线程触发滚动到底部
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(() =>
            {
                LogScrollViewer?.ScrollToEnd();
            });
        }
    }
}