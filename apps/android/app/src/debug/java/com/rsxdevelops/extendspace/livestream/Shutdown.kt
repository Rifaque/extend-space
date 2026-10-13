package com.rsxdevelops.extendspace.livestream

import java.io.IOException

/**
 * Says goodbye and then closes, without letting the goodbye hold anything up.
 *
 * [goodbye] writes to a socket, and a write to a peer that has stopped reading blocks until the
 * socket's send timeout, five seconds here. So it runs on a thread of its own and is given
 * [budgetMillis]. Then [close] runs whatever happened: closing the socket is also what ends a write
 * that is still blocked, here or on any other thread. The goodbye is best effort; the close is not.
 *
 * @return whether the goodbye finished within its budget.
 */
internal fun sayGoodbyeThenClose(budgetMillis: Long, goodbye: () -> Unit, close: () -> Unit): Boolean {
    val speaker = Thread(
        {
            try {
                goodbye()
            } catch (exception: RuntimeException) {
                // Best effort: a goodbye that fails is a goodbye that was not said.
            }
        },
        "extendspace-live-goodbye",
    )
    speaker.isDaemon = true
    speaker.start()
    try {
        speaker.join(budgetMillis)
    } finally {
        close()
    }
    return !speaker.isAlive
}

/**
 * Runs [action] until it succeeds, at most [attempts] times, pausing between tries. The last
 * failure is thrown. Used for taking the local socket's name, which a session that is still
 * shutting down on another thread may hold for a moment longer.
 */
internal fun <T> retryOnIoFailure(attempts: Int, pauseMillis: Long, keepTrying: () -> Boolean = { true }, pause: (Long) -> Unit = Thread::sleep, action: () -> T): T {
    var tried = 0
    while (true) {
        try {
            return action()
        } catch (exception: IOException) {
            tried++
            if (tried >= attempts || !keepTrying()) throw exception
            pause(pauseMillis)
        }
    }
}
