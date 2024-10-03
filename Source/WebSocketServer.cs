using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace eft_dma_radar.Source;

public class WebSocketServer
{
    private TcpListener _server;
    private bool _isRunning;
    private ConcurrentBag<TcpClient> _clients = new();

    public WebSocketServer(string ip, int port)
    {
        _server = new TcpListener(IPAddress.Parse(ip), port);
    }

    public void Start()
    {
        _server.Start();
        _isRunning = true;
        Console.WriteLine("Server has started on {0}, Waiting for a connection…", _server.LocalEndpoint);
        AcceptClientsAsync();
    }

    private async void AcceptClientsAsync()
    {
        while (_isRunning)
        {
            var client = await _server.AcceptTcpClientAsync();
            _clients.Add(client);
            Console.WriteLine("A client connected.");
            _ = ProcessClientAsync(client);
        }
    }

    private async Task ProcessClientAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();

        // WebSocket handshake
        while (!stream.DataAvailable) ;
        while (client.Available < 3) ; // Ensure enough data is available

        byte[] buffer = new byte[client.Available];
        stream.Read(buffer, 0, buffer.Length);
        string request = Encoding.UTF8.GetString(buffer);

        if (Regex.IsMatch(request, "^GET", RegexOptions.IgnoreCase))
        {
            Console.WriteLine("=====Handshaking from client=====\n{0}", request);

            string secWebSocketKey = Regex.Match(request, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
            string secWebSocketAccept = ComputeWebSocketAcceptKey(secWebSocketKey);

            // Send handshake response
            byte[] response = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Accept: " + secWebSocketAccept + "\r\n\r\n");
            await stream.WriteAsync(response, 0, response.Length);

            Console.WriteLine("Handshake sent");
        }

        // Process client messages
        while (client.Connected)
        {
            byte[] messageBuffer = new byte[1024];
            try
            {
                int bytesRead = await stream.ReadAsync(messageBuffer, 0, messageBuffer.Length);
                if (bytesRead == 0)
                {
                    Console.WriteLine("Client disconnected.");
                    break; // Client disconnected
                }

                // Decode message
                string message = Encoding.UTF8.GetString(messageBuffer, 0, bytesRead);
                Console.WriteLine($"Received: {message}");

                // Echo message back or handle it
                byte[] responseMessage = EncodeMessage(message);
                await stream.WriteAsync(responseMessage, 0, responseMessage.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex.Message}");
                break;
            }
        }

        // Cleanup
        client.Close();
    }

    private string ComputeWebSocketAcceptKey(string secWebSocketKey)
    {
        // Compute the Sec-WebSocket-Accept key as per RFC6455
        string concatKey = secWebSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        byte[] hash = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(concatKey));
        return Convert.ToBase64String(hash);
    }

    public async Task BroadcastPlayersAsync(ReadOnlyDictionary<string, Player> allPlayers)
    {
        var message = JsonSerializer.Serialize(allPlayers);
        byte[] encodedMessage = EncodeMessage(message);

        foreach (var client in _clients)
        {
            if (client.Connected)
            {
                var stream = client.GetStream();
                await stream.WriteAsync(encodedMessage, 0, encodedMessage.Length);
            }
        }
    }

    private byte[] EncodeMessage(string message)
    {
        byte[] rawMessage = Encoding.UTF8.GetBytes(message);
        int frameSize = rawMessage.Length + 2;
        byte[] frame = new byte[frameSize];

        frame[0] = 0x81; // FIN and text frame opcode
        frame[1] = (byte)rawMessage.Length;
        Buffer.BlockCopy(rawMessage, 0, frame, 2, rawMessage.Length);

        return frame;
    }

    public void Stop()
    {
        _isRunning = false;
        _server.Stop();
        foreach (var client in _clients)
        {
            client.Close();
        }
    }
}