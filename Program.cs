using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

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

public class ClientInfo
{
    public string Username { get; set; } = "";

    // TCP client đại diện cho một máy client đang kết nối tới server
    public TcpClient TcpClient { get; set; } = null!;

    // Writer dùng để ghi dữ liệu JSON xuống NetworkStream gửi về client
    public StreamWriter Writer { get; set; } = null!;

    public DateTime LastPong { get; set; } = DateTime.Now;
    public double LastLatencyMs { get; set; }
    public string NetworkQuality { get; set; } = "Good";

    public bool IsOnline { get; set; } = true;
}

class Program
{
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

    static readonly ConcurrentDictionary<string, ClientInfo> Clients = new();

    static readonly ConcurrentDictionary<string, DateTime> PendingPings = new();

    static readonly string DataDir = "ServerData";
    static readonly string FileDir = Path.Combine(DataDir, "Files");
    static readonly string UsersPath = Path.Combine(DataDir, "users.json");
    static readonly string MessagesPath = Path.Combine(DataDir, "messages.json");

    static readonly object FileLock = new();

    static Dictionary<string, string> Users = new();
    static List<ChatPacket> Messages = new();

    static async Task Main()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(FileDir);

        LoadData();

        // ================== TCP SERVER TRỌNG TÂM ==================
        // TcpListener là thành phần chính của TCP server.
        // IPAddress.Any nghĩa là server nhận kết nối từ mọi card mạng.
        // Port 5000 là cổng mà client sẽ kết nối tới.
        TcpListener listener = new TcpListener(IPAddress.Any, 5000);

        // Bắt đầu mở cổng 5000 và lắng nghe client.
        listener.Start();

        Console.WriteLine("[SERVER] TCP server đang chạy tại port 5000");

        _ = PingLoop();

