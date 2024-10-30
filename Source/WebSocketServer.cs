using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Numerics;

namespace eft_dma_radar.Source;

public class GameDTO
{
    public string MessageId { get; set; }
    public bool IsScav { get; set; }
    public string MapName { get; set; }
}

public class PlayerDTO
{
    public string MessageId { get; set; }
    /// <summary>
    /// Player is a Local PMC Operator.
    /// </summary>
    public bool IsLocalPlayer { get; set; }

    /// <summary>
    /// Player is Alive/Not Dead.
    /// </summary>
    public bool IsAlive { get; set; }

    /// <summary>
    /// Player is Active (has not exfil'd).
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Player name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Type of player unit.
    /// </summary>
    public PlayerType Type { get; set; }

    /// <summary>
    /// Player's current health (sum of all 7 body parts).
    /// </summary>
    public string HealthStatus { get; set; }

    /// <summary>
    /// Player's Unity Position in Local Game World.
    /// </summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    /// <summary>
    /// Player's Rotation (direction/pitch) in Local Game World.
    /// 90 degree offset ~already~ applied to account for 2D-Map orientation.
    /// </summary>
    public float Direction { get; set; }
    public float Pitch { get; set; }

    /// <summary>
    /// Key = Slot Name, Value = Item 'Long Name' in Slot
    /// </summary>
    public Dictionary<string, string> Gear { get; set; }
}

public class WebSocketServer
{
    private TcpListener _server;
    private bool _isRunning;
    private static ConcurrentDictionary<TcpClient, NetworkStream> _clients = new();
    private System.Threading.Timer playerBroadcastTimer; // Keep a reference to the Timer
    private System.Threading.Timer gameBroadcastTimer; // Keep a reference to the Timer

    /// <summary>
    /// All Players in Local Game World (including dead/exfil'd) 'Player' collection.
    /// </summary>
    private ReadOnlyDictionary<string, Player> AllPlayers
    {
        get => Memory.Players;
    }

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
        // Start the periodic broadcast task
        StartBroadcastingPlayerData(TimeSpan.FromMilliseconds(500));
        StartBroadcastingGameData(TimeSpan.FromSeconds(10));

