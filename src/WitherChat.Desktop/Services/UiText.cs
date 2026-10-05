using CommunityToolkit.Mvvm.ComponentModel;
using WitherChat.Core.Models;
using WitherChat.Desktop.Models;

namespace WitherChat.Desktop.Services;

public sealed partial class UiText : ObservableObject
{
    private bool _english;

    public void SetLanguage(string language)
    {
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        if (_english == english)
        {
            return;
        }

        _english = english;
        OnPropertyChanged(string.Empty);
    }

    private string Pick(string russian, string english) => _english ? english : russian;

    public string HideToTray => Pick("Скрыть в трей", "Hide to tray");
    public string Minimize => Pick("Свернуть", "Minimize");
    public string Maximize => Pick("Развернуть", "Maximize");
    public string RestoreWindow => Pick("Восстановить окно", "Restore window");
    public string ChannelPlaceholder => Pick("имя канала", "channel name");
    public string Channels => Pick("Каналы", "Channels");
    public string MyChannel => Pick("Мой канал", "My channel");
    public string AddChannel => Pick("Добавить канал", "Add channel");
    public string RemoveChannel => Pick("Удалить выбранный", "Remove selected");
    public string ChannelRemovalFailed => Pick(
        "Не удалось удалить канал. Попробуйте ещё раз.",
        "Could not remove channel. Please try again.");
    public string ChannelLimitReached => Pick(
        "Можно одновременно подключить не более трёх каналов.",
        "Up to three channels can be connected at once.");
    public string ChannelSearchNoResults => Pick("Каналы не найдены", "No channels found");
    public string ChannelSearchUnavailableWithoutAuth => Pick(
        "Поиск каналов недоступен без входа. Введите точный логин вручную.",
        "Channel search is unavailable while signed out. Enter the exact login manually.");
    public string ChannelSearchFailed => Pick(
        "Не удалось выполнить поиск. Введите точный логин вручную.",
        "Channel search failed. Enter the exact login manually.");
    public string CurrentChannel => Pick("Канал", "Channel");
    public string Guest => Pick("Гость", "Guest");
    public string TwitchNotConnected => Pick("Twitch не подключён", "Twitch not connected");
    public string Connect => Pick("Подключить", "Connect");
    public string Disconnect => Pick("Отключить", "Disconnect");
    public string ApiConnected => Pick("API подключён", "API connected");
    public string ApiDisconnected => Pick("API отключён", "API disconnected");
    public string ConnectTwitchAccount => Pick("Подключить аккаунт Twitch", "Connect Twitch account");
    public string ConnectTwitch => Pick("Подключить Twitch", "Connect Twitch");
    public string ChooseChannel => Pick("Выбрать канал", "Choose channel");
    public string ConnectTwitchIntro => Pick(
        "Для чтения чата Twitch теперь требуется вход. Можно заранее выбрать канал, а затем безопасно войти через официальный сайт Twitch.",
        "Twitch now requires sign-in to read chat. You can select a channel first, then sign in safely on the official Twitch website.");
    public string SignInWithTwitch => Pick("Войти через Twitch", "Sign in with Twitch");
    public string FullAccessDescription => Pick(
        "Полный доступ: писать сообщения, модерировать чат и управлять каналом.",
        "Full access: send messages, moderate chat, and manage the channel.");
    public string WatchChannelWithoutSignIn => Pick(
        "Выбрать канал для просмотра",
        "Select a channel to watch");
    public string WatchOnlyDescription => Pick(
        "Канал будет выбран для просмотра. Войти в Twitch можно после выбора канала.",
        "The channel will be selected for viewing. You can sign in to Twitch after selecting it.");
    public string TwitchChannelName => Pick("Ник канала Twitch", "Twitch channel name");
    public string TwitchChannelNameRequired => Pick(
        "Введите ник Twitch-канала.",
        "Enter a Twitch channel name.");
    public string ManualChannelTab => Pick("Подключить канал", "Connect channel");
    public string FollowedChannelsTab => Pick("Отслеживаемые каналы", "Followed channels");
    public string FollowedChannelsTitle => Pick(
        "Каналы, которые вы отслеживаете",
        "Channels you follow");
    public string FollowedChannelsDescription => Pick(
        "Список загружается из каналов, которые вы отслеживаете на Twitch. WitherChat ничего не подписывает и не отписывает — выбор канала только подключает его чат. Одновременно можно подключить до трёх каналов.",
        "This list comes from the channels you follow on Twitch. WitherChat never follows or unfollows channels — selecting one only connects its chat. Up to three channels can be connected at once.");
    public string FollowedChannelsSearchPlaceholder => Pick(
        "Найти среди отслеживаемых каналов",
        "Search followed channels");
    public string FollowedChannelsPermissionRequired => Pick(
        "Для показа списка нужно один раз разрешить WitherChat читать отслеживаемые каналы. Текущие настройки и подключённые каналы сохранятся.",
        "To show this list, allow WitherChat to read your followed channels once. Your current settings and connected channels will be preserved.");
    public string GrantFollowedChannelsAccess => Pick(
        "Разрешить доступ к списку",
        "Allow list access");
    public string FollowedChannelsSignInRequired => Pick(
        "Сначала войдите через Twitch, чтобы увидеть отслеживаемые каналы.",
        "Sign in with Twitch to see your followed channels.");
    public string FollowedChannelsEmpty => Pick(
        "В вашем Twitch-аккаунте нет отслеживаемых каналов.",
        "Your Twitch account has no followed channels.");
    public string FollowedChannelsNoMatches => Pick(
        "Среди отслеживаемых каналов ничего не найдено.",
        "No followed channels match your search.");
    public string FollowedChannelsLoadFailed => Pick(
        "Не удалось загрузить отслеживаемые каналы. Проверьте подключение и попробуйте обновить список.",
        "Could not load followed channels. Check your connection and refresh the list.");
    public string ChatLogs => Pick("Логи чата", "Chat logs");
    public string Donate => Pick("Донат", "Donate");
    public string Advanced => Pick("Дополнительно", "Advanced");
    public string SupportDeveloper => Pick("Поддержать разработчика", "Support the developer");
    public string SupportDeveloperDescription => Pick(
        "Выберите удобную платформу или скопируйте адрес криптокошелька.",
        "Choose a platform or copy a cryptocurrency wallet address.");
    public string SupportPlatforms => Pick("Платформы", "Platforms");
    public string CryptoWallets => Pick("Криптокошельки", "Crypto wallets");
    public string Open => Pick("Открыть", "Open");
    public string Copy => Pick("Копировать", "Copy");
    public string Boosty => "Boosty";
    public string DonationAlerts => "DonationAlerts";
    public string DonationAlertsAccount => Pick("Аккаунт DonationAlerts", "DonationAlerts account");
    public string DonationAlertsIntegrationIntro => Pick(
        "Нажмите «Подключить DonationAlerts» и войдите через официальный сайт. Новые донаты будут сразу появляться в отдельном окне с именем, суммой и сообщением.",
        "Select Connect DonationAlerts and sign in on the official website. New donations will immediately appear in a separate window with the sender, amount, and message.");
    public string ConnectDonationAlerts => Pick("Подключить DonationAlerts", "Connect DonationAlerts");
    public string DisconnectDonationAlerts => Pick("Отключить DonationAlerts", "Disconnect DonationAlerts");
    public string OpenDonationWindow => Pick("Открыть окно донатов", "Open donation window");
    public string AutoOpenDonationWindow => Pick(
        "Автоматически показывать окно при новом донате",
        "Automatically show the window for a new donation");
    public string DonationAlertsNotConnected => Pick("DonationAlerts не подключён", "DonationAlerts not connected");
    public string DonationAlertsAccountNotConnected => Pick(
        "Аккаунт DonationAlerts не подключён",
        "DonationAlerts account is not connected");
    public string DonationAlertsConnectDescription => Pick(
        "Войдите в аккаунт DonationAlerts, чтобы получать новые донаты в реальном времени.",
        "Sign in to DonationAlerts to receive new donations in real time.");
    public string DonationAlertsAccountConnected(string account) => Pick(
        "Подключён аккаунт: " + account,
        "Connected account: " + account);
    public string DonationAlertsOpeningLogin => Pick(
        "Открываем безопасный вход DonationAlerts…",
        "Opening secure DonationAlerts sign-in…");
    public string DonationAlertsConnecting => Pick(
        "Подключаем поток донатов…",
        "Connecting donation stream…");
    public string DonationAlertsConnected => Pick(
        "Донаты поступают в реальном времени",
        "Donations are arriving in real time");
    public string DonationAlertsReconnecting => Pick(
        "Связь потеряна, переподключаемся…",
        "Connection lost, reconnecting…");
    public string DonationAlertsReconnectForHistory => Pick(
        "Переподключите DonationAlerts, чтобы разрешить загрузку истории донатов.",
        "Reconnect DonationAlerts to allow donation history access.");
    public string DonationAlertsWindowTitle => Pick("Донаты", "Donations");
    public string RecentDonations => Pick("Последние донаты", "Recent donations");
    public string RefreshDonations => Pick("Обновить донаты", "Refresh donations");
    public string RepeatDonation => Pick("Повторить донат", "Replay donation");
    public string DonationAlertsObsControlReady(string sourceName) => Pick(
        "Прямое управление DonationAlerts подключено",
        "Direct DonationAlerts control is connected");
    public string DonationAlertsObsControlMissing => Pick(
        "Прямое управление DonationAlerts не подключено. Если токен ещё не импортирован, один раз добавьте официальный виджет «Оповещения» в OBS и нажмите «Обновить управление».",
        "Direct DonationAlerts control is not connected. If the token has not been imported yet, add the official Alerts widget to OBS once and select Refresh control.");
    public string FindDonationAlertsObsSource => Pick("Обновить управление", "Refresh control");
    public string DonationAlertsObsControlFailed(string details) => Pick(
        "DonationAlerts не выполнил команду: " + details,
        "DonationAlerts did not complete the command: " + details);
    public string DonationAlertsControlReconnecting => Pick(
        "Соединение с DonationAlerts потеряно. Выполняется автоматическое переподключение…",
        "The DonationAlerts connection was lost. Reconnecting automatically…");
    public string DonationSent => Pick("отправил", "sent");
    public string DonationAudioMessage => Pick("Голосовое сообщение", "Audio message");
    public string DonationHistoryLoading => Pick("Загружаем историю донатов…", "Loading donation history…");
    public string DonationHistoryRefreshTimedOut => Pick(
        "DonationAlerts слишком долго отвечает. Обновление остановлено — нажмите кнопку ещё раз.",
        "DonationAlerts took too long to respond. Refresh stopped; select the button to try again.");
    public string DonationHistoryEmpty => Pick("Донатов пока нет", "No donations yet");
    public string DonationHistoryEmptyDescription => Pick(
        "Новые донаты появятся здесь сразу после получения.",
        "New donations will appear here as soon as they arrive.");
    public string DonationHistoryLoadFailed(string details) => Pick(
        "Не удалось загрузить историю: " + details,
        "Could not load donation history: " + details);
    public string DonationHistoryCount(int count) => Pick(
        $"Загружено: {count}",
        $"Loaded: {count}");
    public string CurrentDonation => Pick("Сейчас показывается", "Now showing");
    public string DonationDisplayRemaining(int seconds) => Pick(
        $"Автоматически завершится через {seconds} сек.",
        $"Ends automatically in {seconds} sec.");
    public string DonationPlaybackControlledByDonationAlerts => Pick(
        "Сейчас проигрывается в DonationAlerts",
        "Now playing in DonationAlerts");
    public string DonationWaitingForServerStart => Pick(
        "Запрос отправлен. Ожидаем подтверждение запуска от DonationAlerts…",
        "Request sent. Waiting for DonationAlerts to confirm playback…");
    public string DonationServerStartTimedOut => Pick(
        "DonationAlerts не подтвердил запуск уведомления. Очередь продолжена без повторного запуска.",
        "DonationAlerts did not confirm that the alert started. The queue continued without replaying it.");
    public string DonationAlertsWaiting => Pick("Ожидаем новый донат", "Waiting for a new donation");
    public string DonationAlertsWaitingDescription => Pick(
        "Окно обновится сразу после получения доната.",
        "This window updates as soon as a donation arrives.");
    public string DonationFrom => Pick("От", "From");
    public string DonationMessage => Pick("Сообщение", "Message");
    public string DonationAnonymous => Pick("Аноним", "Anonymous");
    public string StreamEvents => Pick("События эфира", "Stream events");
    public string StreamEventsDescription => Pick(
        "Подписки, подарки, рейды, Bits, награды, Super Chat, участники YouTube и DonationAlerts в одной временной шкале.",
        "Subscriptions, gifts, raids, Bits, rewards, Super Chats, YouTube members, and DonationAlerts in one timeline.");
    public string NoStreamEvents => Pick("Событий эфира пока нет", "No stream events yet");
    public string EventFilterAll => Pick("Все", "All");
    public string EventFilterTwitch => "Twitch";
    public string EventFilterYouTube => "YouTube";
    public string EventFilterDonations => Pick("Донаты", "Donations");
    public string SmartChatFilters => Pick("Умный режим", "Smart view");
    public string SmartChatAll => Pick("Все", "All");
    public string SmartChatQuestions => Pick("Вопросы", "Questions");
    public string SmartChatMentions => Pick("Упоминания", "Mentions");
    public string SmartChatPaid => Pick("Платные", "Paid");
    public string SmartChatFirst => Pick("Первые", "First-time");
    public string SmartChatSuspicious => Pick("Подозрительные", "Suspicious");
    public string SmartChatRoles => Pick("Роли", "Roles");
    public string SmartChatHelp => Pick(
        "Фильтр работает локально и не удаляет сообщения. Режим «Подозрительные» отмечает повторы, избыток ссылок и капса.",
        "The filter runs locally and never deletes messages. Suspicious view detects repeats, excessive links, and caps.");
    public string StreamProtection => Pick("Защита эфира", "Stream protection");
    public string StreamProtectionDescription => Pick(
        "Быстро ограничьте чат во время наплыва сообщений или временно остановите его вывод в WitherChat и OBS.",
        "Quickly limit chat during a message surge or temporarily stop displaying it in WitherChat and OBS.");
    public string ProtectionTwitchSection => Pick("Ограничения Twitch-чата", "Twitch chat limits");
    public string ProtectionTwitchSectionDescription => Pick(
        "Эти параметры изменят настоящий чат текущего Twitch-канала только после нажатия «Применить к Twitch».",
        "These options change the current channel's real Twitch chat only after you select Apply to Twitch.");
    public string ProtectionTwitchPermissionHint => Pick(
        "Нужны права владельца канала или модератора.",
        "Channel owner or moderator permissions are required.");
    public string ProtectionSlowMode => Pick("Медленный режим", "Slow mode");
    public string ProtectionSlowModeDescription => Pick(
        "Каждый зритель сможет отправлять не более одного сообщения за выбранный интервал.",
        "Each viewer can send no more than one message during the selected interval.");
    public string ProtectionSlowSeconds => Pick("Интервал", "Interval");
    public string ProtectionSecondsShort => Pick("сек", "sec");
    public string ProtectionSubscriberMode => Pick("Только подписчики", "Subscribers only");
    public string ProtectionSubscriberModeDescription => Pick(
        "Отправлять сообщения смогут только платные подписчики канала.",
        "Only paid channel subscribers will be able to send messages.");
    public string ProtectionFollowerMode => Pick("Только фолловеры", "Followers only");
    public string ProtectionFollowerModeDescription => Pick(
        "Писать смогут только зрители, которые следят за каналом не меньше указанного времени.",
        "Only viewers who have followed the channel for at least the selected time can send messages.");
    public string ProtectionFollowerMinutes => Pick("Стаж фолловера", "Follow age");
    public string ProtectionMinutesShort => Pick("мин", "min");
    public string ProtectionLocalSection => Pick("Только в WitherChat", "WitherChat only");
    public string ProtectionPauseDisplay => Pick("Пауза новых сообщений в окне", "Pause new messages in the app");
    public string ProtectionPauseDisplayDescription => Pick(
        "Новые сообщения продолжат записываться в лог и отправляться в OBS, но в окне они будут пропущены и позже не появятся.",
        "New messages remain in the log and continue to OBS, but are skipped in the app and will not appear later.");
    public string ProtectionSuppressObs => Pick("Не выводить новые сообщения в OBS", "Stop sending new messages to OBS");
    public string ProtectionSuppressObsDescription => Pick(
        "Сообщения останутся в приложении и логах, но не попадут в браузерный OBS-оверлей.",
        "Messages remain visible and logged in the app, but are not sent to the OBS browser overlay.");
    public string ProtectionActions => Pick("Действия", "Actions");
    public string ProtectionApply => Pick("Применить к Twitch", "Apply to Twitch");
    public string ProtectionApplyHint => Pick(
        "Сохранит ограничения из левого блока в текущем Twitch-чате.",
        "Saves the limits from the left section to the current Twitch chat.");
    public string ProtectionApplied => Pick("Настройки защиты применены", "Protection settings applied");
    public string ProtectionLocalOnly => Pick(
        "Эти переключатели работают сразу — кнопка «Применить» для них не нужна. Настройки Twitch не изменяются.",
        "These switches work immediately—Apply is not required. Twitch settings are not changed.");
    public string ProtectionClearChat => Pick("Очистить Twitch-чат", "Clear Twitch chat");
    public string ProtectionClearChatHint => Pick(
        "Удалит все сообщения у всех зрителей. После нажатия появится подтверждение.",
        "Deletes every message for all viewers. A confirmation will appear first.");
    public string ProtectionClearConfirm => Pick(
        "Удалить все сообщения из текущего Twitch-чата? Это действие нельзя отменить.",
        "Delete every message in the current Twitch chat? This cannot be undone.");
    public string ProtectionChatCleared => Pick("Twitch-чат очищен", "Twitch chat cleared");
    public string ProtectionSuppressed(int count) => Pick(
        $"Пропущено в окне: {count}", $"Skipped in app: {count}");
    public string StreamMoments => Pick("Моменты эфира", "Stream moments");
    public string StreamMomentsDescription => Pick(
        "Сохранённые сообщения и события с вашей заметкой.",
        "Saved messages and events with your notes.");
    public string SaveStreamMoment => Pick("Сохранить момент", "Save moment");
    public string MomentNote => Pick("Заметка к моменту", "Moment note");
    public string MomentNotePlaceholder => Pick("Например: клип после победы", "For example: clip after the win");
    public string NoStreamMoments => Pick("Сохранённых моментов пока нет", "No saved moments yet");
    public string DeleteMoment => Pick("Удалить момент", "Delete moment");
    public string ExportMoments => Pick("Экспорт JSON", "Export JSON");
    public string ExportDifferentFileRequired => Pick(
        "Для экспорта выберите другой файл: исходный лог нельзя перезаписывать.",
        "Choose a different export file: the original chat log cannot be overwritten.");
    public string MomentsWriteRetry => Pick(
        "Не удалось записать файл моментов. Проверьте доступ к диску и попробуйте ещё раз.",
        "Could not write the moments file. Check disk access and try again.");
    public string MomentsLoadProblem => Pick(
        "Проблема с файлом моментов. Исходные данные сохранены; проверьте доступ или резервную копию.",
        "The moments file could not be loaded normally. Original data was preserved; check access or the backup.");
    public string MomentsExportDifferentFileRequired => Pick(
        "Для экспорта выберите файл за пределами папки данных WitherChat.",
        "Choose an export file outside the WitherChat data folder.");
    public string MomentsSaveFailed(string details) => Pick(
        "Не удалось сохранить моменты: " + details,
        "Could not save moments: " + details);
    public string CreateTwitchClip => Pick("Создать клип Twitch", "Create Twitch clip");
    public string TwitchClipSignInRequired => Pick(
        "Войдите в Twitch, чтобы создавать клипы",
        "Sign in to Twitch to create clips");
    public string TwitchClipLiveRequired => Pick(
        "Клип можно создать только во время эфира выбранного Twitch-канала",
        "A clip can only be created while the selected Twitch channel is live");
    public string TwitchClipReconnectRequired => Pick(
        "Нажмите, чтобы один раз переподключить Twitch и разрешить создание клипов",
        "Select to reconnect Twitch once and allow clip creation");
    public string TwitchClipCreating => Pick("Создаём клип…", "Creating clip…");
    public string TwitchClipCreatingDescription => Pick(
        "Twitch сохраняет последние 30 секунд эфира. Не нажимайте кнопку повторно.",
        "Twitch is saving the latest 30 seconds of the stream. Do not select the button again.");
    public string TwitchClipPermissionTitle => Pick(
        "Нужно разрешение Twitch",
        "Twitch permission required");
    public string TwitchClipPermissionDescription => Pick(
        "Подтвердите вход в браузере. После этого WitherChat сразу создаст клип.",
        "Confirm sign-in in the browser. WitherChat will create the clip immediately afterward.");
    public string TwitchClipPermissionNotGranted => Pick(
        "Twitch не выдал разрешение на создание клипов. Переподключите аккаунт и разрешите доступ.",
        "Twitch did not grant clip permission. Reconnect the account and allow access.");
    public string TwitchClipCreated => Pick("Клип создан", "Clip created");
    public string TwitchClipCreatedDescription => Pick(
        "Клип опубликован. На обработку ссылки Twitch может потребоваться несколько секунд.",
        "The clip is published. Twitch may need a few seconds before the link is ready.");
    public string TwitchClipFailed => Pick("Не удалось создать клип", "Could not create clip");
    public string TwitchClipRestricted => Pick(
        "Владелец канала ограничил создание клипов, либо ваш аккаунт заблокирован в этом чате.",
        "The channel owner restricted clips, or your account is banned from this chat.");
    public string TwitchClipUnavailable => Pick(
        "Для этого эфира или категории сейчас нельзя создать клип.",
        "A clip cannot be created for this stream or category right now.");
    public string TwitchClipTemporaryFailure => Pick(
        "Twitch временно не ответил. Проверьте подключение и попробуйте ещё раз.",
        "Twitch did not respond. Check the connection and try again.");
    public string TwitchClipOpen => Pick("Открыть в Twitch", "Open in Twitch");
    public string TwitchClipCopyLink => Pick("Копировать ссылку", "Copy link");
    public string TwitchClipLinkCopied => Pick(
        "Ссылка на клип скопирована",
        "Clip link copied");
    public string PaidEvent => Pick("Платное событие", "Paid event");
    public string StreamEventTitle(string kind, int count) => kind switch
    {
        StreamEventKinds.Subscription => Pick("Новая подписка", "New subscription"),
        StreamEventKinds.Resubscription => Pick("Продление подписки", "Resubscription"),
        StreamEventKinds.GiftSubscription => Pick("Подарочная подписка", "Gift subscription"),
        StreamEventKinds.CommunityGift => Pick($"Подарено подписок: {Math.Max(1, count)}", $"Gift subscriptions: {Math.Max(1, count)}"),
        StreamEventKinds.Membership => Pick("Участник канала", "Channel member"),
        StreamEventKinds.MembershipGift => Pick($"Подарено участий: {Math.Max(1, count)}", $"Gift memberships: {Math.Max(1, count)}"),
        StreamEventKinds.GiftMembershipReceived => Pick("Получено подарочное участие", "Gift membership received"),
        StreamEventKinds.Raid => Pick($"Рейд · {Math.Max(0, count)} зрителей", $"Raid · {Math.Max(0, count)} viewers"),
        StreamEventKinds.Cheer => Pick($"Bits: {Math.Max(0, count):N0}", $"Bits: {Math.Max(0, count):N0}"),
        StreamEventKinds.ChannelPoints => Pick("Награда за баллы канала", "Channel Points reward"),
        StreamEventKinds.SuperChat => "Super Chat",
        StreamEventKinds.SuperSticker => "Super Sticker",
        StreamEventKinds.Poll => Pick("Опрос YouTube", "YouTube poll"),
        StreamEventKinds.Donation => Pick("Новый донат", "New donation"),
        StreamEventKinds.Announcement => Pick("Объявление", "Announcement"),
        StreamEventKinds.Charity => Pick("Благотворительный донат", "Charity donation"),
        _ => Pick("Событие эфира", "Stream event")
    };
    public string DonationNoMessage => Pick("Без сообщения", "No message");
    public string SkipDonation => Pick("Пропустить", "Skip");
    public string HideDonation => Pick("Скрыть", "Hide");
    public string ClearDonationQueue => Pick("Очистить очередь", "Clear queue");
    public string TestDonation => Pick("Тестовый донат", "Test donation");
    public string DonationQueue(int count) => Pick(
        count == 1 ? "Ещё 1 донат в очереди" : $"Ещё {count} донатов в очереди",
        count == 1 ? "1 more donation queued" : $"{count} more donations queued");
    public string DonationTestSender => Pick("Тестовый зритель", "Test viewer");
    public string DonationTestMessage => Pick(
        "Спасибо за WitherChat! Это проверка окна донатов.",
        "Thank you for WitherChat! This is a donation window test.");
    public string Twitch => "Twitch";
    public string YouTube => "YouTube";
    public string YouTubeChannelOwnerBadge => Pick("Владелец YouTube-канала", "YouTube channel owner");
    public string YouTubeModeratorBadge => Pick("Модератор YouTube", "YouTube moderator");
    public string YouTubeSponsorBadge => Pick("Спонсор YouTube-канала", "YouTube channel member");
    public string UnbanStatusPending => Pick("Ожидает решения", "Pending review");
    public string UnbanStatusApproved => Pick("Одобрена", "Approved");
    public string UnbanStatusDenied => Pick("Отклонена", "Denied");
    public string UnbanStatusAcknowledged => Pick("Рассмотрена", "Reviewed");
    public string UnbanStatusCanceled => Pick("Отменена", "Canceled");
    public string ChatViewMode => Pick("Режим чата", "Chat view");
    public string CombinedChat => Pick("Общий", "Combined");
    public string SplitChat => Pick("Раздельно", "Split");
    public string ChatViewModeHelp => Pick(
        "Общий режим смешивает Twitch и YouTube по времени. Раздельный режим показывает две независимые колонки, когда подключены оба чата. В компактном окне всегда используется общая лента.",
        "Combined view mixes Twitch and YouTube by time. Split view shows two independent columns when both chats are connected. Compact mode always uses the combined feed.");
    public string TwitchChat => Pick("Чат Twitch", "Twitch chat");
    public string YouTubeChat => Pick("Чат YouTube", "YouTube chat");
    public string NoTwitchMessages => Pick("Сообщений Twitch пока нет", "No Twitch messages yet");
    public string NoYouTubeMessages => Pick("Сообщений YouTube пока нет", "No YouTube messages yet");
    public string YouTubeAccount => Pick("Аккаунт YouTube", "YouTube account");
    public string YouTubeNotConnected => Pick("YouTube не подключён", "YouTube not connected");
    public string YouTubeReadOnlyIntro => Pick(
        "Нажмите кнопку и войдите в Google. WitherChat сам найдёт активную трансляцию, подключит её чат и позволит владельцу канала модерировать сообщения. Действия выполняются напрямую через официальный YouTube API.",
        "Sign in to Google. WitherChat will find the active broadcast, connect its chat, and let the channel owner moderate messages through the official YouTube API.");
    public string ConnectYouTube => Pick("Подключить YouTube", "Connect YouTube");
    public string DisconnectYouTube => Pick("Отключить YouTube", "Disconnect YouTube");
    public string EnableYouTubeModeration => Pick("Разрешить модерацию YouTube", "Enable YouTube moderation");
    public string YouTubeModerationReady => Pick(
        "Модерация YouTube подключена",
        "YouTube moderation is connected");
    public string YouTubeModerationPermissionRequired => Pick(
        "Переподключите YouTube один раз, чтобы разрешить удаление сообщений и блокировку зрителей.",
        "Reconnect YouTube once to allow deleting messages and banning viewers.");
    public string YouTubeWaitingForBroadcast => Pick(
        "Аккаунт подключён. Ожидаем активный эфир с включённым live-чатом…",
        "Account connected. Waiting for an active broadcast with live chat enabled…");
    public string YouTubeChatConnected => Pick("YouTube-чат подключён", "YouTube chat connected");
    public string YouTubeConnecting => Pick("Подключаем YouTube…", "Connecting YouTube…");
    public string YouTubeOpeningLogin => Pick(
        "Открываем безопасный вход через Google…",
        "Opening secure Google sign-in…");
    public string Steam => "Steam";
    public string Telegram => "Telegram";
    public string UsdtTrc20 => "USDT TRC20";
    public string UsdtTon => "USDT TON";
    public string UsdtBsc => "USDT BSC";
    public string AdvancedIntro => Pick(
        "Параметры собственного приложения Twitch. Неверные значения помешают входу.",
        "Custom Twitch application settings. Invalid values will prevent sign-in.");
    public string OnboardingSection => Pick("Обучение", "Tutorial");
    public string OnboardingRepeat => Pick("Повторить обучение", "Replay tutorial");
    public string OnboardingRepeatDescription => Pick(
        "Ещё раз покажет основные элементы WitherChat и объяснит назначение каждой панели.",
        "Walk through the main WitherChat controls and panels again.");
    public string OnboardingEyebrow => Pick("БЫСТРЫЙ СТАРТ", "QUICK START");
    public string OnboardingBack => Pick("Назад", "Back");
    public string OnboardingNext => Pick("Далее", "Next");
    public string OnboardingSkip => Pick("Пропустить", "Skip");
    public string OnboardingFinish => Pick("Начать работу", "Start using WitherChat");
    public string ContextTutorialEyebrow => Pick("ПОДСКАЗКА ПО РАЗДЕЛУ", "FEATURE GUIDE");
    public string ContextTutorialFinish => Pick("Готово", "Done");
    public string OpenSectionTutorial => Pick("Показать обучение по этому разделу", "Show a guide for this section");
    public string OpenChannelsTutorial => Pick("Обучение по панели каналов", "Channels panel guide");
    public string OpenLogsTutorial => Pick("Обучение по журналам чата", "Chat logs guide");
    public string OpenModerationTutorial => Pick("Обучение по модерации", "Moderation guide");
    public string OpenConnectTutorial => Pick("Обучение по подключению Twitch", "Twitch connection guide");
    public string OpenSettingsTutorial => Pick("Обучение по всем разделам настроек", "Settings sections guide");
    public string OpenDonationsTutorial => Pick("Обучение по DonationAlerts", "DonationAlerts guide");
    public string OpenEventsTutorial => Pick("Обучение по событиям эфира", "Stream events guide");
    public string OpenProtectionTutorial => Pick("Обучение по защите эфира", "Stream protection guide");
    public string OpenMomentsTutorial => Pick("Обучение по моментам эфира", "Stream moments guide");
    public string OpenSmartChatTutorial => Pick("Обучение по умным режимам", "Smart chat guide");

