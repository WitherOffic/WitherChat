# Платформы и пакеты WitherChat

`WitherChat.sln` собирает общий клиент 0.5.1A для Windows, Linux и macOS.
Исходный WPF-релиз 0.3.3 находится в `WitherChat.Windows.sln` и остаётся
контрольной реализацией до достижения полной функциональной совместимости.
Для сборки закреплена feature-band .NET SDK 9.0.3xx (минимум 9.0.316, в CI —
security-patch 9.0.317); приложения по-прежнему нацелены на `net8.0`, а
релизные пакеты публикуются самодостаточными с .NET Runtime 8.0.30.

## Целевые платформы

```text
Windows: win-x64, win-arm64
Linux:   linux-x64, linux-arm64
macOS:   osx-x64, osx-arm64
```

- Windows-пакеты предназначены для 64-битных Windows 10/11.
- Linux-пакеты предназначены для обычных glibc-дистрибутивов с графической
  сессией; отдельной musl-сборки сейчас нет.
- В `.app` задан минимум macOS 13.0.

Self-contained означает, что отдельно ставить .NET не требуется. Системные
библиотеки ОС, графическая сессия и сетевой доступ всё равно необходимы.

Все платформы используют один `MainWindow.axaml`, одну тему Avalonia и
встроенный Inter. Платформенный слой отвечает только за возможности ОС,
уведомления, трей и runtime identifiers.

## Каталоги артефактов

```text
artifacts/WitherChat/
  windows/current/<version>/
  windows/history/<version>/
  linux/current/<version>/
  linux/history/<version>/
  macos/current/<version>/
  macos/history/<version>/
```

`current` содержит собираемые версии, а `history` — сохранённые архивные
версии. Скрипт не удаляет и не перемещает чужие версии автоматически. Повторная
сборка того же номера полностью заменяет только каталог этого номера, чтобы в
пакет не попали устаревшие файлы от предыдущего запуска.

Windows получает каталоги и ZIP для x64/ARM64, а также single-file EXE. Для
публичной раздачи single-file варианта предназначен ZIP: в нём рядом с EXE
лежат обязательные уведомления о лицензиях.
Linux получает portable-каталоги и tar.gz с PNG-иконкой, `install.sh` и
`uninstall.sh`. Установщик создаёт корректный `.desktop` с абсолютным путём в
`XDG_DATA_HOME` (обычно `~/.local/share`) и не требует root.
macOS получает `.app`-пакеты для Intel и Apple Silicon внутри tar.gz.
Каждый пакет содержит номер сборки, статус и уведомления о лицензиях сторонних
компонентов; отладочные символы и пользовательские сессии в пакет не входят.

## Публикация

```powershell
./build/Publish-WitherChat.ps1 -Platform all -Version 0.5.1A
```

Допустимые значения `-Platform`: `windows`, `linux`, `macos`, `all`.
Скрипт работает в PowerShell 7 на каждой поддерживаемой ОС. Unix-архивы
создаются встроенной библиотекой .NET: права запуска проверяются после записи,
а Git, `tar` и `gzip` отдельно устанавливать не нужно.

После каждой упаковки внутри каталога версии создаётся `PACKAGE_LINKS.md`.
В нём загрузки чётко разделены по ОС и архитектуре; консоль также печатает
отдельный блок ссылок для Windows, Linux и macOS.

## Контроль качества

```powershell
dotnet restore WitherChat.sln -p:RuntimeFrameworkVersion=8.0.30
dotnet build WitherChat.sln -c Release --no-restore -warnaserror
dotnet run --project tests/WitherChat.Core.SmokeTests -c Release --no-build --no-restore
dotnet run --project tests/WitherChat.Desktop.SmokeTests -c Release --no-build --no-restore
dotnet test tests/WitherChat.Avalonia.HeadlessTests -c Release --no-build --no-restore
```

Workflow `.github/workflows/cross-platform.yml` повторяет эти проверки на
Windows, Ubuntu и macOS и публикует пакеты как CI-артефакты.

Кроссплатформенные пакеты считаются предварительными, пока не пройдут живой
OAuth/UI/OBS/DonationAlerts-прогон на каждой ОС. Публичный Windows-релиз
дополнительно требует Authenticode-подпись. Публичный macOS-релиз требует
Developer ID signing и notarization; создаваемая CI ad-hoc подпись предназначена
только для внутренней проверки структуры `.app`. Linux-пакет не подписан и до
публичного релиза должен публиковаться вместе с SHA-256 из каталога версии.

.NET 8 остаётся поддерживаемым до 10 ноября 2026 года. Если WitherChat будет
поддерживаться после этой даты, целевую среду и SDK необходимо заранее перенести
на поддерживаемую LTS-версию и повторить весь кроссплатформенный прогон.
