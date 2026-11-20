using System;
using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Spark.Traffic;

namespace Pane.TrafficService {
  sealed class TrafficWindowsService:ServiceBase {
    TrafficEngine engine;PipeServer server;DirectoryGuard codeGuard,dataGuard;
    public TrafficWindowsService(){ServiceName="PaneTraffic";CanStop=true;AutoLog=false;}
    protected override void OnStart(string[] args) {
      var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Pane","traffic","data");
      try {
        codeGuard=DirectoryGuard.OpenProtected(AppDomain.CurrentDomain.BaseDirectory,true);dataGuard=DirectoryGuard.OpenProtected(data,false);
        engine=new TrafficEngine(data);server=new PipeServer(engine,"pane-traffic-v1");
      }catch{
        try{StopComponents();}catch(Exception error){Console.Error.WriteLine("traffic-start-cleanup:"+error.GetType().Name);}
        throw;
      }
    }
    protected override void OnStop(){RequestAdditionalTime(20000);StopComponents();}
    void StopComponents() {
      try{server?.Dispose();}finally{try{engine?.Dispose();}finally{try{dataGuard?.Dispose();}finally{codeGuard?.Dispose();}}}
    }
  }
  static class Program {
    static int Main(string[] args) {
      if(args.Length==5&&args[0]=="--console"&&args[1]=="--isolate"&&args[3]=="--pipe"&&args[4].StartsWith("pane-traffic-test-",StringComparison.Ordinal)) {
        var root=Path.GetFullPath(args[2]);if(!Path.IsPathRooted(args[2]))return 2;
        Directory.CreateDirectory(root);
        try {
          using(var identity=WindowsIdentity.GetCurrent())using(var engine=new TrafficEngine(root,identity.User.Value))using(var server=new PipeServer(engine,args[4],identity.User.Value))using(var stop=new ManualResetEvent(false)) {
            Console.CancelKeyPress+=(_,ev)=>{ev.Cancel=true;stop.Set();};Console.Error.WriteLine("traffic-isolated-ready");
            var input=new Thread(()=>{while(Console.ReadLine()!=null){}stop.Set();}){IsBackground=true};input.Start();stop.WaitOne();return 0;
          }
        }catch(Win32Exception error){Console.Error.WriteLine("traffic-isolated-start:"+error.NativeErrorCode);return 1;}
      }
      if(args.Length!=0)return 2;
      try{ServiceBase.Run(new TrafficWindowsService());return 0;}catch(Exception ex){Console.Error.WriteLine("traffic-service:"+ex.GetType().Name);return 1;}
    }
  }
}
