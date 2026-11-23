package com.rsxdevelops.extendspace.link

import android.net.LocalServerSocket
import android.net.LocalSocket
import android.net.LocalSocketAddress
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.protocol.Protocol
import java.io.IOException
import java.util.concurrent.LinkedBlockingQueue
import java.io.InputStream
import java.io.OutputStream

// The socket seam under UsbHostLink: listening, accepting and one connection, with nothing of the
// protocol. The real ones are android.net.LocalServerSocket and LocalSocket in the abstract
// namespace, which adb's `forward tcp:<port> localabstract:extendspace` reaches; tests use pipes.

/** Starts listening. Throws [IOException] if the name cannot be taken. */
internal fun interface HostListenerFactory {
    fun listen(): HostListener
}

/** A listening socket. */
internal interface HostListener {
    /** Blocks until a peer connects. Throws [IOException] once [close] has been called. */
    fun accept(): HostConnection

    /** Stops listening and makes a blocked [accept] return (by throwing). Safe more than once. */
    fun close()
}

/** One accepted connection. */
internal interface HostConnection {
    val input: InputStream
    val output: OutputStream

    /** The connecting process's user ID, or null if it cannot be known. */
    val peerUid: Int?

    /**
     * True when the listener has already authenticated the peer (a Wi-Fi connection from a trusted PC,
     * D75); the link's adb user-ID check is for the local socket only.
     */
    val preauthorized: Boolean get() = false

    /** The route the connection came by. */
    val transport: TransportKind get() = TransportKind.USB

    /** Bounds each blocking read; 0 for none. A read that runs out throws `SocketTimeoutException`-like [IOException]. */
    fun setReadTimeout(millis: Int)

    /** Closes both directions; a read or write blocked on it, on any thread, then fails. Safe more than once. */
    fun close()
}

/**
 * Listens on several routes at once (D75): the local socket that adb forwards to, and the Wi-Fi
 * endpoint when [includeWifi] says so at the moment listening starts. Connections are handed on in
 * arrival order. A route that cannot listen is logged and left out; only if none can is listening a
 * failure. Closing closes every route.
 */
internal class CompositeHostListenerFactory(
    private val usb: HostListenerFactory,
    private val wifi: HostListenerFactory?,
    private val includeWifi: () -> Boolean,
    private val log: (String) -> Unit = {},
) : HostListenerFactory {
    override fun listen(): HostListener {
        val routes = mutableListOf<HostListener>()
        var lastFailure: IOException? = null
        for (factory in listOfNotNull(usb, wifi?.takeIf { includeWifi() })) {
            try {
                routes += factory.listen()
            } catch (failure: IOException) {
                lastFailure = failure
                log("a route could not listen: ${failure.message}")
            } catch (failure: IllegalStateException) {
                lastFailure = IOException(failure.message, failure)
                log("a route could not listen: ${failure.message}")
            }
        }
        if (routes.isEmpty()) throw lastFailure ?: IOException("No route to listen on.")
        return if (routes.size == 1) routes[0] else CompositeListener(routes)
    }

    private class CompositeListener(private val routes: List<HostListener>) : HostListener {
        private val arrivals = LinkedBlockingQueue<Any>()

        @Volatile private var closed = false

        init {
            for (route in routes) {
                Thread(
                    {
                        while (!closed) {
                            val connection = try {
                                route.accept()
                            } catch (exception: IOException) {
                                arrivals.offer(RouteEnded)
                                return@Thread
                            }
                            if (closed) connection.close() else arrivals.offer(connection)
                        }
                    },
                    "extendspace-link-route",
                ).apply { isDaemon = true }.start()
            }
        }

        // Counted across calls: a route that ended during an earlier accept() is still gone.
        private var ended = 0

        override fun accept(): HostConnection {
            while (true) {
                when (val next = arrivals.take()) {
                    is HostConnection -> if (closed) next.close() else return next
                    RouteEnded -> if (++ended >= routes.size || closed) throw IOException("Listening for the PC stopped.")
                    Closed -> throw IOException("The listener was closed.")
                }
            }
        }

        override fun close() {
            if (closed) return
            closed = true
            routes.forEach { it.close() }
            arrivals.offer(Closed)
        }

        private object RouteEnded

        private object Closed
    }
}

/** The real listener: a local socket in the abstract namespace. */
internal class LocalSocketListenerFactory(private val name: String = Protocol.LOCAL_SOCKET_NAME) : HostListenerFactory {
    override fun listen(): HostListener = LocalSocketListener(LocalServerSocket(name), name)
}

private class LocalSocketListener(private val server: LocalServerSocket, private val name: String) : HostListener {
    @Volatile private var closed = false

    override fun accept(): HostConnection {
        val socket = server.accept()
        if (closed) {
            socket.close()
            throw IOException("The listener was closed.")
        }
        return LocalSocketConnection(socket)
    }

    override fun close() {
        if (closed) return
        closed = true
        // accept() does not notice its socket being closed, so it is woken by connecting to it, as
        // the experiment's wakeAccept does.
        try {
            LocalSocket().use { it.connect(LocalSocketAddress(name, LocalSocketAddress.Namespace.ABSTRACT)) }
        } catch (exception: IOException) {
            // Nothing was blocked in accept(), or it is already closing.
        }
        try {
            server.close()
        } catch (exception: IOException) {
            // Already closed.
        }
    }
}

private class LocalSocketConnection(private val socket: LocalSocket) : HostConnection {
    override val input: InputStream = socket.inputStream
    override val output: OutputStream = socket.outputStream

    override val peerUid: Int?
        get() = try {
            socket.peerCredentials.uid
        } catch (exception: IOException) {
            null
        }

    override fun setReadTimeout(millis: Int) {
        socket.soTimeout = millis
    }

    override fun close() {
        try {
            // Shutting the input down is what wakes a reader blocked on this socket.
            socket.shutdownInput()
            socket.shutdownOutput()
        } catch (exception: IOException) {
            // Already closed by the other side.
        }
        try {
            socket.close()
        } catch (exception: IOException) {
            // Nothing more can be done.
        }
    }
}
