// SPDX-License-Identifier: GPL-2.0-or-later
#include <obs-module.h>
#include <obs-frontend-api.h>
#include <util/base.h>
#include <QAction>
#include <QApplication>
#include <QFileDialog>
#include <QEventLoop>
#include <QElapsedTimer>
#include <QFileInfo>
#include <QFocusEvent>
#include <QDockWidget>
#include <QTabWidget>
#include <functional>
#include <QLabel>
#include <QPointer>
#include <QProcess>
#include <QPushButton>
#include <QSettings>
#include <QShowEvent>
#include <QStackedLayout>
#include <QThread>
#include <QTimer>
#include <QVBoxLayout>
#include <QWidget>
#include <windows.h>
#include <array>
#include <memory>
#include "worker-support.hpp"

OBS_DECLARE_MODULE()
OBS_MODULE_AUTHOR("WitherChat")
MODULE_EXPORT const char *obs_module_name(void) { return "WitherChat Dock"; }
MODULE_EXPORT const char *obs_module_description(void) { return "Full WitherChat UI in OBS; Windows prototype."; }

namespace {
constexpr auto dockId = "witherchat-dock-v1";
constexpr auto pipePath = L"\\\\.\\pipe\\WitherChat-ObsDock-v1-9E69A68D-87D9-47F1-99AE-F35AA2DCC3EA";
decltype(&obs_frontend_add_dock_by_id) addDock;
decltype(&obs_frontend_remove_dock) removeDock;
decltype(&obs_frontend_add_tools_menu_qaction) addMenu;
decltype(&obs_frontend_get_main_window) getMainWindow;
decltype(&obs_frontend_add_event_callback) addEvent;
decltype(&obs_frontend_remove_event_callback) removeEvent;
decltype(&obs_find_module_file) findFile;
decltype(&bfree) freeObs;
QString bundledExe;
QPointer<QAction> menuAction;
decltype(&blog) logObs;
void trace(const char *text) { if (logObs) logObs(LOG_INFO, "[WitherChat Dock] %s", text); }

// Pipe I/O runs on a worker with bounded waits, never on OBS's normal UI path.
QByteArray request(const QByteArray &command)
{
    // After a response the server closes one instance and creates the next.
    // WaitNamedPipe returns immediately when no instance exists, even with a timeout.
    // Retry that small handoff gap on this worker, without launching a second EXE.
    DWORD sessionId = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &sessionId)) return "ERROR pipe-session";
    const auto sessionPath = witherchat::sessionPipeName(QString::fromWCharArray(pipePath), sessionId).toStdWString();
    QElapsedTimer connecting;
    connecting.start();
    HANDLE pipe = INVALID_HANDLE_VALUE;
    bool busy = false;
    while (connecting.elapsed() < 800) {
        const auto remaining = static_cast<DWORD>(qMax<qint64>(1, 800 - connecting.elapsed()));
        if (WaitNamedPipeW(sessionPath.c_str(), qMin<DWORD>(remaining, 100))) {
            pipe = CreateFileW(sessionPath.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                               OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (pipe != INVALID_HANDLE_VALUE) break;
        }
        const auto error = GetLastError();
        if (error == ERROR_PIPE_BUSY || error == ERROR_SEM_TIMEOUT) busy = true;
        else if (error == ERROR_ACCESS_DENIED) return "ERROR pipe-access-denied";
        else if (error != ERROR_FILE_NOT_FOUND && error != ERROR_PATH_NOT_FOUND)
            return "ERROR pipe-connect";
        QThread::msleep(qMin<DWORD>(remaining, 20));
    }
    if (pipe == INVALID_HANDLE_VALUE) return busy ? "ERROR pipe-busy" : "ERROR no-chat";
    HANDLE event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!event) { CloseHandle(pipe); return "ERROR pipe-event"; }
    QElapsedTimer deadline;
    deadline.start();
    auto io = [&](bool write, char *data, DWORD size, DWORD &count) {
        ResetEvent(event);
        OVERLAPPED overlap{};
        overlap.hEvent = event;
        BOOL done = write ? WriteFile(pipe, data, size, &count, &overlap)
                          : ReadFile(pipe, data, size, &count, &overlap);
        if (!done && GetLastError() == ERROR_IO_PENDING) {
            const auto remaining = static_cast<DWORD>(qMax<qint64>(0, 2000 - deadline.elapsed()));
            if (WaitForSingleObject(event, remaining) == WAIT_OBJECT_0)
                done = GetOverlappedResult(pipe, &overlap, &count, FALSE);
            else {
                CancelIoEx(pipe, &overlap);
                GetOverlappedResult(pipe, &overlap, &count, TRUE);
                done = FALSE;
            }
        }
        return done != FALSE;
    };
    auto bytes = command + '\n';
    DWORD count = 0;
    QByteArray result;
    bool complete = false;
    if (io(true, bytes.data(), static_cast<DWORD>(bytes.size()), count) &&
        count == static_cast<DWORD>(bytes.size())) {
        std::array<char, 129> buffer{};
        while (result.size() < 129 && deadline.elapsed() < 2000) {
            const auto available = static_cast<DWORD>(129 - result.size());
            if (!io(false, buffer.data(), available, count) || count == 0) break;
            result.append(buffer.data(), static_cast<int>(count));
            const auto end = result.indexOf('\n');
            if (end >= 0) { result = result.left(end).trimmed(); complete = true; break; }
        }
        if (!complete) result = "ERROR pipe-frame";
    }
    CloseHandle(event);
    CloseHandle(pipe);
    return result.isEmpty() ? QByteArray("ERROR pipe-timeout") : result;
}

