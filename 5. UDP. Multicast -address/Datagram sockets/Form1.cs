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
            public Message()
            {

            }
        }
        
        public SynchronizationContext uiContext;
        private Socket receiveSocket;
        private string currentUserName;
        private Dictionary<string, DateTime> users = new Dictionary<string, DateTime>();
        private const int USER_TIMEOUT_SECONDS = 30;

        public Form1()
        {
            InitializeComponent();
            // Получим контекст синхронизации для текущего потока 
            uiContext = SynchronizationContext.Current;
            currentUserName = Environment.UserDomainName + @"\" + Environment.UserName;
            labelUserName.Text = "You: " + currentUserName;
            WaitClientQuery();
            CheckUsersTimeout();
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
                        
                        // Обновляем список пользователей
                        string userKey = m.user + " (" + clientIP + ")";
                        lock (users)
                        {
                            users[userKey] = DateTime.Now;
                        }
                        
                        // Обновляем список пользователей в UI
                        UpdateUsersList();
                        
                        // Добавляем сообщение в чат (не показываем собственные сообщения, они уже добавлены)
                        if (m.user != currentUserName)
                        {
                            string messageText = $"[{DateTime.Now:HH:mm:ss}] {m.user}: {m.mes}";
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
                        if (!user.Contains(currentUserName))
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
            
            // Добавляем собственное сообщение в чат сразу
            string ownMessage = $"[{DateTime.Now:HH:mm:ss}] You: {messageText}";
            listBoxMessages.Items.Add(ownMessage);
            listBoxMessages.TopIndex = listBoxMessages.Items.Count - 1;
            
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
                    m.user = currentUserName;
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
    }
}
