using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatClient2
{
    // Gói dữ liệu gửi qua mạng
    public class ChatPacket
    {
        public string Type { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;
    }

    // Model để hiển thị từng tin nhắn lên UI
    public class ChatMessageItem
    {
        public string Sender { get; set; } = "";
        public string SenderLabel { get; set; } = "";
        public string Message { get; set; } = "";
        public string TimeText { get; set; } = "";
        public bool IsMine { get; set; }

        // 0 = trái, 2 = phải
        public int BubbleColumn => IsMine ? 2 : 1;

        // Tin nhắn của mình màu xanh, người khác màu xám
        public SolidColorBrush BubbleBrush =>
            IsMine
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 99, 235))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 41, 59));

        // Chỉ hiện avatar khi là người khác
        public Visibility OtherAvatarVisibility => IsMine ? Visibility.Collapsed : Visibility.Visible;

        // Chỉ hiện nhãn tên khi là người khác
        public Visibility SenderVisibility => IsMine ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed partial class MainWindow : Window
    {
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        private ObservableCollection<string> _onlineUsers = new();
        private ObservableCollection<ChatMessageItem> _messages = new();

        private string _myName = "";
        private string _selectedUser = "";

        public MainWindow()
        {
            this.InitializeComponent();

            lstUsers.ItemsSource = _onlineUsers;
            lstMessages.ItemsSource = _messages;
        }

        // Nút kết nối tới server
        private async void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            _myName = txtMyName.Text.Trim();
            string serverIp = txtServerIp.Text.Trim();

            if (string.IsNullOrWhiteSpace(_myName))
            {
                await ShowMessage("Lỗi", "Hãy nhập tên của bạn.");
                return;
            }

            if (string.IsNullOrWhiteSpace(serverIp))
            {
                await ShowMessage("Lỗi", "Hãy nhập IP server.");
                return;
            }

            try
            {
                _client = new TcpClient();
                await _client.ConnectAsync(serverIp, 5000);

                NetworkStream stream = _client.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                // Báo server biết mình đã online
                await SendPacketAsync(new ChatPacket
                {
                    Type = "login",
                    From = _myName
                });

                // Bắt đầu lắng nghe dữ liệu server gửi về
                _ = ListenServerAsync();

                btnConnect.IsEnabled = false;
                txtMyName.IsEnabled = false;
                txtServerIp.IsEnabled = false;

                await ShowMessage("Thành công", "Đã kết nối tới server.");
            }
            catch (Exception ex)
            {
                await ShowMessage("Kết nối lỗi", ex.Message);
            }
        }

        // Luôn chờ server gửi dữ liệu về
        private async Task ListenServerAsync()
        {
            try
            {
                while (true)
                {
                    if (_reader == null) return;

                    string? line = await _reader.ReadLineAsync();

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    ChatPacket? packet = JsonSerializer.Deserialize<ChatPacket>(line);

                    if (packet == null)
                        continue;

                    await DispatcherQueue.EnqueueAsync(() =>
                    {
                        // Server gửi danh sách người online
                        if (packet.Type == "onlineList")
                        {
                            _onlineUsers.Clear();

                            string[] users = packet.Message.Split(',', StringSplitOptions.RemoveEmptyEntries);

                            foreach (string user in users)
                            {
                                if (user != _myName)
                                    _onlineUsers.Add(user);
                            }

                            txtChatWith.Text = "ONLINE: " + packet.Message;
                        }
                        // Server gửi tin nhắn mới
                        else if (packet.Type == "message")
                        {
                            if (packet.From == _selectedUser)
                            {
                                _messages.Add(new ChatMessageItem
                                {
                                    Sender = packet.From,
                                    SenderLabel = packet.From,
                                    Message = packet.Message,
                                    TimeText = packet.Time.ToString("HH:mm"),
                                    IsMine = false
                                });
                            }
                            else
                            {
                                _messages.Add(new ChatMessageItem
                                {
                                    Sender = packet.From,
                                    SenderLabel = packet.From,
                                    Message = $"[Tin mới] {packet.Message}",
                                    TimeText = packet.Time.ToString("HH:mm"),
                                    IsMine = false
                                });
                            }

                            ScrollToBottom();
                        }
                    });
                }
            }
            catch
            {
                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    await ShowMessage("Mất kết nối", "Client đã mất kết nối tới server.");
                });
            }
        }

        // Chọn người để chat
        private void lstUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstUsers.SelectedItem != null)
            {
                _selectedUser = lstUsers.SelectedItem.ToString() ?? "";
                txtChatWith.Text = $"Đang chat với: {_selectedUser}";
                _messages.Clear();
            }
        }

        // Bấm nút gửi
        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        // Nhấn Enter để gửi
        private async void txtMessage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await SendMessageAsync();
            }
        }

        // Hàm gửi tin nhắn
        private async Task SendMessageAsync()
        {
            if (_writer == null)
            {
                await ShowMessage("Lỗi", "Chưa kết nối server.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedUser))
            {
                await ShowMessage("Lỗi", "Hãy chọn người để chat.");
                return;
            }

            string msg = txtMessage.Text.Trim();

            if (string.IsNullOrWhiteSpace(msg))
                return;

            // Thêm ngay tin nhắn của mình lên giao diện
            _messages.Add(new ChatMessageItem
            {
                Sender = _myName,
                SenderLabel = "Bạn",
                Message = msg,
                TimeText = DateTime.Now.ToString("HH:mm"),
                IsMine = true
            });

            ScrollToBottom();

            // Gửi dữ liệu lên server
            await SendPacketAsync(new ChatPacket
            {
                Type = "message",
                From = _myName,
                To = _selectedUser,
                Message = msg,
                Time = DateTime.Now
            });

            txtMessage.Text = "";
        }

        // Hàm gửi packet JSON
        private async Task SendPacketAsync(ChatPacket packet)
        {
            if (_writer == null) return;

            string json = JsonSerializer.Serialize(packet);
            await _writer.WriteLineAsync(json);
        }

        // Tự kéo xuống cuối danh sách
        private void ScrollToBottom()
        {
            if (_messages.Count > 0)
            {
                lstMessages.ScrollIntoView(_messages[_messages.Count - 1]);
            }
        }

        // Hộp thoại thông báo
        private async Task ShowMessage(string title, string content)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };

            await dialog.ShowAsync();
        }
    }

    // Helper để gọi về UI thread
    public static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action action)
        {
            var tcs = new TaskCompletionSource<object?>();

            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }

        public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Func<Task> action)
        {
            var tcs = new TaskCompletionSource<object?>();

            dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    tcs.SetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }
    }
}