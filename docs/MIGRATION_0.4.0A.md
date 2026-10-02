# WitherChat 0.4.0A для Windows, Linux и macOS

## База

Единая кроссплатформенная версия называется `0.4.0A`. Перенос основан на
публичном релизе `v0.3.3` и его коммите `e129bbe`.
Стабильный WPF-клиент не переписывается на месте: он остаётся в `src/WitherChat`
и собирается через `WitherChat.Windows.sln`. Новая реализация разделена на:

- `WitherChat.Core` — Twitch, EventSub, IRC, OAuth, логи, OBS и хранение данных;
- `WitherChat.Desktop` — единый интерфейс Avalonia;
- `tests/*SmokeTests` — проверки логики и UI-моделей;
- `tests/WitherChat.Avalonia.HeadlessTests` — рендер основных состояний и
  сравнение с PNG-эталонами.

Такое разделение позволяет переносить функции по одной и сравнивать их со
стабильной Windows-версией.

## Уже перенесено

- Twitch OAuth и восстановление сессии;
- IRC и EventSub, отправка сообщений и переподключение;
- Twitch/7TV/BetterTTV emotes и badges;
- несколько каналов, фильтры и закреплённые сообщения;
- общая модерация Twitch и YouTube, Twitch AutoMod и unban requests;
- журналирование и просмотр логов;
- OBS Browser Source;
- локализация, темы, настройки окна и системный трей;
- интервал обновления количества зрителей из 0.3.3;
- параметры тени, обводки и фона OBS-оверлея из 0.3.3.

## Платформенные границы

Общий код не зависит от WPF или WinForms. Windows-вызовы изолированы:

- DPAPI используется только на Windows;
- уведомление через `shell32.dll` находится только в desktop-платформенном слое;
- Linux и macOS используют общий Avalonia tray API;
- пути данных выбираются через `%LOCALAPPDATA%`, `~/Library/Application Support`
  или `$XDG_CONFIG_HOME`.

На macOS Twitch-сессия хранится в системном Keychain, на Linux — в Secret
Service через `secret-tool`. Windows использует DPAPI и переносит существующую
сессию 0.3.3. Если системное защищённое хранилище недоступно, токен остаётся
только в памяти до закрытия приложения; UI явно сообщает об этом. Старый
portable AES-файл читается только для однократной миграции.

## Матрица артефактов

| ОС | Runtime identifiers | Формат |
|---|---|---|
| Windows | `win-x64`, `win-arm64` | каталог, ZIP, single-file EXE |
| Linux | `linux-x64`, `linux-arm64` | каталог, `.tar.gz`, `.desktop`, PNG |
| macOS | `osx-x64`, `osx-arm64` | `.app` внутри `.tar.gz` |

macOS-пакеты пока не подписаны и не notarized. Для публичного распространения
нужны Apple Developer ID, подпись, hardened runtime и notarization. Linux-пакет
пока portable; AppImage/Flatpak/Deb/RPM можно добавить после проверки на
целевых дистрибутивах.

## Проверка

```powershell
dotnet restore WitherChat.sln
dotnet build WitherChat.sln -c Release --no-restore
dotnet run --project tests/WitherChat.Core.SmokeTests -c Release --no-build --no-restore
dotnet run --project tests/WitherChat.Desktop.SmokeTests -c Release --no-build --no-restore
dotnet test tests/WitherChat.Avalonia.HeadlessTests -c Release --no-build --no-restore
```

Полная упаковка:

```powershell
./build/Publish-WitherChat.ps1 -Platform all -Version 0.4.0A
```

GitHub Actions выполняет сборку и smoke-тесты нативно на Windows, Ubuntu и
macOS, а затем сохраняет платформенные пакеты как workflow artifacts.

## Перед публичным кроссплатформенным релизом

1. Прогнать OAuth, отправку сообщений, EventSub и OBS на реальных Linux/macOS.
2. Проверить tray в GNOME/KDE и поведение меню macOS.
3. Сравнить визуальные темы сообщений с WPF 0.3.3 и закрыть отличия.
4. Подписать Windows/macOS-пакеты и добавить notarization.
5. Выбрать форматы установки для Linux и настроить автообновление.
