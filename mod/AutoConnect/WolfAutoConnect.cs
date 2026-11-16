using System;
using System.IO;
using System.Xml;
using UnityEngine;

// Клиентский мод: при открытии главного меню подключается к серверу, адрес которого
// лаунчер записал в %APPDATA%\7DaysToDie\WolfAutoConnect.xml.
//
// Почему мод, а не параметр запуска: в 7 Days to Die нет ключа командной строки для
// автоподключения (проверено по сборке игры). Игра умеет только
// ConnectionManager.Instance.Connect(GameServerInfo) — его и вызываем.
//
// Мод ничего не делает, если файла конфигурации нет или в нём Enabled != true,
// поэтому его наличие в наборе не мешает обычной игре.
public class WolfAutoConnectMod : IModApi
{
    private static bool subscribed;
    private static bool done;

    public void InitMod(Mod _modInstance)
    {
        try
        {
            if (subscribed) { return; }
            subscribed = true;
            ModEvents.MainMenuOpened.RegisterHandler(OnMainMenuOpened);
            Debug.Log("[WolfAutoConnect] инициализирован, конфиг: " + ConfigPath());
        }
        catch (Exception e)
        {
            Debug.LogError("[WolfAutoConnect] ошибка инициализации: " + e);
        }
    }

    private static string ConfigPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(Path.Combine(appData, "7DaysToDie"), "WolfAutoConnect.xml");
    }

    private static void OnMainMenuOpened(ref ModEvents.SMainMenuOpenedData _data)
    {
        try
        {
            if (done) { return; }
            string ip;
            int port;
            if (!ReadConfig(out ip, out port)) { return; }

            done = true;
            GameServerInfo info = new GameServerInfo();
            info.SetValue(GameInfoString.IP, ip);
            info.SetValue(GameInfoInt.Port, port);
            Debug.Log("[WolfAutoConnect] подключаюсь к " + ip + ":" + port);
            ConnectionManager.Instance.Connect(info);
            // Гасим флаг: следующий прямой запуск игры (из Steam) уже не должен тянуть на сервер.
            // Лаунчер перед каждым «Играть» снова включает автовход.
            DisableConfig();
        }
        catch (Exception e)
        {
            Debug.LogError("[WolfAutoConnect] не удалось подключиться: " + e);
        }
    }

    private static bool ReadConfig(out string ip, out int port)
    {
        ip = null;
        port = 0;
        try
        {
            string file = ConfigPath();
            if (!File.Exists(file)) { return false; }

            XmlDocument doc = new XmlDocument();
            doc.Load(file);
            XmlNode root = doc.DocumentElement;
            if (root == null) { return false; }

            if (!string.Equals(Value(root, "Enabled"), "true", StringComparison.OrdinalIgnoreCase)) { return false; }

            ip = Value(root, "ServerIP");
            if (string.IsNullOrEmpty(ip)) { return false; }

            int parsed;
            if (int.TryParse(Value(root, "ServerPort"), out parsed) && parsed > 0) { port = parsed; }
            else { port = 26900; }
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[WolfAutoConnect] ошибка конфигурации: " + e.Message);
            return false;
        }
    }

    private static string Value(XmlNode root, string name)
    {
        XmlNode node = root.SelectSingleNode(name);
        return node == null ? null : node.InnerText.Trim();
    }

    // Переписываем конфиг с Enabled=false, сохраняя адрес (лаунчер всё равно пишет файл заново).
    private static void DisableConfig()
    {
        try
        {
            string file = ConfigPath();
            XmlDocument doc = new XmlDocument();
            doc.Load(file);
            XmlNode root = doc.DocumentElement;
            if (root == null) { return; }
            XmlNode enabled = root.SelectSingleNode("Enabled");
            if (enabled != null) { enabled.InnerText = "false"; }
            doc.Save(file);
        }
        catch (Exception e)
        {
            Debug.LogError("[WolfAutoConnect] не удалось обновить конфиг: " + e.Message);
        }
    }
}
