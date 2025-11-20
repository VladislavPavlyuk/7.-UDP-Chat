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
        private const int MESSAGE_DEDUP_SECONDS = 10; // окно для предотвращения дублирования (увеличено для надежности)
        private DateTime appStartTime; // время запуска приложения для таймера
        private CancellationTokenSource cancellationTokenSource; // для остановки бесконечных циклов
        private bool isReceiving = false; // флаг для предотвращения множественных вызовов WaitClientQuery
        
        // Методы для вывода сообщений
        private void ShowError(string message)
        {
            try
            {
                if (uiContext != null && cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                {
                    string timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}";
                    uiContext.Post(d =>
                    {
                        try
                        {
                            if (cancellationTokenSource == null || cancellationTokenSource.Token.IsCancellationRequested)
                                return;
                                
                            labelStatus.ForeColor = System.Drawing.Color.Red;
                            labelStatus.Text = timestampedMessage;
                            
                            // Автоматически скрываем сообщение через 5 секунд
                            Task.Delay(5000, cancellationTokenSource.Token).ContinueWith(t =>
                            {
                                if (!t.IsCanceled && uiContext != null && cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                                {
                                    uiContext.Post(d2 =>
                                    {
                                        if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested && 
                                            labelStatus.Text == timestampedMessage)
                                        {
                                            labelStatus.Text = "";
                                        }
                                    }, null);
                                }
                            }, TaskScheduler.Default);
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
                if (uiContext != null && cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                {
                    string timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
                    uiContext.Post(d =>
                    {
                        try
                        {
                            if (cancellationTokenSource == null || cancellationTokenSource.Token.IsCancellationRequested)
                                return;
                                
                            labelStatus.ForeColor = System.Drawing.Color.Green;
                            labelStatus.Text = timestampedMessage;
                            
                            // Автоматически скрываем сообщение через 3 секунды
                            Task.Delay(3000, cancellationTokenSource.Token).ContinueWith(t =>
                            {
                                if (!t.IsCanceled && uiContext != null && cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                                {
                                    uiContext.Post(d2 =>
                                    {
                                        if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested && 
                                            labelStatus.Text == timestampedMessage)
                                        {
                                            labelStatus.Text = "";
                                        }
                                    }, null);
                                }
                            }, TaskScheduler.Default);
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
            
            // Инициализируем CancellationTokenSource для управления задачами
            cancellationTokenSource = new CancellationTokenSource();
            
            // Инициализируем и запускаем таймер
            appStartTime = DateTime.Now;
            labelTimer.Text = "00:00:00";
            timer1.Start();
            
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
        
        // Обработчик события таймера
        private void timer1_Tick(object sender, EventArgs e)
        {
            try
            {
                TimeSpan elapsed = DateTime.Now - appStartTime;
                labelTimer.Text = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
            }
            catch
            {
                // Игнорируем ошибки обновления таймера
            }
        }
        // Multicast — это такая технология сетевой адресации, при которой сообщение доставляются сразу группе получателей

        // прием сообщения
        private async void WaitClientQuery()
        {
            // Предотвращаем множественные вызовы
            if (isReceiving)
            {
                return;
            }
            
            isReceiving = true;
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
                    
                    // Устанавливаем таймаут для ReceiveFrom, чтобы можно было периодически проверять CancellationToken
                    receiveSocket.ReceiveTimeout = 1000; // 1 секунда
                    
                    while (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // Проверяем отмену перед блокирующей операцией
                            cancellationTokenSource.Token.ThrowIfCancellationRequested();
                            
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
                            // Для собственных сообщений: используем словарь sentMessages для отслеживания отправленных сообщений
                            // Сообщение удаляется из словаря ТОЛЬКО после успешного добавления в UI
                            // Это гарантирует, что каждое сообщение будет показано хотя бы один раз
                            bool isOwnMessage = false;
                            string messageHash = "";
                            
                            if (m.messageType == "chat" && clientIP == localIP)
                            {
                                isOwnMessage = true;
                                messageHash = $"{m.user}:{m.mes}";
                                
                                // Проверяем, есть ли это сообщение в словаре отправленных
                                lock (sentMessages)
                                {
                                    if (sentMessages.ContainsKey(messageHash))
                                    {
                                        // Сообщение найдено в словаре - это наше отправленное сообщение, вернувшееся через multicast
                                        // Проверяем время отправки для предотвращения локального echo (очень редкий случай)
                                        TimeSpan elapsed = DateTime.Now - sentMessages[messageHash];
                                        
                                        // Если прошло меньше 0.1 мс - это возможно локальный echo, пропускаем
                                        // Минимальный порог для максимальной надежности отображения
                                        if (elapsed.TotalMilliseconds < 0.1)
                                        {
                                            // Очень свежее сообщение - пропускаем для предотвращения локального echo
                                            continue;
                                        }
                                        
                                        // Прошло достаточно времени - это сообщение вернулось через multicast
                                        // НЕ удаляем из словаря здесь - удалим только после успешного добавления в UI
                                        // Это гарантирует, что сообщение будет показано даже при повторном получении
                                    }
                                    // Если сообщения нет в словаре - значит оно уже было обработано ранее (удалено после показа)
                                    // или это очень старое сообщение. В любом случае ВСЕГДА показываем его, 
                                    // так как оно пришло через multicast и может быть важным
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
                                uiContext.Post(d => 
                                {
                                    if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                                    {
                                        listBoxMessages.Items.Add(notificationText);
                                        listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                    }
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
                            uiContext.Post(d => 
                            {
                                if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                                {
                                    listBoxMessages.Items.Add(notificationText);
                                    listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                }
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
                            uiContext.Post(d => 
                            {
                                if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                                {
                                    listBoxMessages.Items.Add(notificationText);
                                    listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                }
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
                                // Захватываем значения переменных локально для использования в замыкании
                                bool ownMsg = isOwnMessage;
                                string msgHash = messageHash;
                                uiContext.Post(d => 
                                {
                                    if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                                    {
                                        listBoxMessages.Items.Add(messageText);
                                        listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
                                        
                                        // Удаляем собственное сообщение из словаря только после успешного добавления в UI
                                        // Это гарантирует, что сообщение будет показано хотя бы один раз
                                        if (ownMsg && !string.IsNullOrEmpty(msgHash))
                                        {
                                            lock (sentMessages)
                                            {
                                                if (sentMessages.ContainsKey(msgHash))
                                                {
                                                    sentMessages.Remove(msgHash);
                                                }
                                            }
                                        }
                                    }
                                }, null);
                            }
                        }
                        catch (SocketException ex)
                        {
                            // Проверяем отмену
                            if (cancellationTokenSource == null || cancellationTokenSource.Token.IsCancellationRequested)
                            {
                                break;
                            }
                            
                            // SocketException с кодом 10060 (WSAETIMEDOUT) - это нормально, просто продолжаем
                            if (ex.SocketErrorCode != SocketError.TimedOut)
                            {
                                ShowError($"Network receive error: {ex.Message}");
                            }
                            
                            // Небольшая задержка перед следующей попыткой, но проверяем отмену
                            if (cancellationTokenSource == null || cancellationTokenSource.Token.WaitHandle.WaitOne(1000))
                            {
                                break; // Отмена запрошена или CancellationTokenSource disposed
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Запрос на отмену - выходим из цикла
                            break;
                        }
                        catch (Exception ex)
                        {
                            // Другие ошибки при обработке сообщения
                            ShowError($"Message processing error: {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Нормальная остановка
                }
                catch (SocketException ex)
                {
                    ShowError($"Network error: {ex.Message}");
                    // Пытаемся переподключиться через 2 секунды, но только если не запрошена отмена
                    if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        isReceiving = false; // Сбрасываем флаг перед повторным вызовом
                        var cts = cancellationTokenSource; // Сохраняем ссылку
                        Task.Delay(2000).ContinueWith(t => 
                        {
                            if (cts != null && !cts.Token.IsCancellationRequested)
                            {
                                WaitClientQuery();
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    ShowError($"Receive error: {ex.Message}");
                    // Пытаемся переподключиться через 2 секунды, но только если не запрошена отмена
                    if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        isReceiving = false; // Сбрасываем флаг перед повторным вызовом
                        var cts = cancellationTokenSource; // Сохраняем ссылку
                        Task.Delay(2000).ContinueWith(t => 
                        {
                            if (cts != null && !cts.Token.IsCancellationRequested)
                            {
                                WaitClientQuery();
                            }
                        });
                    }
                }
                finally
                {
                    isReceiving = false;
                }
            });
        }
        
        // Обновление списка пользователей
        private void UpdateUsersList()
        {
            try
            {
                if (uiContext != null && cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Собираем данные вне блокировки UI потока
                    List<string> usersList;
                    lock (users)
                    {
                        usersList = users.Keys
                            .Where(u => !u.Contains(localIP))
                            .OrderBy(x => x)
                            .ToList();
                    }
                    
                    // Обновляем UI только если есть изменения
                    uiContext.Post(d =>
                    {
                        try
                        {
                            if (cancellationTokenSource == null || cancellationTokenSource.Token.IsCancellationRequested)
                                return;
                                
                            listBoxUsers.Items.Clear();
                            foreach (var user in usersList)
                            {
                                listBoxUsers.Items.Add(user);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Игнорируем ошибки обновления UI
                            System.Diagnostics.Debug.WriteLine($"Update users list UI error: {ex.Message}");
                        }
                    }, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateUsersList error: {ex.Message}");
            }
        }
        
        // Проверка таймаута пользователей
        private async void CheckUsersTimeout()
        {
            await Task.Run(async () =>
            {
                try
                {
                    while (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(5000, cancellationTokenSource.Token); // Проверяем каждые 5 секунд
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
                            
                            // Очищаем старые записи отправленных сообщений (только те, которые старше 120 секунд)
                            // Увеличено до максимума для гарантии, что все сообщения успеют вернуться через multicast
                            // даже при серьезных задержках сети или проблемах с сетью
                            lock (sentMessages)
                            {
                                var expiredMessages = sentMessages.Where(m => 
                                    (DateTime.Now - m.Value).TotalSeconds > 120
                                ).ToList();
                                foreach (var msg in expiredMessages)
                                {
                                    sentMessages.Remove(msg.Key);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Нормальная остановка
                            break;
                        }
                        catch (Exception ex)
                        {
                            ShowError($"CheckUsersTimeout loop error: {ex.Message}");
                            try
                            {
                                await Task.Delay(5000, cancellationTokenSource.Token); // Продолжаем работу после ошибки
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Нормальная остановка
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
                
                // Показываем временное сообщение о отправке
                ShowInfo($"Sending message...");
                
                // Создаем хеш сообщения для предотвращения дублирования
                // Используем уникальный идентификатор: nickname + текст + timestamp для большей надежности
                string messageHash = $"{currentNickname}:{messageText}";
                DateTime sendTime = DateTime.Now;
                lock (sentMessages)
                {
                    // Добавляем сообщение в словарь с текущим временем
                    // Сообщение будет удалено только после успешного добавления в UI
                    sentMessages[messageHash] = sendTime;
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
                // Останавливаем таймер
                timer1.Stop();
            }
            catch
            {
                // Игнорируем ошибки остановки таймера
            }
            
            try
            {
                // Запрашиваем отмену всех задач
                if (cancellationTokenSource != null)
                {
                    cancellationTokenSource.Cancel();
                    
                    // Даем время задачам завершиться
                    Thread.Sleep(100);
                }
            }
            catch
            {
                // Игнорируем ошибки отмены
            }
            
            try
            {
                // Отправляем уведомление об отключении
                SendLeaveNotification();
            }
            catch (Exception ex)
            {
                // Не используем ShowError здесь, так как CancellationTokenSource может быть уже disposed
                System.Diagnostics.Debug.WriteLine($"Leave notification error: {ex.Message}");
            }
            finally
            {
                try
                {
                    // Закрываем сокет
                    if (receiveSocket != null)
                    {
                        try
                        {
                            receiveSocket.Close();
                        }
                        catch
                        {
                            // Игнорируем ошибки закрытия
                        }
                        receiveSocket = null;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Socket close error: {ex.Message}");
                }
                
                try
                {
                    // Освобождаем ресурсы CancellationTokenSource после закрытия сокета
                    if (cancellationTokenSource != null)
                    {
                        cancellationTokenSource.Dispose();
                        cancellationTokenSource = null;
                    }
                }
                catch
                {
                    // Игнорируем ошибки освобождения
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
                    if (cancellationTokenSource == null)
                        return;
                        
                    // Небольшая задержка перед первым heartbeat
                    await Task.Delay(2000, cancellationTokenSource.Token);
                    
                    while (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
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
                        
                        try
                        {
                            if (cancellationTokenSource == null)
                                break;
                            await Task.Delay(5000, cancellationTokenSource.Token); // Отправляем heartbeat каждые 5 секунд
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Нормальная остановка
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
                            uiContext.Post(d => 
                        {
                            if (cancellationTokenSource != null && !cancellationTokenSource.Token.IsCancellationRequested)
                            {
                                textBoxNickname.Text = currentNickname;
                            }
                        }, null);
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
