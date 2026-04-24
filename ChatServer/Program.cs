using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Gói dữ liệu gửi qua mạng
public class ChatPacket
{
    public string Type { get; set; } = "";     // login, message, onlineList
    public string From { get; set; } = "";     // người gửi
    public string To { get; set; } = "";       // người nhận
    public string Message { get; set; } = "";  // nội dung tin nhắn
    public DateTime Time { get; set; } = DateTime.Now;
}

// Thông tin client đang kết nối
public class ClientInfo
{
    public string Username { get; set; } = "";
    public TcpClient TcpClient { get; set; } = null!;
    public StreamWriter Writer { get; set; } = null!;
}

class Program
{
    static TcpListener? listener;

    // Dùng UTF8 không BOM để tránh lỗi JSON khi ghi nhiều lần
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    // Lưu danh sách user online
    static ConcurrentDictionary<string, ClientInfo> clients = new();

    static async Task Main(string[] args)
    {
        int port = 5000;

        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Console.WriteLine($"[SERVER] Đang mo may chu {port}...");
        Console.WriteLine("[SERVER] Đang ket noi...");

        while (true)
        {
            TcpClient tcpClient = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(tcpClient);
        }
    }

    static async Task HandleClientAsync(TcpClient tcpClient)
    {
        string currentUsername = "";

        try
        {
            NetworkStream stream = tcpClient.GetStream();

            // Chỉ tạo 1 reader / writer cho mỗi client
            StreamReader reader = new StreamReader(stream, Utf8NoBom);
            StreamWriter writer = new StreamWriter(stream, Utf8NoBom)
            {
                AutoFlush = true
            };

            while (true)
            {
                string? line = await reader.ReadLineAsync();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                ChatPacket? packet = JsonSerializer.Deserialize<ChatPacket>(line);

                if (packet == null)
                    continue;

                // Client đăng nhập
                if (packet.Type == "login")
                {
                    currentUsername = packet.From;

                    clients[currentUsername] = new ClientInfo
                    {
                        Username = currentUsername,
                        TcpClient = tcpClient,
                        Writer = writer
                    };

                    Console.WriteLine($"[ONLINE] {currentUsername}");
                    await BroadcastOnlineList();
                }
                // Client gửi tin nhắn
                else if (packet.Type == "message")
                {
                    Console.WriteLine($"[MSG] {packet.From} -> {packet.To}: {packet.Message}");
                    await ForwardToUser(packet.To, packet);
                }
            }
        }
        catch
        {
            Console.WriteLine($"[DISCONNECT] {currentUsername}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(currentUsername))
            {
                clients.TryRemove(currentUsername, out _);
                await BroadcastOnlineList();
            }

            tcpClient.Close();
        }
    }

    // Chuyển tiếp tin nhắn tới đúng user
    static async Task ForwardToUser(string username, ChatPacket packet)
    {
        if (clients.TryGetValue(username, out ClientInfo? client))
        {
            try
            {
                string json = JsonSerializer.Serialize(packet);
                await client.Writer.WriteLineAsync(json);
            }
            catch
            {
                Console.WriteLine($"[ERROR] Không gửi được đến {username}");
            }
        }
    }

    // Gửi danh sách user online cho tất cả client
    static async Task BroadcastOnlineList()
    {
        string onlineUsers = string.Join(",", clients.Keys);

        ChatPacket packet = new ChatPacket
        {
            Type = "onlineList",
            Message = onlineUsers
        };

        string json = JsonSerializer.Serialize(packet);

        foreach (var item in clients.Values)
        {
            try
            {
                await item.Writer.WriteLineAsync(json);
            }
            catch
            {
             
            }
        }
    }
}