    public string ContextTutorialTitle(TutorialTopic topic, int step) => (topic, step) switch
    {
        (TutorialTopic.Channels, 0) => Pick("Ваши каналы", "Your channels"),
        (TutorialTopic.Channels, 1) => Pick("Статус эфира с первого взгляда", "Live status at a glance"),
        (TutorialTopic.Channels, 2) => Pick("Добавление и переключение", "Add and switch channels"),
        (TutorialTopic.Logs, 0) => Pick("Архив по каналам и дням", "Archive by channel and day"),
        (TutorialTopic.Logs, 1) => Pick("Поиск и фильтры", "Search and filters"),
        (TutorialTopic.Logs, 2) => Pick("Содержимое журнала", "Log contents"),
        (TutorialTopic.Logs, 3) => Pick("Экспорт и хранение", "Export and storage"),
        (TutorialTopic.Moderation, 0) => Pick("Twitch и YouTube", "Twitch and YouTube"),
        (TutorialTopic.Moderation, 1) => Pick("Выбор платформы", "Choose a platform"),
        (TutorialTopic.Moderation, 2) => "Twitch AutoMod",
        (TutorialTopic.Moderation, 3) => Pick("Блокировки Twitch", "Twitch bans"),
        (TutorialTopic.Moderation, 4) => Pick("Модерация YouTube", "YouTube moderation"),
        (TutorialTopic.Connect, 0) => Pick("Подключение Twitch", "Connect Twitch"),
        (TutorialTopic.Connect, 1) => Pick("Режим только для чтения", "Read-only mode"),
        (TutorialTopic.Connect, 2) => Pick("Отслеживаемые каналы", "Followed channels"),
        (TutorialTopic.ObsPlugin, 0) => Pick("Плагин: статус и установка", "Plugin: status and installation"),
        (TutorialTopic.ObsPlugin, 1) => Pick("Чат внутри OBS", "Chat inside OBS"),
        (TutorialTopic.ObsPlugin, 2) => Pick("Обновить или удалить", "Update or remove"),
        (TutorialTopic.ObsPlugin, 3) => Pick("Док и оверлей — разные задачи", "Dock and overlay serve different purposes"),
        (TutorialTopic.Settings, 8) => Pick("Управление плагином OBS", "Manage the OBS plugin"),
        (TutorialTopic.Settings, 9) => Pick("Док или оверлей?", "Dock or overlay?"),
        (TutorialTopic.Settings, 0) => Pick("Разделы настроек", "Settings sections"),
        (TutorialTopic.Settings, 1) => Pick("Поведение программы", "App behavior"),
        (TutorialTopic.Settings, 2) => Pick("Вид объединённого чата", "Combined chat appearance"),
        (TutorialTopic.Settings, 3) => Pick("Запись журналов", "Chat logging"),
        (TutorialTopic.Settings, 4) => Pick("OBS-оверлей", "OBS overlay"),
        (TutorialTopic.Settings, 5) => Pick("Аккаунты и интеграции", "Accounts and integrations"),
        (TutorialTopic.Settings, 6) => Pick("Поддержка проекта", "Support the project"),
        (TutorialTopic.Settings, 7) => Pick("Дополнительные параметры", "Advanced options"),
        (TutorialTopic.Donations, 0) => Pick("Прямое управление DonationAlerts", "Direct DonationAlerts control"),
        (TutorialTopic.Donations, 1) => Pick("История донатов", "Donation history"),
        (TutorialTopic.Donations, 2) => Pick("Повтор и очередь", "Replay and queue"),
        (TutorialTopic.Donations, 3) => Pick("Текущий донат и кнопка «Скрыть»", "Current donation and Hide"),
        (TutorialTopic.DonationsSetup, 0) => Pick("Подключение DonationAlerts", "Connect DonationAlerts"),
        (TutorialTopic.DonationsSetup, 1) => Pick("Безопасная авторизация", "Secure authorization"),
        (TutorialTopic.Events, 0) => Pick("Все события в одной шкале", "Every event in one timeline"),
        (TutorialTopic.Events, 1) => Pick("Фильтры платформ и донатов", "Platform and donation filters"),
        (TutorialTopic.Protection, 0) => Pick("Настройки защиты Twitch", "Twitch protection settings"),
        (TutorialTopic.Protection, 1) => Pick("Аварийные переключатели", "Emergency switches"),
        (TutorialTopic.Protection, 2) => Pick("Применение и очистка", "Apply and clear"),
        (TutorialTopic.Moments, 0) => Pick("Сохранённые моменты", "Saved moments"),
        (TutorialTopic.Moments, 1) => Pick("Заметки и экспорт", "Notes and export"),
        (TutorialTopic.SmartChat, 0) => Pick("Сфокусированный чат", "A focused chat"),
        (TutorialTopic.SmartChat, 1) => Pick("Локальная проверка сообщений", "Local message checks"),
        _ => string.Empty
    };

