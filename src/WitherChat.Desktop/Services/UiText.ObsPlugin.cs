namespace WitherChat.Desktop.Services;

public sealed partial class UiText
{
    public string ObsPluginTitle => Pick("Плагин OBS", "OBS plugin");
    public string ObsPluginInstall => Pick("Установить", "Install");
    public string ObsPluginUpdate => Pick("Обновить", "Update");
    public string ObsPluginCheck => Pick("Проверить", "Check");
    public string ObsPluginChooseFolder => Pick("Выбрать папку OBS", "Choose OBS folder");
    public string ObsPluginChecking => Pick("Проверка…", "Checking…");
    public string ObsPluginInstalling => Pick("Установка…", "Installing…");
    public string ObsPluginInstalled => Pick("Установлен", "Installed");
    public string ObsPluginMissing => Pick("Не установлен", "Not installed");
    public string ObsPluginUpdateAvailable => Pick("Есть обновление", "Update available");
    public string ObsPluginNotFound => Pick("OBS не найден. Выберите папку установки.", "OBS not found. Choose its installation folder.");
    public string ObsPluginSelectFolder => Pick("Найдено несколько OBS. Выберите нужную папку.", "Several OBS installations found. Choose a folder.");
    public string ObsPluginIncompatible => Pick("Папка или версия OBS не подходит.", "OBS folder or version is incompatible.");
    public string ObsPluginUnavailable => Pick("Установка доступна в готовом EXE для Windows x64.", "Installation is available in the Windows x64 single-file EXE.");
    public string ObsPluginHelp => Pick("Для OBS 32.2.2 x64 / Qt 6.11.1. Закройте OBS перед установкой или удалением. Профили и сцены сохраняются.", "For OBS 32.2.2 x64 / Qt 6.11.1. Close OBS before installing or removing. Profiles and scenes are preserved.");
    public string ObsPluginCloseObs => Pick("Закройте OBS и нажмите «Проверить».", "Close OBS and click Check.");
    public string ObsPluginSuccess => Pick("Готово. Откройте OBS → Сервис → WitherChat. Если ранее выбран другой EXE, выберите установленный WitherChat.exe в папке плагина.", "Done. Open OBS → Tools → WitherChat. If a different EXE was selected before, select the installed WitherChat.exe in the plugin folder.");
    public string ObsPluginCancelled => Pick("Установка отменена.", "Installation cancelled.");
    public string ObsPluginFailed => Pick("Установить не удалось. Старые файлы сохранены. Проверьте права доступа и закройте OBS.", "Installation failed. Previous files are preserved. Check permissions and close OBS.");
    public string ObsPluginRollbackFailed => Pick("Установка прервана, восстановить все файлы не удалось. Резервная копия: data/obs-plugins/witherchat-obs-backups в папке OBS.", "Installation interrupted; some files could not be restored. Backup: data/obs-plugins/witherchat-obs-backups in the OBS folder.");
    public string ObsPluginBusy => Pick("Другая установка уже выполняется.", "Another installation is in progress.");
}