// SetParent sends synchronous native messages to OBS. Keep the GUI message pump
// running during shutdown, otherwise both processes can wait on each other.
void finishWorker(QThread *thread)
{
    if (!thread->isFinished()) {
        QEventLoop loop;
        QObject::connect(thread, &QThread::finished, &loop, &QEventLoop::quit, Qt::QueuedConnection);
        if (!thread->isFinished()) loop.exec(QEventLoop::ExcludeUserInputEvents);
    }
    thread->wait();
    delete thread;
}

class NativeSurface final : public QWidget {
public:
    HWND child = nullptr;
    std::function<void()> showDonations;
    std::function<void()> showChat;
    explicit NativeSurface(QWidget *parent) : QWidget(parent)
    {
        setMinimumSize(280, 280);
        setAttribute(Qt::WA_NativeWindow);
        setFocusPolicy(Qt::StrongFocus);
    }
    void activateChild()
    {
        if (child && IsWindow(child) && IsWindowVisible(child) &&
            GetParent(child) == reinterpret_cast<HWND>(winId())) SetFocus(child);
    }
protected:
    void focusInEvent(QFocusEvent *event) override
    {
        QWidget::focusInEvent(event);
        QTimer::singleShot(0, this, [this] { activateChild(); });
    }
    bool nativeEvent(const QByteArray &eventType, void *message, qintptr *result) override
    {
        const auto *native = static_cast<MSG *>(message);
        static const auto donationMessage = RegisterWindowMessageW(L"WitherChat.ObsDock.ShowDonations.v1");
        static const auto chatMessage = RegisterWindowMessageW(L"WitherChat.ObsDock.ShowChat.v1");
        const auto sender = reinterpret_cast<HWND>(native->wParam);
        if (native->message == donationMessage && IsWindow(sender) &&
            GetParent(sender) == reinterpret_cast<HWND>(winId()) && showDonations) {
            showDonations();
            *result = 0;
            return true;
        }
        if (native->message == chatMessage && IsWindow(sender) &&
            GetParent(sender) == reinterpret_cast<HWND>(winId()) && showChat) {
            showChat(); *result = 0; return true;
        }
        return QWidget::nativeEvent(eventType, message, result);
    }
};

class DockWidget final : public QWidget {
    QStackedLayout *pages;
    NativeSurface *surface;
    NativeSurface *donationSurface;
    QTabWidget *tabs;
    QLabel *donationStatus;
    HWND donationWindow = nullptr;
    QLabel *status;
    QPushButton *startButton;
    QPushButton *chooseButton;
    QThread *worker = nullptr;
    QTimer retryTimer;
    QTimer healthTimer;
    HWND parentWindow = nullptr;
    HWND childWindow = nullptr;
    bool launched = false;
    bool stopping = false;
    int attempts = 0;
    QString executable;

