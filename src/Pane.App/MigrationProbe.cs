using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Dispatching;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Services.Modules;
using Pane.Settings;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Pane;
public sealed partial class App {
    MigrationHarness? migration;
    async Task StartMigrationProbe() {
        migration = new MigrationHarness(Glass, Store, queue);
        Modules = migration.Host;
        await migration.Host.InitializeAsync(cancellation.Token);
        migration.ShowDesktop();
    }
}

// Reuse Pane ModuleHost, GlassPanel, IPanelView, XAML controls and compositor.
// This isolated spike adds only the missing business state and presentation;
// it has no Electron/Node runtime and never imports a user's old data.
internal sealed class MigrationHarness {
    readonly GlassParameters glass;
    readonly SettingsStore store;
    readonly DispatcherQueue queue;
    readonly List<GlassPanel> windows = [];
    readonly List<GlassPanel> ordinary = [];
    readonly DispatcherQueueTimer frames;
    readonly DispatcherQueueTimer sampling;
    GlassPanel? desktop;
    bool showingDesktop;
    string draft = "Pane migration draft — edit this text";
    string traffic = "Waiting for a real network sample";
    int revision, samples, switches, views, cancelled, completed;
    public ModuleHost Host { get; }
    public event Action? Changed;
    public string Draft => draft;
    public string Traffic => traffic;
    public string Evidence => $"Views created: {views} | switches: {switches} | samples: {samples} | cancelled: {cancelled} | completed: {completed}";
    public string ImageFile => Path.Combine(AppContext.BaseDirectory, "probe-history.png");
    public MigrationHarness(GlassParameters glass, SettingsStore store, DispatcherQueue queue) {
        this.glass=glass; this.store=store; this.queue=queue;
        frames=queue.CreateTimer(); frames.Interval=TimeSpan.FromMilliseconds(8);
        frames.Tick+=(_,_)=>{foreach(var p in windows) if(p.Active)p.Tick(); if(!windows.Any(p=>p.Active))frames.Stop();};
        sampling=queue.CreateTimer(); sampling.Interval=TimeSpan.FromSeconds(1);
        sampling.Tick+=(_,_)=>Sample();
        Host=new ModuleHost([new ProbeModule("probe-notes",()=>MakeOrdinary("notes")),
            new ProbeModule("probe-history",()=>MakeOrdinary("history")),
            new ProbeModule("probe-traffic",()=>{MakeOrdinary("traffic");sampling.Start();}),
            new ProbeModule("probe-desktop",MakeDesktop)],store,()=>false);
    }
    GlassPanel MakePanel(ProbeView view,string title, bool secondary) {
        var panel=new GlassPanel(title,view,view,glass,settingsWindow:secondary,minSize:new Size(320,200));
        windows.Add(panel); panel.TickRequested+=()=>frames.Start(); panel.Prepare();
        return panel;
    }
    void MakeOrdinary(string kind) {
        var panel=MakePanel(new ProbeView(this,kind,false),"Pane probe · "+kind,true);
        ordinary.Add(panel);
    }
    void MakeDesktop() {
        desktop=MakePanel(new ProbeView(this,"desktop",true),"Pane migration · Card desktop",true);
        desktop.Hidden+=(_,_)=>{if(showingDesktop)ShowOrdinary();};
    }
    public void ShowDesktop() {
        showingDesktop=true; switches++;
        desktop!.Show(); foreach(var p in ordinary)p.Hide();
        Changed?.Invoke(); Record("desktop");
    }
    public void ShowOrdinary() {
        showingDesktop=false; switches++;
        for(int i=0;i<ordinary.Count;i++) ordinary[i].Show(near:true,px:80+i*440,py:180,activate:i==0);
        desktop!.Hide(); Changed?.Invoke(); Record("ordinary");
    }
    public void Toggle(){if(showingDesktop)ShowOrdinary();else ShowDesktop();}
    public void Exit(){sampling.Stop();frames.Stop();foreach(var p in windows)p.Dispose();Application.Current.Exit();}
    public void Edit(string value){if(draft==value)return;draft=value;revision++;Changed?.Invoke();Record("edit");}
    public void ViewCreated(){views++;}
    public void Cancelled(){cancelled++;Changed?.Invoke();Record("cancel");}
    public void Completed(){completed++;Changed?.Invoke();Record("complete");}
    void Sample() {
        try {
            long rx=0,tx=0;
            foreach(var n in NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)){
                var s=n.GetIPStatistics();rx+=s.BytesReceived;tx+=s.BytesSent;
            }
            samples++;traffic=$"Real adapter totals\nReceived: {rx:N0} bytes\nSent: {tx:N0} bytes\nSamples: {samples}";
        } catch(Exception e){traffic=e.Message;}
        Changed?.Invoke();
        if(samples%10==0)Record("sampling");
    }
    public void Record(string operation) {
        File.WriteAllText(Path.Combine(store.DirectoryPath,"probe-state.json"),JsonSerializer.Serialize(new{
            operation,revision,draft,samples,switches,views,cancelled,completed,showingDesktop,
            handles=windows.Select(w=>new{input=w.Input.ToInt64(),visual=w.Visual.ToInt64(),w.Shown}).ToArray()
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    sealed class ProbeModule(string id,Action start):ICapabilityModule {
        public ModuleInfo Info=>new(id,ModuleCategory.System,"square",true,[]);
        public Task StartAsync(CancellationToken token){token.ThrowIfCancellationRequested();start();return Task.CompletedTask;}
        public Task StopAsync(CancellationToken token)=>Task.CompletedTask;
    }
}

internal sealed class ProbeView : UserControl, IPanelView {
    readonly MigrationHarness state;
    readonly StackPanel root=new(){Spacing=12,Padding=new Thickness(20)};
    readonly string kind;
    readonly bool card;
    readonly TextBlock evidence=new(){FontSize=12,TextWrapping=TextWrapping.Wrap};
    readonly TextBlock rate=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock taskState=new(){Text="No page task"};
    TextBox? editor;
    CancellationTokenSource? task;
    bool active,editing;
    public Size PreferredSize=>kind=="desktop"?new(1160,680):new(410,480);
    public event EventHandler<Size>? SizeRequested;
    public event EventHandler? CloseRequested;
    public UIElement? DragRegion=>null;
    public ProbeView(MigrationHarness state,string kind,bool card) {
        this.state=state;this.kind=kind;this.card=card;state.ViewCreated();
        root.Children.Add(new TextBlock{Text=kind=="desktop"?"Pane native migration probe":"Pane · "+kind,FontSize=22});
        root.Children.Add(new TextBlock{Text="Native window/lifecycle spike · isolated data",TextWrapping=TextWrapping.Wrap});
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        AddButton(controls,card?"Show ordinary windows":"Show card desktop",()=>state.Toggle());
        AddButton(controls,"Close this presentation",()=>CloseRequested?.Invoke(this,EventArgs.Empty));
        if(kind=="desktop")AddButton(controls,"Exit probe",state.Exit);
        root.Children.Add(controls);
        if(kind=="desktop"){
            var columns=new Grid{ColumnSpacing=16};
            for(int i=0;i<3;i++)columns.ColumnDefinitions.Add(new ColumnDefinition());
            string[] kinds=["notes","history","traffic"];
            for(int i=0;i<3;i++){
                var child=new ProbeView(state,kinds[i],true); Grid.SetColumn(child,i); columns.Children.Add(child);
                child.CloseRequested+=(_,_)=>{child.Visibility=Visibility.Collapsed;child.OnHidden();};
                children.Add(child);
            }
            root.Children.Add(columns);
        }else if(kind=="notes"){
            editor=new TextBox{Text=state.Draft,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=180,Header="Shared note draft"};
            editor.TextChanged+=(_,_)=>{if(!editing)state.Edit(editor.Text);}; root.Children.Add(editor);
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
            AddButton(actions,"Start 8s page task",StartTask);AddButton(actions,"Cancel task",Cancel);
            root.Children.Add(actions);root.Children.Add(taskState);
        }else if(kind=="history"){
            root.Children.Add(new TextBlock{Text="Fixture gallery — Pane documentation image",TextWrapping=TextWrapping.Wrap});
            var image=new Image{Height=170,Source=new BitmapImage(new Uri(state.ImageFile))};
            image.ImageOpened+=(_,_)=>state.Record("image-decoded");root.Children.Add(image);
            root.Children.Add(new TextBlock{Text="Image view and decoded source stay alive across switches.",TextWrapping=TextWrapping.Wrap});
        }else {
            root.Children.Add(new TextBlock{Text="Real network counters; process traffic service not connected.",TextWrapping=TextWrapping.Wrap});root.Children.Add(rate);
        }
        root.Children.Add(evidence); Content=new ScrollViewer{Content=root};
        state.Changed+=Refresh; Refresh();
        PreviewKeyDown+=(_,e)=>{if(!e.Handled&&e.Key==VirtualKey.F8){e.Handled=true;state.Toggle();}};
    }
    readonly List<ProbeView> children=[];
    void Refresh(){editing=true;if(editor!=null&&editor.Text!=state.Draft)editor.Text=state.Draft;editing=false;rate.Text=state.Traffic;evidence.Text=state.Evidence;}
    static void AddButton(Panel parent,string text,Action action){var b=new Button{Content=text};b.Click+=(_,_)=>action();parent.Children.Add(b);}
    async void StartTask(){
        Cancel();var current=task=new();taskState.Text="Running page task…";
        try{await Task.Delay(8000,current.Token);if(active&&task==current){taskState.Text="Result accepted";state.Completed();}}
        catch(OperationCanceledException){}finally{if(task==current)task=null;current.Dispose();}
    }
    void Cancel(){if(task is {} pending){task=null;pending.Cancel();taskState.Text="Cancelled";state.Cancelled();}}
    public void OnShowing(PanelShowContext context){active=true;foreach(var child in children)child.OnShowing(context);Refresh();}
    public void OnShown(){state.Record("shown");}
    public void OnHidden(){active=false;Cancel();foreach(var child in children)child.OnHidden();state.Record("hidden");}
}
