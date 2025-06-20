using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace UDPoverTCP {
    public partial class MainPage : ContentPage {
        private Socket? listenSocket;
        private Socket? clientSocket;
        private CancellationTokenSource? _cancellationSource;
        private Socket? _listenSocket;
        private Socket? _clientSocket;
        private readonly Lock _connectionLock = new();
        private Socket? _activeTcpConnection;
        private IPEndPoint lastUdpTargetEndPoint = new(IPAddress.Any, 0);
        private int unpackCount = 0;
        private int packCount = 0;
        private readonly IDispatcherTimer _refreshTimer;
        public MainPage() {
            InitializeComponent();
            _refreshTimer = Dispatcher.CreateTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(500);
            _refreshTimer.Tick += (s, e) => {
                InfoLabel.Text = $"总解包数：{unpackCount}，总打包数：{packCount}";
            };
            _refreshTimer.Start();
            ListenPortEntry.Text = Preferences.Get("ListenPort", "8720");
            ClientPortEntry.Text = Preferences.Get("ClientPort", "8720");
            ForwardAddressEntry.Text = Preferences.Get("ForwardAddress", "127.0.0.1:8721");
        }
        private async void TcpListenButton_Clicked(object sender, EventArgs e) {
            await StartTcpProxy().ConfigureAwait(false);
        }
        private async void UdpListenButton_Clicked(object sender, EventArgs e) {
            await StartUdpProxy().ConfigureAwait(false);
        }
        private static async Task<IPEndPoint> ParseEndpointAsync(string endpoint) {
            if (string.IsNullOrWhiteSpace(endpoint)) {
                throw new ArgumentException("Endpoint cannot be empty");
            }

            // 拆分地址和端口
            int lastColonIndex = endpoint.LastIndexOf(':');
            if (lastColonIndex < 0) {
                throw new FormatException("Invalid endpoint format. Missing port number.");
            }

            string addressPart = endpoint[..lastColonIndex];
            string portPart = endpoint[(lastColonIndex + 1)..];

            // 解析端口
            if (!int.TryParse(portPart, out int port) || port < 0 || port > 65535) {
                throw new FormatException("Invalid port number");
            }

            // 解析 IP 或域名
            if (IPAddress.TryParse(addressPart, out IPAddress? ipAddress)) {
                return new IPEndPoint(ipAddress, port);
            } else {
                // 异步 DNS 解析（支持 MAUI 所有平台）
                IPAddress[] hostEntries = await Dns.GetHostAddressesAsync(addressPart);
                if (hostEntries == null || hostEntries.Length == 0) {
                    throw new SocketException(11001); // HostNotFound
                }

                // 返回第一个 IPv4 地址（可调整策略）
                foreach (var addr in hostEntries) {
                    if (addr.AddressFamily == AddressFamily.InterNetwork) {
                        return new IPEndPoint(addr, port);
                    }
                }

                // 若无 IPv4 则返回第一个地址（IPv6）
                return new IPEndPoint(hostEntries[0], port);
            }
        }

        public async Task StartTcpProxy() {
            // 清理旧实例
            StopProxy();

            try {
                _cancellationSource = new CancellationTokenSource();
                CancellationToken token = _cancellationSource.Token;
                int tcpPort = int.Parse(ListenPortEntry.Text);
                int udpPort = int.Parse(ClientPortEntry.Text);
                // 创建监听Socket
                _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listenSocket.Bind(new IPEndPoint(IPAddress.Any, tcpPort));
                _listenSocket.Listen(10);

                // 创建客户端UDP Socket
                _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _clientSocket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.1"), udpPort));

                IPEndPoint targetEndPoint = await ParseEndpointAsync(ForwardAddressEntry.Text).ConfigureAwait(false);
                lastUdpTargetEndPoint = targetEndPoint;

                Application.Current?.Dispatcher.Dispatch(() => {
                    LogEditor.Text += $"[{DateTime.Now:HH:mm:ss}] INFO: TCP监听{tcpPort}，UDP使用{udpPort}\n";
                });
                // 启动TCP监听任务
                _ = Task.Run(() => ListenTcpConnections(_listenSocket, _clientSocket, token), token);

                // 启动UDP接收任务
                _ = Task.Run(() => ReceiveUdpData(_clientSocket, token), token);
            } catch (Exception ex) {
                LogError(ex);
            }
        }

        public async Task StartUdpProxy() {
            // 清理旧实例
            StopProxy();

            try {
                _cancellationSource = new CancellationTokenSource();
                CancellationToken token = _cancellationSource.Token;
                int udpPort = int.Parse(ListenPortEntry.Text);
                // 创建监听Socket
                _activeTcpConnection = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                IPEndPoint targetEndPoint = await ParseEndpointAsync(ForwardAddressEntry.Text).ConfigureAwait(false);
                _activeTcpConnection.Connect(targetEndPoint);
                lastUdpTargetEndPoint = targetEndPoint;

                // 创建客户端UDP Socket
                _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _clientSocket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.1"), udpPort));

                Application.Current?.Dispatcher.Dispatch(() => {
                    LogEditor.Text += $"[{DateTime.Now:HH:mm:ss}] INFO: UDP监听{udpPort}，TCP发送到{targetEndPoint}\n";
                });

                // 启动TCP监听任务
                _ = Task.Run(() => HandleTcpConnection(_activeTcpConnection, _clientSocket, token), token);

                // 启动UDP接收任务
                _ = Task.Run(() => ReceiveUdpData(_clientSocket, token), token);
            } catch (Exception ex) {
                LogError(ex);
            }
        }

        public void StopProxy() {
            _cancellationSource?.Cancel();

            lock (_connectionLock) {
                _activeTcpConnection?.Close();
                _activeTcpConnection = null;
            }

            _listenSocket?.Close();
            _clientSocket?.Close();
        }

        private async Task ListenTcpConnections(Socket listenSocket, Socket udpSocket, CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    Socket tcpSocket = await listenSocket.AcceptAsync(token);

                    // 关闭前一个连接（如果存在）
                    lock (_connectionLock) {
                        _activeTcpConnection?.Close();
                        _activeTcpConnection = tcpSocket;
                    }

                    // 处理新连接
                    _ = Task.Run(() => HandleTcpConnection(tcpSocket, udpSocket, token), token);
                }
            } catch (OperationCanceledException) { /* 正常退出 */ } catch (Exception ex) {
                LogError(ex);
            }
        }

        private async Task HandleTcpConnection(Socket tcpSocket, Socket udpSocket, CancellationToken token) {
            try {
                byte[] buffer = new byte[4096];
                List<byte> dataBuffer = new(8192);

                while (!token.IsCancellationRequested) {
                    int received = await tcpSocket.ReceiveAsync(buffer, SocketFlags.None, token);
                    if (received == 0) {
                        break; // 连接关闭
                    }

                    IPEndPoint targetEndPoint;
                    lock (_connectionLock) {
                        targetEndPoint = lastUdpTargetEndPoint;
                    }
                    // 添加到数据缓冲区
                    dataBuffer.AddRange(buffer.AsSpan(0, received));

                    // 处理所有完整数据包
                    while (dataBuffer.Count >= 4) {
                        int packetSize = BitConverter.ToInt32([.. dataBuffer.GetRange(0, 4)], 0);
                        if (dataBuffer.Count < packetSize + 4) {
                            break;
                        }
                        Interlocked.Increment(ref unpackCount);

                        // 提取数据包
                        byte[] packetData = [.. dataBuffer.GetRange(4, packetSize)];
                        await udpSocket.SendToAsync(packetData, SocketFlags.None, targetEndPoint);

                        // 移除已处理数据
                        dataBuffer.RemoveRange(0, packetSize + 4);
                    }
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogError(ex);
            } finally {
                lock (_connectionLock) {
                    if (ReferenceEquals(_activeTcpConnection, tcpSocket))
                        _activeTcpConnection = null;
                }
                tcpSocket.Close();
            }
        }

        private async Task ReceiveUdpData(Socket udpSocket, CancellationToken token) {
            try {
                byte[] buffer = new byte[65507 + 4]; // UDP最大包大小 + 四字节长度
                IPEndPoint endpoint = new(IPAddress.Any, 0);

                while (!token.IsCancellationRequested) {
                    SocketReceiveFromResult result = await udpSocket.ReceiveFromAsync(buffer.AsMemory(4), SocketFlags.None, endpoint, token);
                    if (result.ReceivedBytes == 0) {
                        continue;
                    }

                    // 添加4字节长度头
                    BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), result.ReceivedBytes);

                    // 通过TCP发送
                    Socket? sendSocket;
                    lock (_connectionLock) {
                        sendSocket = _activeTcpConnection;
                        lastUdpTargetEndPoint = (IPEndPoint)result.RemoteEndPoint;
                    }

                    if (sendSocket?.Connected == true) {
                        Interlocked.Increment(ref packCount);
                        await sendSocket.SendAsync(buffer.AsMemory(0, result.ReceivedBytes + 4), SocketFlags.None, token);
                    }
                }
            } catch (OperationCanceledException) { /* 正常退出 */ } catch (Exception ex) {
                LogError(ex);
            }
        }

        private void LogError(Exception ex) {
            Application.Current?.Dispatcher.Dispatch(() => {
                LogEditor.Text += $"[{DateTime.Now:HH:mm:ss}] ERROR: {ex}\n";
            });
        }
        private async void TcpListenButton_Clicked_old(object sender, EventArgs e) {
            try {
                listenSocket?.Close();
                listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listenSocket.Bind(new IPEndPoint(IPAddress.Any, int.Parse(ListenPortEntry.Text)));
                listenSocket.Listen();
                clientSocket?.Close();
                clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                clientSocket.Bind(new IPEndPoint(IPAddress.Any, int.Parse(ClientPortEntry.Text)));
                IPEndPoint targetEndPoint = await ParseEndpointAsync(ForwardAddressEntry.Text).ConfigureAwait(false);
                try {
                    Socket tcpSocket = listenSocket;
                    Socket udpSocket = clientSocket;
                    Socket? connectSocket = null;
                    new Task(async () => {
                        while (true) {
                            connectSocket = await tcpSocket.AcceptAsync().ConfigureAwait(false);
                            try {
                                List<byte> bytes = [];
                                byte[] buffer = new byte[1024];
                                while (true) {
                                    int length = await connectSocket.ReceiveAsync(buffer).ConfigureAwait(false);
                                    if (length == 0) {
                                        break;
                                    }
                                    bytes.AddRange(buffer.Take(length));
                                    if (bytes.Count >= 4) {
                                        int length2 = BitConverter.ToInt32([.. bytes], 0);
                                        if (bytes.Count >= length2 + 4) {
                                            byte[] data = [.. bytes.Skip(4).Take(length2)];
                                            bytes.RemoveRange(0, length2 + 4);
                                            await clientSocket.SendToAsync(new ArraySegment<byte>(data), SocketFlags.None, targetEndPoint);
                                        }
                                    }
                                }
                            } catch (Exception ex) {
                                Application.Current?.Dispatcher.Dispatch(() => {
                                    LogEditor.Text += ex;
                                });
                            }
                        }
                    }).Start();
                    new Task(async () => {
                        while (true) {
                            byte[] buffer = new byte[udpSocket.Available + 4];
                            IPEndPoint receiveEndPoint = new(IPAddress.Any, 0);
                            SocketReceiveFromResult result = await udpSocket.ReceiveFromAsync(buffer.AsMemory(4), SocketFlags.None, receiveEndPoint).ConfigureAwait(false);
                            if (result.ReceivedBytes == 0) {
                                break;
                            }
                            if (BitConverter.TryWriteBytes(buffer, result.ReceivedBytes)) {
                                if (connectSocket is Socket sendSocket) {
                                    await sendSocket.SendAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                                }
                            }
                        }
                    }).Start();
                } catch (Exception ex) {
                    Application.Current?.Dispatcher.Dispatch(() => {
                        LogEditor.Text += ex;
                    });
                }
            } catch (Exception ex) {
                Application.Current?.Dispatcher.Dispatch(() => {
                    LogEditor.Text += ex;
                });
            }
        }
        private async void UdpListenButton_Clicked_old(object sender, EventArgs e) {
            try {
                listenSocket?.Close();
                listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                listenSocket.Bind(new IPEndPoint(IPAddress.Any, int.Parse(ListenPortEntry.Text)));
                clientSocket?.Close();
                clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                clientSocket.Connect(new IPEndPoint(IPAddress.Any, int.Parse(ClientPortEntry.Text)));
                IPEndPoint targetEndPoint = await ParseEndpointAsync(ForwardAddressEntry.Text).ConfigureAwait(false);
                new Task(async () => {
                    while (true) {
                        byte[] buffer = new byte[listenSocket.Available + 4];
                        IPEndPoint receiveEndPoint = new(IPAddress.Any, 0);
                        SocketReceiveFromResult result = await listenSocket.ReceiveFromAsync(buffer.AsMemory(4), SocketFlags.None, receiveEndPoint).ConfigureAwait(false);
                        if (result.ReceivedBytes == 0) {
                            break;
                        }
                        if (BitConverter.TryWriteBytes(buffer, result.ReceivedBytes)) {
                            await clientSocket.SendAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                        }
                    }
                }).Start();
            } catch (Exception ex) {
                LogEditor.Text += ex;
            }
        }

        private void ListenPortEntry_TextChanged(object sender, TextChangedEventArgs e) {
            Preferences.Set("ListenPort", ListenPortEntry.Text);
        }

        private void ClientPortEntry_TextChanged(object sender, TextChangedEventArgs e) {
            Preferences.Set("ClientPort", ClientPortEntry.Text);
        }

        private void ForwardAddressEntry_TextChanged(object sender, TextChangedEventArgs e) {
            Preferences.Set("ForwardAddress", ForwardAddressEntry.Text);
        }
    }
}