    public string ContextTutorialDescription(TutorialTopic topic, int step) => (topic, step) switch
    {
        (TutorialTopic.Channels, 0) => Pick(
            "В этой панели собраны ваш канал и каналы, добавленные для просмотра. Нажатие по всей карточке сразу переключает активный чат.",
            "This panel contains your own channel and channels added for viewing. Select anywhere on a card to switch the active chat."),
        (TutorialTopic.Channels, 1) => Pick(
            "Карточки обновляют состояние эфира и число зрителей. Ваш канал отмечен отдельно и остаётся в списке, пока подключён Twitch-аккаунт.",
            "Cards refresh live state and viewer count. Your own channel is marked separately and remains while the Twitch account is connected."),
        (TutorialTopic.Channels, 2) => Pick(
            "Можно держать до трёх каналов, искать новые по нику, быстро переключаться и удалять дополнительные каналы.",
            "Keep up to three channels, find new ones by login, switch instantly, and remove additional channels."),
        (TutorialTopic.Logs, 0) => Pick(
            "Слева выбираются канал и день трансляции. WitherChat хранит историю отдельно для каждого канала.",
            "Choose a channel and stream date on the left. WitherChat stores history separately for every channel."),
        (TutorialTopic.Logs, 1) => Pick(
            "Ищите текст или зрителя и ограничивайте результаты по роли: стример, модератор, подписчик или VIP.",
            "Search text or a viewer and narrow results by broadcaster, moderator, subscriber, or VIP role."),
        (TutorialTopic.Logs, 2) => Pick(
            "В журнале сохраняются время, бейджи, имя и содержимое сообщения. Ссылки и эмоты остаются распознаваемыми.",
            "The log preserves timestamps, badges, names, and message text. Links and emotes remain identifiable."),
        (TutorialTopic.Logs, 3) => Pick(
            "Откройте папку хранения, экспортируйте выбранную историю в TXT или JSONL либо удалите ненужный журнал с подтверждением.",
            "Open the storage folder, export selected history as TXT or JSONL, or delete an unwanted log after confirmation."),
        (TutorialTopic.Moderation, 0) => Pick(
            "Общая панель объединяет Twitch и YouTube. Нажатие ПКМ по строке сообщения открывает единое меню, а действие автоматически отправляется в нужную платформу.",
            "The shared panel combines Twitch and YouTube. Right-click a message row for the shared menu; actions are automatically sent to the correct platform."),
        (TutorialTopic.Moderation, 1) => Pick(
            "Переключайтесь между Twitch и YouTube в одной панели. Вкладка доступна только при наличии прав модератора и активного чата трансляции.",
            "Switch between Twitch and YouTube in one panel. A tab is available only with moderator permission and an active live chat."),
        (TutorialTopic.Moderation, 2) => Pick(
            "На вкладке AutoMod можно разрешать или отклонять сообщения, удержанные автоматической модерацией Twitch.",
            "The AutoMod tab lets you allow or deny messages held by Twitch automatic moderation."),
        (TutorialTopic.Moderation, 3) => Pick(
            "Во вкладках Twitch можно искать заблокированных зрителей, снимать наказание и обрабатывать запросы на разбан. Кнопка обновления получает актуальное состояние с Twitch.",
            "The Twitch tabs let you find banned viewers, remove punishment, and process unban requests. Refresh retrieves the latest state from Twitch."),
        (TutorialTopic.Moderation, 4) => Pick(
            "Для YouTube доступны удаление сообщения, временная и постоянная блокировка, а также снятие блокировок, созданных в текущем сеансе WitherChat. Старому подключению YouTube может потребоваться однократное подтверждение новых прав в настройках.",
            "YouTube supports message deletion, temporary or permanent bans, and removing bans created in the current WitherChat session. A legacy YouTube connection may need one-time permission approval in Settings."),
        (TutorialTopic.Connect, 0) => Pick(
            "Вход через официальный OAuth Twitch открывает чтение, отправку сообщений, список подписок и доступные функции модерации.",
            "Official Twitch OAuth enables reading, sending messages, followed channels, and permitted moderation features."),
        (TutorialTopic.Connect, 1) => Pick(
            "Для просмотра чужого канала вход не обязателен: введите ник канала и подключите чат в режиме чтения.",
            "Sign-in is optional for watching another channel: enter its login and connect in read-only mode."),
        (TutorialTopic.Connect, 2) => Pick(
            "После входа вкладка отслеживаемых каналов показывает ваши подписки, live-статус и число зрителей.",
            "After sign-in, Followed channels shows your subscriptions, live state, and viewer count."),
        (TutorialTopic.ObsPlugin, 0) => Pick(
            "Статус показывает, установлен ли плагин и нужно ли обновление. Если OBS не найден, выберите его корневую папку. Закройте OBS и нажмите «Установить»; Windows может запросить права администратора.",
            "Status shows whether the plugin is installed or needs an update. If OBS is not found, choose its root folder. Close OBS and select Install; Windows may request administrator permission."),
        (TutorialTopic.ObsPlugin, 1) => Pick(
            "В OBS откройте Сервис → WitherChat. Плагин сам запускает чат и встраивает его в док: отдельно открывать EXE не нужно. В маленькой панели инструменты, аккаунты и настройки доступны через меню.",
            "In OBS, open Tools → WitherChat. The plugin launches the chat and embeds it in a dock; no separate EXE launch is needed. A small dock keeps tools, accounts, and settings in its menu."),
        (TutorialTopic.ObsPlugin, 2) => Pick(
            "Новый EXE предлагает обновить установленный плагин. «Удалить плагин» требует подтверждения и удаляет только известные файлы WitherChat и его служебные копии. Другие плагины, сцены, профили OBS и аккаунты чата сохраняются.",
            "A new EXE can update the installed plugin. Remove plugin asks for confirmation and deletes only known WitherChat files and service copies. Other plugins, scenes, OBS profiles, and chat accounts are preserved."),
        (TutorialTopic.ObsPlugin, 3) => Pick(
            "Док — полноценный чат для работы внутри OBS: сообщения, модерация, логи и донаты. Оверлей — отображение сообщений зрителям через источник «Браузер»: включите его и скопируйте URL в источник сцены.",
            "A dock is the full chat workspace inside OBS: messages, moderation, logs, and donations. An overlay displays messages to viewers through a Browser source: enable it and copy the URL into the scene source."),
        (TutorialTopic.Settings, 8) => Pick(
            "В блоке плагина видны статус, установка и обновление, выбор папки OBS и удаление с подтверждением. Для действий с файлами закройте OBS. Неизвестные файлы внутри папки плагина сохраняются.",
            "The plugin card provides status, install/update, OBS folder selection, and confirmed removal. Close OBS before file operations. Unknown files inside the plugin folder are preserved."),
        (TutorialTopic.Settings, 9) => Pick(
            "Док запускается через Сервис → WitherChat в OBS и сохраняет функции приложения. Оверлей добавляется в сцену как источник «Браузер» и показывает сообщения зрителям. Это независимые варианты.",
            "Open the dock through Tools → WitherChat in OBS for the full app. Add the overlay as a Browser scene source to display messages to viewers. These options are independent."),
        (TutorialTopic.Settings, 0) => Pick(
            "Навигация слева группирует программу, чат, логи, OBS, аккаунты, поддержку и дополнительные параметры.",
            "The left navigation groups app, chat, logs, OBS, accounts, support, and advanced settings."),
        (TutorialTopic.Settings, 1) => Pick(
            "Здесь настраиваются тема, язык, положение системных кнопок, запуск вместе с Windows, сворачивание в трей, анимации и компактный режим.",
            "Configure theme, language, system-button position, Windows startup, tray behavior, animations, and compact mode here."),
        (TutorialTopic.Settings, 2) => Pick(
            "Выберите общий поток Twitch и YouTube либо две отдельные колонки. Здесь же настраиваются шрифт, лимит сообщений, бейджи и эмоты.",
            "Choose one combined Twitch and YouTube feed or two separate columns. Font, message limits, badges, and emotes are configured here too."),
        (TutorialTopic.Settings, 3) => Pick(
            "Включите запись сообщений, выберите папку, формат и максимальное число строк. Просмотр и экспорт выполняются в отдельной панели логов.",
            "Enable message logging, choose its folder, format, and line limit. Review and export logs from the dedicated Logs panel."),
        (TutorialTopic.Settings, 4) => Pick(
            "Скопируйте URL в источник «Браузер» OBS и настройте тему, размер, прозрачность, выравнивание, исчезновение, бейджи и эмоты.",
            "Copy the URL into an OBS Browser source and configure theme, size, opacity, alignment, fade, badges, and emotes."),
        (TutorialTopic.Settings, 5) => Pick(
            "Здесь подключаются Twitch, YouTube и DonationAlerts. YouTube сам находит активную трансляцию, а DonationAlerts открывает отдельное окно донатов.",
            "Connect Twitch, YouTube, and DonationAlerts here. YouTube finds the active broadcast, while DonationAlerts opens a separate donation window."),
        (TutorialTopic.Settings, 6) => Pick(
            "Раздел поддержки содержит проверенные адреса кошельков и быстрые ссылки проекта. Нажатие по адресу копирует его полностью.",
            "Support contains verified wallet addresses and project links. Selecting an address copies it in full."),
        (TutorialTopic.Settings, 7) => Pick(
            "Дополнительные OAuth-параметры предназначены для ручной настройки интеграций. Изменяйте их только если понимаете назначение Client ID и URI возврата.",
            "Advanced OAuth options are intended for manual integration setup. Change them only if you understand Client IDs and redirect URIs."),
        (TutorialTopic.Donations, 0) => Pick(
            "Зелёный статус означает, что WitherChat напрямую подключён к виджету DonationAlerts. OBS не используется как API управления.",
            "A green status means WitherChat is directly connected to the DonationAlerts widget. OBS is not used as the control API."),
        (TutorialTopic.Donations, 1) => Pick(
            "История загружается из вашего аккаунта. Кнопка обновления безопасно объединяет новые записи без повторов.",
            "History loads from your account. Refresh safely merges new entries without duplicates."),
        (TutorialTopic.Donations, 2) => Pick(
            "Круглая стрелка повторяет выбранный донат один раз. Всплески складываются в FIFO-очередь и воспроизводятся последовательно.",
            "The circular arrow replays the selected donation once. Bursts enter a FIFO queue and play sequentially."),
        (TutorialTopic.Donations, 3) => Pick(
            "Карточка появляется только после подтверждения запуска сервером DonationAlerts. «Скрыть» доступно только пока донат реально проигрывается.",
            "The card appears only after DonationAlerts confirms playback. Hide is available only while the donation is actually playing."),
        (TutorialTopic.DonationsSetup, 0) => Pick(
            "Нажмите «Подключить DonationAlerts», чтобы открыть официальный OAuth-сайт. После входа WitherChat загрузит историю и подготовит прямое управление уведомлениями.",
            "Select Connect DonationAlerts to open the official OAuth site. After sign-in, WitherChat loads history and prepares direct alert control."),
        (TutorialTopic.DonationsSetup, 1) => Pick(
            "WitherChat не просит вставлять API-ключ вручную. Сессия сохраняется безопасно, а доступ можно отозвать выходом из аккаунта в настройках.",
            "WitherChat does not ask you to paste an API key. The session is stored securely and can be revoked by signing out in Settings."),
        (TutorialTopic.Events, 0) => Pick(
            "Здесь в хронологическом порядке собираются подписки, подарки, рейды, Bits, баллы канала, Super Chat, участники YouTube и донаты.",
            "Subscriptions, gifts, raids, Bits, Channel Points, Super Chats, YouTube members, and donations appear here chronologically."),
        (TutorialTopic.Events, 1) => Pick(
            "Оставьте все платформы вместе либо быстро покажите только Twitch, YouTube или платные события.",
            "Keep all platforms together or quickly show only Twitch, YouTube, or paid events."),
        (TutorialTopic.Protection, 0) => Pick(
            "Верхний блок управляет настоящим Twitch-чатом: задайте интервал медленного режима, доступ для подписчиков или минимальный стаж фолловера, затем нажмите «Применить к Twitch».",
            "The upper section controls the real Twitch chat: choose the slow-mode interval, subscriber access, or minimum follow age, then select Apply to Twitch."),
        (TutorialTopic.Protection, 1) => Pick(
            "Нижний блок работает сразу и только внутри WitherChat. Пауза пропускает новые строки в окне, а отдельный переключатель останавливает их отправку в OBS; логи продолжают записываться.",
            "The lower section works immediately and only inside WitherChat. Pause skips new rows in the app, while the separate switch stops delivery to OBS; logs continue to be written."),
        (TutorialTopic.Protection, 2) => Pick(
            "Применение меняет настройки текущего Twitch-чата. Полная очистка требует отдельного подтверждения и не запускается двойным кликом.",
            "Apply changes the current Twitch chat settings. Clearing the whole chat requires a separate confirmation and cannot be triggered by a double click."),
        (TutorialTopic.Moments, 0) => Pick(
            "Откройте ПКМ по сообщению и выберите «Сохранить момент». В список попадут платформа, канал, автор, текст и время.",
            "Right-click a message and choose Save moment. The list keeps its platform, channel, author, text, and time."),
        (TutorialTopic.Moments, 1) => Pick(
            "Добавьте короткую заметку, удаляйте ненужные записи или экспортируйте весь список в переносимый JSON-файл.",
            "Add a short note, delete unwanted entries, or export the whole list to a portable JSON file."),
        (TutorialTopic.SmartChat, 0) => Pick(
            "Режимы показывают вопросы, упоминания, платные события, первые сообщения и зрителей с ролями, не удаляя исходную ленту.",
            "Views show questions, mentions, paid events, first-time messages, and viewers with roles without deleting the original feed."),
        (TutorialTopic.SmartChat, 1) => Pick(
            "Подозрительные сообщения определяются локально по частым повторам, избытку ссылок и капса. Текст не отправляется стороннему сервису.",
            "Suspicious messages are detected locally from rapid repeats, excessive links, and caps. Text is not sent to a third-party service."),
        _ => string.Empty
    };

