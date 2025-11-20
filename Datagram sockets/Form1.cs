using System;
using System.IO;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using System.Collections.Generic;
using System.Linq;


namespace Datagram_sockets
{
    public partial class Form1 : Form
    {
        [Serializable]
        public class Message
        {
            public string mes; // текст сообщения
            public string user; // имя пользователя
            public string oldNickname; // старый nickname (для уведомления о смене)
            public string messageType; // тип сообщения: "chat", "nickname_change", "join", "leave", "heartbeat", "heartbeat"
            public Message()
            {
                messageType = "chat"; // по умолчанию обычное сообщение
            }
        }
        
        public SynchronizationContext uiContext;
        private Socket receiveSocket;
        private string currentUserName;
        private string currentNickname;
        private string localIP;
        private Dictionary<string, DateTime> users = new Dictionary<string, DateTime>(); // nickname (IP) -> DateTime
        private Dictionary<string, string> userNicknames = new Dictionary<string, string>(); // IP -> nickname
        private Dictionary<string, DateTime> sentMessages = new Dictionary<string, DateTime>(); // хеш сообщения -> время отправки
        private const int USER_TIMEOUT_SECONDS = 30;
        private const int MESSAGE_DEDUP_SECONDS = 3; // окно для предотвращения дублирования
        
        // Методы для вывода сообщений
        private void ShowError(string message)
        {
            try
            {
                if (uiContext != null)
                {
                    uiContext.Send(d =>
                    {
                        try
                        {
                            labelStatus.ForeColor = System.Drawing.Color.Red;
                            labelStatus.Text = $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}";
                            // Автоматически скрываем сообщение через 5 секунд
                            Task.Delay(5000).ContinueWith(t =>
                            {
                                if (uiContext != null)
                                {
                                    uiContext.Send(d2 =>
                                    {
                                        if (labelStatus.Text.Contains(message))
                                        {
                                            labelStatus.Text = "";
                                        }
                                    }, null);
                                }
                            });
                        }
                        catch
                        {
                            // Игнорируем ошибки при обновлении UI
                        }
                    }, null);
                }
            }
            catch
            {
                // Игнорируем ошибки, если UI еще не готов
            }
        }
        
        private void ShowInfo(string message)
        {
            try
            {
                if (uiContext != null)
                {
                    uiContext.Send(d =>
                    {
                        try
                        {
                            labelStatus.ForeColor = System.Drawing.Color.Green;
                            labelStatus.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
                            // Автоматически скрываем сообщение через 3 секунды
                            Task.Delay(3000).ContinueWith(t =>
                            {
                                if (uiContext != null)
                                {
                                    uiContext.Send(d2 =>
                                    {
                                        if (labelStatus.Text.Contains(message))
                                        {
                                            labelStatus.Text = "";
                                        }
                                    }, null);
                                }
                            });
                        }
                        catch
                        {
                            // Игнорируем ошибки при обновлении UI
                        }
                    }, null);
                }
            }
            catch
            {
                // Игнорируем ошибки, если UI еще не готов
            }
        }

        public Form1()
        {
            InitializeComponent();
            // Загружаем иконку приложения
            try
            {
                string iconPath = System.IO.Path.Combine(Application.StartupPath, "app.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    this.Icon = new System.Drawing.Icon(iconPath);
                }
            }
            catch
            {
                // Если не удалось загрузить иконку, используем иконку по умолчанию
            }
            // Получим контекст синхронизации для текущего потока 
            uiContext = SynchronizationContext.Current;
            currentUserName = Environment.UserDomainName + @"\" + Environment.UserName;
            currentNickname = currentUserName; // по умолчанию nickname = системное имя
            textBoxNickname.Text = currentNickname;
            
            // Определяем локальный IP адрес для multicast
            try
            {
                // Пробуем получить первый не-loopback IPv4 адрес
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        localIP = ip.ToString();
                        break;
                    }
                }
                