        while (_isRunning)
        {
            TcpClient client = await _server.AcceptTcpClientAsync();
            Console.WriteLine("A client connected.");
            _clients[client] = client.GetStream();
            _ = Task.Run(() => ProcessClientAsync(client));
        }
    }

    private async Task ProcessClientAsync(TcpClient client)
    {
        NetworkStream stream = client.GetStream();

        try
        {
            while (true)
            {
                while (!stream.DataAvailable) await Task.Delay(10);
                while (client.Available < 3) await Task.Delay(10); // match against "get"

                byte[] bytes = new byte[client.Available];
                await stream.ReadAsync(bytes, 0, bytes.Length);
                string s = Encoding.UTF8.GetString(bytes);

                if (Regex.IsMatch(s, "^GET", RegexOptions.IgnoreCase))
                {
                    Console.WriteLine("=====Handshaking from client=====\n{0}", s);

                    string swk = Regex.Match(s, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
                    string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                    byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create()
                        .ComputeHash(Encoding.UTF8.GetBytes(swka));
                    string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

                    byte[] response = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");

                    await stream.WriteAsync(response, 0, response.Length);
                }
                else
                {
                    bool fin = (bytes[0] & 0b10000000) != 0,
                        mask = (bytes[1] & 0b10000000) != 0;
                    int opcode = bytes[0] & 0b00001111;
                    if (opcode != 0x1) // Assume we're expecting text messages
                    {
                        Console.WriteLine("Unrecognized frame opcode: {0}", opcode);
                        continue; // Skip processing this frame
                    }

                    ulong offset = 2,
                        msglen = bytes[1] & (ulong)0b01111111;

                    if (msglen == 126)
                    {
                        msglen = BitConverter.ToUInt16(new byte[] { bytes[3], bytes[2] }, 0);
                        offset = 4;
                    }
                    else if (msglen == 127)
                    {
                        msglen = BitConverter.ToUInt64(
                            new byte[]
                            {
                                bytes[9], bytes[8], bytes[7], bytes[6], bytes[5], bytes[4], bytes[3], bytes[2]
                            }, 0);
                        offset = 10;
                    }

                    if (msglen == 0)
                    {
                        Console.WriteLine("msglen == 0");
                    }
                    else if (mask)
                    {
                        byte[] decoded = new byte[msglen];
                        byte[] masks = new byte[4]
                            { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
                        offset += 4;

                        for (ulong i = 0; i < msglen; ++i)
                            decoded[i] = (byte)(bytes[offset + i] ^ masks[i % 4]);

                        string text = Encoding.UTF8.GetString(decoded);
                        Console.WriteLine("{0}", text);

                        // Broadcast message to all clients except sender
                        BroadcastMessage(client, text);
                    }
                    else
                    {
                        Console.WriteLine("mask bit not set");
                    }

                    Console.WriteLine();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Exception: {0}", ex);
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Close();
        }
    }

    private static void BroadcastMessage(TcpClient sender, string message)
    {
        // Construct WebSocket frame
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] frame = CreateWebSocketFrame(messageBytes);

        foreach (var kvp in _clients)
        {
            TcpClient client = kvp.Key;
            NetworkStream stream = kvp.Value;

            // if (client != sender)
            {
                try
                {
                    stream.Write(frame, 0, frame.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error sending message to a client: {0}", ex.Message);
                    // Consider removing the client from the list if writing fails repeatedly
                    // remove client from list
                    _clients.TryRemove(client, out _);
                }
            }
        }
    }

    private static void BroadcastMessage(string message)
    {
        // Construct WebSocket frame
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        byte[] frame = CreateWebSocketFrame(messageBytes);

        foreach (var kvp in _clients)
        {
            TcpClient client = kvp.Key;
            NetworkStream stream = kvp.Value;

            // if (client != sender)
            {
                try
                {
                    stream.Write(frame, 0, frame.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error sending message to a client: {0}", ex.Message);
                    // Consider removing the client from the list if writing fails repeatedly
                    // remove client from list
                    _clients.TryRemove(client, out _);
                }
            }
        }
    }

    private static byte[] CreateWebSocketFrame(byte[] message)
    {
        byte[] prefix;
        if (message.Length <= 125)
        {
            prefix = new byte[] { 0x81, (byte)message.Length };
        }
        else if (message.Length <= 65535)
        {
            prefix = new byte[] { 0x81, 126, (byte)(message.Length >> 8), (byte)message.Length };
        }
        else
        {
            // Note: Handling frames larger than 65535 in length is beyond the basic implementation
            throw new InvalidOperationException("Message too long");
        }

        byte[] frame = new byte[prefix.Length + message.Length];
        Buffer.BlockCopy(prefix, 0, frame, 0, prefix.Length);
        Buffer.BlockCopy(message, 0, frame, prefix.Length, message.Length);

        return frame;
    }

    private void StartBroadcastingPlayerData(TimeSpan interval)
    {
        playerBroadcastTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (_clients.Count == 0 || Memory.InGame == false)
                {
                    return;
                }
                    
                // Create a collection of PlayerDTO objects
                var playerDtos = AllPlayers.Select(pair => new PlayerDTO
                {
                    MessageId = "players",
                    IsLocalPlayer = pair.Value.Type == PlayerType.LocalPlayer,
                    IsAlive = pair.Value.IsAlive,
                    IsActive = pair.Value.IsActive,
                    Name = pair.Value.Name,
                    Type = pair.Value.Type,
                    HealthStatus = pair.Value.HealthStatus,
                    X = pair.Value.Position.X,
                    Y = pair.Value.Position.Y,
                    Z = pair.Value.Position.Z,
                    Direction = pair.Value.Rotation.X,
                    Pitch = pair.Value.Rotation.Y,
                    Gear = pair.Value.Gear.ToDictionary(x => x.Key, x => x.Value.Short)
                }).ToList();

                string jsonString =
                    JsonSerializer.Serialize(playerDtos, new JsonSerializerOptions { WriteIndented = true });
                BroadcastMessage(jsonString);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }, null, TimeSpan.Zero, interval);
    }

    private void StartBroadcastingGameData(TimeSpan interval)
    {
        gameBroadcastTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (_clients.Count == 0 || Memory.InGame == false)
                {
                    return;
                }
                
                var gameDto = new GameDTO
                {
                    MessageId = "game",
                    MapName = Memory.MapName,
                    IsScav = Memory.IsScav,
                };

                string jsonString =
                    JsonSerializer.Serialize(gameDto, new JsonSerializerOptions { WriteIndented = true });
                BroadcastMessage(jsonString);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }, null, TimeSpan.Zero, interval);
    }
    
    public void Stop()
    {
        _isRunning = false;
        _server.Stop();
        foreach (var client in _clients)
        {
            client.Key.Close();
        }
    }
}