    public string ContextTutorialHint(TutorialTopic topic, int step) => (topic, step) switch
    {
        (TutorialTopic.Channels, _) => Pick("Открывайте эту подсказку снова кнопкой ? в заголовке каналов.", "Open this guide again with ? in the Channels header."),
        (TutorialTopic.Logs, 0) => Pick("Запись и формат файлов настраиваются в Настройки → Логи чата.", "Logging and formats are configured in Settings → Chat logs."),
        (TutorialTopic.Logs, _) => Pick("Фильтры не изменяют исходный файл журнала.", "Filters never modify the original log file."),
        (TutorialTopic.Moderation, _) => Pick("Обучение ничего не отправляет в Twitch или YouTube и не выполняет действий над зрителями.", "The guide sends nothing to Twitch or YouTube and performs no viewer actions."),
        (TutorialTopic.Connect, _) => Pick("Авторизация всегда открывается на официальном сайте Twitch.", "Authorization always opens on the official Twitch site."),
        (TutorialTopic.ObsPlugin, 2) => Pick("Если чат запущен из папки плагина, для удаления откройте отдельный EXE вне OBS.", "If the chat runs from the plugin folder, use a standalone EXE outside OBS to remove it."),
        (TutorialTopic.ObsPlugin, _) => Pick("Обучение не устанавливает, не обновляет и не удаляет плагин.", "This guide does not install, update, or remove the plugin."),
        (TutorialTopic.Settings, 8 or 9) => Pick("Отмена настроек не отменяет уже выполненную установку или удаление плагина.", "Settings Cancel does not undo a completed plugin installation or removal."),
        (TutorialTopic.Settings, 4) => Pick("Проверьте результат кнопкой тестового сообщения до начала эфира.", "Use the test-message button before going live."),
        (TutorialTopic.Settings, _) => Pick("Изменения можно сохранить или отменить внизу окна.", "Save or cancel changes at the bottom of the window."),
        (TutorialTopic.Donations, 2) => Pick("Повторные быстрые клики не создают дубли в очереди.", "Repeated rapid clicks do not create queue duplicates."),
        (TutorialTopic.Donations, _) => Pick("Если сервер не подтвердил запуск, скрывать нечего — кнопка останется недоступной.", "Without a confirmed server start there is nothing to hide, so the button remains unavailable."),
        (TutorialTopic.DonationsSetup, _) => Pick("Авторизация выполняется только на официальном сайте DonationAlerts.", "Authorization takes place only on the official DonationAlerts site."),
        (TutorialTopic.Events, _) => Pick("События также получают отдельное оформление в чате и OBS.", "Events also receive dedicated cards in chat and OBS."),
        (TutorialTopic.Protection, _) => Pick("Обучение не применяет настройки и не очищает чат.", "The guide never applies settings or clears chat."),
        (TutorialTopic.Moments, _) => Pick("Моменты хранятся локально в профиле WitherChat.", "Moments are stored locally in the WitherChat profile."),
        (TutorialTopic.SmartChat, _) => Pick("Вернитесь к «Все», чтобы снова увидеть полную ленту.", "Return to All to see the complete feed again."),
        _ => Pick("Кнопка ? всегда возвращает эту подсказку.", "The ? button always reopens this guide.")
    };