                // Если не нашли, пробуем через подключение
                if (string.IsNullOrEmpty(localIP) || localIP == "127.0.0.1")
                {
                    using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                    {
                        socket.Connect("8.8.8.8", 65530);
                        IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                        localIP = endPoint.Address.ToString();
                    }
                }
            }
            catch
            {
                localIP = "127.0.0.1"; // fallback
            }
            
            // Инициализируем свой nickname в словаре
            lock (userNicknames)
            {
                userNicknames[localIP] = currentNickname;
            }
            
            try
            {
                WaitClientQuery();
                CheckUsersTimeout();
                StartHeartbeat();
                
                // Отправляем уведомление о подключении с задержкой, чтобы сокет успел инициализироваться
                Task.Delay(1500).ContinueWith(t => SendJoinNotification());
            }
            catch (Exception ex)
            {
                ShowError($"Initialization error: {ex.Message}");
            }
        }
        // Multicast — это такая технология сетевой адресации, при которой сообщение доставляются сразу группе получателей

        // прием сообщения
        private async void WaitClientQuery()
        {
            await Task.Run(() =>
            {
                try
                {
                    // установим для сокета адрес локальной конечной точки
                    IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Any,
                        49152 /* порт */);

                    // создаем дейтаграммный сокет
                    receiveSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    IPAddress ip = IPAddress.Parse("235.0.0.0");
                    // Регистрируем multicast-адрес 235.0.0.0
                    receiveSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));
                    receiveSocket.Bind(ipEndPoint);
                    
                    while (true)
                    {
                        try
                        {
                            EndPoint remote = new IPEndPoint(0x7F000000, 100);
                            byte[] arr = new byte[1024];
                            int len = receiveSocket.ReceiveFrom(arr, ref remote);
                            string clientIP = ((IPEndPoint)remote).Address.ToString();
                            
                            Message m = null;
                            try
                            {
                                MemoryStream stream = new MemoryStream(arr, 0, len);
                                XmlSerializer serializer = new XmlSerializer(typeof(Message));
                                m = serializer.Deserialize(stream) as Message;
                                stream.Close();
                            }
                            catch (Exception deserializeEx)
                            {
                                ShowError($"Deserialize error: {deserializeEx.Message}");
                                continue; // Пропускаем некорректное сообщение
                            }
                            
                            if (m == null)
                            {
                                continue; // Пропускаем если десериализация не удалась
                            }
                            
                            // Определяем отображаемое имя пользователя
                            string displayName = m.user;
                            lock (userNicknames)
                            {
                                if (userNicknames.ContainsKey(clientIP))
                                {
                                    displayName = userNicknames[clientIP];
                                }
                                else
                                {
                                    userNicknames[clientIP] = m.user;
                                    displayName = m.user;
                                }
                            }
                            
                            // Проверяем, не было ли это сообщение уже отправлено нами (для предотвращения дублирования)
                            if (m.messageType == "chat" && clientIP == localIP)
                            {
                                string messageHash = $"{m.user}:{m.mes}";
                                lock (sentMessages)
                                {
                                    if (sentMessages.ContainsKey(messageHash))
                                    {
                                        // Проверяем, не прошло ли слишком много времени
                                        TimeSpan elapsed = DateTime.Now - sentMessages[messageHash];
                                        if (elapsed.TotalSeconds < MESSAGE_DEDUP_SECONDS)
                                        {
                                            // Это наше недавно отправленное сообщение, пропускаем его
                                            continue;
                                        }
                                        else
                                        {
                                            // Удаляем старую запись
                                            sentMessages.Remove(messageHash);
                                        }
                                    }
                                }
                            }
                            
                            // Обработка различных типов сообщений
                                if (m.messageType == "nickname_change")
                            {
                                string oldDisplayName = displayName;
                                lock (userNicknames)
                                {
                                    userNicknames[clientIP] = m.user; // новый nickname
                                    displayName = m.user;
                                }
                                
                                string notificationText = $"[{DateTime.Now:HH:mm:ss}] *** {oldDisplayName} changed nickname to {m.user} ***";
                                uiContext.Send(d => 
                                {
                                    listBoxMessages.Items.Add(notificationText);
                                    listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                }, null);
                                
                                // Обновляем список пользователей
                                lock (users)
                                {
                                    // Удаляем старую запись по IP
                                    var oldKey = users.Keys.FirstOrDefault(k => k.Contains(clientIP));
                                    if (oldKey != null)
                                    {
                                        users.Remove(oldKey);
                                    }
                                    // Добавляем новую запись с новым nickname
                                    string userKey = m.user + " (" + clientIP + ")";
                                    users[userKey] = DateTime.Now;
                                }
                                
                                UpdateUsersList();
                            }
                                else if (m.messageType == "join")
                            {
                                // Уведомление о подключении (пропускаем собственные уведомления)
                                if (clientIP == localIP)
                                {
                                    continue;
                                }
                                
                                lock (userNicknames)
                                {
                                    userNicknames[clientIP] = m.user;
                                }
                                
                                string notificationText = $"[{DateTime.Now:HH:mm:ss}] *** {m.user} joined the chat ***";
                                uiContext.Send(d => 
                                {
                                    listBoxMessages.Items.Add(notificationText);
                                    listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                }, null);
                                
                                // Добавляем пользователя в список
                                string userKey = m.user + " (" + clientIP + ")";
                                lock (users)
                                {
                                    users[userKey] = DateTime.Now;
                                }
                                
                                UpdateUsersList();
                            }
                            else if (m.messageType == "leave")
                            {
                                // Уведомление об отключении (пропускаем собственные уведомления)
                                if (clientIP == localIP)
                                {
                                    continue;
                                }
                                
                                string notificationText = $"[{DateTime.Now:HH:mm:ss}] *** {displayName} left the chat ***";
                                uiContext.Send(d => 
                                {
                                    listBoxMessages.Items.Add(notificationText);
                                    listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                }, null);
                                
                                // Удаляем пользователя из списка
                                lock (users)
                                {
                                    var userKey = users.Keys.FirstOrDefault(k => k.Contains(clientIP));
                                    if (userKey != null)
                                    {
                                        users.Remove(userKey);
                                    }
                                }
                                
                                lock (userNicknames)
                                {
                                    userNicknames.Remove(clientIP);
                                }
                                
                                UpdateUsersList();
                            }
                            else if (m.messageType == "heartbeat")
                            {
                                // Heartbeat сообщение - обновляем информацию о пользователе без уведомления
                                if (clientIP == localIP)
                                {
                                    continue; // Пропускаем собственные heartbeat
                                }
                                
                                lock (userNicknames)
                                {
                                    userNicknames[clientIP] = m.user;
                                }
                                
                                // Обновляем время последней активности пользователя
                                string userKey = m.user + " (" + clientIP + ")";
                                lock (users)
                                {
                                    users[userKey] = DateTime.Now;
                                }
                                
                                UpdateUsersList();
                            }
                            else
                            {
                                // Обычное сообщение
                                // Обновляем список пользователей
                                string userKey = displayName + " (" + clientIP + ")";
                                lock (users)
                                {
                                    users[userKey] = DateTime.Now;
                                }
                                
                                // Обновляем список пользователей в UI
                                UpdateUsersList();
                                
                                // Добавляем сообщение в чат
                                string messageText = $"[{DateTime.Now:HH:mm:ss}] {displayName}: {m.mes}";
                                uiContext.Send(d => 
                                {
                                    listBoxMessages.Items.Add(messageText);
                                    listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                }, null);
                            }
                        }
                        catch (SocketException ex)
                        {
                            // Ошибка сети - продолжаем работу
                            ShowError($"Network receive error: {ex.Message}");
                            Thread.Sleep(1000); // Небольшая задержка перед следующей попыткой
                        }
                        catch (Exception ex)
                        {
                            // Другие ошибки при обработке сообщения
                            ShowError($"Message processing error: {ex.Message}");
                        }
                    }
                }
                catch (SocketException ex)
                {
                    ShowError($"Network error: {ex.Message}");
                    // Пытаемся переподключиться через 2 секунды
                    Task.Delay(2000).ContinueWith(t => WaitClientQuery());
                }
                catch (Exception ex)
                {
                    ShowError($"Receive error: {ex.Message}");
                    // Пытаемся переподключиться через 2 секунды
                    Task.Delay(2000).ContinueWith(t => WaitClientQuery());
                }
            });
        }
        
        // Обновление списка пользователей
        private void UpdateUsersList()
        {
            try
            {
                if (uiContext != null)
                {
                    uiContext.Send(d =>
                    {
                        try
                        {
                            listBoxUsers.Items.Clear();
                            lock (users)
                            {
                                foreach (var user in users.Keys.OrderBy(x => x))
                                {
                                    // Пропускаем собственные записи (определяем по IP)
                                    if (!user.Contains(localIP))
                                    {
                                        listBoxUsers.Items.Add(user);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowError($"Update users list error: {ex.Message}");
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                ShowError($"UpdateUsersList error: {ex.Message}");
            }
        }
        
        // Проверка таймаута пользователей
        private async void CheckUsersTimeout()
        {
            await Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        try
                        {
                            await Task.Delay(5000); // Проверяем каждые 5 секунд
                            lock (users)
                            {
                                var expiredUsers = users.Where(u => (DateTime.Now - u.Value).TotalSeconds > USER_TIMEOUT_SECONDS).ToList();
                                foreach (var user in expiredUsers)
                                {
                                    users.Remove(user.Key);
                                }
                                if (expiredUsers.Count > 0)
                                {
                                    UpdateUsersList();
                                }
                            }
                            
                            // Очищаем старые записи отправленных сообщений
                            lock (sentMessages)
                            {
                                var expiredMessages = sentMessages.Where(m => 
                                    (DateTime.Now - m.Value).TotalSeconds > MESSAGE_DEDUP_SECONDS * 2
                                ).ToList();
                                foreach (var msg in expiredMessages)
                                {
                                    sentMessages.Remove(msg.Key);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowError($"CheckUsersTimeout loop error: {ex.Message}");
                            await Task.Delay(5000); // Продолжаем работу после ошибки
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"CheckUsersTimeout error: {ex.Message}");
                }
            });
        }

        // Вспомогательный метод для отправки multicast сообщения
        private void SendMulticastMessage(Message message)
        {
            try
            {
                IPAddress multicastIP = IPAddress.Parse("235.0.0.0");
                IPEndPoint ipEndPoint = new IPEndPoint(multicastIP, 49152);

                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                // TTL = 1 означает только локальную сеть, 2 - локальная сеть + один роутер
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                
                // Для Windows: привязываем сокет к локальному интерфейсу перед отправкой
                if (!string.IsNullOrEmpty(localIP) && localIP != "127.0.0.1")
                {
                    try
                    {
                        IPAddress localIPAddr = IPAddress.Parse(localIP);
                        socket.Bind(new IPEndPoint(localIPAddr, 0)); // Порт 0 = любой доступный порт
                    }
                    catch
                    {
                        // Если не удалось привязать к конкретному интерфейсу, используем IPAddress.Any
                        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    }
                }
                else
                {
                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                }

                MemoryStream stream = new MemoryStream();
                XmlSerializer serializer = new XmlSerializer(typeof(Message));
                serializer.Serialize(stream, message);
                byte[] arr = stream.ToArray();
                stream.Close();
                socket.SendTo(arr, ipEndPoint);
                socket.Shutdown(SocketShutdown.Send);
                socket.Close();
            }
            catch (SocketException ex)
            {
                ShowError($"Network send error: {ex.Message}");
            }
            catch (Exception ex)
            {
                ShowError($"Send error: {ex.Message}");
            }
        }
        
        // отправление сообщения
        private async void button1_Click(object sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(textBoxMessage.Text))
                {
                    return;
                }
                
                string messageText = textBoxMessage.Text;
                textBoxMessage.Clear();
                
                // Создаем хеш сообщения для предотвращения дублирования
                string messageHash = $"{currentNickname}:{messageText}";
                lock (sentMessages)
                {
                    sentMessages[messageHash] = DateTime.Now;
                }
                
                await Task.Run(() =>
                {
                    try
                    {
                        Message m = new Message();
                        m.mes = messageText;
                        m.user = currentNickname;
                        m.messageType = "chat";
                        SendMulticastMessage(m);
                        ShowInfo("Message sent");
                    }
                    catch (Exception ex)
                    {
                        ShowError($"Send message error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                ShowError($"Button click error: {ex.Message}");
            }
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                // Отправляем уведомление об отключении
                SendLeaveNotification();
            }
            catch (Exception ex)
            {
                ShowError($"Leave notification error: {ex.Message}");
            }
            finally
            {
                try
                {
                    receiveSocket?.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Socket close error: {ex.Message}");
                }
                base.OnFormClosing(e);
            }
        }
        
        // Отправка сообщения по Enter
        private void textBoxMessage_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    button1_Click(sender, e);
                }
            }
            catch (Exception ex)
            {
                ShowError($"KeyDown error: {ex.Message}");
            }
        }
        
        // Отправка уведомления о подключении
        private async void SendJoinNotification()
        {
            try
            {
                await Task.Run(async () =>
                {
                    try
                    {
                        // Отправляем уведомление о подключении
                        Message joinMsg = new Message();
                        joinMsg.user = currentNickname;
                        joinMsg.messageType = "join";
                        joinMsg.mes = "";
                        SendMulticastMessage(joinMsg);
                        ShowInfo("Connected to chat");
                        
                        // Отправляем несколько heartbeat сообщений подряд для быстрого обнаружения
                        for (int i = 0; i < 3; i++)
                        {
                            await Task.Delay(300); // Небольшая задержка между сообщениями
                            Message heartbeatMsg = new Message();
                            heartbeatMsg.user = currentNickname;
                            heartbeatMsg.messageType = "heartbeat";
                            heartbeatMsg.mes = "";
                            SendMulticastMessage(heartbeatMsg);
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowError($"Join notification error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                ShowError($"SendJoinNotification error: {ex.Message}");
            }
        }
        
        // Отправка уведомления об отключении
        private void SendLeaveNotification()
        {
            try
            {
                Message leaveMsg = new Message();
                leaveMsg.user = currentNickname;
                leaveMsg.messageType = "leave";
                leaveMsg.mes = "";
                SendMulticastMessage(leaveMsg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SendLeaveNotification error: {ex.Message}");
            }
        }
        
        // Периодическая отправка heartbeat сообщений для обнаружения участников
        private async void StartHeartbeat()
        {
            await Task.Run(async () =>
            {
                try
                {
                    // Небольшая задержка перед первым heartbeat
                    await Task.Delay(2000);
                    
                    while (true)
                    {
                        try
                        {
                            Message heartbeatMsg = new Message();
                            heartbeatMsg.user = currentNickname;
                            heartbeatMsg.messageType = "heartbeat";
                            heartbeatMsg.mes = "";
                            SendMulticastMessage(heartbeatMsg);
                        }
                        catch (Exception ex)
                        {
                            ShowError($"Heartbeat send error: {ex.Message}");
                        }
                        
                        await Task.Delay(5000); // Отправляем heartbeat каждые 5 секунд
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"StartHeartbeat error: {ex.Message}");
                }
            });
        }
        
        // Обработчик смены nickname
        private async void buttonChangeNickname_Click(object sender, EventArgs e)
        {
            try
            {
                string newNickname = textBoxNickname.Text.Trim();
                
                if (string.IsNullOrWhiteSpace(newNickname))
                {
                    ShowError("Nickname cannot be empty!");
                    textBoxNickname.Text = currentNickname;
                    return;
                }
                
                if (newNickname == currentNickname)
                {
                    return; // Ничего не изменилось
                }
                
                string oldNickname = currentNickname;
                currentNickname = newNickname;
                textBoxNickname.Text = currentNickname;
                
                // Обновляем свой nickname в словаре
                lock (userNicknames)
                {
                    userNicknames[localIP] = newNickname;
                }
                
                // Отправляем уведомление о смене nickname
                await Task.Run(() =>
                {
                    try
                    {
                        Message m = new Message();
                        m.user = newNickname;
                        m.oldNickname = oldNickname;
                        m.messageType = "nickname_change";
                        m.mes = "";
                        SendMulticastMessage(m);
                        ShowInfo($"Nickname changed to: {newNickname}");
                    }
                    catch (Exception ex)
                    {
                        ShowError($"Error changing nickname: {ex.Message}");
                        // Откатываем изменения при ошибке
                        currentNickname = oldNickname;
                        if (uiContext != null)
                        {
                            uiContext.Send(d => textBoxNickname.Text = currentNickname, null);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                ShowError($"ButtonChangeNickname error: {ex.Message}");
            }
        }
    }
}
