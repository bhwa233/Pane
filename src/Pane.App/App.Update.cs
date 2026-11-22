using Pane.Localization;
using Pane.Contracts;
using Pane.Hosting;
using Pane.Services.Updates;
namespace Pane;
public sealed partial class App
{
    public IUpdateService? Updates {get;private set;}
    /// <summary>随安装包带着的历代更新说明（062）：关于页的更新日志、「更新后」提示条用。工程在启动时赋值（演示模式也用真实的打包数据）；为 null 时这些地方不显示。</summary>
    public IReleaseNotesCatalog? ReleaseHistory {get;set;}=new Pane.Services.Updates.ReleaseNotesCatalog(
        message=>new Pane.Services.SelectionLog(Pane.Hosting.RuntimeMode.Current.DataDirectory).Write(message));
    string? notifiedUpdate;
    bool StartUpdates()
    {
        if(demo)Updates??=new Pane.DesignData.UpdateSample();
        else if(RuntimeMode.PortableExecutable!=null)
        {
            Updates=new UpdateService(RunningVersion,Store.DirectoryPath,()=>Store.Current.Update.AutoUpdate,null,
                portableInstall:(path,hash)=>{
                    RestartProcess.PreparePortableUpdate(path,hash);
                    Shutdown();
                });
        }
        if(Updates is not {} u)return false;
        u.Changed+=(_,_)=>queue.TryEnqueue(()=>{
            if(closing||u.State!=UpdateState.Ready||u.Latest is not {} r||notifiedUpdate==r.Version)return;
            notifiedUpdate=r.Version;
            shell?.NotifyOnce(string.Format(Strings.Update_ReadyNotification, (object?)(r.Version)));
        });
        return u is UpdateService service && StartupUpdate.TryInstall(service,Store);
    }
}
