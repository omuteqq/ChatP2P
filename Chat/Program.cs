﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class ChatNode
{
    private readonly string _username;
    private readonly IPAddress _localIpAddress;
    private readonly UdpClient _udpClient;
    private readonly TcpListener _tcpListener;
    private readonly int _udpPort = 4545;
    private readonly int _tcpPort = 4546;
    private readonly ConcurrentDictionary<IPEndPoint, Peer> _peers = new ConcurrentDictionary<IPEndPoint, Peer>();
    private bool _isRunning = true;

    public ChatNode(IPAddress localIpAddress, string username, int tcpPort = 4546, int udpPort = 4545)
    {
        _localIpAddress = localIpAddress;
        _username = username;
        _udpPort = udpPort;
        _tcpPort = tcpPort;

        _udpClient = new UdpClient(new IPEndPoint(_localIpAddress, _udpPort));
        _udpClient.EnableBroadcast = true;

        _tcpListener = new TcpListener(_localIpAddress, _tcpPort);
        _tcpListener.Start();

        Task.Run(ListenForUdpBroadcasts);
        Task.Run(AcceptTcpConnections);
        Task.Run(SendPeriodicHeartbeat);

        BroadcastPresence();
    }

    private async Task SendPeriodicHeartbeat()
    {
        while (_isRunning)
        {
            await Task.Delay(5000);
            BroadcastPresence();
        }
    }

    private void BroadcastPresence()
    {
        try
        {
            var messageBytes = Encoding.UTF8.GetBytes(_username);
            var broadcastAddress = GetBroadcastAddress();
            var endpoint = new IPEndPoint(broadcastAddress, _udpPort);
            _udpClient.Send(messageBytes, messageBytes.Length, endpoint);
        }
        catch (Exception ex)
        {
            LogEvent($"Error broadcasting presence: {ex.Message}");
        }
    }

    private IPAddress GetBroadcastAddress()
    {
        if (_localIpAddress.ToString().StartsWith("127."))
        {
            return IPAddress.Broadcast;
        }

        var bytes = _localIpAddress.GetAddressBytes();
        bytes[3] = 255;
        return new IPAddress(bytes);
    }

    private async Task ListenForUdpBroadcasts()
    {
        while (_isRunning)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync();
                var username = Encoding.UTF8.GetString(result.Buffer);

                if (result.RemoteEndPoint.Address.Equals(_localIpAddress))
                    continue;

                var tcpEndpoint = new IPEndPoint(result.RemoteEndPoint.Address, _tcpPort);
                if (!_peers.ContainsKey(tcpEndpoint))
                {
                    var peer = new Peer(username, result.RemoteEndPoint.Address);
                    if (_peers.TryAdd(tcpEndpoint, peer))
                    {
                        LogEvent($"Discovered new peer: {username} ({result.RemoteEndPoint.Address})");
                        EstablishTcpConnection(peer);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    LogEvent($"Error receiving UDP broadcast: {ex.Message}");
            }
        }
    }

    private async Task AcceptTcpConnections()
    {
        while (_isRunning)
        {
            try
            {
                var tcpClient = await _tcpListener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleTcpConnection(tcpClient));
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    LogEvent($"Error accepting TCP connection: {ex.Message}");
            }
        }
    }

    private async Task HandleTcpConnection(TcpClient tcpClient)
    {
        try
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            {
                var buffer = new byte[1024];
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead > 0)
                {
                    var username = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var remoteEndpoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint;

                    var peer = new Peer(username, remoteEndpoint.Address) { TcpClient = tcpClient };
                    var peerKey = new IPEndPoint(remoteEndpoint.Address, _tcpPort);

                    if (_peers.TryAdd(peerKey, peer))
                    {
                        LogEvent($"Peer connected: {username} ({remoteEndpoint.Address})");
                        await ListenForPeerMessages(peer, stream);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogEvent($"Error handling TCP connection: {ex.Message}");
        }
    }

    private async Task ListenForPeerMessages(Peer peer, NetworkStream stream)
    {
        var buffer = new byte[1024];

        try
        {
            while (_isRunning)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    break;

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                LogEvent($"{peer.Username}: {message}", isIncoming: true);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            IPEndPoint peerEndpoint = new IPEndPoint(peer.IPAddress, _tcpPort);
            if (_peers.TryRemove(peerEndpoint, out _))
            {
                LogEvent($"Peer disconnected: {peer.Username} ({peer.IPAddress})");
            }
            peer.TcpClient?.Dispose();
        }
    }

    private void EstablishTcpConnection(Peer peer)
    {
        try
        {
            var tcpClient = new TcpClient(new IPEndPoint(_localIpAddress, 0));
            var endpoint = new IPEndPoint(peer.IPAddress, _tcpPort);

            if (!tcpClient.ConnectAsync(endpoint.Address, endpoint.Port).Wait(2000))
            {
                LogEvent($"Connection timeout to {peer.Username} ({peer.IPAddress})");
                return;
            }

            peer.TcpClient = tcpClient;

            var stream = tcpClient.GetStream();
            var usernameBytes = Encoding.UTF8.GetBytes(_username);
            stream.Write(usernameBytes, 0, usernameBytes.Length);

            _ = Task.Run(() => ListenForPeerMessages(peer, stream));
        }
        catch (Exception ex)
        {
            LogEvent($"Error establishing TCP connection to {peer.Username}: {ex.Message}");
        }
    }

    public void BroadcastMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        LogEvent($"You: {message}", isIncoming: false);

        var messageBytes = Encoding.UTF8.GetBytes(message);

        foreach (var peerEntry in _peers)
        {
            var peer = peerEntry.Value;
            try
            {
                if (peer.TcpClient?.Connected == true)
                {
                    var stream = peer.TcpClient.GetStream();
                    stream.Write(messageBytes, 0, messageBytes.Length);
                }
            }
            catch (Exception)
            {
                if (_peers.TryRemove(peerEntry.Key, out _))
                {
                    LogEvent($"Peer disconnected: {peer.Username} ({peer.IPAddress})");
                }
                peer.TcpClient?.Dispose();
            }
        }
    }

    private void LogEvent(string message, bool isIncoming = false)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{timestamp}] {message}");
    }

    public void Stop()
    {
        _isRunning = false;

        _udpClient?.Dispose();
        _tcpListener?.Stop();

        foreach (var peer in _peers.Values)
        {
            peer.TcpClient?.Dispose();
        }
        _peers.Clear();
    }
}

