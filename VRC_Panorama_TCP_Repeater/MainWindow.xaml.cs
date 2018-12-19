using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace VRC_Panorama_TCP_Repeater
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }
        TcpListener server = null;
        CancellationTokenSource cts;

        private string rootDirectory = GetCurrentAppDir() + @"www\";

        private void Log(string message)
        {
            Dispatcher.Invoke(() => { LogTextBox.AppendText($"{DateTime.Now:yyyy/MM/dd HH:mm:ss.fff} | {message}\n"); LogTextBox.ScrollToEnd(); });
        }

        public static string GetCurrentAppDir()
        {
            var path = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (path.Last() == '\\') path = path.Substring(0, path.Length - 1);
            path += "\\";
            return path;
        }

        public async void StartServer(int Port)
        {
            var isFirstTrigger = true;
            server = new TcpListener(IPAddress.Any, Port);
            cts = new CancellationTokenSource();
            server.Start();

            while (!cts.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await server.AcceptTcpClientAsync().WithWaitCancellation(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                    return;
                }
                await Task.Run(async () =>
                {
                    try
                    {
                        using (var stream = client.GetStream())
                        using (var reader = new StreamReader(stream))
                        using (var writer = new BinaryWriter(stream))
                        {
                            var requestLines = new List<string>();
                            while (true)
                            {
                                var line = await reader.ReadLineAsync();
                                if (String.IsNullOrWhiteSpace(line))
                                    break;
                                requestLines.Add(line);
                            }
                            if (requestLines.Count == 0)
                            {
                                return;
                            }
                            var enc = Encoding.UTF8;
                            var requestLine = requestLines.FirstOrDefault();
                            Log($"Request:{requestLine}");

                            var requestCommands = requestLine?.Split(new[] { ' ' }, 3); //"GET /test.txt HTTP/1.1"

                            //3つ以外の時、3つ未満の時、パスが/から始まってない時は不正なHTTP通信
                            if (requestCommands.Length != 3 || requestCommands.Length < 3 || requestCommands[1].StartsWith("/") == false)
                            {
                                Log($"{client.Client.RemoteEndPoint},400");
                                writer.Write(enc.GetBytes("HTTP/1.0 400 Bad Request\r\n"));
                                writer.Write(enc.GetBytes("Content-Type: text/plain\r\n"));
                                writer.Write(enc.GetBytes("\r\n"));
                                writer.Write(enc.GetBytes("Bad Request\r\n"));
                                writer.Flush();
                                return;
                            }

                            var path = requestCommands[1].Substring(1);

                            if (path.StartsWith("?"))// ?で始まる時はコマンドとして処理する
                            {
                                var command = path.Substring(1);
                                if (command == "TRIGGER")
                                {
                                    if (isFirstTrigger) //初回のTriggerはワールドに入ったときに自動的に発動する
                                    {
                                        isFirstTrigger = false;
                                        Log("ワールドに入りました");
                                    }
                                    else
                                    {
                                        Log("TRIGGERコマンド受信");
                                        var t = SendCommandToESP8266Async("GPIOSW"); //処理に時間がかかる場合があるのでawaitせずに実行
                                    }
                                }
                                //コマンド受信時はわざとエラーを返すことで、VRC_Panoramaが再度接続してくるようにする。
                                Log($"{client.Client.RemoteEndPoint},404,{path}");
                                writer.Write(enc.GetBytes("HTTP/1.0 404 Not Found\r\n"));
                                writer.Write(enc.GetBytes("Content-Type: text/plain\r\n"));
                                writer.Write(enc.GetBytes("\r\n"));
                                writer.Write(enc.GetBytes("File Not Found\r\n"));
                                writer.Flush();
                                return;
                            }
                            else //以下は通常のHTTPサーバーとして動作する(wwwフォルダの中身を返す)
                            {
                                if (path == "")
                                {
                                    path = "index.html";
                                }

                                path = Path.Combine(rootDirectory, path);

                                if (!File.Exists(path))
                                {
                                    Log($"{client.Client.RemoteEndPoint},404,{path}");
                                    writer.Write(enc.GetBytes("HTTP/1.0 404 Not Found\r\n"));
                                    writer.Write(enc.GetBytes("Content-Type: text/plain\r\n"));
                                    writer.Write(enc.GetBytes("\r\n"));
                                    writer.Write(enc.GetBytes("File Not Found\r\n"));
                                    writer.Flush();
                                    return;
                                }

                                //MimeType取得(System.Web.dllの参照必要)
                                var contentType = System.Web.MimeMapping.GetMimeMapping(Path.GetFileName(path));

                                var buff = File.ReadAllBytes(path);
                                writer.Write(enc.GetBytes($"HTTP/1.0 200 OK\r\n"));
                                writer.Write(enc.GetBytes($"Content-Type: {contentType}\r\n"));
                                writer.Write(enc.GetBytes($"\r\n"));
                                writer.Write(buff);
                                writer.Flush();

                                Log($"Send to {client.Client.RemoteEndPoint},{path}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex.ToString());
                    }
                    finally
                    {
                        client.Close();
                    }
                });
            }
        }

        public void StopServer()
        {
            if (server == null)
                return;
            cts.Cancel();
            server.Stop();
        }

        private async Task SendCommandToESP8266Async(string command)
        {
            try
            {
                var ipstring = "";
                var portstring = "";
                //UIを触るときは必ずUIスレッドで。
                Dispatcher.Invoke(() => { ipstring = ESP8266IPTextBox.Text; portstring = ESP8266PortTextBox.Text; });
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(IPAddress.Parse(ipstring), int.Parse(portstring));
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream))
                    using (var writer = new BinaryWriter(stream))
                    {
                        var enc = Encoding.UTF8;
                        writer.Write(enc.GetBytes($"{command}\r"));
                        writer.Flush();
                        var line = await reader.ReadToEndAsync();
                        Log(line);

                    }
                }
            }
            catch
            {
                Log("送信失敗");
            }
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            int Port = 0;
            if (int.TryParse(PortTextBox.Text, out Port) == false)
            {
                MessageBox.Show("ポートは整数で入力してください", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            StartServer(Port);
            Log("開始しました");
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopServer();
            Log("終了しました");
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCommandToESP8266Async("GPIOSW");
        }
    }

    static class TaskExtensions
    {
        //ref:https://stackoverflow.com/questions/14524209/what-is-the-correct-way-to-cancel-an-async-operation-that-doesnt-accept-a-cance
        public static async Task<T> WithWaitCancellation<T>(this Task<T> task, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            using (cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).TrySetResult(true), tcs))
            {
                if (task != await Task.WhenAny(task, tcs.Task))
                    throw new OperationCanceledException(cancellationToken);
            }
            return await task;
        }
    }
}
