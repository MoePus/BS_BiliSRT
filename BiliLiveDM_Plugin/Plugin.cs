using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BiliSRT_Plugin
{
    public class Plugin : BilibiliDM_PluginFramework.DMPlugin
    {
        private NamedPipeClientStream DanmakuHimePipe;
        public Plugin()
        {
            this.ReceivedDanmaku += Plugin_ReceivedDanmaku;
            this.PluginAuth = "MoePus";
            this.PluginName = "BiliSRT";
            this.PluginVer = "v0.0.1";
        }

        private void Plugin_ReceivedDanmaku(object sender, BilibiliDM_PluginFramework.ReceivedDanmakuArgs e)
        {
            if (e.Danmaku.MsgType != BilibiliDM_PluginFramework.MsgTypeEnum.Comment)
                return;
            
            var text = e.Danmaku.CommentText.Trim();
            if (!text.StartsWith("!bsr "))
                return;
            
            var key = text.Split(' ')[1].ToLower();
            if (!long.TryParse(key, System.Globalization.NumberStyles.HexNumber, null, out _))
                return;

            WritePipe(key);
        }

        public override void Stop()
        {
            base.Stop();
        }

        private async void WritePipe(string key)
        {
            DanmakuHimePipe = new NamedPipeClientStream("BiliSRT-Pipe");
            await DanmakuHimePipe.ConnectAsync();

            using (StreamWriter sw = new StreamWriter(DanmakuHimePipe))
            {
                sw.WriteLine(key);
            }
        }

        public override void Start()
        {
            base.Start();
        }
    }
}
