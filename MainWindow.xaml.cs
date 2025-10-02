using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChatClientWpf {
    public partial class MainWindow : Window {
        TcpClient? _client;
        NetworkStream? _stream;
        CancellationTokenSource? _cts;
        SemaphoreSlim _writeLock = new(1,1);
        string _username = "";
        DateTime _lastTypingSent = DateTime.MinValue;

        const string ChatLogFile = "chatlog.txt";

        public MainWindow() {
            InitializeComponent();

            // Load saved chat history (optional)
            try {
                if (File.Exists(ChatLogFile)) {
                    foreach (var line in File.ReadAllLines(ChatLogFile)) lstChat.Items.Add(line);
                }
            } catch { /* ignore */ }

            // Ensure theme toggle reflects current theme file
            try {
                string theme = "Light";
                if (File.Exists(App.ThemeFile)) theme = File.ReadAllText(App.ThemeFile).Trim();
                ApplyThemeToggleState(theme);
            } catch { }
        }

        void ApplyThemeToggleState(string themeName) {
            if (themeName == "Dark") {
                btnTheme.IsChecked = true;
                btnTheme.Content = "Dark";
            } else {
                btnTheme.IsChecked = false;
                btnTheme.Content = "Light";
            }
        }

        private async void btnConnect_Click(object sender, RoutedEventArgs e) {
            var ip = txtIp.Text.Trim();
            if (!int.TryParse(txtPort.Text.Trim(), out int port)) { MessageBox.Show("Invalid port"); return; }
            _username = txtUsername.Text.Trim();
            if (string.IsNullOrWhiteSpace(_username)) { MessageBox.Show("Enter username"); return; }

            btnConnect.IsEnabled = false;
            try {
                _client = new TcpClient();
                await _client.ConnectAsync(ip, port);
                _stream = _client.GetStream();
                _cts = new CancellationTokenSource();

                var join = new { type = "join", from = _username, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await SendRawAsync(join);

                _ = Task.Run(() => ReceiveLoop(_cts.Token));

                btnDisconnect.IsEnabled = true;
                btnSend.IsEnabled = true;
                lstChat.Items.Add($"[system] Connected to {ip}:{port}");
            } catch (Exception ex) {
                MessageBox.Show("Could not connect: " + ex.Message);
                btnConnect.IsEnabled = true;
            }
        }

        private async void btnDisconnect_Click(object sender, RoutedEventArgs e) {
            await DoDisconnect();
        }

        async Task DoDisconnect() {
            try {
                _cts?.Cancel();
                if (_stream != null) {
                    var leave = new { type = "leave", from = _username, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    await SendRawAsync(leave);
                }
            } catch { }
            try { _client?.Close(); } catch { }
            Dispatcher.Invoke(() => {
                btnConnect.IsEnabled = true;
                btnDisconnect.IsEnabled = false;
                btnSend.IsEnabled = false;
            });
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e) {
            await SendMessageFromBox();
        }

        private async void txtMessage_KeyDown(object sender, KeyEventArgs e) {
            if (e.Key == Key.Enter) {
                e.Handled = true;
                await SendMessageFromBox();
            }
        }

        async Task SendMessageFromBox() {
            var text = txtMessage.Text.Trim();
            if (string.IsNullOrEmpty(text) || _stream == null) return;

            if (text.StartsWith("/w ")) {
                var parts = text.Substring(3).Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) {
                    var to = parts[0];
                    var msg = parts[1];
                    var pm = new { type = "pm", from = _username, to = to, text = msg, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                    await SendRawAsync(pm);
                } else {
                    lstChat.Items.Add("[system] PM usage: /w username message");
                }
            } else {
                var msg = new { type = "msg", from = _username, text = text, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                await SendRawAsync(msg);
            }

            Dispatcher.Invoke(() => txtMessage.Clear());
        }

        async Task SendRawAsync(object obj) {
            if (_stream == null) return;
            var data = JsonSerializer.SerializeToUtf8Bytes(obj);
            var len = IPAddress.HostToNetworkOrder(data.Length);
            var lenBytes = BitConverter.GetBytes(len);
            await _writeLock.WaitAsync();
            try {
                await _stream.WriteAsync(lenBytes, 0, 4);
                await _stream.WriteAsync(data, 0, data.Length);
                await _stream.FlushAsync();
            } finally {
                _writeLock.Release();
            }
        }

        async Task ReceiveLoop(CancellationToken ct) {
            try {
                while (!ct.IsCancellationRequested) {
                    var json = await ReadMessageAsync(_stream!, ct);
                    if (json == null) break;

                    using (var doc = JsonDocument.Parse(json)) {
                        var root = doc.RootElement;
                        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                        switch (type) {
                            case "msg":
                                {
                                    var from = root.GetProperty("from").GetString() ?? "unknown";
                                    var text = root.GetProperty("text").GetString() ?? "";
                                    var ts = root.TryGetProperty("ts", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                    var line = $"[{UnixToTime(ts)}] {from}: {text}";
                                    Dispatcher.Invoke(() => {
                                        lstChat.Items.Add(line);
                                        try { File.AppendAllText(ChatLogFile, line + Environment.NewLine); } catch {}
                                    });
                                    break;
                                }
                            case "pm":
                                {
                                    var from = root.GetProperty("from").GetString() ?? "unknown";
                                    var to = root.TryGetProperty("to", out var toEl) ? toEl.GetString() ?? "(you)" : "(you)";
                                    var text = root.GetProperty("text").GetString() ?? "";
                                    var ts = root.TryGetProperty("ts", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                                    var line = $"[{UnixToTime(ts)}] [PM] {from} -> {to}: {text}";
                                    Dispatcher.Invoke(() => {
                                        lstChat.Items.Add(line);
                                        try { File.AppendAllText(ChatLogFile, line + Environment.NewLine); } catch {}
                                    });
                                    break;
                                }
                            case "sys":
                                {
                                    var text = root.GetProperty("text").GetString() ?? "";
                                    var line = $"[system] {text}";
                                    Dispatcher.Invoke(() => {
                                        lstChat.Items.Add(line);
                                        try { File.AppendAllText(ChatLogFile, line + Environment.NewLine); } catch {}
                                    });
                                    break;
                                }
                            case "typing":
                                {
                                    var text = root.GetProperty("text").GetString() ?? "";
                                    Dispatcher.Invoke(() => { lblTyping.Content = text; });
                                    _ = Task.Delay(3000).ContinueWith(_ => Dispatcher.Invoke(() => { if (lblTyping.Content.ToString() == text) lblTyping.Content = ""; }));
                                    break;
                                }
                            case "userlist":
                                {
                                    if (root.TryGetProperty("users", out var usersEl) && usersEl.ValueKind == JsonValueKind.Array) {
                                        var list = new List<string>();
                                        foreach (var it in usersEl.EnumerateArray()) {
                                            if (it.ValueKind == JsonValueKind.String) list.Add(it.GetString()!);
                                        }
                                        Dispatcher.Invoke(() => {
                                            lstUsers.ItemsSource = null;
                                            lstUsers.ItemsSource = list;
                                        });
                                    }
                                    break;
                                }
                            default:
                                {
                                    var raw = doc.RootElement.GetRawText();
                                    Dispatcher.Invoke(() => lstChat.Items.Add("[unknown] " + raw));
                                    break;
                                }
                        }
                    }
                }
            } catch (OperationCanceledException) {
                // expected on disconnect
            } catch (Exception ex) {
                Dispatcher.Invoke(() => lstChat.Items.Add("[system] Connection lost: " + ex.Message));
            } finally {
                Dispatcher.Invoke(() => {
                    btnConnect.IsEnabled = true;
                    btnDisconnect.IsEnabled = false;
                    btnSend.IsEnabled = false;
                });
            }
        }

        private async void txtMessage_TextChanged(object sender, TextChangedEventArgs e) {
            // throttle typing events to at most once per second
            if (_stream == null || string.IsNullOrWhiteSpace(_username)) return;
            var now = DateTime.UtcNow;
            if ((now - _lastTypingSent).TotalMilliseconds < 1000) return;
            _lastTypingSent = now;
            var msg = new { type = "typing", from = _username, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            _ = SendRawAsync(msg);
        }

        private void lstUsers_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
            if (lstUsers.SelectedItem is string user) {
                txtMessage.Text = $"/w {user} ";
                txtMessage.Focus();
                txtMessage.CaretIndex = txtMessage.Text.Length;
            }
        }

        private void btnTheme_Checked(object sender, RoutedEventArgs e) {
            App.ApplyTheme("Dark");
            App.SaveTheme("Dark");
            btnTheme.Content = "Dark";
        }

        private void btnTheme_Unchecked(object sender, RoutedEventArgs e) {
            App.ApplyTheme("Light");
            App.SaveTheme("Light");
            btnTheme.Content = "Light";
        }

        static string UnixToTime(long ts) {
            var dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
            return dt.ToString("HH:mm:ss");
        }

        static async Task<string?> ReadMessageAsync(NetworkStream stream, CancellationToken ct) {
            var lenBuf = new byte[4];
            int r = await ReadExactAsync(stream, lenBuf, 0, 4, ct);
            if (r < 4) return null;
            int netLen = BitConverter.ToInt32(lenBuf, 0);
            int len = IPAddress.NetworkToHostOrder(netLen);
            if (len <= 0) return null;
            var buf = new byte[len];
            r = await ReadExactAsync(stream, buf, 0, len, ct);
            if (r < len) return null;
            return Encoding.UTF8.GetString(buf);
        }

        static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct) {
            int total = 0;
            while (total < count) {
                int read = await stream.ReadAsync(buffer, offset + total, count - total, ct);
                if (read == 0) return total;
                total += read;
            }
            return total;
        }
    }
}
