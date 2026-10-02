# Матрица паритета WitherChat 0.3.3 → 0.4.0A

Источник истины — локальный WPF-проект `src/WitherChat` версии 0.3.3.
`Перенесено` означает наличие эквивалента в Avalonia/Core. `Авто` означает,
что состояние покрыто smoke, integration или headless screenshot тестом.
Живые Twitch/OBS и платформенные проверки отмечены отдельно и не подменяются
автотестами.

| Элемент | WPF 0.3.3 | Назначение и состояния | Avalonia 0.4.0A | Перенос | Проверка | Платформенные отличия |
|---|---|---|---|---|---|---|
| Главное окно | `MainWindow.xaml` | normal, compact, min/max, dark/light | `Views/MainWindow.axaml(.cs)` | Да | Авто + Windows screenshot | Нативный Avalonia chrome |
| Верхняя панель аккаунта | `MainWindow.xaml`, `ChatViewModel.cs` | guest, OAuth, API/chat/live | `MainWindow.axaml`, `MainWindowViewModel.cs` | Да | Headless + Windows | Нет |
| Меню каналов | `MainWindow.xaml`, `ChannelSessionViewModel.cs` | до 3 каналов, add/remove/select/search | `MainWindow.axaml`, `ChannelSessionViewModel.cs` | Да | Headless + smoke | Нет |
| Список сообщений | `MainWindow.xaml`, `ChatMessageModel.cs` | обычные, reply, long, pinned, shared | `MainWindow.axaml`, `ChatMessageItemViewModel.cs` | Да | Headless + smoke | Avalonia virtualization |
| Удаление/ban/timeout в истории | `ChatMessageModel.cs` | opacity 0.48, strikethrough, status | `ChatMessageItemViewModel.cs` | Да | Headless + smoke | Нет |
| Поиск и фильтры | `MainWindow.xaml`, `ChatViewModel.cs` | text/user/role, empty | `MainWindow.axaml`, `MainWindowViewModel.cs` | Да | Smoke/manual UI | Нет |
| Прокрутка вниз | `MainWindow.xaml` | follow-latest/deferred | `MainWindow.axaml(.cs)` | Да | Headless/stress | Нет |
| Поле ввода | `MainWindow.xaml` | signed-out/read-only/send/reply | `MainWindow.axaml`, `MainWindowViewModel.cs` | Да | Smoke/manual UI | Нет |
| Контекстное меню | `MainWindow.xaml` | moderation/copy/open | `MainWindow.axaml` | Да | Headless/manual UI | Нативный popup Avalonia |
| Карточка пользователя | `MainWindow.xaml`, `ChatViewModel.cs` | avatar/profile/actions | `MainWindow.axaml`, `MainWindowViewModel.cs` | Да | Manual UI | Нет |
| Настройки | `MainWindow.xaml` | все секции, controls, dirty/cancel/save | `MainWindow.axaml`, `WitherChatSettings.cs` | Да | Headless + smoke | Системные font fallbacks |
| Chat Logs | `Views/ChatLogsPanel.xaml`, `ChatLogService.cs` | daily JSONL/TXT, viewer/export/delete | `MainWindow.axaml`, `ChatLogWriter.cs` | Да | Core smoke | Native folder picker |
| Moderation | `Views/ModerationPanel.xaml` | tabs/loading/error/empty | `MainWindow.axaml`, `MainWindowViewModel.cs` | Да | Headless + smoke | Live QA нужен |
| AutoMod | `ModerationService.cs` | held/allow/deny | `TwitchEventSubClient.cs`, `MainWindowViewModel.cs` | Да | Parser/smoke | Live QA нужен |
| Banned users | `ModerationService.cs` | ban/timeout/unban/cache | `TwitchChatApiClient.cs`, `ModerationCacheStore.cs` | Да | Core smoke | DPAPI/Unix file mode |
| Appeals | `ModerationService.cs` | approve/deny/cache | `TwitchChatApiClient.cs`, `ModerationCacheStore.cs` | Да | Core smoke | Live QA нужен |
| Ban/Timeout dialogs | `Views/ModerationDialog.xaml` | durations/reason/busy/error | `MainWindow.axaml`, `MainWindowViewModel.cs` | Да | Headless/manual UI | Нет |
| Channel Points | `ChatViewModel.cs` | reward title/cost/prompt | `ChatMessage.cs`, `MainWindow.axaml` | Да | Parser/smoke | Live QA нужен |
| Twitch emotes | `ChatViewModel.cs` | static/animated | `TwitchEmoteParser.cs`, `AnimatedEmoteImage.cs` | Да | Decoder/headless | Skia decoder |
| BTTV/7TV | `ThirdPartyEmoteTokenizer.cs` | animated, aliases, zero-width | `ThirdPartyEmoteCatalogService.cs`, `RichChatTextBlock.cs` | Да | Core + headless | Skia decoder |
| Twitch badges | `TwitchBadgeService.cs` | global/channel images | `ChatBadgeItemViewModel.cs` | Да | Smoke/manual network | Live catalog QA нужен |
| OBS overlay | `OverlayServerService.cs` | history, live settings, badges/emotes/theme/font | `ObsOverlayServer.cs` | Да | HTTP integration smoke | localhost Browser Source |
| Twitch OAuth | `AuthService.cs` | browser callback, restore, logout | `TwitchAuthService.cs`, secure stores | Да | Smoke; live QA нужен | DPAPI/Keychain/Secret Service |
| IRC/EventSub | `ReadOnlyChatClient.cs`, `TwitchEventSubClient.cs` | connect/reconnect/messages/mod events | `TwitchIrcClient.cs`, `TwitchEventSubClient.cs` | Да | Parser/smoke; live QA нужен | Нет |
| Темы и controls | `Themes/*`, `MainWindow.xaml` | dark/light, hover/pressed/focus/disabled | `Styles/WitherChatTheme.axaml` | Да | Headless screenshots | Рендер ОС отличается субпиксельно |
| Локализация | `LocalizationService.cs` | RU/EN and runtime switch | `UiText.cs` | Да | Desktop smoke | Нет |
| Шрифты | `Assets/Fonts/Inter-Variable.ttf` | Segoe UI Variable/Inter/fallbacks | embedded Inter + OS font mapping | Да | Build/headless | Inter fallback вне Windows |
| Системные уведомления/tray | `NotificationService.cs`, `App.xaml.cs` | toast/tray/close-to-tray | `Platforms/*`, `App.axaml.cs` | Да | Build; manual OS QA | Реализация зависит от ОС |
| Настройки и миграция | `SettingsService.cs` | settings/token/logs compatibility | `SettingsStore.cs`, `SecureTokenStoreFactory.cs` | Да | Core smoke | Защищённое хранилище ОС |
| Moderation cache | `ModerationCacheService.cs` | restore/stale/refresh/bounds | `ModerationCacheStore.cs` | Да | Core smoke | DPAPI на Windows, mode 600 Unix |

## Сводка

- Перенесено: 30 из 30 учтённых групп.
- Автоматически проверено хотя бы частично: 30 из 30.
- Требует живого Twitch/OBS теста: OAuth, IRC/EventSub, badges, Channel Points,
  AutoMod, bans/appeals и реальный OBS Browser Source.
- Требует ручного платформенного QA: macOS arm64/x64 и Linux x64/arm64.
- Windows-пакеты в текущем диалоге собираются отдельно по просьбе пользователя;
  исходники и publish targets остальных ОС сохранены.

Текущий статус: **READY FOR FUNCTIONAL QA**, но не `READY FOR RELEASE`.
