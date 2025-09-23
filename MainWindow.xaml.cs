using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Threading.Tasks;

public class ChatMessage {
    public string type { get; set; } = "";
    public string? from { get; set; }
    public string? to { get; set; }
    public string? text { get; set; }
    public long ts { get; set; }
}

namespace ChatClientWpf
{
    public partial class MainWindow : Window
    {
        TcpClient? _client;
        NetworkStream? _stream;
        CancellationTokenSource? _cts;
        SemaphoreSlim _writeLock = new(1, 1);
        string _username = "";

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            var ip = txtIp.Text.Trim();
            if (!int.TryParse(txtPort.Text.Trim(), out int port)) return;
            _username = txtUsername.Text.Trim();
            if (string.IsNullOrWhiteSpace(_username)) return;

            btnConnect.IsEnabled = false;

            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);
                _stream = _client.GetStream();
                _cts = new CancellationTokenSource();

                // send join
                var join = new { type = "join", from = _username, to = (string?)null, text = (string?)null, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await SendRawAsync(join);

                // start receiving
                _ = Task.Run(() => ReceiveLoop(_cts.Token));

                btnDisconnect.IsEnabled = true;
                btnSend.IsEnabled = true;
                lstChat.Items.Add("[system] Connected");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not connect: " + ex.Message);
                btnConnect.IsEnabled = true;
            }
        }

        private async void btnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await DoDisconnect();
        }

        async Task DoDisconnect()
        {
            try
            {
                if (_cts != null) _cts.Cancel();
                if (_stream != null)
                {
                    var leave = new { type = "leave", from = _username, to = (string?)null, text = (string?)null, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    await SendRawAsync(leave);
                }
            }
            catch { }
            try { _client?.Close(); } catch { }
            Dispatcher.Invoke(() =>
            {
                btnConnect.IsEnabled = true;
                btnDisconnect.IsEnabled = false;
                btnSend.IsEnabled = false;
            });
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageFromBox();
        }

        private async void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SendMessageFromBox();
            }
        }

        async Task SendMessageFromBox()
        {
            var text = txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(text) || _stream == null) return;

            if (text.StartsWith("/w "))
            {
                // /w target message
                var parts = text.Substring(3).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var to = parts[0];
                    var msg = parts[1];
                    var pm = new { type = "pm", from = _username, to = to, text = msg, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    await SendRawAsync(pm);
                }
                else
                {
                    lstChat.Items.Add("[system] PM usage: /w username message");
                }
            }
            else
            {
                var msg = new { type = "msg", from = _username, to = (string?)null, text = text, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await SendRawAsync(msg);
            }

            Dispatcher.Invoke(() => txtMessage.Clear());
        }

        async Task SendRawAsync(object obj)
        {
            if (_stream == null) return;
            var data = JsonSerializer.SerializeToUtf8Bytes(obj);
            var len = IPAddress.HostToNetworkOrder(data.Length);
            var lenBytes = BitConverter.GetBytes(len);
            await _writeLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(lenBytes, 0, 4);
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        async Task ReceiveLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ReadMessageAsync(_stream!, ct);
                    if (msg == null) break;
                    Dispatcher.Invoke(() => ProcessIncoming(msg));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => lstChat.Items.Add("[system] Connection lost: " + ex.Message));
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    btnConnect.IsEnabled = true;
                    btnDisconnect.IsEnabled = false;
                    btnSend.IsEnabled = false;
                });
            }
        }

        void ProcessIncoming(ChatMessage msg) {
            string type = msg.type;
            string from = msg.from ?? "server";
            string? text = msg.text;

            if (type == "msg") {
                lstChat.Items.Add($"[{UnixToLocal(msg.ts)}] {from}: {text}");
            } else if (type == "pm") {
                var to = msg.to ?? "(you)";
                lstChat.Items.Add($"[{UnixToLocal(msg.ts)}] [PM] {from} -> {to}: {text}");
            } else if (type == "sys") {
                lstChat.Items.Add($"[system] {text}");
            } else {
                lstChat.Items.Add($"[unknown] {JsonSerializer.Serialize(msg)}");
            }
        }


        static string UnixToLocal(long ts)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
            return dt.ToString("HH:mm:ss");
        }

        static async Task<ChatMessage?> ReadMessageAsync(NetworkStream stream, CancellationToken ct) {
            var lenBuf = new byte[4];
            int r = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
            if (r == 0) return null;
            int netLen = BitConverter.ToInt32(lenBuf, 0);
            int len = IPAddress.NetworkToHostOrder(netLen);
            if (len <= 0) return null;
            var buf = new byte[len];
            r = await ReadExactAsync(stream, buf, 0, len, ct);
            if (r < len) return null;
            return JsonSerializer.Deserialize<ChatMessage>(buf);
        }


        static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer, offset + total, count - total, ct);
                if (read == 0) return total;
                total += read;
            }
            return total;
        }
    }
}
