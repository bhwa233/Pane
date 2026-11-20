using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Spark.Traffic {
  public static class SnapshotRanking {
    public static object[] Select(object[] snapshot, Dictionary<string,object> request, int limit, bool activeOnly = false) {
      var keyword=Json.Str(request,"keyword").Trim();
      var mode=Json.Str(request,"rankingMode","rate");
      var sort=Json.Str(request,"rankingSort","total");
      if(keyword.Length>160||(mode!="rate"&&mode!="app-run")||(sort!="total"&&sort!="download"&&sort!="upload"))
        throw new InvalidDataException("invalid-snapshot-ranking");
      var cumulative=mode=="app-run";
      var byPid=int.TryParse(keyword,NumberStyles.None,CultureInfo.InvariantCulture,out var pid);
      var matched=snapshot.Cast<Dictionary<string,object>>().Where(row=>keyword.Length==0||
        Json.Str(row,"name").IndexOf(keyword,StringComparison.OrdinalIgnoreCase)>=0||
        Json.Str(row,"imagePath").IndexOf(keyword,StringComparison.OrdinalIgnoreCase)>=0||
        byPid&&Json.Int(row,"pid",0,0,int.MaxValue)==pid);
      if(activeOnly)
        matched=matched.Where(row=>Read(row,"rxBytesPerSec")>0||Read(row,"txBytesPerSec")>0);
      else if(!cumulative)
        matched=matched.Where(row=>row["rxBytesPerSec"]!=null||row["txBytesPerSec"]!=null);
      if(cumulative) {
        return matched.GroupBy(row=>row.TryGetValue("appRunId",out var id)&&id is string runId&&runId.Length>0?(object)runId:row)
          .Select(group=>new {Group=group,Representative=group.Aggregate((selected,row)=>Pid(row)<Pid(selected)?row:selected)})
          .OrderByDescending(app=>Rank(app.Representative,true,sort)).Take(limit)
          .Select(app=>AppRunRow(app.Group,app.Representative)).ToArray();
      }
      if(sort=="total")return matched.Take(limit).Cast<object>().ToArray();
      return matched.OrderByDescending(row=>Rank(row,false,sort)).Take(limit).Cast<object>().ToArray();
    }

    static int Pid(Dictionary<string,object> row) => Json.Int(row,"pid",0,0,int.MaxValue);

    static object AppRunRow(IGrouping<object,Dictionary<string,object>> group,Dictionary<string,object> representative) {
      if(!(group.Key is string))return representative;
      return new Dictionary<string,object>(representative) {
        ["appRunMemberCount"]=group.Count(),
        ["appRunMembers"]=group.OrderBy(Pid).Take(12)
          .Select(row=>Json.Obj("processInstanceId",row["processInstanceId"],"pid",row["pid"],"name",row["name"])).ToArray()
      };
    }

    static decimal? Rank(Dictionary<string,object> row,bool cumulative,string sort) {
      var receive=Read(row,cumulative?"appRunRxBytes":"rxBytesPerSec");
      var send=Read(row,cumulative?"appRunTxBytes":"txBytesPerSec");
      if(sort=="download")return receive;
      if(sort=="upload")return send;
      return receive.HasValue&&send.HasValue?receive.Value+send.Value:(decimal?)null;
    }

    static decimal? Read(Dictionary<string,object> row,string key) => row.TryGetValue(key,out var value)&&value!=null?
      Convert.ToDecimal(value,CultureInfo.InvariantCulture):(decimal?)null;
  }
}