    public string OnboardingTitle(int step) => step switch
    {
        0 => Pick("Добро пожаловать в WitherChat", "Welcome to WitherChat"),
        1 => Pick("Аккаунт и состояние подключения", "Account and connection status"),
        2 => Pick("Каналы всегда под рукой", "Your channels at a glance"),
        3 => Pick("Чат и закреплённые сообщения", "Chat and pinned messages"),
        4 => Pick("Сообщения без лишних движений", "Send messages effortlessly"),
        5 => Pick("Инструменты трансляции", "Streaming tools"),
        6 => Pick("Логи и история чата", "Chat logs and history"),
        7 => Pick("Чат в OBS без захвата окна", "Chat in OBS without window capture"),
        8 => Pick("Настройки под ваш стиль", "Settings that fit your style"),
        9 => Pick("Компактный режим", "Compact mode"),
        10 => Pick("Управление плагином OBS", "Manage the OBS plugin"),
        11 => Pick("Полный чат в доке OBS", "Full chat in an OBS dock"),
        _ => string.Empty
    };

    public string OnboardingDescription(int step) => step switch
    {
        0 => Pick(
            "Обучение познакомит вас с чатом Twitch и YouTube, модерацией, событиями, моментами, логами, донатами, компактным режимом и плагином OBS. Подробные подсказки открываются кнопкой ? в каждой сложной панели.",
            "This guide introduces Twitch and YouTube chat, moderation, events, moments, logs, donations, compact mode, and the OBS plugin. Use ? in every advanced panel for its detailed guide."),
        1 => Pick(
            "Верхняя панель показывает подключённый Twitch-аккаунт, активный канал, состояние API и чата, статус эфира и количество зрителей.",
            "The header shows your Twitch account, active channel, API and chat connection, live status, and viewer count."),
        2 => Pick(
            "Нажмите на профиль, чтобы открыть список каналов. Можно держать подключёнными до трёх каналов, быстро переключаться между ними и удалять добавленные для просмотра.",
            "Select the profile to open your channel list. Keep up to three channels connected, switch instantly, and remove channels added for viewing."),
        3 => Pick(
            "Здесь появляются сообщения Twitch и YouTube, значки и эмоты. ПКМ по сообщению открывает ответ, последние сообщения зрителя, сохранение момента и доступные действия модерации. Закреплённое сообщение остаётся над лентой.",
            "Twitch and YouTube messages, badges, and emotes appear here. Right-click for replies, recent viewer messages, saved moments, and available moderation actions. The pinned message stays above the feed."),
        4 => Pick(
            "Нижнее поле отправляет сообщения от вашего аккаунта. Если вы прокрутите чат вверх, автопрокрутка остановится; кнопка со стрелкой вернёт вас к новым сообщениям.",
            "The composer sends messages from your account. Scrolling up pauses auto-follow; the arrow button returns you to the latest messages."),
        5 => Pick(
            "Инструменты открывают события эфира, сохранённые моменты с заметками, защиту от спама, логи, донаты и модерацию. Умные фильтры выделяют вопросы, упоминания и платные события без удаления исходной ленты. Доступные действия зависят от аккаунта и прав.",
            "Tools open stream events, saved moments with notes, anti-spam protection, logs, donations, and moderation. Smart filters focus questions, mentions, and paid events without deleting the original feed. Available actions depend on your account and permissions."),
        6 => Pick(
            "При включённой записи WitherChat сохраняет сообщения по дням и каналам. В окне логов можно искать текст и пользователей, фильтровать роли, открывать папку хранения и экспортировать историю в TXT или JSONL.",
            "When logging is enabled, WitherChat stores messages by day and channel. The log viewer can search text and users, filter roles, open the storage folder, and export history as TXT or JSONL."),
        7 => Pick(
            "Включите OBS-оверлей и добавьте его URL в OBS как источник «Браузер». Здесь настраиваются тема сообщений, размер шрифта, значки, эмоуты, время, фон, прозрачность, выравнивание и исчезновение сообщений.",
            "Enable the OBS overlay and add its URL to OBS as a Browser source. Configure message theme, font size, badges, emotes, timestamps, background, opacity, alignment, and message fade-out here."),
        8 => Pick(
            "Разделы сгруппированы по задачам: программа, чат, журналы, OBS-оверлей, аккаунты Twitch, YouTube и DonationAlerts, поддержка и дополнительные параметры. Нажимать можно по всей строке раздела.",
            "Sections are grouped by task: app, chat, logs, OBS overlay, Twitch, YouTube and DonationAlerts accounts, support, and advanced options. The whole section row is clickable."),
        10 => Pick(
            "Статус плагина находится в Настройки → OBS-оверлей. Закройте OBS: здесь можно установить или обновить плагин, выбрать папку и удалить только файлы WitherChat с подтверждением. Профили, сцены и другие плагины OBS не меняются.",
            "Plugin status is in Settings → OBS overlay. Close OBS: install/update the plugin, choose a folder, or confirm removal of only WitherChat files. OBS profiles, scenes, and other plugins are unchanged."),
        11 => Pick(
            "Откройте OBS → Сервис → WitherChat: плагин сам запустит полноценный чат в доке. Отдельный EXE запускать не обязательно. В маленьком доке кнопки собраны в меню, а аккаунт показывается короткой строкой. Сообщения, модерация, логи и донаты остаются доступны.",
            "Open OBS → Tools → WitherChat: the plugin launches the full chat in a dock. No separate EXE launch is needed. A small dock groups buttons in its menu and shows account identity as a short line. Messages, moderation, logs, and donations remain available."),
        9 => Pick(
            "Кнопка в строке заголовка переключает компактный режим — удобно держать чат поверх игры или на втором мониторе. Положение окна сохраняется при переключении.",
            "The title-bar button toggles compact mode—ideal over a game or on a second monitor. The window stays in place when switching."),
        _ => string.Empty
    };

