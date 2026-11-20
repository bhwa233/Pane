using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pane.Contracts;
using Pane.Localization;
using Pane.Services.Screenshots;
using Pane.Settings;

namespace Pane.Services.Clipboard;

internal sealed partial class DataStorage
{
    // 仅供隔离目录测试注入；产品不设置。
    internal static Action<string>? ImportStep {get;set;}
    internal static void ApplyPending(string data)
    {
        string root=Pending(data),journalPath=Path.Combine(root,"transaction.json"),pending=Path.Combine(root,"pending.json");
        if(File.Exists(journalPath)){
            var interrupted=ReadObject(journalPath);
            if(interrupted["committed"]?.GetValue<bool>()!=true){RestoreTransaction(root,interrupted);interrupted["error"]=string.Format(Strings.Data_ImportFailed,"Interrupted");}
            FinishImport(data,interrupted);return;
        }
        if(!File.Exists(pending))return;
        string backup=Path.Combine(data,"backups","before-import-"+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+".zip");
        JsonObject? journal=null;
        try{
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            bool hasNotes=File.Exists(Path.Combine(root,NoteIndex));
            bool hasScreenshots=File.Exists(Path.Combine(root,ScreenshotIndex));
            bool hasOcrHistory=File.Exists(Path.Combine(root,OcrIndex));
            ExportCore(data,backup,new(true,true,ClipboardExport.WithImages,hasNotes,hasScreenshots,hasOcrHistory),null,null,default);
            ImportStep?.Invoke("backup");
            var options=ReadObject(pending);ValidatePayload(root);ValidateNotePayload(root);ValidateScreenshotPayload(root);
            var writes=BuildImport(data,root,options);
            var files=new JsonArray();int index=0;
            string screenshotRoot=Path.Combine(data,"screenshots");
            string ocrRoot=Path.Combine(data,OcrHistoryStore.DirectoryName);
            foreach(var pair in writes){
                string original=Path.Combine(root,"rollback",(index++).ToString());bool exists=File.Exists(pair.Key);
                string target=Path.GetFullPath(pair.Key);
                bool preserveCreated=StorageLocation.Within(target,Path.GetFullPath(screenshotRoot))||
                    Path.GetFileName(target)==OcrHistoryStore.ResultName&&StorageLocation.Within(target,Path.GetFullPath(ocrRoot));
                if(exists){Directory.CreateDirectory(Path.GetDirectoryName(original)!);CopyDurable(pair.Key,original);if(preserveCreated)File.SetCreationTimeUtc(original,File.GetCreationTimeUtc(pair.Key));}
                files.Add((JsonNode)new JsonObject{["target"]=pair.Key,["original"]=original,["exists"]=exists,["source"]=pair.Value,["preserveCreated"]=preserveCreated});
            }
            journal=new JsonObject{["backup"]=backup,["files"]=files,["committed"]=false};
            WriteAtomic(journalPath,journal.ToJsonString());ImportStep?.Invoke("prepared");
            foreach(var item in files){
                var row=item!.AsObject();string target=row["target"]!.GetValue<string>();
                if(row["source"] is {} source)ReplaceDurable(source.GetValue<string>(),target,row["preserveCreated"]?.GetValue<bool>()==true);
                else File.Delete(target);
                ImportStep?.Invoke("applied-file");
            }
            journal["committed"]=true;WriteAtomic(journalPath,journal.ToJsonString());
        }catch(Exception e){
            if(journal!=null){RestoreTransaction(root,journal);}
            journal??=new JsonObject{["backup"]=File.Exists(backup)?backup:""};
            journal["error"]=string.Format(Strings.Data_ImportFailed,Reason(e));
        }
        FinishImport(data,journal);
    }
    static void CopyDurable(string source,string target)
    {
        using var input=File.OpenRead(source);using var output=new FileStream(target,FileMode.Create,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);
    }
    static void ReplaceDurable(string source,string target,bool preserveCreated)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);CopyDurable(source,target+".import-tmp");File.Move(target+".import-tmp",target,true);
        if(preserveCreated)File.SetCreationTimeUtc(target,File.GetCreationTimeUtc(source));
    }
    static void RestoreTransaction(string root,JsonObject journal)
    {
        foreach(var item in journal["files"]!.AsArray()){
            var row=item!.AsObject();string target=row["target"]!.GetValue<string>();
            if(row["exists"]!.GetValue<bool>()){
                string original=row["original"]!.GetValue<string>();
                if(!StorageLocation.Within(Path.GetFullPath(original),Path.GetFullPath(root)))throw Invalid();
                ReplaceDurable(original,target,row["preserveCreated"]?.GetValue<bool>()==true);
            }else File.Delete(target);
            File.Delete(target+".import-tmp");
        }
    }
    static void FinishImport(string data,JsonObject journal)
    {
        WriteAtomic(Outcome(data),new JsonObject{["backup"]=journal["backup"]?.DeepClone(),["error"]=journal["error"]?.DeepClone()}.ToJsonString());
        // 先移除入口，再清理；清理中断不再重复应用或回滚成功的事务。
        File.Delete(Path.Combine(Pending(data),"pending.json"));File.Delete(Path.Combine(Pending(data),"transaction.json"));DeletePending(data);
        string backups=Path.Combine(data,"backups");
        if(Directory.Exists(backups))foreach(string old in Directory.GetFiles(backups,"before-import-*.zip").OrderByDescending(Path.GetFileName,StringComparer.Ordinal).Skip(3))File.Delete(old);
    }
    static Dictionary<string,string?> BuildImport(string data,string root,JsonObject options)
    {
        var writes=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);string output=Path.Combine(root,"apply");Directory.CreateDirectory(output);
        string settingsPath=Path.Combine(data,"settings.json");JsonObject? local=File.Exists(settingsPath)?ReadObject(settingsPath):null;
        JsonObject? finalSettings=local;
        foreach(string name in TransferFiles){
            string source=Path.Combine(root,name);if(!File.Exists(source))continue;
            if(name=="clipboard/history.json")continue;
            string target=LocalFile(data,name);JsonNode value=JsonNode.Parse(File.ReadAllText(source))??throw Invalid();
            if(name=="settings/settings.json"){
                var imported=value.AsObject();LocalSettingsFields.Apply(imported,local);
                var model=JsonSerializer.Deserialize(imported.ToJsonString(),SettingsJson.Default.PaneSettings)??throw Invalid();SettingsStore.Validate(model);finalSettings=imported;
            }else if(options["merge"]!.GetValue<bool>()&&name=="usage/launcher-usage.json"){
                var current=File.Exists(target)?ReadObject(target):new JsonObject();
                foreach(var pair in value.AsObject()){
                    var incoming=pair.Value!.AsObject();int count=incoming["Count"]!.GetValue<int>();var last=incoming["LastUsed"]!.GetValue<DateTimeOffset>();
                    if(current[pair.Key] is {} existing){count=(int)Math.Min(int.MaxValue,(long)count+existing["Count"]!.GetValue<int>());var prior=existing["LastUsed"]!.GetValue<DateTimeOffset>();if(prior>last)last=prior;}
                    current[pair.Key]=new JsonObject{["Count"]=count,["LastUsed"]=last};
                }
                value=current;
            }else if(name=="usage/web-search-history.json"){
                var incoming=JsonSerializer.Deserialize(value.ToJsonString(),Pane.Services.Launcher.WebHistoryJson.Default.DictionaryStringListWebSearchRecord)??[];
                var current=options["merge"]!.GetValue<bool>()&&File.Exists(target)?JsonSerializer.Deserialize(File.ReadAllText(target),Pane.Services.Launcher.WebHistoryJson.Default.DictionaryStringListWebSearchRecord)??[]:[];
                foreach(var pair in incoming){if(!current.TryGetValue(pair.Key,out var list))current[pair.Key]=pair.Value;else list.AddRange(pair.Value);}
                value=JsonNode.Parse(JsonSerializer.Serialize(Pane.Services.Launcher.WebSearchHistory.Normalize(current),Pane.Services.Launcher.WebHistoryJson.Default.DictionaryStringListWebSearchRecord))!;
            }else if(name=="usage/terminal-history.json"){
                // TODO(design): 现有历史没有时间戳，跨文件无法按真实时间排序；暂保留本机顺序优先。
                var current=options["merge"]!.GetValue<bool>()&&File.Exists(target)?JsonNode.Parse(File.ReadAllText(target))!.AsArray():new JsonArray();
                value=new JsonArray(current.Concat(value.AsArray()).Select(n=>n!.GetValue<string>()).Where(s=>!string.IsNullOrWhiteSpace(s)&&!char.IsWhiteSpace(s[0])).Distinct(StringComparer.Ordinal).Take(20).Select(s=>(JsonNode?)JsonValue.Create(s)).ToArray());
            }
            string staged=Path.Combine(output,Path.GetFileName(target));WriteAtomic(staged,value.ToJsonString());writes[target]=staged;
        }
        bool hasNotes=File.Exists(Path.Combine(root,NoteIndex)),hasScreenshots=File.Exists(Path.Combine(root,ScreenshotIndex));
        if(hasNotes||hasScreenshots){
            finalSettings??=ReadObject(Path.Combine(root,CardSettingsFallback));
            bool merge=options["merge"]!.GetValue<bool>();
            if(hasNotes)ApplyNotes(data,root,merge,finalSettings,writes);
            if(hasScreenshots)ApplyScreenshots(data,root,merge,finalSettings,writes);
            var model=JsonSerializer.Deserialize(finalSettings.ToJsonString(),SettingsJson.Default.PaneSettings)??throw Invalid();SettingsStore.Validate(model);
            string staged=Path.Combine(output,"settings.json");WriteAtomic(staged,finalSettings.ToJsonString());writes[settingsPath]=staged;
        }
        if(File.Exists(Path.Combine(root,OcrIndex)))
            ApplyOcrHistory(data,root,options["merge"]!.GetValue<bool>(),ValidateOcrPayload(root),writes);
        string clipSource=Path.Combine(root,"clipboard/history.json");if(!File.Exists(clipSource))return writes;
        var disk=new ClipboardStorage();string index=LocalFile(data,"clipboard/history.json");var old=disk.Load(index);
        string? configured=local?["Storage"]?["LargeFilesDirectory"]?.GetValue<string>();
        string imageRoot=configured!=null&&StorageLocation.Available(configured)?configured:Path.Combine(data,"clipboard");
        var result=options["merge"]!.GetValue<bool>()?old.ToList():new List<StoredClip>();
        var importedImages=new List<string>();
        string Key(StoredClip c,bool incoming)=>c.Entry.Kind==ClipKind.Image?"i:"+ImageHash(incoming?Path.Combine(root,c.Entry.ContentPath!):c.Entry.ContentPath!):c.Entry.Kind==ClipKind.Files?"f:"+c.Fingerprint:ClipboardTextFormats.Fingerprint(c.Entry.Text??"",c.Entry.RichText);
        var keys=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int indexInResult=0;indexInResult<result.Count;indexInResult++)keys.TryAdd(Key(result[indexInResult],false),indexInResult);
        bool mergeClips=options["merge"]!.GetValue<bool>();
        foreach(var c in disk.Load(clipSource)){
            string key=Key(c,true);
            if(mergeClips&&keys.TryGetValue(key,out int position)){
                var existing=result[position];
                if(c.Entry.Collected||existing.Entry.Archived)
                    result[position]=existing with{Entry=existing.Entry with{
                        Collections=ClipboardCollections.Combine(existing.Entry.Collections,c.Entry.Collections),
                        Favorite=existing.Entry.Favorite||c.Entry.Favorite,Archived=existing.Entry.Archived&&c.Entry.Archived}};
                continue;
            }
            if(mergeClips)keys.Add(key,result.Count);
            var entry=c.Entry;string id=Guid.NewGuid().ToString("N");
            if(entry.Kind==ClipKind.Image){
                string full=Path.Combine(imageRoot,id+".png");writes[full]=Path.Combine(root,entry.ContentPath!);importedImages.Add(full);
                string? thumb=null;if(entry.Thumbnail?.Path is {} p){thumb=Path.Combine(imageRoot,id+".thumb.png");writes[thumb]=Path.Combine(root,p);importedImages.Add(thumb);}
                entry=entry with{ContentPath=full,Thumbnail=thumb==null?null:new(thumb)};
            }else entry=entry with{ContentPath=null,Thumbnail=null,Files=null};
            result.Add(new(entry with{Id=id},key));
        }
        int limit=finalSettings?["Clipboard"]?["Limit"]?.GetValue<int>()??500;
        ClipboardCollections.TrimHistory(result,limit);
        ClipboardTextFormats.Trim(result);
        if(ClipboardTextFormats.ImageMergeBytes(result)>ClipboardTextFormats.ImageMergeHistoryByteLimit)throw Invalid();
        long mb=finalSettings?["Clipboard"]?["ImageLimitMegabytes"]?.GetValue<long>()??0;
        if(mb>0){
            long max=mb>long.MaxValue/(1024*1024)?long.MaxValue:mb*1024*1024;
            long Size(string? p)=>p==null?0:File.Exists(writes.GetValueOrDefault(p)??p)?new FileInfo(writes.GetValueOrDefault(p)??p).Length:0;
            var images=result.Where(c=>c.Entry.Kind==ClipKind.Image&&!c.Entry.Pinned&&!c.Entry.Collected).OrderBy(c=>c.Entry.CopiedAt).Select(c=>(Clip:c,Size:Size(c.Entry.ContentPath)+Size(c.Entry.Thumbnail?.Path))).ToArray();
            long bytes=images.Sum(c=>c.Size);foreach(var c in images){if(bytes<=max)break;result.Remove(c.Clip);bytes-=c.Size;}
        }
        var keep=result.SelectMany(c=>new[]{c.Entry.ContentPath,c.Entry.Thumbnail?.Path}).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(string target in importedImages.Where(p=>!keep.Contains(p)))writes.Remove(target);
        foreach(string path in old.SelectMany(c=>new[]{c.Entry.ContentPath,c.Entry.Thumbnail?.Path}).OfType<string>().Where(p=>!keep.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            if(StorageLocation.Within(Path.GetFullPath(path),Path.GetFullPath(imageRoot))||StorageLocation.Within(Path.GetFullPath(path),Path.Combine(Path.GetFullPath(data),"clipboard")))writes[path]=null;
        string stagedIndex=Path.Combine(output,"history.json");disk.Save(stagedIndex,result.OrderByDescending(c=>c.Entry.CopiedAt).ToArray(),_=>{});writes[index]=stagedIndex;return writes;
    }
    static string ImageHash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
}
