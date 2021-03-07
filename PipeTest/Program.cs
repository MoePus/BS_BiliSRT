using System;
using System.IO;
using System.IO.Pipes;

namespace PipeTest
{
    class Program
    {
        static NamedPipeClientStream DanmakuHimePipe;
        private static async void ConnectPipe()
        {
            DanmakuHimePipe = new NamedPipeClientStream("BiliSRT-Pipe");
            await DanmakuHimePipe.ConnectAsync();
        }
        static void Main(string[] args)
        {
            while (true)
            {
                ConnectPipe();
                string line;
                while ((line = Console.ReadLine()) != null)
                {

                    using (StreamWriter sw = new StreamWriter(DanmakuHimePipe))
                    {
                        sw.WriteLine(line);
                    }
                    ConnectPipe();

                }
            }

        }
    }
}
