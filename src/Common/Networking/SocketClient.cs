using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Models;
using Common.Logging;

namespace Common.Networking
{
    public class SocketClient : IDisposable
    {
        protected readonly string _host;
        protected readonly int _port;
        protected TcpClient _client;
        protected NetworkStream _stream;
        protected CancellationTokenSource _cancellationTokenSource;
        protected bool _isConnected;
        protected Thread _receiveThread;
        protected readonly StringBuilder _messageBuilder = new StringBuilder();
        
        public event EventHandler<Message> MessageReceived;
        public event EventHandler Connected;
        public event EventHandler Disconnected;
        
        public virtual bool IsConnected => _client?.Connected ?? false;

        public SocketClient(string host, int port)
        {
            _host = host;
            _port = port;
        }

        public async Task ConnectAsync()
        {
            if (_isConnected)
                return;

            try
            {
                Logger.Connection(LogLevel.Debug, $"Attempting to connect to server {_host}:{_port}...");
                _client = new TcpClient();
                
                // Set a connection timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client.ConnectAsync(_host, _port, cts.Token);
                
                _stream = _client.GetStream();
                _isConnected = true;
                
                _cancellationTokenSource = new CancellationTokenSource();
                
                // Add a small delay to ensure the connection is stable before proceeding
                await Task.Delay(100);
                
                // Start receiving messages in a separate thread
                Logger.Connection(LogLevel.Debug, $"Starting message listener thread for {_host}:{_port}");
                _receiveThread = new Thread(() => HandleMessagesAsync(_cancellationTokenSource.Token).Wait());
                _receiveThread.Start();
                
                Logger.Connection(LogLevel.Debug, $"Calling OnConnectedAsync for {_host}:{_port}");
                await OnConnectedAsync();
                Connected?.Invoke(this, EventArgs.Empty);
                
                Logger.Connection(LogLevel.Debug, $"Successfully connected to server {_host}:{_port}");
            }
            catch (OperationCanceledException)
            {
                Logger.Error($"Connection timeout to server {_host}:{_port}");
                throw;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to connect to server {_host}:{_port}", ex);
                throw;
            }
        }

        public void Disconnect()
        {
            if (!_isConnected)
                return;

            _isConnected = false;
            _cancellationTokenSource?.Cancel();
            _stream?.Close();
            _client?.Close();
            
            Logger.Connection(LogLevel.Debug, $"Disconnected from server {_host}:{_port}");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }

        private async Task HandleMessagesAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();
            
            try
            {
                while (!cancellationToken.IsCancellationRequested && _client.Connected)
                {
                    var bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    
                    if (bytesRead == 0)
                        break;
                        
                    var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(data);
                    
                    // Process complete messages
                    var json = messageBuilder.ToString();
                    var messages = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var messageJson in messages)
                    {
                        if (string.IsNullOrWhiteSpace(messageJson))
                            continue;
                            
                        try
                        {
                            var message = Message.Deserialize(messageJson);
                            if (message != null)
                            {
                                await OnMessageReceivedAsync(message);
                            }
                            else
                            {
                                Logger.Error($"Received null message from deserialization: {messageJson}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error deserializing message: {ex.Message}");
                        }
                    }
                    
                    // Keep any remaining partial message
                    var lastNewline = json.LastIndexOf('\n');
                    if (lastNewline >= 0)
                    {
                        messageBuilder.Remove(0, lastNewline + 1);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex is SocketException)
            {
                // Expected when stopping or disconnecting
            }
            catch (Exception ex)
            {
                Logger.Error("Error handling messages", ex);
            }
            finally
            {
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        public async Task SendMessageAsync(Message message)
        {
            if (!_isConnected)
                throw new InvalidOperationException("Client is not connected");

            if (message == null)
            {
                Logger.Error("Attempted to send null message");
                return;
            }

            try
            {
                var messageJson = Message.Serialize(message);
                if (string.IsNullOrEmpty(messageJson))
                {
                    Logger.Error("Message serialization produced null or empty string");
                    return;
                }
                
                // Add message delimiter
                messageJson += "\n";
                var messageBytes = Encoding.UTF8.GetBytes(messageJson);
                
                Logger.Connection(LogLevel.Debug, $"Sending message to {_host}:{_port}: {messageBytes.Length} bytes, Type={message.Type}");
                Logger.Connection(LogLevel.Debug, $"Raw message: {messageJson.TrimEnd('\n')}");
                
                await _stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                
                // Force flush to ensure the message is sent immediately
                await _stream.FlushAsync();
                
                Logger.Connection(LogLevel.Debug, $"Message sent to {_host}:{_port}: {message.Type}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error sending message to {_host}:{_port}", ex);
                Disconnect();
            }
        }

        protected virtual async Task OnConnectedAsync()
        {
            // Base implementation does nothing, to be overridden by derived classes
            await Task.CompletedTask;
        }

        protected virtual async Task OnMessageReceivedAsync(Message message)
        {
            // Base implementation invokes the MessageReceived event
            MessageReceived?.Invoke(this, message);
            await Task.CompletedTask;
        }

        public void Dispose()
        {
            Disconnect();
            _cancellationTokenSource?.Dispose();
            _client?.Dispose();
        }
    }
} 