public class Peer
{
    public string Username { get; }
    public IPAddress IPAddress { get; }
    public TcpClient TcpClient { get; set; }

    public Peer(string username, IPAddress ipAddress)
    {
        Username = username;
        IPAddress = ipAddress;
    }
}



class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: PeerChat.exe <ip-address> <username>");
            Console.WriteLine("Example for testing on single machine:");
            Console.WriteLine("  PeerChat.exe 127.0.0.1 Alice");
            Console.WriteLine("  PeerChat.exe 127.0.0.2 Bob");
            Console.WriteLine("  PeerChat.exe 127.0.0.3 Charlie");
            return;
        }

        if (!IPAddress.TryParse(args[0], out var ipAddress))
        {
            Console.WriteLine($"Invalid IP address: {args[0]}");
            return;
        }

        string username = args[1];
        int tcpPort = 4546;
        int udpPort = 4545;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--tport":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int tport))
                    {
                        tcpPort = tport;
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("Invalid TCP port specified");
                        return;
                    }
                    break;

                case "--uport":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int uport))
                    {
                        udpPort = uport;
                        i++;
                    }
                    else
                    {
                        Console.WriteLine("Invalid UDP port specified");
                        return;
                    }
                    break;

                default:
                    Console.WriteLine($"Unknown argument: {args[i]}");
                    return;
            }
        }

        var chatNode = new ChatNode(ipAddress, username, tcpPort, udpPort);

        Console.WriteLine($"Chat node started as {username} on {ipAddress}. Type messages and press Enter to send.");
        Console.WriteLine($"Using TCP port: {tcpPort}, UDP port: {udpPort}");
        Console.WriteLine("Type 'exit' to quit.");

        while (true)
        {
            string message = Console.ReadLine();
            if (message?.ToLower() == "exit")
            {
                chatNode.Stop();
                break;
            }

            if (!string.IsNullOrEmpty(message))
            {
                chatNode.BroadcastMessage(message);
            }
        }
    }
}