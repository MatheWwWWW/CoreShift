using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace SmartResourceDispatcher.Core
{
    public enum УровеньРиска
    {
        Безопасно,
        Умеренно
    }

    public record ИнформацияОТвике(
        string Ид,
        string Название,
        string ПростымиСловами,
        string ЗачемОтключать,
        string ДляПрофи,
        УровеньРиска Риск,
        bool Отключено
    );

    public class TweaksEngine
    {
        private const string TelemetryPath = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
        private const string GameDvrPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR";
        private const string PushNotificationsPath = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";

        public static List<ИнформацияОТвике> ПолучитьСписокТвиков()
        {
            return new List<ИнформацияОТвике>
            {
                new ИнформацияОТвике(
                    Ид: "telemetry",
                    Название: "Отправка отчётов и телеметрии Windows",
                    ПростымиСловами: "Система реже собирает и отправляет отчёты о работе компьютера в Microsoft.",
                    ЗачемОтключать: "Меньше фоновой нагрузки на интернет и процессор, когда система занимается своими делами.",
                    ДляПрофи: "Параметр AllowTelemetry = 0 в ветке политик DataCollection ограничивает сбор диагностических данных.",
                    Риск: УровеньРиска.Безопасно,
                    Отключено: IsTelemetryDisabled()
                ),
                new ИнформацияОТвике(
                    Ид: "game_dvr",
                    Название: "Фоновая запись экрана и игровые функции Windows",
                    ПростымиСловами: "Отключает скрытую фоновую запись и игровые подсказки Windows.",
                    ЗачемОтключать: "Освобождает ресурсы видеокарты и памяти в играх и тяжёлых программах.",
                    ДляПрофи: "Отключает захват экрана и оверлеи через параметр AppCaptureEnabled в ветке GameDVR.",
                    Риск: УровеньРиска.Безопасно,
                    Отключено: IsGameDvrDisabled()
                ),
                new ИнформацияОТвике(
                    Ид: "toast_notifications",
                    Название: "Всплывающие уведомления системы",
                    ПростымиСловами: "Убирает всплывающие окошки уведомлений в углу экрана.",
                    ЗачемОтключать: "Ничего не выскакивает и не отвлекает во время игр, фильмов или работы на весь экран.",
                    ДляПрофи: "Задаёт DisableNotificationCenter = 1 в политиках проводника (Explorer).",
                    Риск: УровеньРиска.Умеренно,
                    Отключено: AreNotificationsDisabled()
                )
            };
        }

        public static bool IsTelemetryDisabled()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TelemetryPath, false);
                object? val = key?.GetValue("AllowTelemetry");
                return val is int intVal && intVal == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void SetTelemetryDisabled(bool disable)
        {
            using var key = Registry.LocalMachine.CreateSubKey(TelemetryPath, true);
            key.SetValue("AllowTelemetry", disable ? 0 : 1, RegistryValueKind.DWord);
        }

        public static bool IsGameDvrDisabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(GameDvrPath, false);
                object? val = key?.GetValue("AppCaptureEnabled");
                return val is int intVal && intVal == 0;
            }
            catch
            {
                return false;
            }
        }

        public static void SetGameDvrDisabled(bool disable)
        {
            using var key = Registry.CurrentUser.CreateSubKey(GameDvrPath, true);
            key.SetValue("AppCaptureEnabled", disable ? 0 : 1, RegistryValueKind.DWord);
        }

        public static bool AreNotificationsDisabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PushNotificationsPath, false);
                object? val = key?.GetValue("DisableNotificationCenter");
                return val is int intVal && intVal == 1;
            }
            catch
            {
                return false;
            }
        }

        public static void SetNotificationsDisabled(bool disable)
        {
            using var key = Registry.CurrentUser.CreateSubKey(PushNotificationsPath, true);
            key.SetValue("DisableNotificationCenter", disable ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
