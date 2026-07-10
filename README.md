# WitherChat

Version: `0.0.1`

Лёгкое Windows-приложение на .NET 8 + WPF для работы с live-чатом Twitch-стрима.

Приложение подключается к Twitch через официальный OAuth в системном браузере, читает чат через EventSub WebSocket, позволяет отправлять сообщения от авторизованного аккаунта и выполнять базовую модерацию: ban и timeout.

## Возможности

- вход через официальный Twitch OAuth login/authorize page;
- хранение access token локально через DPAPI, без plain text в UI и логах;
- чтение live-чата через EventSub WebSocket `channel.chat.message`;
- автоматическое подключение к чату авторизованного канала;
- отправка сообщений через Twitch Send Chat Message API;
- ban и timeout через Twitch Moderation API;
- тёмный NeonDark интерфейс;
- поиск, фильтр по пользователю, очистка локального списка;
- ограничение сообщений в памяти;
- Twitch badges, Twitch emotes, BTTV и 7TV как необязательные визуальные улучшения;
- OBS Browser Source overlay через localhost: `http://localhost:17655/overlay/chat`;
- приложение не использует системные звуки Windows для ошибок и предупреждений.

## Требования

- Windows 10/11;
- .NET 8 SDK;
- Visual Studio 2022 или Rider с поддержкой .NET 8 и WPF.

## Сборка

Откройте файл:

```text
TwitchChatMvp.sln
```

или соберите из терминала:

```powershell
dotnet restore TwitchChatMvp.sln
dotnet build TwitchChatMvp.sln --no-restore
```

Для Release-сборки:

```powershell
dotnet build TwitchChatMvp.sln -c Release --no-restore
```

## Настройка Twitch Developer Console

1. Откройте [Twitch Developer Console](https://dev.twitch.tv/console/apps).
2. Создайте новое приложение.
3. Укажите название, например `WitherChat`.
4. В поле OAuth Redirect URLs добавьте:

```text
http://localhost:17654/
```

5. Тип клиента выбирайте как public/native, если такой вариант доступен.
6. Скопируйте `Client ID`.
7. `Client Secret` для desktop-приложения не нужен и в приложении не используется.

## Scopes

Приложение запрашивает только базовые права, нужные для MVP:

```text
user:read:chat
user:write:chat
moderator:manage:banned_users
```

Если Twitch показывает ошибку прав, выйдите из приложения через `Logout / Disconnect Twitch` и войдите заново, чтобы выдать актуальные scopes.

## Первый запуск

1. Запустите приложение.
2. Откройте настройки.
3. Вставьте `Twitch Client ID`.
4. Проверьте Redirect URI:

```text
http://localhost:17654/
```

5. Сохраните настройки.
6. Нажмите `Войти через Twitch`.
7. В браузере откроется официальный Twitch OAuth.
8. Войдите в Twitch и нажмите `Authorize`.
9. После редиректа на `localhost` приложение автоматически получит token, проверит его через Twitch `/oauth2/validate`, получит профиль через Helix Users API и подключит чат вашего канала.

Для Implicit Flow refresh token не выдаётся. Когда access token истечёт, приложение попросит заново подключить Twitch.

## OBS Chat Overlay

1. Откройте `Настройки` → `OBS Overlay`.
2. Включите `Enable overlay`.
3. Проверьте порт, по умолчанию используется:

```text
17655
```

4. Скопируйте URL:

```text
http://localhost:17655/overlay/chat
```

5. В OBS добавьте `Browser Source`.
6. Вставьте URL overlay.
7. В настройках можно изменить размер шрифта, лимит строк, timestamps, badges, emotes, fade out, тень текста, прозрачность фона и выравнивание.
8. Нажмите `Тестовое сообщение` / `Test overlay message`, чтобы проверить Browser Source без ожидания сообщения в Twitch-чате.

Overlay использует push-события через SSE endpoint `/overlay/events`, без polling каждую секунду. Порт overlay отдельный от OAuth redirect port `17654`: overlay остаётся на `17655`, OAuth по умолчанию использует `http://localhost:17654/`.

## Как проверить работу

1. После входа в шапке должен отображаться ваш аккаунт и аватар.
2. Статус чата должен перейти в `connected`.
3. Напишите сообщение в нижнее поле и нажмите Enter или кнопку отправки.
4. Сообщение должно уйти в ваш Twitch-чат и появиться в списке.
5. Откройте контекстное меню сообщения правой кнопкой мыши.
6. Проверьте `Timeout 10 минут` на тестовом аккаунте или модераторском тестовом пользователе.
7. Permanent ban требует подтверждения.

Нельзя забанить самого себя. Если Twitch возвращает 403, проверьте, что аккаунт является broadcaster или модератором канала и что scope `moderator:manage:banned_users` выдан.

## Частые ошибки

### Чат не подключается

- проверьте, что `Client ID` указан;
- проверьте, что Redirect URI в приложении и Twitch Developer Console совпадает точно: `http://localhost:17654/`;
- если вы меняете OAuth Redirect URI или порт в настройках приложения, добавьте такой же Redirect URL в Twitch Developer Console;
- выйдите и войдите заново, если не хватает scope `user:read:chat`;
- откройте лог приложения в `%LOCALAPPDATA%\TwitchChatMvp\logs\app.log`.

### Send недоступен

Кнопка отправки активна только если:

- Twitch-аккаунт подключён;
- EventSub chat подключён;
- поле сообщения не пустое.

### 401 Unauthorized

Access token истёк или отозван. Для Implicit Flow refresh token нет, поэтому нужно заново нажать `Войти через Twitch`.

### 403 Forbidden

Обычно причина одна из трёх:

- не выдан нужный scope;
- аккаунт не broadcaster и не модератор нужного канала;
- выполняется действие, которое Twitch запрещает для текущего пользователя.

### 429 Rate limit

Twitch ограничил частоту запросов. Подождите и повторите действие позже.

### OBS overlay не открывается

- проверьте, что `Enable overlay` включён;
- убедитесь, что порт `17655` не занят другой программой;
- если порт занят, выберите другой порт в настройках и скопируйте новый URL;
- в OBS используйте именно `/overlay/chat`, например `http://localhost:17655/overlay/chat`.

## Безопасность

- приложение не просит Twitch-логин и пароль внутри окна;
- вход выполняется только на официальной странице Twitch в системном браузере;
- `Client Secret` не используется;
- access token не выводится в UI и не пишется в лог;
- OAuth `state` проверяется при callback;
- token хранится локально через Windows DPAPI.
#   W i t h e r C h a t  
 