using System;
using System.IO;
using System.Windows.Forms;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;


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

        public Form1()
        {
            InitializeComponent();
            // Получим контекст синхронизации для текущего потока 
            uiContext = SynchronizationContext.Current;
            WaitClientQuery();
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
                    IPEndPoint ipEndPoint = new IPEndPoint(IPAddress.Any /* Предоставляет IP-адрес, указывающий, что сервер должен контролировать действия клиентов на всех сетевых интерфейсах.*/,
                        49152 /* порт */);

                    // создаем дейтаграммный сокет
                    Socket socket = new Socket(AddressFamily.InterNetwork /*схема адресации*/, SocketType.Dgram /*тип сокета*/, ProtocolType.Udp /*протокол*/ );
                    /* Значение InterNetwork указывает на то, что при подключении объекта Socket к конечной точке предполагается использование IPv4-адреса.
                       Поддерживает датаграммы — ненадежные сообщения с фиксированной (обычно малой) максимальной длиной, передаваемые без установления подключения. 
                     * Возможны потеря и дублирование сообщений, а также их получение не в том порядке, в котором они отправлены. 
                     * Объект Socket типа Dgram не требует установки подключения до приема и передачи данных и может обеспечивать связь со множеством одноранговых узлов.
                     * Dgram использует протокол Datagram (Udp) и InterNetwork.
                     */
                    IPAddress ip = IPAddress.Parse("235.0.0.0");
                    // Регистрируем multicast-адрес 235.0.0.0
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));
                    socket.Bind(ipEndPoint); // Свяжем объект Socket с локальной конечной точкой.  
                    
                    // Как только отправитель посылает дейтаграмму на некоторый multicast-адрес,
                    // роутер немедленно перенаправляет её всем получателям, которые зарегистрировались
                    // на получение информации с этого multicast-адреса
                    while (true)
                    {
                        EndPoint remote = new IPEndPoint(0x7F000000, 100); // информация об удаленном хосте, который отправил датаграмму
                        byte[] arr = new byte[1024];
                        int len = socket.ReceiveFrom(arr, ref remote); // получим UDP-датаграмму
                        string clientIP = ((IPEndPoint)remote).Address.ToString(); // получим IP-адрес удаленного 
                        // Создадим поток, резервным хранилищем которого является память.
                        MemoryStream stream = new MemoryStream(arr, 0, len);
                        // XmlSerializer сериализует и десериализует объект в XML-формате 
                        XmlSerializer serializer = new XmlSerializer(typeof(Message));
                        Message m = serializer.Deserialize(stream) as Message; // выполняем десериализацию
                        // полученную от удаленного узла информацию добавляем в список
                        uiContext.Send(d => listBox1.Items.Add(clientIP), null);
                        uiContext.Send(d => listBox1.Items.Add(m.user), null);
                        uiContext.Send(d => listBox1.Items.Add(m.mes), null);
                        stream.Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Получатель: " + ex.Message);
                }
            });
        }

        // отправление сообщения
        private async void button1_Click(object sender, EventArgs e)
        {
            await Task.Run(() =>
            {
                try
                {
                    // Согласно стандарта RFC 3171, адреса в диапазоне от 224.0.0.0 до 239.255.255.255
                    // используются протоколом IPv4 как multicast-адреса
                    // Как только отправитель посылает дейтаграмму на некоторый multicast-адрес,
                    // роутер немедленно перенаправляет её всем получателям, которые зарегистрировались
                    // на получение информации с этого multicast-адреса
                    IPAddress ip = IPAddress.Parse("235.0.0.0");
                    IPEndPoint ipEndPoint = new IPEndPoint(ip, 49152);

                    // создаем дейтаграммный сокет
                    Socket socket = new Socket(AddressFamily.InterNetwork /*схема адресации*/, SocketType.Dgram /*тип сокета*/, ProtocolType.Udp /*протокол*/ );
                    /* Значение InterNetwork указывает на то, что при подключении объекта Socket к конечной точке предполагается использование IPv4-адреса.
                       Поддерживает датаграммы — ненадежные сообщения с фиксированной (обычно малой) максимальной длиной, передаваемые без установления подключения. 
                     * Возможны потеря и дублирование сообщений, а также их получение не в том порядке, в котором они отправлены. 
                     * Объект Socket типа Dgram не требует установки подключения до приема и передачи данных и может обеспечивать связь со множеством одноранговых узлов.
                     * Dgram использует протокол Datagram (Udp) и InterNetwork.
                     */

                    /* Необходимо установить опцию MulticastTimeTolive, которая влияет на время жизни пакета. 
                     * Если установить её в значение 1, то пакет не выйдет за пределы локальной сети. 
                     * Если же установить её в значение отличное от 1, то дейтаграмма будет проходить через несколько роутеров.
                    */
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    //  Регистрируем multicast-адрес 235.0.0.0
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(ip));

                    // Создадим поток, резервным хранилищем которого является память.
                    MemoryStream stream = new MemoryStream();
                    // XmlSerializer сериализует и десериализует объект в XML-формате 
                    XmlSerializer serializer = new XmlSerializer(typeof(Message));
                    Message m = new Message();
                    m.mes = textBox2.Text; // текст сообщения
                    m.user = Environment.UserDomainName + @"\" + Environment.UserName; // имя пользователя
                    serializer.Serialize(stream, m); // выполняем сериализацию
                    byte[] arr = stream.ToArray(); // записываем содержимое потока в байтовый массив
                    stream.Close();
                    socket.SendTo(arr, ipEndPoint); // передаем UDP-датаграмму на удаленный узел
                    socket.Shutdown(SocketShutdown.Send); // Отключаем объект Socket от передачи.
                    socket.Close(); // закрываем UDP-подключение и освобождаем все ресурсы
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Отправитель: " + ex.Message);
                }
            });
        }
    }
}
