﻿using GuacamoleSharp.Common.Models;
using GuacamoleSharp.Common.Settings;
using Serilog;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GuacamoleSharp.Server
{
    internal static class GSListener
    {
        #region Private Fields

        private static readonly ManualResetEvent _connectDone = new(false);
        private static readonly List<ConnectionState> _connections = new();
        private static readonly ILogger _logger = Log.ForContext(typeof(GSListener));
        private static readonly ManualResetEvent _restartReady = new(false);
        private static ulong _connectionCount = 0;
        private static GSSettings _gssettings = null!;
        private static BackgroundWorker _listenerThread = null!;

        #endregion Private Fields

        #region Internal Methods

        internal static void Close(ConnectionState state)
        {
            try
            {
                lock (state.DisposeLock)
                {
                    if (state.ClientSocket != null && !state.ClientClosed)
                    {
                        _logger.Information("[Connection {Id}] Closing client connection", state.ConnectionId);

                        state.ClientSocket.Shutdown(SocketShutdown.Both);
                        state.ClientSocket.Close();
                    }

                    state.ClientClosed = true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[Connection {id}] Error while closing client connection: {ex}", state.ConnectionId, ex);
            }
        }

        internal static void Restart()
        {
            _listenerThread.CancelAsync();

            _connectDone.Set();
            _restartReady.WaitOne();

            foreach (var connection in _connections)
            {
                GSGuacdClient.Close(connection);
            }

            _connections.Clear();
            _connectionCount = 0;

            StartListening(_gssettings);

            _restartReady.Reset();
        }

        internal static void Send(ConnectionState state, string message, bool isWSF = true)
        {
            state.ClientSendDone.Reset();

            _logger.Debug("[Connection {Id}] >>>G2W> {Message}", state.ConnectionId, message);

            byte[] data = isWSF ? WebSocketHelpers.WriteToFrame(message) : Encoding.UTF8.GetBytes(message);
            state.ClientSocket.BeginSend(data, 0, data.Length, 0, new AsyncCallback(SendCallback), state);

            state.ClientSendDone.WaitOne();
        }

        internal static void StartListening(GSSettings gssettings)
        {
            _gssettings = gssettings;

            IPEndPoint endpoint = new(IPAddress.Any, _gssettings.WebSocket.Port);

            Socket listener = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(endpoint);

            _logger.Information("Socket listening on: {ipEndPoint}", endpoint);

            _listenerThread = new();
            _listenerThread.WorkerReportsProgress = true;
            _listenerThread.WorkerSupportsCancellation = true;
            _listenerThread.DoWork += new DoWorkEventHandler(Listen_DoWork);
            _listenerThread.RunWorkerAsync(listener);
        }

        #endregion Internal Methods

        #region Private Methods

        private static void AcceptCallback(IAsyncResult ar)
        {
            try
            {
                Socket listener = (Socket)ar.AsyncState!;

                ConnectionState state = new();
                state.ClientSocket = listener.EndAccept(ar);
                _connectionCount += 1;
                state.ConnectionId = _connectionCount;

                _connections.Add(state);

                state.ClientSocket.BeginReceive(state.ClientBuffer, 0, state.ClientBuffer.Length, SocketFlags.None, new AsyncCallback(ConnectCallback), state);
            }
            catch (ObjectDisposedException)
            {
                _logger.Warning("Accept callback attempted to perform operation on disposed listener after restart");
            }
        }

        private static void ConnectCallback(IAsyncResult ar)
        {
            var state = (ConnectionState)ar.AsyncState!;

            if (DateTime.Now > state.LastActivity.AddMinutes(_gssettings.WebSocket.MaxInactivityAllowedInMin))
            {
                _logger.Warning("[Connection {Id}] Timeout", state.LastActivity);

                Close(state);
                _connectDone.Set();
                return;
            }

            int receivedLength = state.ClientSocket.EndReceive(ar);

            if (receivedLength > 0)
            {
                var content = Encoding.UTF8.GetString(state.ClientBuffer);
                NameValueCollection query = WebSocketHelpers.ParseQueryStringFromRequest(content);
                var token = query["token"];

                if (token == null)
                {
                    _logger.Warning("[Connection {Id}] Connection is missing the token query param", state.ConnectionId);

                    Close(state);
                    _connectDone.Set();
                    return;
                }

                var painText = TokenEncrypter.DecryptString(_gssettings.Token.Password, token);
                var connection = JsonSerializer.Deserialize<Connection>(painText, new JsonSerializerOptions()
                {
                    PropertyNameCaseInsensitive = true
                });

                if (connection == null)
                {
                    _logger.Warning("[Connection {Id}] Connection serialization returned null", state.ConnectionId);

                    Close(state);
                    _connectDone.Set();
                    return;
                }

                connection.Type = connection.Type.ToLowerInvariant();
                GuacamoleProtocolHelpers.AddDefaultConnectionSettings(connection, _gssettings.Client.DefaultConnectionSettings);
                GuacamoleProtocolHelpers.OverwriteConnectionWithUnencryptedConnectionSettings(connection, query, _gssettings.Client.UnencryptedConnectionSettings);

                state.Connection = connection;
                state.LastActivity = DateTime.Now;

                GSGuacdClient.Connect(_gssettings, connection, state);

                string response = WebSocketHelpers.BuildHttpUpgradeResponseFromRequest(content);

                Send(state, response, false);

                state.ClientHandshakeDone.Set();

                BackgroundWorker _clientThread = new();
                _clientThread.DoWork += new DoWorkEventHandler(Receive_DoWork);
                _clientThread.RunWorkerAsync(state);

                _connectDone.Set();
            }
            else
            {
                state.ClientSocket.BeginReceive(state.ClientBuffer, 0, state.ClientBuffer.Length, SocketFlags.None, new AsyncCallback(ConnectCallback), state);
            }
        }

        private static void Listen_DoWork(object? sender, DoWorkEventArgs e)
        {
            Socket listener = (Socket)e.Argument!;
            listener.Listen(10);

            while (!_listenerThread.CancellationPending)
            {
                _connectDone.Reset();

                listener.BeginAccept(new AsyncCallback(AcceptCallback), listener);

                _connectDone.WaitOne();
            }

            listener.Close();
            _restartReady.Set();
        }

        private static void Receive_DoWork(object? sender, DoWorkEventArgs e)
        {
            ConnectionState state = (ConnectionState)e.Argument!;

            state.GuacdHandshakeDone.WaitOne();

            try
            {
                while (!state.ClientClosed && !_listenerThread.CancellationPending)
                {
                    state.ClientReceiveDone.Reset();

                    state.ClientSocket.BeginReceive(state.ClientBuffer, 0, state.ClientBuffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), state);

                    state.ClientReceiveDone.WaitOne();
                }
            }
            catch (ObjectDisposedException)
            {
                _logger.Warning("[Connection {Id}] Receive callback attempted to perform operation on disposed listener after restart", state.ConnectionId);
            }
            catch (Exception ex)
            {
                _logger.Error("[Connection {Id}] Errow while running socket listener thread: {ex}", state.ConnectionId, ex);
                GSGuacdClient.Close(state);
            }
        }

        private static void ReceiveCallback(IAsyncResult ar)
        {
            var state = (ConnectionState)ar.AsyncState!;

            if (state.ClientClosed)
            {
                state.ClientReceiveDone.Set();
                return;
            }

            int receivedLength;

            try
            {
                receivedLength = state.ClientSocket.EndReceive(ar);
            }
            catch (Exception)
            {
                _logger.Warning("[Connection {Id}] Client socket tried to receive data from closed connection", state.ConnectionId);

                GSGuacdClient.Close(state);
                state.ClientReceiveDone.Set();
                return;
            }

            if (DateTime.Now > state.LastActivity.AddMinutes(_gssettings.WebSocket.MaxInactivityAllowedInMin))
            {
                _logger.Warning("[Connection {Id}] Timeout", state.LastActivity);

                GSGuacdClient.Close(state);
                state.ClientReceiveDone.Set();
                return;
            }

            if (receivedLength <= 0)
            {
                state.ClientReceiveDone.Set();
                return;
            }

            state.ClientResponseOverflowBuffer.Append(WebSocketHelpers.ReadFromFrames(state.ClientBuffer[0..receivedLength], receivedLength));
            string reponse = state.ClientResponseOverflowBuffer.ToString();

            if (!reponse.Contains(';'))
            {
                state.ClientSocket.BeginReceive(state.ClientBuffer, 0, state.ClientBuffer.Length, SocketFlags.None, new AsyncCallback(ReceiveCallback), state);
                return;
            }

            (string message, int delimiterIndex) = GuacamoleProtocolHelpers.ReadProtocolUntilLastDelimiter(reponse);
            state.ClientResponseOverflowBuffer.Remove(0, delimiterIndex);

            if (message.Contains("10.disconnect;"))
            {
                GSGuacdClient.Close(state);
                state.ClientReceiveDone.Set();
                return;
            }

            GSGuacdClient.Send(state, message);

            state.ClientReceiveDone.Set();
        }

        private static void SendCallback(IAsyncResult ar)
        {
            var state = (ConnectionState)ar.AsyncState!;

            if (state.ClientClosed)
            {
                state.ClientSendDone.Set();
                return;
            }

            try
            {
                state.ClientSocket.EndSend(ar);
            }
            catch (Exception)
            {
                _logger.Warning("[Connection {Id}] Client socket tried to send data to closed connection", state.ConnectionId);

                GSGuacdClient.Close(state);
                state.ClientSendDone.Set();
                return;
            }

            state.LastActivity = DateTime.Now;

            state.ClientSendDone.Set();
        }

        #endregion Private Methods
    }
}
