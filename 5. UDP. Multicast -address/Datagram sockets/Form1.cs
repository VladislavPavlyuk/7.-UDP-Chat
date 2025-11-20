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
            public string messageType; // тип сообщения: "chat", "nickname_change", "join", "leave"
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
            
            // Определяем локальный IP адрес
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    localIP = endPoint.Address.ToString();
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
            
            WaitClientQuery();
            CheckUsersTimeout();
            
            // Отправляем уведомление о подключении
            SendJoinNotification();
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
                        EndPoint remote = new IPEndPoint(0x7F000000, 100);
                        byte[] arr = new byte[1024];
                        int len = receiveSocket.ReceiveFrom(arr, ref remote);
                        string clientIP = ((IPEndPoint)remote).Address.ToString();
                        
                        MemoryStream stream = new MemoryStream(arr, 0, len);
                        XmlSerializer serializer = new XmlSerializer(typeof(Message));
                        Message m = serializer.Deserialize(stream) as Message;
                        stream.Close();
                        
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
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Receive error: " + ex.Message);
                }
            });
        }
        
        // Обновление списка пользователей
        private void UpdateUsersList()
        {
            uiContext.Send(d =>
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
            }, null);
        }
        
        // Проверка таймаута пользователей
        private async void CheckUsersTimeout()
        {
            await Task.Run(async () =>
            {
                while (true)
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
            });
        }

        // отправление сообщения
        private async void button1_Click(object sender, EventArgs e)
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
                    IPAddress ip = IPAddress.Parse("235.0.0.0");
                    IPEndPoint ipEndPoint = new IPEndPoint(ip, 49152);

                    Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));

                    MemoryStream stream = new MemoryStream();
                    XmlSerializer serializer = new XmlSerializer(typeof(Message));
                    Message m = new Message();
                    m.mes = messageText;
                    m.user = currentNickname;
                    m.messageType = "chat";
                    serializer.Serialize(stream, m);
                    byte[] arr = stream.ToArray();
                    stream.Close();
                    socket.SendTo(arr, ipEndPoint);
                    socket.Shutdown(SocketShutdown.Send);
                    socket.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Send error: " + ex.Message);
                }
            });
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Отправляем уведомление об отключении
            SendLeaveNotification();
            receiveSocket?.Close();
            base.OnFormClosing(e);
        }
        
        // Отправка сообщения по Enter
        private void textBoxMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                button1_Click(sender, e);
            }
        }
        
        // Отправка уведомления о подключении
        private async void SendJoinNotification()
        {
            await Task.Run(() =>
            {
                try
                {
                    IPAddress ip = IPAddress.Parse("235.0.0.0");
                    IPEndPoint ipEndPoint = new IPEndPoint(ip, 49152);

                    Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));

                    MemoryStream stream = new MemoryStream();
                    XmlSerializer serializer = new XmlSerializer(typeof(Message));
                    Message m = new Message();
                    m.user = currentNickname;
                    m.messageType = "join";
                    m.mes = ""; // пустое сообщение для уведомления
                    serializer.Serialize(stream, m);
                    byte[] arr = stream.ToArray();
                    stream.Close();
                    socket.SendTo(arr, ipEndPoint);
                    socket.Shutdown(SocketShutdown.Send);
                    socket.Close();
                }
                catch (Exception ex)
                {
                    // Игнорируем ошибки при отправке уведомления о подключении
                }
            });
        }
        
        // Отправка уведомления об отключении
        private void SendLeaveNotification()
        {
            try
            {
                IPAddress ip = IPAddress.Parse("235.0.0.0");
                IPEndPoint ipEndPoint = new IPEndPoint(ip, 49152);

                Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));

                MemoryStream stream = new MemoryStream();
                XmlSerializer serializer = new XmlSerializer(typeof(Message));
                Message m = new Message();
                m.user = currentNickname;
                m.messageType = "leave";
                m.mes = ""; // пустое сообщение для уведомления
                serializer.Serialize(stream, m);
                byte[] arr = stream.ToArray();
                stream.Close();
                socket.SendTo(arr, ipEndPoint);
                socket.Shutdown(SocketShutdown.Send);
                socket.Close();
            }
            catch
            {
                // Игнорируем ошибки при отправке уведомления об отключении
            }
        }
        
        // Обработчик смены nickname
        private async void buttonChangeNickname_Click(object sender, EventArgs e)
        {
            string newNickname = textBoxNickname.Text.Trim();
            
            if (string.IsNullOrWhiteSpace(newNickname))
            {
                MessageBox.Show("Nickname cannot be empty!");
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
                    IPAddress ip = IPAddress.Parse("235.0.0.0");
                    IPEndPoint ipEndPoint = new IPEndPoint(ip, 49152);

                    Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));

                    MemoryStream stream = new MemoryStream();
                    XmlSerializer serializer = new XmlSerializer(typeof(Message));
                    Message m = new Message();
                    m.user = newNickname;
                    m.oldNickname = oldNickname;
                    m.messageType = "nickname_change";
                    m.mes = ""; // пустое сообщение для уведомления
                    serializer.Serialize(stream, m);
                    byte[] arr = stream.ToArray();
                    stream.Close();
                    socket.SendTo(arr, ipEndPoint);
                    socket.Shutdown(SocketShutdown.Send);
                    socket.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error changing nickname: " + ex.Message);
                    // Откатываем изменения при ошибке
                    currentNickname = oldNickname;
                    textBoxNickname.Text = currentNickname;
                }
            });
        }
    }
}