    public string OnboardingHint(int step) => step switch
    {
        0 => Pick(
            "Во время обучения окно можно перемещать за верхнюю панель. Повторный запуск находится в Настройки → Программа.",
            "You can move the window by its title bar during the tutorial. Replay it later from Settings → App."),
        1 => Pick(
            "Цветные точки помогают понять состояние одним взглядом.",
            "Colored indicators show connection health at a glance."),
        2 => Pick(
            "Ваш собственный канал отмечен отдельно и удаляется только при выходе из Twitch.",
            "Your own channel is marked separately and is removed only when you sign out of Twitch."),
        3 => Pick(
            "Левая кнопка не выделяет сообщения — используйте правую для действий.",
            "Left-click does not select messages—use right-click for actions."),
        4 => Pick(
            "Когда автопрокрутка приостановлена, счётчик на стрелке показывает количество новых сообщений.",
            "When auto-follow is paused, the arrow badge shows how many new messages arrived."),
        5 => Pick(
            "Модерация Twitch и YouTube появляется только после подтверждения прав. В сложных панелях кнопка ? открывает подробное мини-обучение.",
            "Twitch and YouTube moderation appears only after permissions are confirmed. Use the ? button in advanced panels for a detailed mini-guide."),
        6 => Pick(
            "Запись логов и формат файлов настраиваются в разделе Настройки → Логи чата.",
            "Logging and file formats are configured in Settings → Chat logs."),
        7 => Pick(
            "Кнопка тестового сообщения позволяет проверить оверлей до начала трансляции.",
            "Use the test-message button to verify the overlay before going live."),
        8 => Pick(
            "Изменения можно сохранить или отменить внизу окна.",
            "Save or cancel changes at the bottom of the window."),
        9 => Pick("В мини-режиме время и лишние служебные подписи скрыты. Действия доступны через ПКМ по сообщению.", "Mini mode hides timestamps and extra service labels. Right-click a message for actions."),
        10 => Pick("Установка и удаление — реальные действия. Само обучение их не выполняет.", "Install and remove are real actions. This guide never performs them."),
        11 => Pick(
            "Док нужен для управления чатом; оверлей «Браузер» — для показа сообщений в сцене. Это не одно и то же.",
            "Use a dock to work with chat and a Browser overlay to display messages in a scene. They are different."),
        _ => Pick(
            "Готово — теперь WitherChat настроен для повседневной работы.",
            "All set—WitherChat is ready for everyday use.")
    };
    public string DailyLogs => Pick("Дни", "Daily logs");
    public string LogSearch => Pick("Поиск в журнале", "Search log");
    public string NoChatLogs => Pick("Журналы чата пока отсутствуют", "There are no chat logs yet");
    public string LogMessages => Pick("Сообщения журнала", "Log messages");
    public string ExportTxt => Pick("Экспорт TXT", "Export TXT");
    public string ExportJsonl => Pick("Экспорт JSONL", "Export JSONL");
    public string DeleteLog => Pick("Удалить журнал", "Delete log");
    public string DeleteLogQuestion => Pick(
        "Удалить выбранный журнал без возможности восстановления?",
        "Delete the selected log permanently?");
    public string StreamDate => Pick("Дата стрима:", "Stream date:");
    public string Delete => Pick("Удалить", "Delete");
    public string Browse => Pick("Обзор…", "Browse…");
    public string Close => Pick("Закрыть", "Close");
    public string Filters => Pick("Фильтры", "Filters");
    public string SearchMessages => Pick("Поиск сообщений", "Search messages");
    public string FilterByUser => Pick("Пользователь", "User");
    public string AllRoles => Pick("Все роли", "All roles");
    public string BroadcasterRole => Pick("Стример", "Broadcaster");
    public string ModeratorRole => Pick("Модератор", "Moderator");
    public string SubscriberRole => Pick("Подписчик", "Subscriber");
    public string VipRole => Pick("VIP", "VIP");
    public string DecreaseFont => Pick("Уменьшить шрифт", "Decrease font size");
    public string IncreaseFont => Pick("Увеличить шрифт", "Increase font size");
    public string TimeShort => Pick("Время", "Time");
    public string ShowBadges => Pick("Показывать значки", "Show badges");
    public string TwitchEmotes => Pick("Эмоуты Twitch", "Twitch emotes");
    public string BttvEmotes => Pick("Эмоуты BetterTTV", "BetterTTV emotes");
    public string SevenTvEmotes => Pick("Эмоуты 7TV", "7TV emotes");
    public string ShowChannelPointRedemptions => Pick("Награды за баллы канала", "Channel point rewards");
    public string ChannelPointsMark => Pick("БАЛЛЫ", "POINTS");
    public string WindowControlsOnRight => Pick("Кнопки окна справа", "Window buttons on the right");
    public string WindowControlsPosition => Pick("Расположение кнопок окна", "Window button position");
    public string WindowControlsLeft => Pick("Слева", "Left");
    public string WindowControlsRight => Pick("Справа", "Right");
    public string WindowControlsStyle => Pick("Стиль кнопок окна", "Window button style");
    public string WindowControlsMac => "macOS";
    public string WindowControlsWindows => "Windows";
    public string AlwaysOnTopHelp => Pick(
        "Окно WitherChat будет оставаться поверх остальных окон.",
        "The WitherChat window will stay above other windows.");
    public string ReduceMotion => Pick("Уменьшить анимации", "Reduce motion");
    public string CompactMode => Pick("Компактный режим", "Compact mode");
    public string RestoreFullMode => Pick("Обычный режим", "Normal mode");
    public string Moderation => Pick("Модерация", "Moderation");
    public string TwitchModeration => Pick("Twitch", "Twitch");
    public string YouTubeModeration => Pick("YouTube", "YouTube");
    public string YouTubeBansCreatedHere => Pick(
        "Блокировки YouTube, выданные через WitherChat",
        "YouTube bans issued through WitherChat");
    public string YouTubeBansApiNote => Pick(
        "YouTube не предоставляет список всех блокировок через API. Здесь показаны активные блокировки, выданные в текущем запуске WitherChat.",
        "YouTube does not expose a complete ban list through the API. This list shows active bans issued during the current WitherChat session.");
    public string AutoMod => "AutoMod";
    public string Allow => Pick("Разрешить", "Allow");
    public string BannedUsers => Pick("Баны и тайм-ауты", "Bans and timeouts");
    public string UnbanRequests => Pick("Запросы на разбан", "Unban requests");
    public string BannedUsersShort => Pick("Баны", "Bans");
    public string UnbanRequestsShort => Pick("Запросы", "Requests");
    public string Refresh => Pick("Обновить", "Refresh");
    public string Loading => Pick("Загрузка…", "Loading…");
    public string LoadingChatImages => Pick("Загрузка изображений…", "Loading images…");
    public string NoModerationItems => Pick("Активных записей нет", "There are no active items");
    public string ModerationChannelRequired => Pick("Канал не выбран", "No channel selected");
    public string NoAutoModMessages => Pick("Нет удержанных сообщений AutoMod", "No messages held by AutoMod");
    public string NoBannedUsers => Pick("Нет активных банов и тайм-аутов", "No active bans or timeouts");
    public string NoPendingUnbanRequests => Pick("Нет ожидающих запросов на разбан", "No pending unban requests");
    public string NoApprovedUnbanRequests => Pick("Нет одобренных запросов на разбан", "No approved unban requests");
    public string NoDeniedUnbanRequests => Pick("Нет отклонённых запросов на разбан", "No denied unban requests");
    public string ModerationCacheRestored => Pick(
        "Показаны сохранённые данные; выполняется обновление с Twitch.",
        "Saved data is shown while Twitch is refreshed.");
    public string Unban => Pick("Снять наказание", "Unban");
    public string BanShort => Pick("Бан", "Ban");
    public string UnbanShort => Pick("Разбан", "Unban");
    public string UserLogin => Pick("Логин пользователя", "User login");
    public string Pending => Pick("Ожидают", "Pending");
    public string Approved => Pick("Одобрены", "Approved");
    public string Denied => Pick("Отклонены", "Denied");
    public string Approve => Pick("Одобрить", "Approve");
    public string Deny => Pick("Отклонить", "Deny");
    public string UnbanApproved => Pick("Запрос на разбан одобрен.", "Unban request approved.");
    public string UnbanDenied => Pick("Запрос на разбан отклонён.", "Unban request denied.");
    public string DeleteMessage => Pick("Удалить сообщение", "Delete message");
    public string MessageDeleted => Pick("Сообщение удалено", "Message deleted");
    public string UserTimedOut => Pick("Пользователь получил тайм-аут", "User was timed out");
    public string UserBanned => Pick("Пользователь заблокирован", "User banned");
    public string BanUser => Pick("Заблокировать пользователя", "Ban user");
    public string TimeoutTen => Pick("Тайм-аут на 10 минут", "Timeout for 10 minutes");
    public string CustomTimeout => Pick("Настраиваемый тайм-аут", "Custom timeout");
    public string RecentMessages => Pick("Последние сообщения", "Recent messages");
    public string NoRecentMessages => Pick(
        "Сообщения этого пользователя в текущей сессии отсутствуют.",
        "This user has no messages in the current session.");
    public string Back => Pick("Назад", "Back");
    public string ModerationAction => Pick("Действие модерации", "Moderation action");
    public string ModerationReason => Pick("Причина (необязательно)", "Reason (optional)");
    public string TimeoutDuration => Pick("Длительность тайм-аута", "Timeout duration");
    public string PermanentBan => Pick("Навсегда", "Permanent");
    public string OneMinute => Pick("1 мин", "1 min");
    public string TenMinutes => Pick("10 мин", "10 min");
    public string OneHour => Pick("1 час", "1 hour");
    public string OneDay => Pick("1 день", "1 day");
    public string ConfirmBan => Pick("Заблокировать", "Ban user");
    public string ConfirmTimeout => Pick("Выдать тайм-аут", "Apply timeout");
    public string Cancel => Pick("Отмена", "Cancel");
    public string Save => Pick("Сохранить", "Save");
    public string RemovePunishment => Pick("Снять наказание", "Remove punishment");
    public string CopyUsername => Pick("Копировать имя пользователя", "Copy username");
    public string CopyMessage => Pick("Копировать сообщение", "Copy message");
    public string OpenOnTwitch => Pick("Открыть на Twitch", "Open on Twitch");
    public string OpenOnYouTube => Pick("Открыть на YouTube", "Open on YouTube");
    public string ModerationActionComplete => Pick("Действие модерации выполнено.", "Moderation action completed.");
    public string AutoModAllowed => Pick("Сообщение AutoMod разрешено.", "AutoMod message allowed.");
    public string AutoModDenied => Pick("Сообщение AutoMod отклонено.", "AutoMod message denied.");
    public string SharedChatParticipants(int count) => Pick($"Общий чат · {count}", $"Shared Chat · {count}");
    public string ModerationActionFailed => Pick("Не удалось выполнить действие модерации: ", "Moderation action failed: ");
    public string ModerationPanelFailed(string details) => ModerationActionFailed + details;
    public string LogOpenFailed => Pick(
        "Не удалось открыть папку журналов.",
        "Could not open the log directory.");
    public string ChatLogWriteFailed => Pick(
        "Запись логов приостановлена: проверьте доступность выбранной папки.",
        "Chat log writing is paused: check that the selected folder is available.");
    public string ChatLogWriteRecovered => Pick(
        "Запись логов восстановлена.",
        "Chat log writing has recovered.");
    public string ChatLogQueueOverflow(long count) => Pick(
        $"Логи не успевают записываться: пропущено сообщений — {count}.",
        $"Chat logging cannot keep up: {count} messages were skipped.");
    public string ClearMessages => Pick("Очистить сообщения", "Clear messages");
    public string Reconnect => Pick("Переподключить чат", "Reconnect chat");
    public string MoreTools => Pick("Ещё инструменты", "More tools");
    public string Settings => Pick("Настройки", "Settings");
    public string SettingsIntro => Pick(
        "Разделы сгруппированы для стрима: программа, чат, логи чата, OBS-оверлей, аккаунты Twitch, YouTube и DonationAlerts, поддержка проекта и дополнительные параметры.",
        "Settings are grouped for streaming: app, chat, chat logs, OBS overlay, Twitch, YouTube and DonationAlerts accounts, project support, and advanced options.");
    public string Pinned => Pick("Закреплено", "Pinned");
    public string PinnedMessage => Pick("Закреплённое сообщение", "Pinned message");
    public string PinnedMessagePreview => Pick(
        "Так выглядит закреплённое сообщение в чате",
        "This is how a pinned chat message appears");
    public string PinnedMark => Pick("ЗАКР", "PIN");
    public string LatestMessages => Pick("К последним сообщениям", "Jump to latest messages");
    public string HideHeader => Pick("Скрыть верхнюю панель", "Hide header");
    public string ShowHeader => Pick("Показать верхнюю панель", "Show header");
    public string ComposerPlaceholder => Pick(
        "Подключите аккаунт, чтобы отправлять сообщения",
        "Connect an account to send messages");
    public string ChatEmptyTitle => Pick(
        "Здесь будут появляться сообщения чата",
        "Chat messages will appear here");
    public string SignInHelp => Pick(
        "Войдите через официальную страницу Twitch в браузере. После авторизации приложение подключится к чату выбранного канала.",
        "Sign in on the official Twitch login page in your browser. After authorization, the app will connect to the selected channel's chat.");
    public string ReadOnlyComposerText => Pick(
        "Войдите через Twitch, чтобы читать чат и отправлять сообщения",
        "Sign in with Twitch to read chat and send messages");
    public string YouTubeReadOnlyComposerText => Pick(
        "YouTube-чат подключён. Для отправки сообщений подключите Twitch; модерация YouTube доступна по ПКМ.",
        "YouTube chat is connected. Connect Twitch to send messages; YouTube moderation is available from the context menu.");
    public string Send => Pick("Отправить", "Send");
    public string Program => Pick("Программа", "Program");
    public string Theme => Pick("Тема", "Theme");
    public string UiFontFamily => Pick("Шрифт интерфейса и чата", "Interface and chat font");
    public string DarkTheme => Pick("Тёмная", "Dark");
    public string LightTheme => Pick("Светлая", "Light");
    public string SystemTheme => Pick("Системная", "System");
    public string FontSize => Pick("Размер шрифта", "Font size");
    public string AlwaysOnTop => Pick("Поверх всех окон", "Always on top");
    public string ToastNotifications => Pick("Визуальные уведомления", "Visual notifications");
    public string SoundsDisabled => Pick("Звуки отключены", "Sounds disabled");
    public string SoundsHelp => Pick(
        "WitherChat не воспроизводит системные звуки.",
        "WitherChat does not play system sounds.");
    public string StillRunningTitle => Pick("WitherChat продолжает работать", "WitherChat is still running");
    public string StillRunningMessage => Pick(
        "Приложение свёрнуто в системный трей.",
        "The application was minimized to the system tray.");
    public string AlreadyRunning => Pick(
        "WitherChat уже запущен. Можно сразу показать его окно.",
        "WitherChat is already running. You can show its window now.");
    public string ShowChatWindow => Pick("Показать окно чата", "Show chat window");
    public string ShowChatWindowFailed => Pick(
        "Не удалось связаться с запущенным WitherChat. Откройте его из области уведомлений.",
        "Could not contact the running WitherChat. Open it from the notification area.");
    public string CloseToTray => Pick("Закрывать окно в трей", "Close window to tray");
    public string Chat => Pick("Чат", "Chat");
    public string MessageLimit => Pick("Лимит сообщений", "Message limit");
    public string ViewerRefreshInterval => Pick(
        "Обновление зрителей, секунд",
        "Viewer refresh interval, seconds");
    public string Language => Pick("Язык", "Language");
    public string RussianLanguage => Pick("Русский", "Russian");
    public string EnglishLanguage => Pick("Английский", "English");
    public string Account => Pick("Аккаунты", "Accounts");
    public string ConnectAccount => Pick("Подключить аккаунт", "Connect account");
    public string CancelSignIn => Pick("Отменить вход", "Cancel sign-in");
    public string SignOut => Pick("Выйти", "Sign out");
    public string OAuthSettings => Pick("Настройки Twitch OAuth", "Twitch OAuth settings");
    public string OAuthSettingsHelp => Pick(
        "Изменяйте эти параметры только для собственного приложения Twitch.",
        "Change these values only when using your own Twitch application.");
    public string UseCustomClientId => Pick("Использовать свой Twitch Client ID", "Use custom Twitch Client ID");
    public string TwitchClientId => "Twitch Client ID";
    public string OAuthRedirectUri => "OAuth Redirect URI";
    public string System => Pick("Система", "System");
    public string Platform => Pick("Платформа", "Platform");
    public string OpenLogFolder => Pick("Открыть папку журналов", "Open log directory");
    public string ChatLogging => Pick("Запись журналов чата", "Chat logging");
    public string EnableChatLogging => Pick("Сохранять сообщения в журнал", "Save messages to chat logs");
    public string SaveChatLogTxt => Pick("Сохранять TXT", "Save TXT");
    public string LogChatBadges => Pick("Сохранять значки", "Log badges");
    public string LogChannelPointRedemptions => Pick("Сохранять использование баллов в истории", "Save Channel Points redemptions to chat history");
    public string ChatLogsFolder => Pick("Папка журналов", "Log folder");
    public string LogViewerLimit => Pick("Лимит сообщений в просмотрщике", "Log viewer message limit");
    public string ObsOverlay => Pick("OBS-оверлей", "OBS overlay");
    public string EnableObsOverlay => Pick("Включить оверлей", "Enable overlay");
    public string OverlayUrl => Pick("URL оверлея", "Overlay URL");
    public string CopyUrl => Pick("Скопировать URL", "Copy URL");
    public string TestOverlayMessage => Pick("Тестовое сообщение", "Test overlay message");
    public string OverlayTestText => Pick("Тестовое сообщение WitherChat", "WitherChat test message");
    public string OverlayTestSent => Pick("Тестовое сообщение отправлено в OBS-оверлей.", "Test message sent to the OBS overlay.");
    public string OverlayDisabled => Pick("Сначала включите OBS-оверлей.", "Enable the OBS overlay first.");
    public string OverlayCopied => Pick("URL оверлея скопирован", "Overlay URL copied");
    public string ClipboardUnavailable => Pick(
        "Не удалось скопировать: буфер обмена временно недоступен.",
        "Could not copy: the clipboard is temporarily unavailable.");
    public string OverlayPort => Pick("Порт оверлея", "Overlay port");
    public string OverlayPortRange => Pick("Допустимо: 1024–65535", "Allowed: 1024–65535");
    public string OverlayMessages => Pick("Максимум сообщений в оверлее", "Max messages on overlay");
    public string OverlayMessagesRange => Pick("Допустимо: 1–100", "Allowed: 1–100");
    public string OverlayFontSize => Pick("Размер шрифта", "Font size");
    public string OverlayFontSizeRange => Pick("Допустимо: 10–72", "Allowed: 10–72");
    public string OverlayShowTime => Pick("Показывать время", "Show timestamps");
    public string OverlayShowBadges => Pick("Показывать значки", "Show badges");
    public string OverlayShowEmotes => Pick("Показывать эмоуты", "Show emotes");
    public string OverlayFade => Pick("Исчезновение сообщений, секунд", "Message fade out seconds");
    public string OverlayFadeHint => Pick(
        "Допустимо: 0–600 · 0 = не исчезают",
        "Allowed: 0–600 · 0 = messages stay");
    public string OverlayTextShadow => Pick("Тень текста", "Text shadow");
    public string OverlayTextOutline => Pick("Обводка текста", "Text outline");
    public string OverlayDarkBackground => Pick("Тёмная подложка сообщений", "Dark message background");
    public string OverlayOpacity => Pick("Непрозрачность фона", "Background opacity");
    public string OverlayAlignment => Pick("Выравнивание", "Alignment");
    public string AlignLeft => Pick("Слева", "Left");
    public string AlignCenter => Pick("По центру", "Center");
    public string AlignRight => Pick("Справа", "Right");
    public string MessageVisualTheme => Pick("Тема сообщений OBS-оверлея", "OBS overlay message theme");
    public string MessageThemeDefault => Pick("Обычная", "Default");
    public string MessageThemeTornBlack => Pick("Рваная чёрная", "Torn black");
    public string MessageVisualThemeHelp => Pick(
        "Оформление применяется только к сообщениям OBS-оверлея. Чат в приложении остаётся стандартным.",
        "The style applies only to OBS overlay messages. In-app chat keeps its standard appearance.");
    public string ExitWitherChat => Pick("Завершить WitherChat", "Exit WitherChat");
    public string OpenWitherChat => Pick("Открыть WitherChat", "Open WitherChat");
    public string RestartWitherChat => Pick("Перезапустить WitherChat", "Restart WitherChat");
    public string TrayChannels => Pick("Переключить чат", "Switch chat");
    public string Exit => Pick("Завершить", "Exit");
    public string CloseSettings => Pick("Закрыть настройки", "Close settings");
    public string SignIn => Pick("Войти", "Sign in");
    public string Reply => Pick("Ответ", "Reply");
    public string Collapse => Pick("Свернуть", "Collapse");
    public string Expand => Pick("Развернуть", "Expand");
    public string Version => Pick("версия", "version");
    public string InitialStatus => Pick(
        "Введите канал и нажмите «Подключить».",
        "Enter a channel and select Connect.");
    public string SessionTemporary => Pick(
        "Сессия Twitch сохраняется безопасно на этом устройстве.",
        "The Twitch session is stored securely on this device.");
    public string SecureStorageUnavailable => Pick(
        "Системное защищённое хранилище недоступно. Сессия Twitch сохранена только до закрытия приложения.",
        "Secure system storage is unavailable. The Twitch session is kept only until the application exits.");
    public string SecureConnection => Pick(
        "Устанавливаем защищённое соединение с Twitch IRC...",
        "Establishing a secure Twitch IRC connection...");
    public string OpeningTwitchLogin => Pick(
        "Открываем безопасный вход через Twitch...",
        "Opening secure Twitch sign-in...");
    public string ConfirmSignIn => Pick(
        "Подтвердите вход в открывшемся окне Twitch.",
        "Confirm the sign-in in the Twitch browser window.");
    public string AccountConnected => Pick(
        "Аккаунт подключён. Сессия сохранена безопасно.",
        "Account connected. The session is stored securely.");
    public string SignInCanceled => Pick("Вход отменён.", "Sign-in canceled.");
    public string AccountDisconnected => Pick("Аккаунт отключён.", "Account disconnected.");
    public string MessageSent => Pick("Сообщение отправлено.", "Message sent.");
    public string SessionValidated => Pick("Сессия Twitch проверена.", "Twitch session validated.");
    public string SessionValidationDeferred(string details) => Pick(
        "Проверка сессии временно недоступна. Повтор через минуту: ",
        "Session validation is temporarily unavailable. Retrying in one minute: ") + details;
    public string ChatConnected => Pick("Чат подключён", "Chat connected");
    public string Connecting => Pick("Подключение", "Connecting");
    public string Reconnecting => Pick("Переподключение", "Reconnecting");
    public string ConnectionError => Pick("Ошибка подключения", "Connection error");
    public string ChatDisconnected => Pick("Чат отключён", "Chat disconnected");
    public string Live => Pick("В эфире", "Live");
    public string Offline => Pick("Не в эфире", "Offline");
    public string StreamStatusUnavailable => Pick("Эфир недоступен", "Stream unavailable");

