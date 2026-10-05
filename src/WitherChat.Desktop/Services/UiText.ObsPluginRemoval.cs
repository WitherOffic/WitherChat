namespace WitherChat.Desktop.Services;

public sealed partial class UiText
{
    public string ObsPluginRemove => Pick("Удалить плагин", "Remove plugin");
    public string ObsPluginRemoving => Pick("Удаление…", "Removing…");
    public string ObsPluginRemoved => Pick("Плагин удалён. OBS, другие плагины, профили, сцены и аккаунты чата не изменены.", "Plugin removed. OBS, other plugins, profiles, scenes, and chat accounts were not changed.");
    public string ObsPluginRemovalConfirm => Pick("Удалить только плагин WitherChat? Удалятся его DLL, встроенный EXE, лицензии и служебные копии. Неизвестные файлы сохранятся. Это действие не отменяется кнопкой «Отмена» настроек.", "Remove only the WitherChat plugin? Its DLL, bundled EXE, licenses, and service copies will be deleted. Unknown files are preserved. Settings Cancel does not undo this action.");
    public string ObsPluginConfirmRemove => Pick("Да, удалить", "Yes, remove");
    public string ObsPluginCancelRemove => Pick("Не удалять", "Keep plugin");
    public string ObsPluginRemovalCancelled => Pick("Удаление отменено.", "Removal cancelled.");
    public string ObsPluginRemovalFailed => Pick("Удаление не завершено; часть файлов могла остаться. Закройте OBS и повторите проверку. Другие плагины и профили не затрагиваются.", "Removal was not completed; some files may remain. Close OBS and check again. Other plugins and profiles are not touched.");
    public string ObsPluginRunningFromPlugin => Pick("Этот чат запущен из папки плагина. Закройте его и откройте отдельный EXE вне папки OBS, затем удалите плагин из настроек.", "This chat is running from the plugin folder. Close it and open a standalone EXE outside OBS, then remove the plugin from Settings.");
}
