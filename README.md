# WitherChat

WitherChat — настольное приложение для чтения и управления Twitch-чатом на Windows.

Текущая версия: **0.3.2**. Публичный релиз собирается для Windows x64 как автономный single-file EXE на .NET 8 и не требует отдельной установки .NET Runtime.

## Возможности

- вход через официальный Twitch OAuth и безопасное хранение сессии;
- чтение и отправка сообщений, автоматическое переподключение;
- до трёх одновременно подключённых каналов;
- Twitch badges и emotes, глобальные и канальные 7TV/BetterTTV emotes;
- устойчивая виртуализация и плавная прокрутка больших чатов;
- корректный перенос и сворачивание длинных сообщений;
- отдельное отображение закреплённых сообщений;
- поиск по сообщениям и фильтр по пользователю;
- журналирование чата и встроенный просмотрщик логов;
- инструменты модерации;
- локальный OBS Browser Source с визуальными темами сообщений;
- русский и английский интерфейс;
- тёмная и светлая темы, выбор шрифта;
- компактный режим и режим «Поверх всех окон»;
- работа в системном трее с командами открытия, перезапуска и завершения;
- настраиваемое положение и оформление кнопок окна.

## Сборка

Требуется .NET 8 SDK или новее.

```powershell
dotnet restore WitherChat.sln
dotnet build WitherChat.sln --no-restore
dotnet build WitherChat.sln -c Release --no-restore
```

Автономная публикация Windows x64:

```powershell
dotnet restore src/WitherChat/WitherChat.csproj -r win-x64
dotnet publish src/WitherChat/WitherChat.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

## Структура

```text
src/WitherChat/
  Assets/       иконка, шрифт и изображения тем
  Behaviors/    поведение прокрутки
  Controls/     элементы отображения чата
  Models/       модели данных и настроек
  Services/     Twitch, OBS, хранение, логи и системный трей
  ViewModels/   состояние приложения и обработка сообщений
  Views/        окна и панели настроек
```

Локальные настройки, защищённая сессия и журналы не входят в репозиторий. Собственные метаданные приложения, продукта и издателя используют только название **WitherChat**.
