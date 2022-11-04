﻿using GuacamoleSharp.Helpers;
using GuacamoleSharp.Logic.Sockets;
using GuacamoleSharp.Models;
using GuacamoleSharp.Options;
using Microsoft.Extensions.Options;
using Serilog;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace GuacamoleSharp.Logic.Connections
{
    public class ConnectionProcessorService : BackgroundService
    {
        private static readonly ConcurrentQueue<PendingConnection> _pendingConnections;
        private readonly ClientOptions _clientOptions;
        private readonly GuacamoleSharpOptions _guacamoleSharpOptions;
        private readonly GuacdOptions _guacdOptions;

        static ConnectionProcessorService()
        {
            _pendingConnections = new ConcurrentQueue<PendingConnection>();
        }

        public ConnectionProcessorService(IOptions<ClientOptions> clientOptions, IOptions<GuacamoleSharpOptions> guacamoleSharpOptions, IOptions<GuacdOptions> guacdOptions)
        {
            _clientOptions = clientOptions.Value;
            _guacamoleSharpOptions = guacamoleSharpOptions.Value;
            _guacdOptions = guacdOptions.Value;
        }

        public static void Add(WebSocket socket, Dictionary<string, string> arguments, TaskCompletionSource<bool> complete)
        {
            _pendingConnections.Enqueue(new PendingConnection
            {
                Socket = socket,
                Arguments = arguments,
                Complete = complete
            });
        }

        public override Task StopAsync(CancellationToken stoppingToken)
        {
            return base.StopAsync(stoppingToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();

            while (!stoppingToken.IsCancellationRequested)
            {
                while (_pendingConnections.TryDequeue(out var pendingConnection))
                {
                    Tunnel tunnel;

                    try
                    {
                        var connection = GetConnectionConfiguration(pendingConnection);
                        var endpoint = GetProxyEndPoint();
                        var client = new ClientSocket(pendingConnection.Id, pendingConnection.Socket);
                        var guacd = new GuacdSocket(pendingConnection.Id, endpoint);

                        tunnel = new Tunnel(connection, client, guacd, pendingConnection.Complete);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[{Id}] {Message}.", pendingConnection.Id, ex.Message);
                        Log.Information("[{Id}] Closing connection...", pendingConnection.Id);
                        await pendingConnection.Socket.CloseAsync(WebSocketCloseStatus.InternalServerError, string.Empty, CancellationToken.None);
                        pendingConnection.Complete.TrySetResult(false);
                        continue;
                    }

                    try
                    {
                        await tunnel.OpenAsync();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[{Id}] {Message}.", pendingConnection.Id, ex.Message);
                        Log.Information("[{Id}] Closing connection...", pendingConnection.Id);
                        await tunnel.CloseAsync();
                    }
                }
            }

            await Task.CompletedTask;
        }

        private Connection GetConnectionConfiguration(PendingConnection pendingConnection)
        {
            try
            {
                Log.Information("[{Id}] Building connection configuration...", pendingConnection.Id);

                var token = pendingConnection.Arguments["token"];
                var plainText = TokenEncryptionHelper.DecryptString(_guacamoleSharpOptions.Password, token);
                var connection = JsonSerializer.Deserialize<Connection>(plainText, new JsonSerializerOptions() { PropertyNameCaseInsensitive = true }) ?? throw new Exception($"Connection token failed to serialize");
                connection.Type = connection.Type.ToLowerInvariant();

                foreach (var arg in _clientOptions.DefaultArguments[connection.Type])
                {
                    if (!connection.Arguments.ContainsKey(arg.Key))
                    {
                        connection.Arguments.Add(arg.Key, arg.Value);
                    }
                }

                var paramKeys = pendingConnection.Arguments.Keys
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Intersect(_clientOptions.UnencryptedArguments[connection.Type])
                    .ToList();

                foreach (var key in paramKeys)
                {
                    if (string.IsNullOrWhiteSpace(pendingConnection.Arguments[key])) continue;

                    connection.Arguments[key] = pendingConnection.Arguments[key];
                }

                Log.Debug("[{Id}] Connection configuration: {@Connection}", pendingConnection.Id, connection);

                return connection;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to configure connection: {ex.Message}");
            }
        }

        private IPEndPoint GetProxyEndPoint()
        {
            try
            {
                if (!IPAddress.TryParse(_guacdOptions.Hostname, out IPAddress? ip) || ip == null)
                {
                    ip = Dns.GetHostAddresses(_guacdOptions.Hostname).First(x => x.AddressFamily == AddressFamily.InterNetwork);
                }

                return new IPEndPoint(ip, _guacdOptions.Port);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to configure endpoint: {ex.Message}");
            }
        }
    }
}