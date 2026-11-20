using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Spark.Traffic {
  public sealed class PipeServer : IDisposable {
    sealed class Page {
      public string Sid,Fingerprint,Field;
      public DateTime Expires;
      public Dictionary<string,object> Value;
      public object[] Rows;
      public int Offset;
    }
    readonly TrafficEngine engine;
    readonly string name;
    readonly string isolatedOwnerSid;
    readonly Dictionary<string,object> historyLocks;
    readonly Dictionary<string,Page> pages=new Dictionary<string,Page>();
    readonly List<NamedPipeServerStream> connected=new List<NamedPipeServerStream>();
    readonly object sync=new object();
    readonly SemaphoreSlim slots=new SemaphoreSlim(16);
    readonly CancellationTokenSource stop=new CancellationTokenSource();
    readonly Thread listener;
    volatile bool disposed;
    NamedPipeServerStream pending;
    public PipeServer(TrafficEngine engine,string name,string isolatedOwnerSid=null) {
      this.engine=engine;this.name=name;this.isolatedOwnerSid=isolatedOwnerSid;
      historyLocks=engine.GrantedSids.ToDictionary(sid=>sid,_=>new object());
      listener=new Thread(Listen){IsBackground=true,Name="Traffic pipe accept"};
      pending=CreatePipe(name,engine.GrantedSids,true,isolatedOwnerSid);
      try{listener.Start();}catch{pending.Dispose();throw;}
    }
    void Listen() {
      while(!disposed) {
        try {slots.Wait(stop.Token);}catch(OperationCanceledException){return;}
        NamedPipeServerStream pipe=null;
        var serving=false;
        try {
          lock(sync) {
            if(disposed)return;
            pipe=pending??(pending=CreatePipe(name,engine.GrantedSids,false,isolatedOwnerSid));
          }
          pipe.WaitForConnection();
          lock(sync) {
            if(disposed)return;
            pending=null;
            connected.Add(pipe);
            if(!ThreadPool.QueueUserWorkItem(_=>Serve(pipe))){connected.Remove(pipe);throw new IOException("traffic-pipe-dispatch-failed");}
            serving=true;
          }
        }catch(OperationCanceledException)when(disposed){return;}
        catch(Exception ex)when(ex is IOException||ex is ObjectDisposedException||ex is Win32Exception){
          if(!disposed){Console.Error.WriteLine("traffic-pipe-listener:"+(ex is Win32Exception error?error.NativeErrorCode.ToString():ex.GetType().Name));Thread.Sleep(250);}
        }finally{
          if(!serving){
            lock(sync){if(pending==pipe)pending=null;}
            pipe?.Dispose();slots.Release();
          }
        }
      }
    }
    void Serve(NamedPipeServerStream pipe) {
      var connection=Guid.NewGuid().ToString("N");
      try {
        using(pipe)using(var reader=new StreamReader(pipe,new UTF8Encoding(false,true),false,4096,true))using(var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true,NewLine="\n"}) {
          string sid=null;
          // RunAsClient takes identity from the authenticated pipe token; all subsequent work runs as the service.
          pipe.RunAsClient(()=>{using(var identity=WindowsIdentity.GetCurrent(true)){sid=identity?.User?.Value;}});
          if(sid==null||!engine.GrantedSids.Contains(sid))throw new UnauthorizedAccessException("caller-not-granted");
          while(!disposed) {
            var line=Json.ReadLine(reader,65536);if(line==null)break;
            if(disposed)break;
            string requestId="";
            try {
              var request=Json.Decode(line);requestId=Json.Str(request,"id");
              if(Json.Int(request,"v",0,0,1)!=1||requestId.Length==0||requestId.Length>100)throw new InvalidDataException("invalid-protocol");
              var data=Dispatch(sid,connection,request);
              var message=Json.Encode(Json.Obj("v",1,"id",requestId,"ok",true,"data",data));
              if(Encoding.UTF8.GetByteCount(message)>65536)throw new InvalidOperationException("response-too-large");
              writer.WriteLine(message);
            }catch(Exception ex) {if(disposed)break;writer.WriteLine(Json.Encode(Json.Obj("v",1,"id",requestId,"ok",false,"error",Error(ex))));}
          }
        }
      }catch(OperationCanceledException)when(disposed){}
      catch(Exception ex)when(ex is IOException||ex is ObjectDisposedException||ex is UnauthorizedAccessException||ex is InvalidDataException||ex is DecoderFallbackException){}
      finally {
        try{engine.Release(connection);}catch(Exception ex){Console.Error.WriteLine("traffic-release:"+ex.GetType().Name);}
        lock(sync){connected.Remove(pipe);slots.Release();Monitor.PulseAll(sync);}
      }
    }
    static string Error(Exception ex) => ex is InvalidOperationException||ex is InvalidDataException||ex is UnauthorizedAccessException?ex.Message.Substring(0,Math.Min(256,ex.Message.Length)):"service-error:"+ex.GetType().Name;
    object Dispatch(string sid,string connection,Dictionary<string,object> request) {
      if(Json.Str(request,"scope","own")!="own")throw new UnauthorizedAccessException("scope-not-supported");
      var op=Json.Str(request,"op");
      switch(op) {
        case "status":
        case "scope.setEnabled":
        case "lease.acquire":
        case "lease.renew":
        case "lease.release":
        case "connections":
          return engine.Handle(sid,connection,request);
        case "snapshot": return ReadPage(sid,connection,request,false);
        case "snapshot.ranking":
        case "snapshot.ranking.active": return ReadPage(sid,connection,request,false,ranking:true,activeOnly:op=="snapshot.ranking.active");
        case "history":
        case "clearHistory":
          lock(historyLocks[sid]) {
            if(disposed)throw new ObjectDisposedException(nameof(PipeServer));
            if(op=="history")return ReadPage(sid,connection,request,true);
            var result=engine.Handle(sid,connection,request);
            lock(sync)foreach(var key in pages.Where(p=>p.Value.Sid==sid&&p.Value.Field=="rows").Select(p=>p.Key).ToArray())pages.Remove(key);
            return result;
          }
        default: throw new InvalidDataException("unsupported-operation");
      }
    }
    object ReadPage(string sid,string connection,Dictionary<string,object> request,bool history,bool ranking=false,bool activeOnly=false) {
      // Recheck token scope on every page. Opaque cursors bind caller, complete query and short expiry.
      var status=(Dictionary<string,object>)engine.Handle(sid,connection,Json.Obj("op","status","scope","own"));
      var limit=ranking?Json.Int(request,"limit",12,1,12):Json.Int(request,"limit",100,1,200);
      var paginate=!ranking&&(history||!request.TryGetValue("paginate",out var setting)||!object.Equals(setting,false));
      string fingerprint=null;
      if(paginate) {
        var query=new Dictionary<string,object>(request);query.Remove("id");query.Remove("cursor");
        fingerprint=Json.Encode(query.OrderBy(p=>p.Key).ToDictionary(p=>p.Key,p=>p.Value));
      }
      var token=Json.Str(request,"cursor");Page page;
      lock(sync) {
        foreach(var key in pages.Where(p=>p.Value.Expires<DateTime.UtcNow).Select(p=>p.Key).ToArray())pages.Remove(key);
        if(token.Length>0) {
          if(!paginate||(!history&&!Json.Bool(status,"collecting"))||!pages.TryGetValue(token,out page)||page.Sid!=sid||page.Fingerprint!=fingerprint)throw new InvalidOperationException("cursor-expired");
          pages.Remove(token);
        } else page=null;
      }
      if(page==null) {
        var value=(Dictionary<string,object>)engine.Handle(sid,connection,request);
        var field=history?"rows":"processes";
        var rows=value.TryGetValue(field,out var raw)&&raw is IEnumerable list?(raw as object[]??list.Cast<object>().ToArray()):new object[0];
        if(ranking) rows=SnapshotRanking.Select(rows,request,limit,activeOnly);
        page=new Page {Sid=sid,Fingerprint=fingerprint,Field=field,Value=value,Rows=rows,Expires=DateTime.UtcNow.AddSeconds(30)};
      }
      var selected=new List<object>();var bytes=Encoding.UTF8.GetByteCount(Json.Encode(page.Value.Where(p=>p.Key!=page.Field).ToDictionary(p=>p.Key,p=>p.Value)))+256;
      if(bytes>60000)throw new InvalidOperationException("metadata-too-large");
      while(page.Offset<page.Rows.Length&&selected.Count<limit) {
        var row=page.Rows[page.Offset];var rowBytes=Encoding.UTF8.GetByteCount(Json.Encode(row))+1;
        if(bytes+rowBytes>60000) {if(selected.Count==0)throw new InvalidOperationException("row-too-large");break;}
        selected.Add(row);page.Offset++;bytes+=rowBytes;
      }
      var result=new Dictionary<string,object>(page.Value);result[page.Field]=selected;
      result["nextCursor"]=null;
      if(paginate&&page.Offset<page.Rows.Length) {
        var next=Guid.NewGuid().ToString("N");
        lock(sync){if(pages.Count>=32||pages.Values.Sum(p=>p.Rows.Length)+page.Rows.Length>200000)throw new InvalidOperationException("query-capacity-reached");pages[next]=page;}
        result["nextCursor"]=next;
      }
      return result;
    }
    public void Dispose() {
      lock(sync) {
        if(disposed)return;
        disposed=true;
        stop.Cancel();
        CancelAndClose(pending);
        foreach(var pipe in connected)CancelAndClose(pipe);
        pages.Clear();
      }
      listener.Join();
      lock(sync)while(connected.Count!=0)Monitor.Wait(sync);
      stop.Dispose();slots.Dispose();
    }
    static void CancelAndClose(NamedPipeServerStream pipe) {
      if(pipe==null)return;
      try{CancelIoEx(pipe.SafePipeHandle,IntPtr.Zero);}catch(ObjectDisposedException){}
      finally{pipe.Dispose();}
    }
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafePipeHandle CreateNamedPipe(string name,uint openMode,uint pipeMode,uint instances,uint outBuffer,uint inBuffer,uint timeout,ref SecurityAttributes security);
    [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool CancelIoEx(SafePipeHandle handle,IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr pointer);
    static NamedPipeServerStream CreatePipe(string pipeName,string[] sids,bool first,string isolatedOwnerSid) {
      const string clientReadWriteWithoutCreateInstance="0x0012019b";
      var localServer=isolatedOwnerSid==null?"":"(A;;GA;;;"+isolatedOwnerSid+")";
      var sddl="D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GA;;;BA)"+localServer+string.Concat(sids.Select(sid=>"(A;;"+clientReadWriteWithoutCreateInstance+";;;"+sid+")"));
      if(!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl,1,out var descriptor,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
      try {
        var security=new SecurityAttributes {Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
        // Message mode + PIPE_REJECT_REMOTE_CLIENTS. DACL additionally denies network logons.
        var handle=CreateNamedPipe(@"\\.\pipe\"+pipeName,3u|0x40000000u|(first?0x80000u:0u),0x4|0x2|0x8,16,65536,65536,5000,ref security);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
      }finally{LocalFree(descriptor);}
    }
  }
}