    QByteArray command(bool attach) const
    {
        return QByteArray(attach ? "ATTACH " : "DETACH ") + QByteArray::number(GetCurrentProcessId()) +
            ' ' + QByteArray::number(reinterpret_cast<quintptr>(parentWindow));
    }
    void connectDonations()
    {
        if (worker || stopping || !childWindow) return;
        const auto parent = reinterpret_cast<HWND>(donationSurface->winId());
        const auto attach = QByteArray("ATTACH_DONATIONS ") + QByteArray::number(GetCurrentProcessId()) +
            ' ' + QByteArray::number(reinterpret_cast<quintptr>(parent));
        auto response = std::make_shared<QByteArray>();
        auto *thread = witherchat::createOwnedWorker(this, [attach, response] { *response = request(attach); });
        worker = thread;
        QObject::connect(thread, &QThread::finished, this, [this, response, thread, parent] {
            if (worker != thread) return;
            worker = nullptr;
            thread->deleteLater();
            if (stopping) return;
            trace((QByteArray("donations ") + *response).constData());
            bool ok = false;
            const auto handle = response->startsWith("OK ") ? response->mid(3).toULongLong(&ok) : 0;
            const auto window = reinterpret_cast<HWND>(static_cast<quintptr>(handle));
            if (ok && IsWindow(window) && GetParent(window) == parent) {
                donationWindow = window;
                donationSurface->child = window;
                donationStatus->hide();
                if (tabs->currentIndex() == 1) donationSurface->setFocus();
            } else {
                donationStatus->setText(QStringLiteral("Не удалось открыть донаты в доке. Обновите WitherChat.exe.\n") +
                    QString::fromLatin1(*response));
                donationStatus->show();
            }
        });
        thread->start();
    }
    void showError(const QString &message)
    {
        status->setText(message);
        pages->setCurrentIndex(0);
        startButton->setEnabled(true);
        chooseButton->setEnabled(true);
    }
    void connectChat()
    {
        if (worker || stopping) return;
        startButton->setEnabled(false);
        chooseButton->setEnabled(false);
        status->setText(QStringLiteral("Подключение WitherChat…"));
        parentWindow = reinterpret_cast<HWND>(surface->winId());
        auto response = std::make_shared<QByteArray>();
        const auto attach = command(true);
        auto *thread = witherchat::createOwnedWorker(this, [attach, response] { *response = request(attach); });
        worker = thread;
        QObject::connect(thread, &QThread::finished, this, [this, response, thread] {
            if (worker != thread) return;
            worker = nullptr;
            thread->deleteLater();
            if (stopping) return;
            const auto result = *response;
            trace(result.constData());
            if (result.startsWith("OK ")) {
                bool ok = false;
                const auto handle = result.mid(3).toULongLong(&ok);
                childWindow = reinterpret_cast<HWND>(static_cast<quintptr>(handle));
                if (ok && IsWindow(childWindow) && GetParent(childWindow) == parentWindow) {
                    surface->child = childWindow;
                    pages->setCurrentIndex(1);
                    connectDonations();
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
                    QTimer::singleShot(1000, this, [this] {
                        RECT rect{};
                        if (GetClientRect(childWindow, &rect)) {
                            const auto details = QByteArray("native-child size=") +
                                QByteArray::number(rect.right) + 'x' + QByteArray::number(rect.bottom) +
                                " parent=" + QByteArray::number(reinterpret_cast<quintptr>(GetParent(childWindow)));
                            trace(details.constData());
                        }
                    });
#endif
                    startButton->setEnabled(true);
                    chooseButton->setEnabled(true);
                    return;
                }
            }
            if (result == "ERROR no-chat" || result == "ERROR pipe-busy") {
                if (result == "ERROR no-chat" && !launched) {
                    if (!QFileInfo::exists(executable)) {
                        showError(QStringLiteral("Выберите новый WitherChat.exe с поддержкой OBS."));
                        return;
                    }
                    const QStringList args{QStringLiteral("--obs-dock"),
                        QString::number(GetCurrentProcessId()),
                        QString::number(reinterpret_cast<quintptr>(parentWindow))};
                    if (!QProcess::startDetached(executable, args, QFileInfo(executable).absolutePath())) {
                        showError(QStringLiteral("Не удалось запустить чат. Проверьте блокировку Windows."));
                        return;
                    }
                    launched = true;
                }
                if (++attempts <= 10) { retryTimer.start(500); return; }
                showError(result == "ERROR pipe-busy"
                    ? QStringLiteral("Чат занят другим подключением. Подождите и повторите.")
                    : QStringLiteral("Чат не ответил. Закройте старую версию WitherChat и повторите."));
                return;
            }
            if (result == "ERROR already-attached")
                showError(QStringLiteral("Чат уже встроен в другое окно OBS."));
            else if (result == "ERROR incompatible-dpi")
                showError(QStringLiteral("Масштабирование OBS и чата несовместимо. Встраивание отменено."));
            else showError(QStringLiteral("Не удалось встроить чат: ") + QString::fromLatin1(result));
        });
        thread->start();
    }
public:
    DockWidget()
    {
        setMinimumSize(280, 280);
        pages = new QStackedLayout(this);
        auto *welcome = new QWidget(this);
        auto *layout = new QVBoxLayout(welcome);
        status = new QLabel(QStringLiteral("WitherChat — полный чат внутри OBS.\nНажмите «Открыть чат»."), welcome);
        status->setWordWrap(true);
        startButton = new QPushButton(QStringLiteral("Открыть чат"), welcome);
        chooseButton = new QPushButton(QStringLiteral("Выбрать WitherChat.exe"), welcome);
        layout->addStretch();
        layout->addWidget(status);
        layout->addWidget(startButton);
        layout->addWidget(chooseButton);
        layout->addStretch();
        pages->addWidget(welcome);
        tabs = new QTabWidget(this);
        surface = new NativeSurface(tabs);
        donationSurface = new NativeSurface(tabs);
        auto *donationLayout = new QVBoxLayout(donationSurface);
        donationStatus = new QLabel(QStringLiteral("Подключение DonationAlerts…"), donationSurface);
        donationStatus->setWordWrap(true);
        donationLayout->addWidget(donationStatus);
        tabs->addTab(surface, QStringLiteral("Чат"));
        tabs->addTab(donationSurface, QStringLiteral("Донаты"));
        pages->addWidget(tabs);
        surface->showDonations = [this] { tabs->setCurrentIndex(1); };
        donationSurface->showChat = [this] { tabs->setCurrentIndex(0); };
        QObject::connect(tabs, &QTabWidget::currentChanged, this, [this](int index) {
            auto *active = index == 1 ? donationSurface : surface;
            active->setFocus();
            active->activateChild();
            if (index == 1 && !donationWindow) connectDonations();
        });
        QSettings settings(QStringLiteral("WitherChat"), QStringLiteral("ObsDock"));
        executable = witherchat::resolveExecutable(
            settings.value(QStringLiteral("Executable"), bundledExe).toString(), bundledExe);
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
        // An isolated probe must test its own bundled build, never a user's
        // selected executable or profile. No setting is changed or saved.
        if (qEnvironmentVariableIsSet("WITHERCHAT_OBS_DOCK_PROBE")) executable = bundledExe;
#endif
        retryTimer.setSingleShot(true);
        QObject::connect(&retryTimer, &QTimer::timeout, this, [this] { connectChat(); });
        QObject::connect(startButton, &QPushButton::clicked, this, [this] { open(); });
        QObject::connect(chooseButton, &QPushButton::clicked, this, [this] {
            const auto path = QFileDialog::getOpenFileName(this, QStringLiteral("WitherChat.exe"),
                executable, QStringLiteral("WitherChat (WitherChat*.exe)"));
            if (path.isEmpty()) return;
            executable = QFileInfo(path).absoluteFilePath();
            QSettings settings(QStringLiteral("WitherChat"), QStringLiteral("ObsDock"));
            settings.setValue(QStringLiteral("Executable"), executable);
            status->setText(executable);
        });
        healthTimer.setInterval(1000);
        QObject::connect(&healthTimer, &QTimer::timeout, this, [this] {
            if (childWindow && (!IsWindow(childWindow) || GetParent(childWindow) != parentWindow)) {
                childWindow = nullptr;
                surface->child = nullptr;
                donationWindow = nullptr;
                donationSurface->child = nullptr;
                showError(QStringLiteral("Чат закрыт или вынесен в обычное окно. Нажмите «Открыть чат»."));
            }
        });
        healthTimer.start();
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
        QTimer::singleShot(15000, this, [this] {
            if (worker || !childWindow || stopping) { trace("native-burst unavailable"); return; }
            const auto attach = command(true);
            auto passed = std::make_shared<int>(0);
            auto *thread = witherchat::createOwnedWorker(this, [attach, passed] {
                for (int index = 0; index < 64; ++index) {
                    if (!request(attach).startsWith("OK ")) break;
                    ++*passed;
                }
            });
            worker = thread;
            QObject::connect(thread, &QThread::finished, this, [this, thread, passed] {
                if (worker != thread) return;
                worker = nullptr;
                thread->deleteLater();
                trace((QByteArray("native-burst passed=") + QByteArray::number(*passed) + "/64").constData());
            });
            thread->start();
        });
        QTimer::singleShot(14000, this, [this] {
            if (!childWindow || !donationWindow) { trace("parity probe missing-window"); return; }
            if (getMainWindow) {
                auto *main = static_cast<QWidget *>(getMainWindow());
                main->hide();
                main->showNormal();
                main->activateWindow();
            }
            tabs->setCurrentIndex(1);
            donationSurface->activateChild();
            RECT rect{};
            GetClientRect(donationWindow, &rect);
            open();
            trace((QByteArray("native-open-from-donations tab=") + QByteArray::number(tabs->currentIndex()) +
                " worker=" + QByteArray::number(worker ? 1 : 0)).constData());
            tabs->setCurrentIndex(1);
            donationSurface->activateChild();
            trace((QByteArray("native-donations size=") + QByteArray::number(rect.right) + 'x' +
                QByteArray::number(rect.bottom) + " focus=" +
                QByteArray::number(GetFocus() == donationWindow ? 1 : 0)).constData());
            tabs->setCurrentIndex(0);
            surface->activateChild();
            trace((QByteArray("native-chat focus=") + QByteArray::number(GetFocus() == childWindow ? 1 : 0) +
                " visible=" + QByteArray::number(IsWindowVisible(childWindow) ? 1 : 0)).constData());
            QTimer::singleShot(100, this, [this] {
                for (auto window = childWindow; window; window = GetParent(window)) {
                    trace((QByteArray("visibility-chain hwnd=") + QByteArray::number(reinterpret_cast<quintptr>(window)) +
                        " own-visible=" + QByteArray::number((GetWindowLongPtrW(window, GWL_STYLE) & WS_VISIBLE) ? 1 : 0) +
                        " enabled=" + QByteArray::number(IsWindowEnabled(window) ? 1 : 0)).constData());
                }
                trace((QByteArray("surface-id expected=") + QByteArray::number(reinterpret_cast<quintptr>(parentWindow)) +
                    " current=" + QByteArray::number(surface->winId())).constData());
                surface->activateChild();
                trace((QByteArray("native-chat settled-focus=") + QByteArray::number(GetFocus() == childWindow ? 1 : 0) +
                    " visible=" + QByteArray::number(IsWindowVisible(childWindow) ? 1 : 0)).constData());
            });
            PostMessageW(parentWindow, RegisterWindowMessageW(L"WitherChat.ObsDock.ShowDonations.v1"),
                reinterpret_cast<WPARAM>(childWindow), 0);
            QTimer::singleShot(300, this, [this] {
                trace((QByteArray("native-show-donations tab=") + QByteArray::number(tabs->currentIndex())).constData());
                PostMessageW(reinterpret_cast<HWND>(donationSurface->winId()),
                    RegisterWindowMessageW(L"WitherChat.ObsDock.ShowChat.v1"),
                    reinterpret_cast<WPARAM>(donationWindow), 0);
            });
            QTimer::singleShot(700, this, [this] {
                trace((QByteArray("native-back-to-chat tab=") + QByteArray::number(tabs->currentIndex())).constData());
            });
            QTimer::singleShot(2000, this, [this] {
                for (auto *ancestor = parentWidget(); ancestor; ancestor = ancestor->parentWidget()) {
                    if (auto *panel = qobject_cast<QDockWidget *>(ancestor)) {
                        panel->setFloating(false); panel->resize(360, 440); break;
                    }
                }
            });
            QTimer::singleShot(3000, this, [this] {
                trace((QByteArray("native-docked parent-valid=") +
                    QByteArray::number(GetParent(childWindow) == reinterpret_cast<HWND>(surface->winId()) ? 1 : 0) +
                    " visible=" + QByteArray::number(IsWindowVisible(childWindow) ? 1 : 0)).constData());
                for (auto *ancestor = parentWidget(); ancestor; ancestor = ancestor->parentWidget()) {
                    if (auto *panel = qobject_cast<QDockWidget *>(ancestor)) {
                        panel->setFloating(true); panel->resize(600, 640); panel->show(); break;
                    }
                }
            });
            QTimer::singleShot(4000, this, [this] {
                RECT resized{};
                GetClientRect(childWindow, &resized);
                trace((QByteArray("native-refloated parent-valid=") +
                    QByteArray::number(GetParent(childWindow) == reinterpret_cast<HWND>(surface->winId()) ? 1 : 0) +
                    " size=" + QByteArray::number(resized.right) + 'x' + QByteArray::number(resized.bottom) +
                    " visible=" + QByteArray::number(IsWindowVisible(childWindow) ? 1 : 0)).constData());
            });
            open();
        });
#endif
    }
    void open()
    {
        if (stopping) return;
        if (childWindow && IsWindow(childWindow) && GetParent(childWindow) == parentWindow) {
            pages->setCurrentIndex(1);
            tabs->setCurrentIndex(0);
            surface->setFocus();
            surface->activateChild();
            return;
        }
        // Repeated clicks while launching must not reset the launch/retry state.
        if (worker || retryTimer.isActive()) return;
        attempts = 0;
        launched = false;
        connectChat();
    }
    void shutdown()
    {
        if (stopping) return;
        stopping = true;
        retryTimer.stop();
        healthTimer.stop();
        if (worker) {
            auto *thread = worker;
            worker = nullptr;
            finishWorker(thread);
        }
        if (donationWindow) {
            const auto detach = QByteArray("DETACH ") + QByteArray::number(GetCurrentProcessId()) + ' ' +
                QByteArray::number(static_cast<quintptr>(donationSurface->winId()));
            auto *thread = witherchat::createOwnedWorker(this, [detach] { const auto response = request(detach); trace(response.constData()); });
            thread->start();
            finishWorker(thread);
            donationWindow = nullptr;
            donationSurface->child = nullptr;
        }
        if (parentWindow) {
            // Detach before OBS destroys the native parent, without terminating the chat.
            const auto detach = command(false);
            auto *thread = witherchat::createOwnedWorker(this, [detach] { const auto result = request(detach); trace(result.constData()); });
            thread->start();
            finishWorker(thread);
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
            const auto details = QByteArray("native-child after-detach exists=") +
                QByteArray::number(IsWindow(childWindow) ? 1 : 0) +
                " parent=" + QByteArray::number(reinterpret_cast<quintptr>(GetParent(childWindow))) +
                " child-style=" + QByteArray::number((GetWindowLongPtrW(childWindow, GWL_STYLE) & WS_CHILD) ? 1 : 0);
            trace(details.constData());
#endif
        }
    }
    ~DockWidget() override { shutdown(); }
};
QPointer<DockWidget> dock;
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
bool baselineProbe = false;
#endif
void frontendEvent(enum obs_frontend_event event, void *)
{
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
    if (baselineProbe && event == OBS_FRONTEND_EVENT_FINISHED_LOADING) {
        QTimer::singleShot(20000, qApp, [] {
            trace("baseline completed without dock; closing isolated OBS");
            if (getMainWindow) static_cast<QWidget *>(getMainWindow())->close();
        });
    }
#endif
    if (event == OBS_FRONTEND_EVENT_EXIT && dock) dock->shutdown();
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
    if (event == OBS_FRONTEND_EVENT_FINISHED_LOADING && dock &&
        qEnvironmentVariableIsSet("WITHERCHAT_OBS_DOCK_PROBE"))
    {
        QTimer::singleShot(200, dock, [] {
            if (auto *panel = dock->parentWidget()) { panel->show(); panel->raise(); }
            for (auto *ancestor = dock->parentWidget(); ancestor; ancestor = ancestor->parentWidget()) {
                if (auto *panel = qobject_cast<QDockWidget *>(ancestor)) {
                    panel->setFloating(true); panel->show(); panel->raise(); break;
                }
            }
            dock->show();
            dock->open();
        });
        QTimer::singleShot(25000, dock, [] {
            trace("native probe completed; closing isolated OBS");
            if (getMainWindow) static_cast<QWidget *>(getMainWindow())->close();
        });
    }
#endif
}
}

