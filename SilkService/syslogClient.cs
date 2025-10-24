using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SilkService;

public class SysLogClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly UdpClient _udpClient;

    public SysLogClient(string host, int port = 514)
    {
        _host = host;
        _port = port;
        _udpClient = new UdpClient();
    }

    public void Send(string message, string appName = "SilkETWService", int facility = 1, int severity = 6)
    {
        try
        {
            // Syslog PRI = (facility * 8) + severity
            int pri = (facility * 8) + severity;
            string timestamp = DateTime.UtcNow.ToString("O");
            string hostname = Dns.GetHostName();

            string syslogMessage = $"<{pri}>{timestamp} {hostname} info: client {appName} query: {message}";
            SilkUtility.WriteToServiceTextLog($"[debug] Sending to syslog: {syslogMessage}");

            byte[] bytes = Encoding.UTF8.GetBytes(syslogMessage);

            _udpClient.Send(bytes, bytes.Length, _host, _port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Syslog send failed: {ex.Message}");
            SilkUtility.WriteToServiceTextLog($"Syslog send failed: {ex.Message}");
        }
    }

    public void Close() => _udpClient?.Close();
}