        while (true)
        {
            // AcceptTcpClientAsync() là điểm server chờ client kết nối.
            // Khi một client kết nối thành công, server nhận được TcpClient.
            TcpClient tcpClient = await listener.AcceptTcpClientAsync();

            Console.WriteLine("[TCP] Có client mới kết nối.");

            // Mỗi client được xử lý bằng một Task riêng.
            // Nhờ vậy nhiều client có thể chat cùng lúc.
            _ = HandleClient(tcpClient);
        }
    }

    static void LoadData()
    {
        if (File.Exists(UsersPath))
        {
            Users =
                JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(UsersPath)
                ) ?? new();
        }

        if (File.Exists(MessagesPath))
        {
            Messages =
                JsonSerializer.Deserialize<List<ChatPacket>>(
                    File.ReadAllText(MessagesPath)
                ) ?? new();
        }
    }

    static void SaveUsers()
    {
        lock (FileLock)
        {
            File.WriteAllText(
                UsersPath,
                JsonSerializer.Serialize(
                    Users,
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
    }

    static void SaveMessages()
    {
        lock (FileLock)
        {
            File.WriteAllText(
                MessagesPath,
                JsonSerializer.Serialize(
                    Messages,
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
    }

    static async Task HandleClient(TcpClient tcpClient)
    {
        string currentUser = "";

        try
        {
            // ================== TCP STREAM TRỌNG TÂM ==================
            // NetworkStream là luồng dữ liệu TCP giữa server và client.
            // Server đọc dữ liệu client gửi lên và ghi dữ liệu trả về qua stream này.
            NetworkStream stream = tcpClient.GetStream();

            // StreamReader đọc dữ liệu text JSON từ client.
            StreamReader reader = new StreamReader(stream, Utf8);

            // StreamWriter ghi dữ liệu text JSON về client.
            // AutoFlush = true giúp gửi ngay, không bị giữ trong buffer.
            StreamWriter writer = new StreamWriter(stream, Utf8)
            {
                AutoFlush = true
            };

            while (true)
            {
                // Mỗi packet JSON được gửi theo một dòng.
                // ReadLineAsync() sẽ chờ đến khi client gửi xong một dòng.
                string? line = await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ChatPacket? packet = JsonSerializer.Deserialize<ChatPacket>(line);

                if (packet == null)
                    continue;

                if (packet.Type == "login")
                {
                    string username = packet.Username.Trim();
                    string password = packet.Password.Trim();

                    if (string.IsNullOrWhiteSpace(username) ||
                        string.IsNullOrWhiteSpace(password))
                    {
                        await Send(writer, new ChatPacket
                        {
                            Type = "loginResult",
                            Success = false,
                            Message = "Tài khoản hoặc mật khẩu trống."
                        });

                        continue;
                    }

                    if (!Users.ContainsKey(username))
                    {
                        Users[username] = password;
                        SaveUsers();
                    }

                    if (Users[username] != password)
                    {
                        await Send(writer, new ChatPacket
                        {
                            Type = "loginResult",
                            Success = false,
                            Message = "Sai mật khẩu."
                        });

                        continue;
                    }

                    currentUser = username;

                    Clients[currentUser] = new ClientInfo
                    {
                        Username = currentUser,
                        TcpClient = tcpClient,
                        Writer = writer,
                        LastPong = DateTime.Now,
                        IsOnline = true
                    };

                    await Send(writer, new ChatPacket
                    {
                        Type = "loginResult",
                        Success = true,
                        Message = "Đăng nhập thành công."
                    });

                    await SendHistory(currentUser, writer);
                    await BroadcastOnlineList();

                    Console.WriteLine("[LOGIN] " + currentUser);
                }
                else if (packet.Type == "message")
                {
                    packet.Time = DateTime.Now;

                    Messages.Add(packet);
                    SaveMessages();

                    // Server chuyển tin nhắn qua TCP tới người nhận.
                    await Forward(packet.To, packet);

                    // Gửi lại cho chính người gửi để giao diện đồng bộ.
                    await Forward(packet.From, packet);

                    Console.WriteLine($"[MESSAGE] {packet.From} -> {packet.To}: {packet.Message}");
                }
                else if (packet.Type == "fileUpload")
                {
                    if (string.IsNullOrWhiteSpace(packet.FileName) ||
                        string.IsNullOrWhiteSpace(packet.FileData))
                    {
                        await Forward(packet.From, new ChatPacket
                        {
                            Type = "file",
                            Success = false,
                            Message = "File không hợp lệ."
                        });

                        continue;
                    }

                    // ================== GỬI FILE TRỌNG TÂM ==================
                    // Client gửi file dưới dạng Base64 trong JSON.
                    // Server giải mã Base64 thành byte[] rồi lưu file vào ổ đĩa.
                    byte[] fileBytes = Convert.FromBase64String(packet.FileData);

                    string fileId = Guid.NewGuid().ToString("N");
                    string safeFileName = Path.GetFileName(packet.FileName);
                    string savePath = Path.Combine(FileDir, fileId + "_" + safeFileName);

                    await File.WriteAllBytesAsync(savePath, fileBytes);

                    ChatPacket filePacket = new ChatPacket
                    {
                        Type = "file",
                        From = packet.From,
                        To = packet.To,
                        FileId = fileId,
                        FileName = safeFileName,
                        FileSize = fileBytes.Length,
                        Message = $"Đã gửi file: {safeFileName}",
                        Time = DateTime.Now,
                        Success = true
                    };

                    // Chỉ lưu thông tin file vào lịch sử.
                    // Dữ liệu file thật được lưu trong ServerData/Files.
                    Messages.Add(filePacket);
                    SaveMessages();

                    await Forward(filePacket.To, filePacket);
                    await Forward(filePacket.From, filePacket);

                    Console.WriteLine(
                        $"[FILE] {packet.From} -> {packet.To}: {safeFileName} ({FormatFileSize(fileBytes.Length)})"
                    );
                }
                else if (packet.Type == "fileDownload")
                {
                    // Client không cần nhận file ngay lúc người khác gửi.
                    // Khi cần, client gửi FileId lên server để tải lại file.
                    string? path = Directory
                        .GetFiles(FileDir, packet.FileId + "_*")
                        .FirstOrDefault();

                    if (path == null)
                    {
                        await Send(writer, new ChatPacket
                        {
                            Type = "fileDownloadResult",
                            Success = false,
                            Message = "Không tìm thấy file trên server."
                        });

                        continue;
                    }

                    byte[] data = await File.ReadAllBytesAsync(path);

                    string originalFileName =
                        Path.GetFileName(path).Substring(packet.FileId.Length + 1);

                    await Send(writer, new ChatPacket
                    {
                        Type = "fileDownloadResult",
                        FileId = packet.FileId,
                        FileName = originalFileName,
                        FileSize = data.Length,
                        FileData = Convert.ToBase64String(data),
                        Success = true,
                        Message = "Tải file thành công."
                    });

                    Console.WriteLine(
                        $"[DOWNLOAD] {currentUser} tải {originalFileName} ({FormatFileSize(data.Length)})"
                    );
                }
                else if (packet.Type == "pong")
                {
                    // ================== PING-PONG TRỌNG TÂM ==================
                    // Client trả về pong sau khi nhận ping.
                    // Server dùng PingId để tính đúng độ trễ của lần ping đó.
                    if (Clients.TryGetValue(packet.From, out var client))
                    {
                        client.LastPong = DateTime.Now;

                        if (PendingPings.TryRemove(packet.PingId, out DateTime sentTime))
                        {
                            double latency = (DateTime.Now - sentTime).TotalMilliseconds;

                            client.LastLatencyMs = latency;
                            client.NetworkQuality = GetNetworkQuality(latency);

                            Console.WriteLine(
                                $"[PONG] {packet.From} - {latency:0}ms - {client.NetworkQuality}"
                            );
                        }
                    }
                }
                else if (packet.Type == "setStatus")
                {
                    if (Clients.TryGetValue(packet.From, out var client))
                    {
                        client.IsOnline = packet.Message == "online";
                        await BroadcastOnlineList();

                        Console.WriteLine($"[STATUS] {packet.From}: {packet.Message}");
                    }
                }
            }
        }
        catch
        {
            Console.WriteLine("[DISCONNECT] " + currentUser);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(currentUser))
            {
                Clients.TryRemove(currentUser, out _);
                await BroadcastOnlineList();
            }

            tcpClient.Close();
        }
    }

    static async Task SendHistory(string username, StreamWriter writer)
    {
        foreach (var msg in Messages)
        {
            if (msg.From == username || msg.To == username)
            {
                await Send(writer, msg);
            }
        }
    }

    static async Task Forward(string username, ChatPacket packet)
    {
        if (Clients.TryGetValue(username, out var client))
        {
            await Send(client.Writer, packet);
        }
    }

    static async Task Send(StreamWriter writer, ChatPacket packet)
    {
        // ================== GỬI TCP TRỌNG TÂM ==================
        // Object ChatPacket được chuyển thành JSON.
        // WriteLineAsync gửi JSON qua TCP stream.
        // Mỗi dòng tương ứng một packet để bên client đọc bằng ReadLineAsync().
        string json = JsonSerializer.Serialize(packet);
        await writer.WriteLineAsync(json);
    }

    static async Task BroadcastOnlineList()
    {
        var online = Clients.Values
            .Where(x => x.IsOnline)
            .Select(x => x.Username)
            .ToList();

        ChatPacket packet = new ChatPacket
        {
            Type = "onlineList",
            Message = string.Join(",", online)
        };

        foreach (var client in Clients.Values)
        {
            try
            {
                await Send(client.Writer, packet);
            }
            catch { }
        }
    }

    static async Task PingLoop()
    {
        while (true)
        {
            await Task.Delay(5000);

            foreach (var pair in Clients.ToArray())
            {
                string username = pair.Key;
                ClientInfo client = pair.Value;

                // Nếu quá 15 giây không có pong, server xem client đã mất kết nối.
                if ((DateTime.Now - client.LastPong).TotalSeconds > 15)
                {
                    Console.WriteLine($"[TIMEOUT] {username} offline vì không phản hồi ping.");

                    Clients.TryRemove(username, out _);
                    await BroadcastOnlineList();

                    continue;
                }

                try
                {
                    string pingId = Guid.NewGuid().ToString("N");

                    PendingPings[pingId] = DateTime.Now;

                    // Server gửi ping qua TCP.
                    // Client nhận ping thì phải trả pong cùng PingId.
                    await Send(client.Writer, new ChatPacket
                    {
                        Type = "ping",
                        PingId = pingId,
                        Time = DateTime.Now
                    });
                }
                catch
                {
                    Clients.TryRemove(username, out _);
                    await BroadcastOnlineList();
                }
            }
        }
    }

    static string GetNetworkQuality(double latencyMs)
    {
        if (latencyMs < 100)
            return "Good";

        if (latencyMs < 300)
            return "Medium";

        return "Weak";
    }

    static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";

        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.0") + " KB";

        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}