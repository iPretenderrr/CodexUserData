using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUserData
{
    // Streaming projection of one JSONL event. Large instructions/tool results/final answers
    // are scanned, never retained. Only shallow lifecycle metadata enters the activity model.
    internal sealed class ActivityJsonLine
    {
        private sealed class Frame {internal bool Array;internal int Phase;internal string Path,Key;}
        private readonly List<Frame> frames=new List<Frame>();
        private readonly MemoryStream token=new MemoryStream();
        private readonly JavaScriptSerializer json=new JavaScriptSerializer();
        private readonly Dictionary<string,object> root=new Dictionary<string,object>(),payload=new Dictionary<string,object>();
        private bool quoted,escaped,capture,literal,bad,finished;
        private int unicode;
        private static bool Wanted(string path,string key){return path=="root"?(key=="type"||key=="timestamp"):path=="payload"&&(key=="type"||key=="id"||key=="session_id"||key=="turn_id"||key=="timestamp"||key=="parent_thread_id"||key=="thread_source"||key=="role"||key=="phase");}
        private Frame Top {get{return frames.Count==0?null:frames[frames.Count-1];}}
        private bool ValuePosition {get{var f=Top;return f==null?!finished:f.Array?(f.Phase==0||f.Phase==2):f.Phase==2;}}
        private void EndValue(){var f=Top;if(f==null){finished=true;return;}f.Phase=3;}
        internal void Feed(byte b)
        {
            if(bad)return;
            if(quoted)
            {
                if(capture){if(token.Length<4096)token.WriteByte(b);else{capture=false;token.SetLength(0);}}
                if(unicode>0){if(!(b>=48&&b<=57||b>=65&&b<=70||b>=97&&b<=102))bad=true;unicode--;return;}
                if(escaped){escaped=false;if(b==117)unicode=4;else if(b!=34&&b!=92&&b!=47&&b!=98&&b!=102&&b!=110&&b!=114&&b!=116)bad=true;return;}if(b==92){escaped=true;return;}if(b!=34){if(b<32)bad=true;return;}
                quoted=false;var f=Top;string value=null;
                if(capture)try{value=json.Deserialize<string>(Encoding.UTF8.GetString(token.GetBuffer(),0,(int)token.Length));}catch(ArgumentException){bad=true;}
                if(f!=null&&!f.Array&&(f.Phase==0||f.Phase==4)){f.Key=value;f.Phase=1;}
                else{if(f!=null&&Wanted(f.Path,f.Key)&&value!=null)(f.Path=="root"?root:payload)[f.Key]=value;EndValue();}return;
            }
            if(literal)
            {
                if(b!=44&&b!=125&&b!=93&&b!=32&&b!=9&&b!=13){if(token.Length<64)token.WriteByte(b);else bad=true;return;}
                try{json.DeserializeObject(Encoding.UTF8.GetString(token.GetBuffer(),0,(int)token.Length));}catch(ArgumentException){bad=true;}
                literal=false;EndValue();
            }
            if(b==32||b==9||b==13)return;
            var top=Top;
            if(b==34)
            {
                bool key=top!=null&&!top.Array&&(top.Phase==0||top.Phase==4);
                if(!key&&!ValuePosition){bad=true;return;}quoted=true;escaped=false;capture=key||top!=null&&Wanted(top.Path,top.Key);token.SetLength(0);if(capture)token.WriteByte(b);return;
            }
            if(b==123||b==91)
            {
                if(!ValuePosition||frames.Count>=64){bad=true;return;}
                string path=top==null?(b==123?"root":null):top.Path=="root"&&top.Key=="payload"&&b==123?"payload":null;
                frames.Add(new Frame{Array=b==91,Path=path});return;
            }
            if(b==125||b==93)
            {
                if(top==null||top.Array!=(b==93)||top.Phase!=0&&top.Phase!=3){bad=true;return;}frames.RemoveAt(frames.Count-1);EndValue();return;
            }
            if(b==58){if(top==null||top.Array||top.Phase!=1){bad=true;return;}top.Phase=2;return;}
            if(b==44){if(top==null||top.Phase!=3){bad=true;return;}top.Phase=top.Array?2:4;top.Key=null;return;}
            if(!ValuePosition){bad=true;return;}literal=true;token.SetLength(0);token.WriteByte(b);
        }
        internal bool Finish(ActivityTurn turn)
        {
            bool valid=!bad&&!quoted&&!literal&&finished&&frames.Count==0;object type,time;
            if(valid&&root.TryGetValue("type",out type)&&root.TryGetValue("timestamp",out time))
            {
                DateTimeOffset at;if(DateTimeOffset.TryParse(time as string,out at))turn.Accept(type as string,payload,at.ToUnixTimeSeconds(),at.ToUnixTimeMilliseconds());
            }
            Reset();return valid;
        }
        internal void Reset(){frames.Clear();root.Clear();payload.Clear();token.SetLength(0);unicode=0;quoted=escaped=capture=literal=bad=finished=false;}
    }
}