MODULE_EXPORT bool obs_module_load(void)
{
    const auto frontend = GetModuleHandleW(L"obs-frontend-api.dll");
    const auto core = GetModuleHandleW(L"obs.dll");
    if (!frontend || !core || !qApp) return false;
    addDock = reinterpret_cast<decltype(addDock)>(GetProcAddress(frontend, "obs_frontend_add_dock_by_id"));
    removeDock = reinterpret_cast<decltype(removeDock)>(GetProcAddress(frontend, "obs_frontend_remove_dock"));
    addMenu = reinterpret_cast<decltype(addMenu)>(GetProcAddress(frontend, "obs_frontend_add_tools_menu_qaction"));
    getMainWindow = reinterpret_cast<decltype(getMainWindow)>(GetProcAddress(frontend, "obs_frontend_get_main_window"));
    addEvent = reinterpret_cast<decltype(addEvent)>(GetProcAddress(frontend, "obs_frontend_add_event_callback"));
    removeEvent = reinterpret_cast<decltype(removeEvent)>(GetProcAddress(frontend, "obs_frontend_remove_event_callback"));
    findFile = reinterpret_cast<decltype(findFile)>(GetProcAddress(core, "obs_find_module_file"));
    freeObs = reinterpret_cast<decltype(freeObs)>(GetProcAddress(core, "bfree"));
    logObs = reinterpret_cast<decltype(logObs)>(GetProcAddress(core, "blog"));
    if (!addDock || !removeDock || !addMenu || !addEvent || !removeEvent || !findFile || !freeObs) return false;
#ifdef WITHERCHAT_OBS_DOCK_DIAGNOSTICS
    if (qEnvironmentVariableIsSet("WITHERCHAT_OBS_DOCK_BASELINE")) {
        baselineProbe = true;
        addEvent(frontendEvent, nullptr);
        trace("baseline: no dock, no chat, no menu");
        return true;
    }
#endif
    if (auto *path = findFile(obs_current_module(), "WitherChat.exe")) {
        bundledExe = QString::fromUtf8(path);
        freeObs(path);
    }
    dock = new DockWidget;
    if (!addDock(dockId, "WitherChat", dock)) { delete dock; dock = nullptr; return false; }
    menuAction = static_cast<QAction *>(addMenu("WitherChat — открыть чат"));
    if (menuAction) QObject::connect(menuAction, &QAction::triggered, dock, [] {
        if (auto *panel = dock->parentWidget()) { panel->show(); panel->raise(); }
        dock->open();
    });
    addEvent(frontendEvent, nullptr);
    trace("module loaded; waiting for Open chat");
    return true;
}
MODULE_EXPORT void obs_module_unload(void)
{
    if (removeEvent) removeEvent(frontendEvent, nullptr);
    if (dock) { dock->shutdown(); removeDock(dockId); dock = nullptr; }
    delete menuAction;
    menuAction = nullptr;
}
