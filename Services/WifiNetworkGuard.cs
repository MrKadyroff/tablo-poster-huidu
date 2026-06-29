using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LedImageUpdaterService.Models;

namespace LedImageUpdaterService.Services;

public sealed class WifiNetworkGuard
{
    private readonly ILogger<WifiNetworkGuard> _logger;

    public WifiNetworkGuard(ILogger<WifiNetworkGuard> logger)
    {
        _logger = logger;
    }

    public void ValidateTarget(string targetIp, ServiceOptions options)
    {
        if (!IPAddress.TryParse(targetIp, out var targetAddress))
        {
            throw new InvalidOperationException(
                $"Неверный IP-адрес контроллера в config/screen.xml: '{targetIp}'. " +
                $"Ожидается формат 192.168.22.2");
        }

        if (options.RequirePrivateAddress && !IsPrivate(targetAddress))
        {
            throw new InvalidOperationException(
                $"IP-адрес контроллера {targetIp} не является адресом локальной сети " +
                $"(не из диапазонов 10.x.x.x / 172.16-31.x.x / 192.168.x.x). " +
                $"Проверьте config/screen.xml или отключите RequirePrivateAddress.");
        }

        if (!options.EnforceWifiOnly)
        {
            _logger.LogDebug("EnforceWifiOnly=false, пропуск проверки Wi-Fi интерфейса");
            return;
        }

        var localAddress = ResolveLocalAddressForTarget(targetAddress);
        if (localAddress is null)
        {
            var allNics = ListInterfacesSummary();
            throw new InvalidOperationException(
                $"Не удаётся определить маршрут до контроллера {targetIp}. " +
                $"Проверьте, что ПК подключён к Wi-Fi сети контроллера. " +
                $"Сетевые интерфейсы на этом ПК:\n{allNics}");
        }

        var nic = FindInterfaceByLocalAddress(localAddress);
        if (nic is null)
        {
            throw new InvalidOperationException(
                $"Маршрут до {targetIp} идёт через адрес {localAddress}, " +
                $"но такой адрес не найден ни на одном сетевом интерфейсе.");
        }

        _logger.LogDebug("Маршрут до {Target}: интерфейс '{Nic}' ({Type}), локальный IP {Local}",
            targetIp, nic.Name, nic.NetworkInterfaceType, localAddress);

        if (!IsWifiInterface(nic))
        {
            var allNics = ListInterfacesSummary();
            throw new InvalidOperationException(
                $"Маршрут до контроллера {targetIp} идёт через интерфейс '{nic.Name}' " +
                $"({nic.NetworkInterfaceType}), а не через Wi-Fi. " +
                $"Убедитесь что ПК подключён к Wi-Fi точке доступа контроллера ({targetIp}), " +
                $"а не через кабель или другую сеть. " +
                $"Если это намеренно — отключите EnforceWifiOnly в appsettings.json. " +
                $"Доступные интерфейсы:\n{allNics}");
        }

        _logger.LogInformation("Wi-Fi интерфейс OK: '{Nic}', локальный IP {Local} → контроллер {Target}",
            nic.Name, localAddress, targetIp);
    }

    private static IPAddress? ResolveLocalAddressForTarget(IPAddress target)
    {
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    private static NetworkInterface? FindInterfaceByLocalAddress(IPAddress localAddress)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(ni => ni.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localAddress)));
    }

    private static bool IsWifiInterface(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            return true;

        string fingerprint = ($"{ni.Name} {ni.Description}").ToLowerInvariant();
        return fingerprint.Contains("wifi")
               || fingerprint.Contains("wi-fi")
               || fingerprint.Contains("wlan")
               || fingerprint.Contains("airport");
    }

    private static string ListInterfacesSummary()
    {
        var lines = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n =>
            {
                var addrs = n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString());
                return $"  • {n.Name} ({n.NetworkInterfaceType}): {string.Join(", ", addrs)}";
            });
        return string.Join("\n", lines);
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
               || (bytes[0] == 192 && bytes[1] == 168);
    }
}
