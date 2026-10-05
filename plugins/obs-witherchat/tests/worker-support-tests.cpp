// SPDX-License-Identifier: GPL-2.0-or-later
#include "worker-support.hpp"
#include <QCoreApplication>
#include <QFile>
#include <QPointer>
#include <QTemporaryDir>
#include <iostream>
#include <limits>
#include <memory>
#include <stdexcept>

namespace {
void check(bool condition, const char *message)
{
    if (!condition) throw std::runtime_error(message);
}
}
int main(int argc, char **argv)
{
    QCoreApplication application(argc, argv);
    try {
        QTemporaryDir temporary;
        check(temporary.isValid(), "temporary directory");
        const auto bundled = temporary.filePath(QStringLiteral("WitherChat.exe"));
        const auto selected = temporary.filePath(QStringLiteral("Selected.exe"));
        for (const auto &path : {bundled, selected}) {
            QFile file(path);
            check(file.open(QIODevice::WriteOnly), "create fixture executable");
            check(file.write("fixture") == 7, "write fixture executable");
        }
        check(witherchat::resolveExecutable(selected, bundled) == selected, "keep valid selection");
        check(witherchat::resolveExecutable(temporary.path(), bundled) == bundled, "directory is not an executable");
        check(witherchat::resolveExecutable(temporary.filePath(QStringLiteral("gone.exe")), bundled) == bundled,
              "missing selected executable falls back to bundled");
        check(witherchat::resolveExecutable(QString(), bundled) == bundled, "empty selection falls back to bundled");
        check(witherchat::sessionPipeName(QStringLiteral("channel"), 0) == QStringLiteral("channel-s0"), "session zero");
        check(witherchat::sessionPipeName(QStringLiteral("channel"), 2) == QStringLiteral("channel-s2"), "session isolation");
        check(witherchat::sessionPipeName(QStringLiteral("channel"), 2147483647UL) ==
              QStringLiteral("channel-s2147483647"), "native/managed suffix agreement");
        for (int iteration = 0; iteration < 64; ++iteration) {
            auto owner = std::make_unique<QObject>();
            QPointer<QThread> thread = witherchat::createOwnedWorker(owner.get(), [] {});
            check(thread->parent() == owner.get(), "worker lifetime owned by dock");
            thread->start();
            check(thread->wait(3000), "worker finishes");
            thread->deleteLater();
            // Deliberately do not process deferred deletes: unload still owns
            // every finished worker and destroys its plugin callable in time.
            owner.reset();
            check(thread.isNull(), "no worker retained past dock destruction");
        }
        std::cout << "PASS native helpers: path recovery, session names, 64 owned-worker cycles\n";
        return 0;
    } catch (const std::exception &exception) {
        std::cerr << exception.what() << '\n';
        return 1;
    }
}
