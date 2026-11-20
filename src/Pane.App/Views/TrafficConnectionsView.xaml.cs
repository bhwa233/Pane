using System.Net.NetworkInformation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Pane.Hosting;
using Pane.Localization;
using Pane.Services.Traffic;
using Pane.Views.Controls;
using Windows.Foundation;
using Windows.System;

namespace Pane.Views;

public sealed partial class TrafficConnectionsView : UserControl, ISecondaryView, IDisposable
{
    readonly PaneSegmented kind;
    TrafficProcessIdentity? process;
    CancellationTokenSource? pending;

    internal TrafficConnectionsView()
    {
        InitializeComponent();
        kind = new([(TrafficConnectionsClient.Tcp, "TCP"), (TrafficConnectionsClient.Udp, "UDP")], TrafficConnectionsClient.Tcp,
            ModeStrong, ModeDim, ModeFill.Background, ModeFill.BorderBrush);
        AutomationProperties.SetName(kind, Strings.Traffic_ConnectionsProtocol);
        kind.Changed += (_, _) => _ = RefreshAsync();
        KindHost.Child = kind;
        var close = new KeyboardAccelerator { Key = VirtualKey.W, Modifiers = VirtualKeyModifiers.Control };
        close.Invoked += (_, args) => { args.Handled = true; CloseRequested?.Invoke(this, EventArgs.Empty); };
        KeyboardAccelerators.Add(close);
    }

    public Size PreferredSize => new(550, 370);
    public Size MinimumSize => new(380, 290);
    public UIElement DragRegion => Header;
    event EventHandler<Size>? IPanelView.SizeRequested { add { } remove { } }
    event EventHandler<bool>? ISecondaryView.TopmostChanged { add { } remove { } }
    public event EventHandler? CloseRequested;
    public void OnShowing(PanelShowContext context) { }
    public void OnShown() { }
    public void OnHidden() => Dispose();
    public void Dispose() { pending?.Cancel(); pending = null; }

    internal void Open(TrafficProcessIdentity target)
    {
        if (process != target) kind.SetValue(TrafficConnectionsClient.Tcp);
        process = target;
        ProcessName.Text = $"{target.Name} (PID {target.Pid})";
        ToolTipService.SetToolTip(ProcessName, ProcessName.Text);
        _ = RefreshAsync();
    }

    async void RefreshClicked(object sender, RoutedEventArgs args) => await RefreshAsync();
    void CloseClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, EventArgs.Empty);

    async Task RefreshAsync()
    {
        if (process is not { } target) return;
        pending?.Cancel();
        using var request = new CancellationTokenSource();
        pending = request;
        RefreshButton.IsEnabled = false;
        Endpoints.Items.Clear();
        Status.Text = Strings.Traffic_ProcessConnecting;
        try
        {
            var result = await TrafficConnectionsClient.ReadAsync(target, kind.Value, request.Token);
            if (request.IsCancellationRequested || pending != request) return;
            Status.Text = result.State switch
            {
                TrafficConnectionsState.NotInstalled => Strings.Traffic_ProcessNotInstalled,
                TrafficConnectionsState.AccessDenied => Strings.Traffic_ProcessAccessDenied,
                TrafficConnectionsState.Disabled => Strings.Traffic_ProcessDisabled,
                TrafficConnectionsState.ProcessGone => Strings.Traffic_ConnectionsProcessGone,
                TrafficConnectionsState.Available when result.Rows.Count == 0 => Strings.Traffic_ConnectionsEmpty,
                TrafficConnectionsState.Available when result.Truncated => string.Format(Strings.Traffic_ConnectionsLimited, result.Rows.Count),
                TrafficConnectionsState.Available => string.Format(Strings.Traffic_ConnectionsCount, result.Rows.Count),
                _ => Strings.Traffic_ConnectionsUnavailable
            };
            foreach (var row in result.Rows) Endpoints.Items.Add(CreateItem(row));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally
        {
            if (pending == request) { pending = null; RefreshButton.IsEnabled = true; }
        }
    }

    ListViewItem CreateItem(TrafficEndpoint row)
    {
        var title = new TextBlock { Text = row.Remote == null ? row.Local : $"{row.Local}  →  {row.Remote}",
            FontSize = ModeStrong.FontSize, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var state = row.State switch
        {
            null => Strings.Traffic_ConnectionsUdpLocal,
            TcpState.Listen => Strings.Traffic_ConnectionsListen,
            TcpState.Established => Strings.Traffic_ConnectionsEstablished,
            TcpState.Unknown => Strings.Traffic_ConnectionsUnknown,
            _ => row.State.ToString()
        };
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(title);
        content.Children.Add(new TextBlock { Text = state, FontSize = ModeDim.FontSize, Foreground = ModeDim.Foreground });
        var item = new ListViewItem { Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(8, 5, 8, 5) };
        AutomationProperties.SetName(item, title.Text + " · " + state);
        return item;
    }
}