    public string ViewerCount(int count)
    {
        var formatted = count.ToString(
            "N0",
            global::System.Globalization.CultureInfo.GetCultureInfo(_english ? "en-US" : "ru-RU"));
        if (_english)
        {
            return formatted + (count == 1 ? " viewer" : " viewers");
        }

        var lastTwoDigits = count % 100;
        var lastDigit = count % 10;
        var suffix = lastTwoDigits is >= 11 and <= 14
            ? " зрителей"
            : lastDigit switch
            {
                1 => " зритель",
                2 or 3 or 4 => " зрителя",
                _ => " зрителей"
            };
        return formatted + suffix;
    }

    public string Minutes(int count) => Pick($"{count} мин", $"{count} min");

    public string ConnectedTo(string channel) =>
        Pick("Подключено к #", "Connected to #") + channel;

    public string ConnectingTo(string channel) =>
        Pick("Подключаемся к #", "Connecting to #") + channel;

    public string ConnectionLost => Pick(
        "Соединение потеряно, восстанавливаем...",
        "Connection lost, reconnecting...");

    public string CouldNotConnect => Pick("Не удалось подключиться.", "Could not connect.");
    public string SignInFailed(string details) => Pick("Не удалось войти: ", "Sign-in failed: ") + details;
    public string MessageNotSent(string details) => Pick("Сообщение не отправлено: ", "Message was not sent: ") + details;
    public string SessionEnded(string details) => Pick("Сессия Twitch завершена: ", "Twitch session ended: ") + details;
    public string BadgesUnavailable(string details) => Pick(
        "Значки Twitch временно недоступны: ",
        "Twitch badges are temporarily unavailable: ") + details;
    public string EmotesUnavailable(string details) => Pick(
        "Дополнительные эмоуты временно недоступны: ",
        "Additional emotes are temporarily unavailable: ") + details;
    public string SettingsSaveFailed(string details) => Pick(
        "Не удалось сохранить настройки: ",
        "Could not save settings: ") + details;
    public string OverlayStartFailed(string details) => Pick(
        "Не удалось запустить OBS-оверлей: ",
        "Could not start the OBS overlay: ") + details;
    public string BrowserOpenFailed(string? details = null) => string.IsNullOrEmpty(details)
        ? Pick("Не удалось открыть браузер. Откройте ссылку вручную.", "Could not open the browser. Open the link manually.")
        : Pick("Не удалось открыть браузер: ", "Could not open the browser: ") + details;
}
