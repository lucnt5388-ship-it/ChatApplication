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
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace ChatClient2
{
    public class ChatPacket
    {
        public string Type { get; set; } = "";

        public string From { get; set; } = "";
        public string To { get; set; } = "";

        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        public string Message { get; set; } = "";
        public DateTime Time { get; set; } = DateTime.Now;

        public string PingId { get; set; } = "";
        public double LatencyMs { get; set; }
        public string NetworkQuality { get; set; } = "";

        public string FileId { get; set; } = "";
        public string FileName { get; set; } = "";
        public long FileSize { get; set; }
        public string FileData { get; set; } = "";

        public bool Success { get; set; }
    }

    public class OnlineUserItem
    {
        public string Username { get; set; } = "";
        public string AvatarText =>
            string.IsNullOrWhiteSpace(Username)
                ? "?"
                : Username.Substring(0, 1).ToUpper();
    }

    public class ChatMessageItem
    {
        public string Sender { get; set; } = "";
        public string SenderLabel { get; set; } = "";
        public string Message { get; set; } = "";
        public string TimeText { get; set; } = "";

        public bool IsMine { get; set; }
        public bool IsFile { get; set; }

        public string FileId { get; set; } = "";

        public int BubbleColumn => IsMine ? 1 : 0;

        public SolidColorBrush BubbleBrush =>
            IsMine
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 99, 235))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 41, 59));

        public CornerRadius BubbleCorner =>
            IsMine
                ? new CornerRadius(18, 18, 4, 18)
                : new CornerRadius(18, 18, 18, 4);

        public Visibility SenderVisibility =>
            IsMine ? Visibility.Collapsed : Visibility.Visible;

        public Visibility DownloadVisibility =>
            IsFile ? Visibility.Visible : Visibility.Collapsed;
    }

    public sealed partial class MainWindow : Window
    {
        private const string ServerIp = "127.0.0.1";
        private const int ServerPort = 5000;

        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        private readonly ObservableCollection<OnlineUserItem> _onlineUsers = new();
        private readonly ObservableCollection<ChatMessageItem> _messages = new();

        private string _myName = "";
        private string _selectedUser = "";

        public MainWindow()
        {
            this.InitializeComponent();

            lstUsers.ItemsSource = _onlineUsers;
            lstMessages.ItemsSource = _messages;
        }

        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            _myName = txtUsername.Text.Trim();
            string password = txtPassword.Password.Trim();

            if (string.IsNullOrWhiteSpace(_myName) ||
                string.IsNullOrWhiteSpace(password))
            {
                await ShowMessage("Lỗi", "Nhập tài khoản và mật khẩu.");
                return;
            }

            try
            {
                // ================== TCP CLIENT TRỌNG TÂM ==================
                // TcpClient là phía client dùng để kết nối tới server TCP.
                // Client kết nối đến IP 127.0.0.1 và port 5000.
                _client = new TcpClient();
                await _client.ConnectAsync(ServerIp, ServerPort);

                // NetworkStream là đường truyền TCP giữa client và server.
                // Client đọc dữ liệu server gửi về và ghi dữ liệu gửi lên server qua stream này.
                NetworkStream stream = _client.GetStream();

                // Reader nhận JSON từ server.
                _reader = new StreamReader(stream, Encoding.UTF8);

                // Writer gửi JSON lên server.
                // AutoFlush giúp gửi ngay sau WriteLineAsync().
                _writer = new StreamWriter(stream, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                await SendPacketAsync(new ChatPacket
                {
                    Type = "login",
                    Username = _myName,
                    Password = password,
                    From = _myName
                });

                // Chạy luồng nghe server riêng để UI không bị đứng.
                _ = ListenServerAsync();

                txtMe.Text = _myName;
                txtStatus.Text = "Đã kết nối server";
                btnLogin.IsEnabled = false;
                txtUsername.IsEnabled = false;
                txtPassword.IsEnabled = false;
            }
            catch (Exception ex)
            {
                await ShowMessage("Lỗi kết nối", ex.Message);
            }
        }

        private async Task ListenServerAsync()
        {
            try
            {
                while (true)
                {
                    if (_reader == null)
                        return;

                    // ================== NHẬN TCP TRỌNG TÂM ==================
                    // Server gửi mỗi gói tin là một dòng JSON.
                    // Client dùng ReadLineAsync() để đọc từng packet.
                    string? line = await _reader.ReadLineAsync();

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    ChatPacket? packet = JsonSerializer.Deserialize<ChatPacket>(line);

                    if (packet == null)
                        continue;

                    await DispatcherQueue.EnqueueAsync(async () =>
                    {
                        if (packet.Type == "loginResult")
                        {
                            txtStatus.Text = packet.Message;

                            if (!packet.Success)
                                await ShowMessage("Đăng nhập thất bại", packet.Message);
                        }
                        else if (packet.Type == "onlineList")
                        {
                            UpdateOnlineUsers(packet.Message);
                        }
                        else if (packet.Type == "message")
                        {
                            ShowIncomingMessage(packet);
                        }
                        else if (packet.Type == "file")
                        {
                            ShowIncomingFile(packet);
                        }
                        else if (packet.Type == "fileDownloadResult")
                        {
                            await SaveDownloadedFile(packet);
                        }
                        else if (packet.Type == "ping")
                        {
                            // ================== PING-PONG TRỌNG TÂM ==================
                            // Server gửi ping để kiểm tra client còn sống.
                            // Client phải trả pong kèm PingId để server tính đúng độ trễ.
                            await SendPacketAsync(new ChatPacket
                            {
                                Type = "pong",
                                From = _myName,
                                PingId = packet.PingId,
                                Time = DateTime.Now
                            });

                            txtNetwork.Text = "Ping: đã phản hồi server";
                        }
                    });
                }
            }
            catch
            {
                await DispatcherQueue.EnqueueAsync(() =>
                {
                    txtStatus.Text = "Mất kết nối server";
                    txtNetwork.Text = "Ping: mất kết nối";
                });
            }
        }

        private void UpdateOnlineUsers(string userList)
        {
            _onlineUsers.Clear();

            string[] users = userList.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (string user in users)
            {
                if (user != _myName)
                {
                    _onlineUsers.Add(new OnlineUserItem
                    {
                        Username = user
                    });
                }
            }
        }

        private void ShowIncomingMessage(ChatPacket packet)
        {
            if (packet.From != _myName)
            {
                _selectedUser = packet.From;
                SetChatHeader(packet.From);
            }

            _messages.Add(new ChatMessageItem
            {
                Sender = packet.From,
                SenderLabel = packet.From,
                Message = packet.Message,
                TimeText = packet.Time.ToString("HH:mm"),
                IsMine = packet.From == _myName,
                IsFile = false
            });

            ScrollToBottom();
        }

        private void ShowIncomingFile(ChatPacket packet)
        {
            if (packet.From != _myName)
            {
                _selectedUser = packet.From;
                SetChatHeader(packet.From);
            }

            _messages.Add(new ChatMessageItem
            {
                Sender = packet.From,
                SenderLabel = packet.From,
                Message = $"📎 {packet.FileName} ({FormatFileSize(packet.FileSize)})",
                TimeText = packet.Time.ToString("HH:mm"),
                IsMine = packet.From == _myName,
                IsFile = true,
                FileId = packet.FileId
            });

            ScrollToBottom();
        }

        private void lstUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstUsers.SelectedItem is not OnlineUserItem user)
                return;

            _selectedUser = user.Username;
            SetChatHeader(_selectedUser);
        }

        private void SetChatHeader(string username)
        {
            txtChatTitle.Text = username;
            txtChatSub.Text = "Đang chat";
            txtChatAvatar.Text =
                string.IsNullOrWhiteSpace(username)
                    ? "?"
                    : username.Substring(0, 1).ToUpper();
        }

        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void txtMessage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                await SendMessageAsync();
        }

        private async Task SendMessageAsync()
        {
            if (_writer == null)
            {
                await ShowMessage("Lỗi", "Chưa đăng nhập.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedUser))
            {
                await ShowMessage("Lỗi", "Chọn người để chat.");
                return;
            }

            string msg = txtMessage.Text.Trim();

            if (string.IsNullOrWhiteSpace(msg))
                return;

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

        private async void btnSendFile_Click(object sender, RoutedEventArgs e)
        {
            if (_writer == null)
            {
                await ShowMessage("Lỗi", "Chưa đăng nhập.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_selectedUser))
            {
                await ShowMessage("Lỗi", "Chọn người để gửi file.");
                return;
            }

            FileOpenPicker picker = new FileOpenPicker();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add("*");

            StorageFile file = await picker.PickSingleFileAsync();

            if (file == null)
                return;

            try
            {
                // ================== UPLOAD FILE TRỌNG TÂM ==================
                // Client đọc file thành byte[].
                // Sau đó đổi byte[] thành Base64 để gửi trong JSON qua TCP.
                // Server sẽ lưu file thật và trả lại FileId cho đoạn chat.
                var buffer = await FileIO.ReadBufferAsync(file);
                byte[] fileBytes = new byte[buffer.Length];

                using (DataReader reader = DataReader.FromBuffer(buffer))
                {
                    reader.ReadBytes(fileBytes);
                }

                await SendPacketAsync(new ChatPacket
                {
                    Type = "fileUpload",
                    From = _myName,
                    To = _selectedUser,
                    FileName = file.Name,
                    FileSize = fileBytes.Length,
                    FileData = Convert.ToBase64String(fileBytes),
                    Time = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                await ShowMessage("Lỗi gửi file", ex.Message);
            }
        }

        private async void btnDownloadFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string fileId)
            {
                // ================== DOWNLOAD FILE TRỌNG TÂM ==================
                // Bubble file chỉ giữ FileId.
                // Khi bấm tải, client gửi FileId lên server để xin file thật.
                await SendPacketAsync(new ChatPacket
                {
                    Type = "fileDownload",
                    From = _myName,
                    FileId = fileId
                });
            }
        }

        private async Task SaveDownloadedFile(ChatPacket packet)
        {
            if (!packet.Success)
            {
                await ShowMessage("Tải file thất bại", packet.Message);
                return;
            }

            try
            {
                string downloadDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "ChatDownloads"
                );

                Directory.CreateDirectory(downloadDir);

                string savePath = Path.Combine(downloadDir, packet.FileName);

                byte[] data = Convert.FromBase64String(packet.FileData);

                await File.WriteAllBytesAsync(savePath, data);

                await ShowMessage(
                    "Tải file thành công",
                    "File đã lưu tại Desktop/ChatDownloads/" + packet.FileName
                );
            }
            catch (Exception ex)
            {
                await ShowMessage("Lỗi tải file", ex.Message);
            }
        }

        private async void btnOnline_Click(object sender, RoutedEventArgs e)
        {
            await SendPacketAsync(new ChatPacket
            {
                Type = "setStatus",
                From = _myName,
                Message = "online"
            });

            txtStatus.Text = "Trạng thái: Online";
        }

        private async void btnOffline_Click(object sender, RoutedEventArgs e)
        {
            await SendPacketAsync(new ChatPacket
            {
                Type = "setStatus",
                From = _myName,
                Message = "offline"
            });

            txtStatus.Text = "Trạng thái: Offline";
        }

        private async Task SendPacketAsync(ChatPacket packet)
        {
            if (_writer == null)
                return;

            // ================== GỬI TCP TRỌNG TÂM ==================
            // Client serialize object thành JSON.
            // WriteLineAsync gửi JSON qua TCP đến server.
            // Server đọc bằng ReadLineAsync().
            string json = JsonSerializer.Serialize(packet);
            await _writer.WriteLineAsync(json);
        }

        private void ScrollToBottom()
        {
            scrollMessages.ChangeView(null, scrollMessages.ScrollableHeight, null);
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";

            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("0.0") + " KB";

            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }

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

    public static class DispatcherQueueExtensions
    {
        public static Task EnqueueAsync(
            this Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
            Action action)
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

        public static Task EnqueueAsync(
            this Microsoft.UI.Dispatching.DispatcherQueue dispatcher,
            Func<Task> action)
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