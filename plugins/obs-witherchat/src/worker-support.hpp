// SPDX-License-Identifier: GPL-2.0-or-later
#pragma once
#include <QFileInfo>
#include <QObject>
#include <QString>
#include <QThread>
#include <utility>

namespace witherchat {
inline QString sessionPipeName(const QString &name, unsigned long sessionId)
{
    return name + QStringLiteral("-s") + QString::number(sessionId);
}
inline QString resolveExecutable(const QString &preferred, const QString &bundled)
{
    // Preserve a valid user-selected executable. A missing file/directory must
    // not make the complete bundled installation unusable after a disk change.
    return QFileInfo(preferred).isFile() ? preferred : bundled;
}

template<typename Function>
QThread *createOwnedWorker(QObject *owner, Function &&function)
{
    auto *thread = QThread::create(std::forward<Function>(function));
    // Finished workers still awaiting deleteLater must not outlive the dock
    // (and the plugin DLL containing their callable's destruction code).
    thread->setParent(owner);
    return thread;
}
}
